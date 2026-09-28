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
    private const int HdrVersion1 = 1;     // offset 4
    private const int HdrVersion2 = 2;     // offset 8
    private const int HdrUnitsOffset = 5;  // offset 20：units(分组) 数组起点
    private const int HdrUnitSize = 6;     // offset 24
    private const int HdrUnitCount = 7;    // offset 28
    private const int UnitNameField = 8;   // unit 记录内名称字段偏移
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
    // ---- units 解析面（T2-1：GPU 归属的稳定分组来源） ----
    private byte[]? _unitNameBuf;
    private bool _unitsAvailable;
    private string _unitsNote = "";
    private int _version1;
    private int _version2;
    private string _lastDiag = "";
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
                    // 诊断（T2-1 必做项）：原为仅 Debug.WriteLine —— trim 宿主下
                    // 「从未 Connected」与「签名不符/无权限/布局失败」在日志里不可区分。
                    LogState($"header signature mismatch: 0x{sig:X8} (expected 0x{HwinfoSignature:X8})");
                    Cleanup();
                    return;
                }

                _entryOffset = _accessor.ReadInt32(HdrEntryOffset * 4);
                _entrySize = _accessor.ReadInt32(HdrEntrySize * 4);
                _entryCount = _accessor.ReadInt32(HdrEntryCount * 4);

                // 布局 fail-fast（此前 §7 声称的「格式变更自动禁用」只实现了范围检查那一半）：
                // 必须把 entrySize 与硬编码字段偏移交叉验证，否则记录被撑大/挪字段时
                // 会通过校验并按老偏移静默读出垃圾。
                // 断言用**不等式**，不把本机实测的 460/392 当必需值。
                if (_entrySize < 160 || _entrySize > 1024 ||
                    _entryOffset <= 0 || _entryOffset > 65536 ||
                    _entryCount <= 0 || _entryCount > 4096 ||
                    _entrySize < HwinfoSharedMemoryLayout.EntryValue + sizeof(double))
                {
                    LogState($"layout rejected: offset={_entryOffset} size={_entrySize} count={_entryCount} " +
                             $"(需 size>={HwinfoSharedMemoryLayout.EntryValue + sizeof(double)} 以容下 value 字段)");
                    Cleanup();
                    return;
                }

                _labelBuf = new byte[HwinfoStrLen];
                _available = true;
                _firstOpenTime = DateTime.UtcNow;

                // units 解析面尽力初始化（越界/字段不足 ⇒ 只禁用该面，entries 面照常工作）
                TryLoadUnitsFace();

                // header version 打点（本机实测 2/2；HWiNFO 7.33 changelog 声明布局有变而
                // 「客户端应当无需改动」—— 此类「应当」不作为不校验的理由，故留版本指纹）
                _version1 = _accessor.ReadInt32(HdrVersion1 * 4);
                _version2 = _accessor.ReadInt32(HdrVersion2 * 4);
                SensorLogger.ForceLog(
                    $"[HWiNFO] Connected: {_entryCount} sensors, entrySize={_entrySize} " +
                    $"version={_version1}/{_version2} unitsFace={_unitsAvailable} {_unitsNote}");
            }
            catch (Exception ex)
            {
                // OpenExisting 在非高完整性宿主下可能 UnauthorizedAccess；原实现只 Debug.WriteLine
                LogState($"init failed: {ex.GetType().Name}: {Trim(ex.Message)}");
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
        _unitNameBuf = null;
        _unitsAvailable = false;
        _unitsNote = "disconnected";
        _available = false;
    }

    // ========================================================================
    // units（硬件分组）解析面 —— T2-1 新增面，不影响既有 entries 侧方法
    // ========================================================================

    /// <summary>
    /// 加载 units 数组。任何越界/字段不足/头信息异常 ⇒ 仅禁用本解析面
    /// （<see cref="_unitsAvailable"/>=false + 原因），entries 面照常可用，
    /// 消费端据此退回保守归属（见 GpuHwinfoAssociation.AssociateWithoutUnits）。
    /// 调用方已持 _lock 且已完成 entries 布局校验。
    /// </summary>
    private void TryLoadUnitsFace()
    {
        _unitsAvailable = false;
        _unitsNote = "";
        try
        {
            var acc = _accessor;
            if (acc == null) { _unitsNote = "no accessor"; return; }

            int unitsOffset = acc.ReadInt32(HdrUnitsOffset * 4);
            int unitSize = acc.ReadInt32(HdrUnitSize * 4);
            int unitCount = acc.ReadInt32(HdrUnitCount * 4);

            if (unitsOffset <= 0 || unitSize <= 0 || unitCount <= 0 || unitCount > 4096)
            {
                _unitsNote = $"header absent/invalid (offset={unitsOffset} size={unitSize} count={unitCount})";
                return;
            }
            // 不等式校验：unit 记录必须容得下 8B 头部 + 128B 名称字段
            if (unitSize < UnitNameField + HwinfoStrLen)
            {
                _unitsNote = $"unitSize={unitSize} < required {UnitNameField + HwinfoStrLen}";
                return;
            }
            // units 数组不得越过 entries 起点
            long unitsEnd = (long)unitsOffset + (long)unitSize * unitCount;
            if (unitsEnd > _entryOffset)
            {
                _unitsNote = $"units array overruns entries start ({unitsEnd} > {_entryOffset})";
                return;
            }

            _unitNameBuf = new byte[HwinfoStrLen];
            _unitsAvailable = true;
            _unitsNote = $"units={unitCount}";
        }
        catch (Exception ex)
        {
            _unitsAvailable = false;
            _unitsNote = $"units probe threw {ex.GetType().Name}";
        }
    }

    /// <summary>units 解析面当前是否可用（诊断/测试缝隙）。</summary>
    internal bool UnitsFaceAvailable
    {
        get { lock (_lock) { return _available && _unitsAvailable; } }
    }

    /// <summary>
    /// 读取当前布局快照（units + entries 全量，纯读）。不可用/校验失败返回 null。
    /// 与既有逐标签读取方法互不影响：既有方法签名与匹配语义全部冻结。
    /// </summary>
    internal HwinfoLayoutSnapshot? TryGetLayoutSnapshot()
    {
        lock (_lock)
        {
            if (!IsAvailable) return null;
            var acc = _accessor;
            if (acc == null) return null;

            try
            {
                var units = new List<HwinfoUnitRecord>();
                bool unitsOk = _unitsAvailable && _unitNameBuf != null;
                string unitsReason = "";
                if (unitsOk)
                {
                    int unitsOffset = acc.ReadInt32(HdrUnitsOffset * 4);
                    int unitSize = acc.ReadInt32(HdrUnitSize * 4);
                    int unitCount = acc.ReadInt32(HdrUnitCount * 4);
                    var nameBuf = _unitNameBuf!;
                    for (int u = 0; u < unitCount; u++)
                    {
                        int b = unitsOffset + unitSize * u;
                        acc.ReadArray(b + UnitNameField, nameBuf, 0, HwinfoStrLen);
                        units.Add(new HwinfoUnitRecord
                        {
                            Index = u,
                            Id = acc.ReadInt32(b),
                            Instance = acc.ReadInt32(b + 4),
                            Name = DecodeLabel(nameBuf),
                        });
                    }
                }
                else
                {
                    unitsReason = _unitsNote;
                }

                var entries = new List<HwinfoEntryDescriptor>(_entryCount);
                for (int i = 0; i < _entryCount; i++)
                {
                    int b = _entryOffset + _entrySize * i;
                    acc.ReadArray(b + EntryLabel, _labelBuf!, 0, HwinfoStrLen);
                    entries.Add(new HwinfoEntryDescriptor
                    {
                        Index = i,
                        Type = acc.ReadInt32(b + EntryType),
                        SensorIndex = acc.ReadInt32(b + HwinfoSharedMemoryLayout.EntrySensorIndex),
                        Label = DecodeLabel(_labelBuf!),
                        Value = acc.ReadDouble(b + EntryValue),
                    });
                }

                return new HwinfoLayoutSnapshot
                {
                    Version1 = _version1,
                    Version2 = _version2,
                    EntryOffset = _entryOffset,
                    EntrySize = _entrySize,
                    EntryCount = _entryCount,
                    Entries = entries,
                    UnitsAvailable = unitsOk,
                    UnitsUnavailableReason = unitsOk ? "" : unitsReason,
                    Units = unitsOk ? units : [],
                };
            }
            catch (Exception ex)
            {
                LogState($"layout snapshot read failed: {ex.GetType().Name}: {Trim(ex.Message)}");
                _available = false;
                return null;
            }
        }
    }

    private static string Trim(string s) => s.Length <= 160 ? s : s[..160] + "…";

    /// <summary>诊断打点：仅在状态跃变时 ForceLog，避免 1s 刷新链刷爆 10MB 轮转日志。</summary>
    private void LogState(string message)
    {
        if (string.Equals(_lastDiag, message, StringComparison.Ordinal)) return;
        _lastDiag = message;
        Debug.WriteLine($"[HWiNFO] {message}");
        try { SensorLogger.ForceLog($"[HWiNFO] {message}"); }
        catch { /* 日志失败不得影响采集 */ }
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

    /// <summary>units 面/快照用的解码（委托布局层统一实现；与既有 ReadLabel 的 ASCII 语义在纯 ASCII 标签上等价）</summary>
    private static string DecodeLabel(byte[] buf) =>
        HwinfoSharedMemoryLayout.ReadAsciiZ(buf);

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
