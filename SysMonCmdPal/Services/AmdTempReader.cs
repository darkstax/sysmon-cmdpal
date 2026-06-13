// Copyright (c) 2026 SysMonCmdPal
// CPU 温度采集 — AMD ADL PMLOG（用户态，无需管理员权限）。
// 完全复刻 btop4win/src/amd_temp.cpp 的 ADL 方案。
// 回退级: AMD ADL → HWiNFO 共享内存 → -1(不可用)
// 同时提供多适配器 GPU 数据读取（供 AdlGpuReader 使用）。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SysMonCmdPal;

/// <summary>
/// 通过 AMD ADL 读取 CPU 温度，不需要管理员权限。
/// btop4win 同款方案：atiadlxx.dll → ADL2_New_QueryPMLogData_Get → sensor 504 (CPU)。
/// 使用手动内存分配避免 P/Invoke struct marshal。
/// </summary>
internal sealed class AmdTempReader
{
    public static AmdTempReader Instance { get; } = new();

    // ---- ADL constants (from adl_sdk.h / adl_defines.h — official AMD ADL SDK v18) ----
    private const int ADL_OK = 0;
    private const int ADL_PMLOG_MAX_SENSORS = 256;
    // Official sensor IDs from ADL_PMLOG_SENSORS enum
    private const int PMLOG_TEMP_EDGE = 8;      // GPU edge die temperature (°C)
    private const int PMLOG_TEMP_MEM = 9;        // Memory temperature (°C)
    private const int PMLOG_TEMP_VRVDDC = 10;    // VR VDDC temperature (°C)
    private const int PMLOG_TEMP_HOTSPOT = 27;   // GPU hotspot / junction (°C)
    private const int PMLOG_TEMP_GFX = 28;       // GFX temperature (°C)
    private const int PMLOG_TEMP_SOC = 29;       // SoC temperature (°C)
    private const int PMLOG_TEMP_CPU = 32;       // CPU temperature (°C)
    private const int PMLOG_LOAD_GFX = 50;       // GFX utilization (%)
    // Legacy sensor IDs (500–504): used by btop4win / some forks. Keep for fallback scan.
    private const int PMLEGACY_TEMP_EDGE = 500;
    private const int PMLEGACY_TEMP_VRVDDC = 502;
    private const int PMLEGACY_TEMP_SOC = 503;
    private const int PMLEGACY_TEMP_CPU = 504;

    // Official sensor IDs to probe (in priority order for CPU temp)
    private static readonly int[] CpuTempSensorIds = [PMLOG_TEMP_CPU, PMLOG_TEMP_SOC];
    // All interesting sensor IDs for diagnostic logging
    private static readonly int[] InterestingSensorIds = [
        PMLOG_TEMP_EDGE, PMLOG_TEMP_MEM, PMLOG_TEMP_VRVDDC, PMLOG_TEMP_HOTSPOT,
        PMLOG_TEMP_GFX, PMLOG_TEMP_SOC, PMLOG_TEMP_CPU, 33 /*CPU_POWER*/
    ];

    // PMLOG struct layout (official SDK):
    //   int size                          — 4 bytes (offset 0)
    //   ADLSingleSensorData sensors[256]  — 256 × 8 bytes (offset 4)
    //     Each entry: int supported + int value
    // Total: 4 + 256 * 8 = 2052 bytes
    // We allocate 2056 to be safe (some drivers may write a slightly larger struct).
    private const int PMLOG_STRUCT_SIZE = 2056;
    private const int PMLOG_SENSORS_OFFSET = 4;
    private const int PMLOG_ENTRY_SIZE = 8;

    // ---- HWiNFO shared memory fallback ----
    private const string HWINFO_MAP = @"Global\HWiNFO_SENS_SM2";
    private const uint HWINFO_SIGNATURE = 0x53695748;
    private const int HWINFO_TYPE_TEMP = 1;
    private const int HWINFO_STR_LEN = 128;

    // ---- ADL delegates (__stdcall) ----
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr AdlMallocCb(int size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AdlCreateFn(IntPtr callback, int enumFlag);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AdlDestroyFn();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AdlAdapterCountFn(ref int count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Adl2PmlogFn(int context, int adapterIndex, IntPtr data);

    // ---- ADL state ----
    private readonly object _initLock = new();  // 保护 InitAdl 的并发访问
    private IntPtr _adlLib;
    private bool _adlAvailable;
    private bool _adlInitAttempted;     // InitAdl 已尝试过（成功或确认不支持）
    private int _adapterIndex = -1;
    private int _adapterCount;           // ADL 枚举到的总适配器数
    private AdlDestroyFn? _adlDestroy;
    private Adl2PmlogFn? _adlPmlog;
    private AdlMallocCb? _adlMallocCb; // must stay alive for native thunk

    // ---- HWiNFO state ----
    private bool _hwAvailable;
    private IntPtr _hwMap;
    private IntPtr _hwView;
    private int _hwEntrySize, _hwEntryCount, _hwEntryOffset;
    private DateTime _lastHwAttempt = DateTime.MinValue;
    private static readonly TimeSpan HwRetryCooldown = TimeSpan.FromSeconds(30);

    private AmdTempReader()
    {
        // 不在构造函数中启动后台线程。
        // InitAdl 由 ReadViaAdl() 惰性调用，通过 _initLock 保证线程安全。
        // 之前的后台线程方案会和 ReadViaAdl 并发调 InitAdl，导致 ADL 库状态损坏 → 0xc0000005 崩溃。
    }

    // ================================
    // Init: AMD ADL
    // ================================

    private void InitAdl()
    {
        lock (_initLock)
        {
            // 已尝试过（无论成功或确认不支持），不重复初始化
            if (_adlInitAttempted) return;

            try
            {
                AdlLog("InitAdl: starting");

                // Step 1: Load atiadlxx.dll — 先尝试不带路径（和 btop4win 一致），再带完整路径
                bool loaded = NativeLibrary.TryLoad("atiadlxx.dll", out _adlLib);
                AdlLog($"InitAdl: TryLoad('atiadlxx.dll') = {loaded}, lib={_adlLib:X}");
                if (!loaded)
                {
                    loaded = NativeLibrary.TryLoad(@"C:\Windows\System32\atiadlxx.dll", out _adlLib);
                    AdlLog($"InitAdl: TryLoad('C:\\Windows\\System32\\atiadlxx.dll') = {loaded}, lib={_adlLib:X}");
                }
                if (!loaded)
                {
                    loaded = NativeLibrary.TryLoad("atiadlxy.dll", out _adlLib);
                    AdlLog($"InitAdl: TryLoad('atiadlxy.dll') = {loaded}, lib={_adlLib:X}");
                }
                if (!loaded)
                {
                    AdlLog("InitAdl: all DLL load attempts failed");
                    _adlInitAttempted = true;
                    return;
                }

                // Step 2: ADL_Main_Control_Create(callback, 1)
                if (!NativeLibrary.TryGetExport(_adlLib, "ADL_Main_Control_Create", out var createPtr))
                { AdlLog("InitAdl: ADL_Main_Control_Create export not found"); CleanupAdl(); _adlInitAttempted = true; return; }
                var create = Marshal.GetDelegateForFunctionPointer<AdlCreateFn>(createPtr);

                // ADL malloc — use process default heap
                _adlMallocCb = AdlMalloc;
                var cbPtr = Marshal.GetFunctionPointerForDelegate(_adlMallocCb);

                int createResult = create(cbPtr, 1);
                AdlLog($"InitAdl: ADL_Main_Control_Create result = {createResult}");
                if (createResult < 0) { CleanupAdl(); _adlInitAttempted = true; return; }

                // Step 3: Resolve remaining entry points
                _adlDestroy = GetDelegate<AdlDestroyFn>(_adlLib, "ADL_Main_Control_Destroy");
                if (_adlDestroy == null) { AdlLog("InitAdl: ADL_Main_Control_Destroy not found"); CleanupAdl(); _adlInitAttempted = true; return; }

                var adapters = GetDelegate<AdlAdapterCountFn>(_adlLib, "ADL_Adapter_NumberOfAdapters_Get");
                _adlPmlog = GetDelegate<Adl2PmlogFn>(_adlLib, "ADL2_New_QueryPMLogData_Get");
                if (adapters == null || _adlPmlog == null) { AdlLog($"InitAdl: adapters={adapters != null}, pmlog={_adlPmlog != null}"); CleanupAdl(); _adlInitAttempted = true; return; }

                // Step 4: Enumerate adapters, find one with PMLOG CPU temp sensor
                int count = 0;
                int adapterResult = adapters(ref count);
                _adapterCount = count;
                AdlLog($"InitAdl: ADL_Adapter_NumberOfAdapters_Get result={adapterResult}, count={count}");
                if (adapterResult < 0 || count <= 0) { CleanupAdl(); _adlInitAttempted = true; return; }

                IntPtr probeBuf = IntPtr.Zero;
                try
                {
                    probeBuf = Marshal.AllocHGlobal(PMLOG_STRUCT_SIZE);
                    InitPmlogBuffer(probeBuf);

                    for (int i = 0; i < count; i++)
                    {
                        int pmlogResult = _adlPmlog(0, i, probeBuf);

                        if (pmlogResult >= 0)
                        {
                            // 检查 CPU 温度传感器（官方 ID 32）
                            int cpuSup = ReadSensorSupported(probeBuf, PMLOG_TEMP_CPU);
                            int cpuVal = ReadSensorValue(probeBuf, PMLOG_TEMP_CPU);
                            int socVal = ReadSensorValue(probeBuf, PMLOG_TEMP_SOC);
                            int gfxVal = ReadSensorValue(probeBuf, PMLOG_TEMP_GFX);
                            AdlLog($"InitAdl: adapter {i}: pmlog=ok, CPU[{PMLOG_TEMP_CPU}]={cpuVal}(sup={cpuSup}), SOC[{PMLOG_TEMP_SOC}]={socVal}, GFX[{PMLOG_TEMP_GFX}]={gfxVal}");

                            if (cpuSup != 0 && cpuVal > 0 && cpuVal < 150)
                            {
                                _adapterIndex = i;
                                _adlAvailable = true;
                                _adlInitAttempted = true;
                                AdlLog($"InitAdl: SUCCESS — adapter {i}, CPU temp={cpuVal}°C");
                                return;
                            }
                        }
                        else
                        {
                            AdlLog($"InitAdl: adapter {i}: pmlog={pmlogResult} (not supported)");
                        }
                    }
                }
                finally
                {
                    if (probeBuf != IntPtr.Zero) Marshal.FreeHGlobal(probeBuf);
                }

                // 没有 adapter 支持 PMLOG CPU 温度
                AdlLog($"InitAdl: FAILED — {count} adapters found, none support PMLOG CPU temp (sensor 32)");
                Debug.WriteLine($"[SysMon] ADL: {count} adapters found, none support PMLOG CPU temp");
                CleanupAdl();
                _adlInitAttempted = true;
            }
            catch (Exception ex)
            {
                AdlLog($"InitAdl: EXCEPTION — {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine($"[SysMon] ADL init: {ex.Message}");
                CleanupAdl();
                _adlInitAttempted = true;
            }
        }
    }

    // ADL malloc callback — uses process heap (same as btop4win)
    private static IntPtr AdlMalloc(int size) => Marshal.AllocHGlobal(size);

    // ---- PMLOG struct helpers (official SDK layout) ----

    private static void InitPmlogBuffer(IntPtr buf)
    {
        // Zero the entire struct
        for (int i = 0; i < PMLOG_STRUCT_SIZE; i += 4)
            Marshal.WriteInt32(buf, i, 0);
        // Set size field (offset 0)
        Marshal.WriteInt32(buf, 0, PMLOG_STRUCT_SIZE);
    }

    /// <summary>读取 sensors[i].supported</summary>
    private static int ReadSensorSupported(IntPtr buf, int sensorId)
    {
        if (sensorId < 0 || sensorId >= ADL_PMLOG_MAX_SENSORS) return 0;
        return Marshal.ReadInt32(buf, PMLOG_SENSORS_OFFSET + sensorId * PMLOG_ENTRY_SIZE);
    }

    /// <summary>读取 sensors[i].value</summary>
    private static int ReadSensorValue(IntPtr buf, int sensorId)
    {
        if (sensorId < 0 || sensorId >= ADL_PMLOG_MAX_SENSORS) return 0;
        return Marshal.ReadInt32(buf, PMLOG_SENSORS_OFFSET + sensorId * PMLOG_ENTRY_SIZE + 4);
    }

    /// <summary>扫描缓冲区，找到所有 supported != 0 的传感器，返回 (id, supported, value) 列表</summary>
    private static List<(int id, int supported, int value)> ScanAllSensors(IntPtr buf)
    {
        var result = new List<(int, int, int)>();
        for (int i = 0; i < ADL_PMLOG_MAX_SENSORS; i++)
        {
            int sup = ReadSensorSupported(buf, i);
            if (sup != 0)
            {
                int val = ReadSensorValue(buf, i);
                result.Add((i, sup, val));
            }
        }
        return result;
    }

    private static T? GetDelegate<T>(IntPtr lib, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(lib, name, out var ptr)) return null;
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    // ================================
    // Init: HWiNFO shared memory
    // ================================

    private void InitHwInfo()
    {
        try
        {
            _hwMap = OpenFileMapping(FILE_MAP_READ, false, HWINFO_MAP);
            if (_hwMap == IntPtr.Zero) return;

            _hwView = MapViewOfFile(_hwMap, FILE_MAP_READ, 0, 0, IntPtr.Zero);
            if (_hwView == IntPtr.Zero) { HwCleanup(); return; }

            int[] header = new int[12];
            Marshal.Copy(_hwView, header, 0, 12);
            if ((uint)header[0] != HWINFO_SIGNATURE) { HwCleanup(); return; }

            _hwEntryOffset = header[8];
            _hwEntrySize = header[9];
            _hwEntryCount = header[10];

            // HWiNFOReading struct size = 4+4+4+128+128+16+8+8+8+8 = 316 (with pack=1)
            // Minimum: need at least the header fields + 128 bytes for label
            if (_hwEntrySize < 160) { HwCleanup(); return; }

            _hwAvailable = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] HWiNFO init: {ex.Message}");
            HwCleanup();
        }
    }

    // ================================
    // Public API
    // ================================

    /// <summary>ADL 是否可用（atiadlxx.dll 已加载，PMLOG 支持 CPU 温度）</summary>
    public bool IsAdlAvailable => _adlAvailable;

    // ================================
    // Multi-GPU adapter data (供 AdlGpuReader 使用)
    // ================================

    /// <summary>单个 ADL 适配器的 GPU 传感器数据</summary>
    internal readonly record struct AdlGpuAdapterData(
        int AdapterIndex,
        double Temperature,    // °C, edge/hotspot/gfx, -1 if unavailable
        double LoadPercent,    // GFX utilization %, -1 if unavailable
        bool HasGpuData        // at least one GPU sensor reported valid data
    );

    /// <summary>
    /// 枚举所有 ADL 适配器的 GPU 数据（温度 + GFX 使用率）。
    /// 仅返回有 GPU 传感器数据的适配器（edge/hotspot/gfx 温度或 GFX 负载）。
    /// </summary>
    internal List<AdlGpuAdapterData> ReadAllGpuAdapters()
    {
        var result = new List<AdlGpuAdapterData>();

        if (!_adlInitAttempted) InitAdl();
        if (_adlPmlog == null || _adapterCount <= 0) return result;

        IntPtr buf = IntPtr.Zero;
        try
        {
            buf = Marshal.AllocHGlobal(PMLOG_STRUCT_SIZE);

            for (int i = 0; i < _adapterCount; i++)
            {
                InitPmlogBuffer(buf);
                int pmResult = _adlPmlog(0, i, buf);
                if (pmResult < 0)
                {
                    AdlLog($"ReadAllGpuAdapters: adapter {i}: pmlog error {pmResult}");
                    continue;
                }

                // GPU edge temperature (sensor 8)
                int edgeSup = ReadSensorSupported(buf, PMLOG_TEMP_EDGE);
                int edgeVal = ReadSensorValue(buf, PMLOG_TEMP_EDGE);
                double temp = -1;
                if (edgeSup != 0 && edgeVal > 0 && edgeVal < 150)
                    temp = edgeVal;

                // Fallback: hotspot (sensor 27) → GFX (sensor 28)
                if (temp < 0)
                {
                    int hsSup = ReadSensorSupported(buf, PMLOG_TEMP_HOTSPOT);
                    int hsVal = ReadSensorValue(buf, PMLOG_TEMP_HOTSPOT);
                    if (hsSup != 0 && hsVal > 0 && hsVal < 150)
                        temp = hsVal;
                }
                if (temp < 0)
                {
                    int gfxSup = ReadSensorSupported(buf, PMLOG_TEMP_GFX);
                    int gfxVal = ReadSensorValue(buf, PMLOG_TEMP_GFX);
                    if (gfxSup != 0 && gfxVal > 0 && gfxVal < 150)
                        temp = gfxVal;
                }

                // GFX utilization (sensor 50)
                int loadSup = ReadSensorSupported(buf, PMLOG_LOAD_GFX);
                int loadVal = ReadSensorValue(buf, PMLOG_LOAD_GFX);
                double load = -1;
                if (loadSup != 0 && loadVal >= 0 && loadVal <= 100)
                    load = loadVal;

                bool hasGpuData = temp > 0 || load >= 0;
                AdlLog($"ReadAllGpuAdapters: adapter {i}: temp={temp:F0}°C, load={load:F0}%, hasGpu={hasGpuData}");

                if (hasGpuData)
                    result.Add(new AdlGpuAdapterData(i, temp, load, true));
            }
        }
        catch (Exception ex)
        {
            AdlLog($"ReadAllGpuAdapters exception: {ex.Message}");
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }

        return result;
    }

    /// <summary>仅通过 ADL PMLOG 读取 CPU 温度（不走 HWiNFO 回退）</summary>
    public int ReadCpuTempViaAdlOnly()
    {
        return ReadViaAdl();
    }

    /// <summary>通过 ADL PMLOG 读取 GPU 边缘温度（sensor 8 = TEMPERATURE_EDGE）</summary>
    public int ReadGpuTempViaAdl()
    {
        if (!_adlInitAttempted) InitAdl();
        if (!_adlAvailable || _adlPmlog == null || _adapterIndex < 0) return -1;

        IntPtr buf = IntPtr.Zero;
        try
        {
            buf = Marshal.AllocHGlobal(PMLOG_STRUCT_SIZE);
            InitPmlogBuffer(buf);

            int pmResult = _adlPmlog(0, _adapterIndex, buf);
            if (pmResult < 0) return -1;

            // Check GPU edge temperature (sensor 8)
            int edgeSup = ReadSensorSupported(buf, PMLOG_TEMP_EDGE);
            int edgeVal = ReadSensorValue(buf, PMLOG_TEMP_EDGE);
            if (edgeSup != 0 && edgeVal > 0 && edgeVal < 150)
                return edgeVal;

            // Fallback to hotspot (sensor 27) then GFX (sensor 28)
            int hotspotVal = ReadSensorValue(buf, PMLOG_TEMP_HOTSPOT);
            if (edgeSup != 0 && hotspotVal > 0 && hotspotVal < 150)
                return hotspotVal;

            int gfxVal = ReadSensorValue(buf, PMLOG_TEMP_GFX);
            if (ReadSensorSupported(buf, PMLOG_TEMP_GFX) != 0 && gfxVal > 0 && gfxVal < 150)
                return gfxVal;
        }
        catch (Exception ex)
        {
            AdlLog($"ReadGpuTempViaAdl 异常: {ex.Message}");
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
        return -1;
    }

    /// <summary>读取 CPU 温度（完整回退: ADL → HWiNFO）</summary>
    public int ReadCpuTemp()
    {
        int t = ReadViaAdl();
        if (t > 0) return t;
        t = ReadViaHwInfo(TempLabelFilter.Cpu);
        return t > 0 ? t : -1;
    }

    /// <summary>仅通过 HWiNFO 共享内存读取 CPU 温度（跳过 ADL）</summary>
    public int ReadCpuTempViaHwInfoOnly()
    {
        return ReadViaHwInfo(TempLabelFilter.Cpu);
    }

    /// <summary>仅通过 HWiNFO 共享内存读取 GPU 温度</summary>
    public int ReadGpuTempViaHwInfo()
    {
        return ReadViaHwInfo(TempLabelFilter.Gpu);
    }

    private enum TempLabelFilter { Cpu, Gpu }

    private int ReadViaAdl()
    {
        // 惰性初始化 ADL（首次调用时尝试，后续不再重试已失败的初始化）
        if (!_adlInitAttempted)
        {
            AdlLog("ReadViaAdl: first call, invoking InitAdl()");
            InitAdl(); // 内部有 lock 保护，线程安全
        }

        if (!_adlAvailable || _adlPmlog == null || _adapterIndex < 0) return -1;

        IntPtr buf = IntPtr.Zero;
        try
        {
            buf = Marshal.AllocHGlobal(PMLOG_STRUCT_SIZE);
            InitPmlogBuffer(buf);

            int pmResult = _adlPmlog(0, _adapterIndex, buf);
            if (pmResult < 0)
            {
                AdlLog($"ReadViaAdl: pmlog error {pmResult}");
                return -1;
            }

            // 优先检查 CPU 温度传感器 (ID 32, 29)
            foreach (int id in CpuTempSensorIds)
            {
                int supported = ReadSensorSupported(buf, id);
                int val = ReadSensorValue(buf, id);
                if (supported != 0 && val > 0 && val < 150)
                    return val;
            }
        }
        catch (Exception ex)
        {
            AdlLog($"ADL PMLOG read exception: {ex.Message}");
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
        return -1;
    }

    private static void AdlLog(string msg)
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SysMonCmdPal", "adl_debug.log");
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { /* ignore */ }
    }

    // ================================
    // Read: HWiNFO shared memory (generic)
    // ================================

    private int ReadViaHwInfo(TempLabelFilter filter)
    {
        // 冷却重试 HWiNFO 初始化（用户可能稍后启动 HWiNFO）
        if (!_hwAvailable && DateTime.UtcNow - _lastHwAttempt > HwRetryCooldown)
        {
            _lastHwAttempt = DateTime.UtcNow;
            HwCleanup();
            InitHwInfo();
        }

        if (!_hwAvailable || _hwView == IntPtr.Zero) return -1;

        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < _hwEntryCount; i++)
                {
                    var ptr = IntPtr.Add(_hwView, _hwEntryOffset + _hwEntrySize * i);
                    int type = Marshal.ReadInt32(ptr, 0);
                    if (type != HWINFO_TYPE_TEMP) continue;

                    string label = Marshal.PtrToStringAnsi(ptr + 12, HWINFO_STR_LEN) ?? "";
                    label = label.TrimEnd('\0');

                    int valueOffset = 12 + HWINFO_STR_LEN + HWINFO_STR_LEN + 16;
                    double val = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(ptr, valueOffset));
                    if (val <= 0 || val > 150) continue; // sanity check

                    bool matches = filter switch
                    {
                        TempLabelFilter.Cpu => IsCpuTempLabel(label, pass == 0),
                        TempLabelFilter.Gpu => IsGpuTempLabel(label, pass == 0),
                        _ => false,
                    };

                    if (matches)
                        return (int)(val + 0.5);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] HWiNFO read: {ex.Message}");
        }
        return -1;
    }

    private static bool IsCpuTempLabel(string label, bool firstPass)
    {
        bool isPreferred = label.Contains("CPU Package") || label.Contains("Tctl/Tdie") ||
                           label.Contains("CPU Die") || label.Contains("CPU CCD") ||
                           label.Contains("CPU Tctl");
        bool isCpu = label.Contains("CPU");
        return firstPass ? isPreferred : isCpu;
    }

    private static bool IsGpuTempLabel(string label, bool firstPass)
    {
        bool isPreferred = label.Contains("GPU Core") || label.Contains("GPU Hot Spot") ||
                           label.Contains("GPU Junction") || label.Contains("GPU Temperature");
        bool isGpu = label.Contains("GPU");
        return firstPass ? isPreferred : isGpu;
    }

    // ================================
    // Cleanup
    // ================================

    private void CleanupAdl()
    {
        try { _adlDestroy?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] ADL destroy: {ex.Message}"); }
        if (_adlLib != IntPtr.Zero) { NativeLibrary.Free(_adlLib); _adlLib = IntPtr.Zero; }
        _adlDestroy = null;
        _adlPmlog = null;
        _adlMallocCb = null;
        _adlAvailable = false;
        _adapterIndex = -1;
        _adapterCount = 0;
    }

    private void HwCleanup()
    {
        if (_hwView != IntPtr.Zero) { UnmapViewOfFile(_hwView); _hwView = IntPtr.Zero; }
        if (_hwMap != IntPtr.Zero) { CloseHandle(_hwMap); _hwMap = IntPtr.Zero; }
        _hwAvailable = false;
    }

    public void Shutdown()
    {
        CleanupAdl();
        HwCleanup();
    }

    // ---- kernel32 P/Invoke (HWiNFO shared memory only) ----
    private const uint FILE_MAP_READ = 4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenFileMapping(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, IntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
