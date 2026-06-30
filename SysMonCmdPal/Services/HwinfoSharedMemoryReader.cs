// Copyright (c) 2026 SysMonCmdPal
// HWiNFO 共享内存读取器 — 从 HWiNFO 的 Global\HWiNFO_SENS_SM2 读取传感器数据
// 用户态操作，不需要管理员权限，不受 AppContainer 限制
// 已知限制: HWiNFO 共享内存每 ~12 小时需要重置（HWiNFO 重启）
//
// v2.3: 改用 .NET 托管 API (MemoryMappedFile) 替代 P/Invoke，
//       减少 IL 中的共享内存 P/Invoke 调用链，降低杀软误报概率。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace SysMonCmdPal;

/// <summary>
/// 从 HWiNFO 共享内存读取传感器数据。
/// 提供 CPU/GPU 温度以及全量传感器读取。
/// </summary>
internal sealed class HwinfoSharedMemoryReader : IDisposable
{
    public static HwinfoSharedMemoryReader Instance { get; } = new();

    // ---- HWiNFO shared memory constants ----
    private const string HwinfoMapName = @"Global\HWiNFO_SENS_SM2";
    private const uint HwinfoSignature = 0x53695748; // "HWiS" little-endian
    private const int HwinfoTypeTemp = 1;
    private const int HwinfoStrLen = 128;

    // Header field indices (as int32 array from base)
    private const int HdrSignature = 0;   // offset 0
    private const int HdrEntryOffset = 8;  // offset 32: byte offset to first entry
    private const int HdrEntrySize = 9;    // offset 36: size per entry
    private const int HdrEntryCount = 10;  // offset 40: number of entries

    // Per-entry field offsets (within each entry struct, pack=1)
    private const int EntryType = 0;       // int32: sensor type
    private const int EntryLabel = 12;     // char[128]: sensor label (ANSI)
    private const int EntryValue = 284;    // double: current value

    // ---- State ----
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private byte[]? _labelBuf;   // reused buffer for ANSI label reading
    private int _entryOffset;
    private int _entrySize;
    private int _entryCount;
    private bool _available;
    private DateTime _firstOpenTime = DateTime.MinValue;
    private DateTime _lastRetryTime = DateTime.MinValue;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TwelveHourWarning = TimeSpan.FromHours(12);
    private readonly object _lock = new();

    // ---- 12-hour reset tracking ----
    /// <summary>首次成功打开 HWiNFO 共享内存的时间</summary>
    public DateTime FirstOpenTime => _firstOpenTime;

    /// <summary>是否接近 12 小时重置窗口</summary>
    public bool IsNearResetWindow =>
        _available && (DateTime.UtcNow - _firstOpenTime) > ElevenHourMark;

    private static readonly TimeSpan ElevenHourMark = TimeSpan.FromHours(11);

    /// <summary>距离 12 小时窗口的剩余时间</summary>
    public TimeSpan TimeUntilReset =>
        _available ? TwelveHourWarning - (DateTime.UtcNow - _firstOpenTime) : TimeSpan.Zero;

    /// <summary>HWiNFO 共享内存是否可用</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                if (!_available && DateTime.UtcNow - _lastRetryTime > RetryCooldown)
                {
                    _lastRetryTime = DateTime.UtcNow;
                    TryInit();
                }
                return _available;
            }
        }
    }

    // ========================================================================
    // Init / Cleanup
    // ========================================================================

    private void TryInit()
    {
        lock (_lock)
        {
            Cleanup();
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(HwinfoMapName, MemoryMappedFileRights.Read);
                _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                // Read header (12 x int32 = 48 bytes)
                int sig = _accessor.ReadInt32(0);
                if ((uint)sig != HwinfoSignature)
                {
                    Debug.WriteLine($"[HWiNFO] Invalid signature: 0x{sig:X8} (expected 0x{HwinfoSignature:X8})");
                    Cleanup();
                    return;
                }

                _entryOffset = _accessor.ReadInt32(HdrEntryOffset * 4);
                _entrySize = _accessor.ReadInt32(HdrEntrySize * 4);
                _entryCount = _accessor.ReadInt32(HdrEntryCount * 4);

                if (_entrySize < 160 || _entryOffset <= 0 || _entryCount <= 0)
                {
                    Debug.WriteLine($"[HWiNFO] Invalid layout: offset={_entryOffset} size={_entrySize} count={_entryCount}");
                    Cleanup();
                    return;
                }

                _labelBuf = new byte[HwinfoStrLen];
                _available = true;
                _firstOpenTime = DateTime.UtcNow;

                SensorLogger.ForceLog($"[HWiNFO] Connected: {_entryCount} sensors, entrySize={_entrySize}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HWiNFO] Init exception: {ex.Message}");
                Cleanup();
            }
        }
    }

    private void Cleanup()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
        _labelBuf = null;
        _available = false;
    }

    /// <summary>强制重置连接（HWiNFO 重启后调用）</summary>
    public void Reconnect()
    {
        lock (_lock)
        {
            SensorLogger.ForceLog("[HWiNFO] Reconnecting...");
            Cleanup();
            _firstOpenTime = DateTime.MinValue;
            TryInit();
        }
    }

    // ========================================================================
    // CPU Temperature
    // ========================================================================

    private static readonly string[] CpuPreferredLabels =
        ["CPU Package", "Tctl/Tdie", "CPU Die", "CPU CCD", "CPU Tctl"];

    /// <summary>读取 CPU 温度（两遍扫描：先精确匹配，再模糊匹配）</summary>
    public (double Temp, string Label) ReadCpuTemp()
    {
        lock (_lock)
        {
            if (!_available || _accessor == null) return (-1, "");

            try
            {
                // Pass 0: preferred labels
                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypeTemp) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (val <= 0 || val > 150) continue;

                    foreach (var pref in CpuPreferredLabels)
                        if (label.Contains(pref, StringComparison.OrdinalIgnoreCase))
                            return (val, label);
                }

                // Pass 1: any label containing "CPU"
                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypeTemp) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (val <= 0 || val > 150) continue;

                    if (label.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                        return (val, label);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HWiNFO] ReadCpuTemp exception: {ex.Message}");
                // HWiNFO may have been closed — mark for reconnect
                _available = false;
            }

            return (-1, "");
        }
    }

    // ========================================================================
    // GPU Temperature
    // ========================================================================

    private static readonly string[] GpuPreferredLabels =
        ["GPU Core", "GPU Hot Spot", "GPU Junction", "GPU Temperature"];

    /// <summary>读取 GPU 温度</summary>
    public (double Temp, string Label) ReadGpuTemp()
    {
        lock (_lock)
        {
            if (!_available || _accessor == null) return (-1, "");

            try
            {
                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypeTemp) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (val <= 0 || val > 150) continue;

                    foreach (var pref in GpuPreferredLabels)
                        if (label.Contains(pref, StringComparison.OrdinalIgnoreCase))
                            return (val, label);
                }

                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypeTemp) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (val <= 0 || val > 150) continue;

                    if (label.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                        return (val, label);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HWiNFO] ReadGpuTemp exception: {ex.Message}");
                _available = false;
            }

            return (-1, "");
        }
    }

    // ========================================================================
    // All Temperature Sensors
    // ========================================================================

    /// <summary>读取所有温度传感器</summary>
    public List<(string Label, double Value)> ReadAllTemps()
    {
        var result = new List<(string, double)>();
        lock (_lock)
        {
            if (!_available || _accessor == null) return result;

            try
            {
                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypeTemp) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (string.IsNullOrEmpty(label) || val <= -100 || val > 200) continue;

                    result.Add((label, val));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HWiNFO] ReadAllTemps exception: {ex.Message}");
                _available = false;
            }
        }

        return result;
    }

    // ========================================================================
    // Helpers (托管 API，无 P/Invoke)
    // ========================================================================

    /// <summary>从指定条目基址读取 ANSI 标签字符串</summary>
    private string ReadLabel(int baseOffset)
    {
        var buf = _labelBuf!;
        _accessor!.ReadArray(baseOffset + EntryLabel, buf, 0, HwinfoStrLen);
        int len = 0;
        while (len < HwinfoStrLen && buf[len] != 0) len++;
        return len > 0 ? Encoding.ASCII.GetString(buf, 0, len) : "";
    }

    /// <summary>从指定条目基址读取 double 值</summary>
    private double ReadValue(int baseOffset)
    {
        return _accessor!.ReadDouble(baseOffset + EntryValue);
    }

    public void Dispose()
    {
        lock (_lock) { Cleanup(); }
    }
}
