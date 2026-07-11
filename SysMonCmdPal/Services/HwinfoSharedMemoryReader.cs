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
    private const int HwinfoTypePower = 5;
    private const int HwinfoTypeUsage = 7;
    private const int HwinfoTypeData = 8;
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

                if (_entrySize < 160 || _entrySize > 1024 ||
                    _entryOffset <= 0 || _entryOffset > 65536 ||
                    _entryCount <= 0 || _entryCount > 4096)
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

    /// <summary>读取第 index 个 GPU 温度（0=第一个/集显, 1=第二个/独显）。
    /// 严格匹配 "GPU Temperature"，不匹配 Hot Spot/Memory Junction 等子项。</summary>
    public (double Temp, string Label) ReadGpuTemp(int index = 0)
    {
        lock (_lock)
        {
            if (!_available || _accessor == null) return (-1, "");

            try
            {
                int found = 0;
                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypeTemp) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (val <= 0 || val > 150) continue;

                    // 只匹配精确的 "GPU Temperature"（排除 Hot Spot / Memory Junction / Thermal Limit）
                    if (!label.Equals("GPU Temperature", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (found == index)
                        return (val, label);
                    found++;
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

    /// <summary>
    /// 读取 GPU 使用率。返回 (独显CoreLoad%, 集显Utilization%)。
    /// 独显优先 GPU Core Load，集显用 GPU Utilization。
    /// </summary>
    public (double DgpuLoad, double IgpuLoad, string DgpuLabel, string IgpuLabel) ReadGpuUsageAll()
    {
        lock (_lock)
        {
            if (!_available || _accessor == null) return (-1, -1, "", "");

            try
            {
                double dgpu = -1, igpu = -1;
                string dgpuLabel = "", igpuLabel = "";

                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypeUsage) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (val < 0 || val > 100) continue;

                    // 独显：GPU Core Load（精确匹配，不用 D3D Usage 回退避免匹配到集显的 D3D）
                    if (dgpu < 0 && label.Contains("GPU Core Load", StringComparison.OrdinalIgnoreCase))
                    {
                        dgpu = val;
                        dgpuLabel = label;
                    }
                    // 集显：GPU Utilization
                    else if (igpu < 0 && label.Contains("GPU Utilization", StringComparison.OrdinalIgnoreCase))
                    {
                        igpu = val;
                        igpuLabel = label;
                    }

                    if (dgpu >= 0 && igpu >= 0) break;
                }

                return (dgpu, igpu, dgpuLabel, igpuLabel);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HWiNFO] ReadGpuUsageAll exception: {ex.Message}");
                _available = false;
            }

            return (-1, -1, "", "");
        }
    }

    /// <summary>
    /// 读取 GPU 显存使用率 (%)。匹配 GPU Memory Usage。
    /// </summary>
    public (double Usage, string Label) ReadGpuMemoryUsage()
    {
        lock (_lock)
        {
            if (!_available || _accessor == null) return (-1, "");

            try
            {
                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypeUsage) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (val < 0 || val > 100) continue;

                    if (label.Contains("GPU Memory Usage", StringComparison.OrdinalIgnoreCase))
                        return (val, label);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HWiNFO] ReadGpuMemoryUsage exception: {ex.Message}");
                _available = false;
            }

            return (-1, "");
        }
    }

    /// <summary>
    /// 读取 GPU 显存 (MB)。返回 (已分配MB, 可用MB, 总量MB)。
    /// 匹配标签：GPU Memory Allocated + GPU Memory Available。
    /// 总量 = 已分配 + 可用。如果没有这两个标签返回 (-1, -1, -1)。
    /// </summary>
    public (double UsedMB, double AvailableMB, double TotalMB) ReadGpuMemoryMB()
    {
        lock (_lock)
        {
            if (!_available || _accessor == null) return (-1, -1, -1);

            try
            {
                double allocated = -1, available = -1;
                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypeData) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (val < 0) continue;

                    if (label.Contains("GPU Memory Allocated", StringComparison.OrdinalIgnoreCase))
                        allocated = val;
                    else if (label.Contains("GPU Memory Available", StringComparison.OrdinalIgnoreCase))
                        available = val;
                }

                if (allocated >= 0 && available >= 0)
                {
                    double total = allocated + available;
                    return (allocated, available, total);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HWiNFO] ReadGpuMemoryMB exception: {ex.Message}");
                _available = false;
            }

            return (-1, -1, -1);
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

    /// <summary>
    /// 读取 CPU 功率 (W)。匹配优先标签：CPU Package Power / Package Power / CPU PPT / CPU Power。
    /// </summary>
    public (double Power, string Label) ReadCpuPower()
    {
        lock (_lock)
        {
            if (!_available || _accessor == null) return (-1, "");

            try
            {
                string[] PreferredLabels =
                    ["CPU Package Power", "Package Power", "CPU PPT", "CPU Power", "Core Power (SVI2 TFN)"];

                // Pass 0: preferred labels
                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypePower) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (string.IsNullOrEmpty(label) || val < 0 || val > 500) continue;

                    foreach (var pref in PreferredLabels)
                        if (label.Contains(pref, StringComparison.OrdinalIgnoreCase))
                            return (val, label);
                }

                // Pass 1: any label containing both "CPU" and "Power" (or "PPT")
                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypePower) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (string.IsNullOrEmpty(label) || val < 0 || val > 500) continue;

                    if ((label.Contains("CPU", StringComparison.OrdinalIgnoreCase) &&
                         label.Contains("Power", StringComparison.OrdinalIgnoreCase)) ||
                        label.Contains("PPT", StringComparison.OrdinalIgnoreCase))
                        return (val, label);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HWiNFO] ReadCpuPower exception: {ex.Message}");
                _available = false;
            }
        }

        return (-1, "");
    }

    /// <summary>
    /// 读取 GPU 功率 (W)。返回集显+独显总和。
    /// 匹配标签：GPU ASIC Power（集显）、GPU Power（独显）。
    /// </summary>
    public (double TotalPower, string Detail) ReadGpuPower()
    {
        lock (_lock)
        {
            if (!_available || _accessor == null) return (-1, "");

            try
            {
                double asicPower = 0, gpuPower = 0;
                string asicLabel = "", gpuLabel = "";

                for (int i = 0; i < _entryCount; i++)
                {
                    int baseOff = _entryOffset + _entrySize * i;
                    if (_accessor.ReadInt32(baseOff + EntryType) != HwinfoTypePower) continue;

                    string label = ReadLabel(baseOff);
                    double val = ReadValue(baseOff);
                    if (string.IsNullOrEmpty(label) || val <= 0 || val > 500) continue;

                    // 集显：GPU ASIC Power（APU 集成 GPU 总功率）
                    if (label.Contains("GPU ASIC Power", StringComparison.OrdinalIgnoreCase) ||
                        label.Contains("APU GPU Power", StringComparison.OrdinalIgnoreCase))
                    {
                        asicPower = val;
                        asicLabel = label;
                    }
                    // 独显：GPU Power（独立 GPU 总功率，不包含子项如 NVVDD/FBVDD）
                    else if (label.Equals("GPU Power", StringComparison.OrdinalIgnoreCase) ||
                             label.Equals("GPU Chip Power", StringComparison.OrdinalIgnoreCase))
                    {
                        gpuPower = val;
                        gpuLabel = label;
                    }
                }

                double total = asicPower + gpuPower;
                if (total > 0)
                {
                    var parts = new List<string>();
                    if (asicPower > 0) parts.Add($"iGPU {asicPower:F1}");
                    if (gpuPower > 0) parts.Add($"dGPU {gpuPower:F1}");
                    return (total, string.Join(" + ", parts));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HWiNFO] ReadGpuPower exception: {ex.Message}");
                _available = false;
            }
        }

        return (-1, "");
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
