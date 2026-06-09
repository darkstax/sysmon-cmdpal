// Copyright (c) 2026 SysMonCmdPal
// CPU 温度采集 — AMD ADL PMLOG（用户态，无需管理员权限）。
// 完全复刻 btop4win/src/amd_temp.cpp 的 ADL 方案。
// 回退级: AMD ADL → HWiNFO 共享内存 → -1(不可用)

using System;
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

    // ---- ADL constants (from adl_sdk.h) ----
    private const int ADL_OK = 0;
    private const int ADL_PMLOG_MAX_SENSORS = 256;
    private const int PMLOG_TEMP_CPU = 504;
    private const int PMLOG_TEMP_SOC = 503;
    private const int PMLOG_TEMP_EDGE = 500;
    private const int PMLOG_TEMP_VRVDDC = 502;

    private static readonly int[] TempSensorIds = [PMLOG_TEMP_CPU, PMLOG_TEMP_SOC, PMLOG_TEMP_EDGE, PMLOG_TEMP_VRVDDC];

    // PMLOG struct layout: int size, int version, int supported[256], int values[256]
    // Total: 4 + 4 + 256*4 + 256*4 = 2056 bytes
    private const int PMLOG_STRUCT_SIZE = 2056;
    private const int PMLOG_OFFSET_SUPPORTED = 8;
    private const int PMLOG_OFFSET_VALUES = 8 + 256 * 4;

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
    private IntPtr _adlLib;
    private bool _adlAvailable;
    private int _adapterIndex = -1;
    private AdlDestroyFn? _adlDestroy;
    private Adl2PmlogFn? _adlPmlog;
    private AdlMallocCb? _adlMallocCb; // must stay alive for native thunk

    // ---- HWiNFO state ----
    private bool _hwAvailable;
    private IntPtr _hwMap;
    private IntPtr _hwView;
    private int _hwEntrySize, _hwEntryCount, _hwEntryOffset;

    private AmdTempReader()
    {
        InitAdl();
        if (!_adlAvailable)
            InitHwInfo();
    }

    // ================================
    // Init: AMD ADL
    // ================================

    private void InitAdl()
    {
        try
        {
            // Step 1: Load atiadlxx.dll from System32 (explicit path, user-mode)
            _adlLib = LoadLibraryW(@"C:\Windows\System32\atiadlxx.dll");
            if (_adlLib == IntPtr.Zero)
            {
                _adlLib = LoadLibraryW(@"C:\Windows\System32\atiadlxy.dll");
                if (_adlLib == IntPtr.Zero) return;
            }

            // Step 2: ADL_Main_Control_Create(callback, 1)
            var createPtr = GetProcAddress(_adlLib, "ADL_Main_Control_Create");
            if (createPtr == IntPtr.Zero) { CleanupAdl(); return; }
            var create = Marshal.GetDelegateForFunctionPointer<AdlCreateFn>(createPtr);

            // ADL malloc — use process default heap
            _adlMallocCb = AdlMalloc;
            var cbPtr = Marshal.GetFunctionPointerForDelegate(_adlMallocCb);

            if (create(cbPtr, 1) < 0) { CleanupAdl(); return; }

            // Step 3: Resolve remaining entry points
            _adlDestroy = GetDelegate<AdlDestroyFn>(_adlLib, "ADL_Main_Control_Destroy");
            if (_adlDestroy == null) { CleanupAdl(); return; }

            var adapters = GetDelegate<AdlAdapterCountFn>(_adlLib, "ADL_Adapter_NumberOfAdapters_Get");
            _adlPmlog = GetDelegate<Adl2PmlogFn>(_adlLib, "ADL2_New_QueryPMLogData_Get");
            if (adapters == null || _adlPmlog == null) { CleanupAdl(); return; }

            // Step 4: Enumerate adapters, find one with PMLOG CPU temp sensor
            int count = 0;
            if (adapters(ref count) < 0 || count <= 0) { CleanupAdl(); return; }

            IntPtr probeBuf = IntPtr.Zero;
            try
            {
                probeBuf = Marshal.AllocHGlobal(PMLOG_STRUCT_SIZE);
                InitPmlogBuffer(probeBuf);

                for (int i = 0; i < count; i++)
                {
                    if (_adlPmlog(0, i, probeBuf) >= 0 &&
                        Marshal.ReadInt32(probeBuf, PMLOG_OFFSET_SUPPORTED + PMLOG_TEMP_CPU * 4) != 0)
                    {
                        _adapterIndex = i;
                        _adlAvailable = true;
                        return;
                    }
                }
            }
            finally
            {
                if (probeBuf != IntPtr.Zero) Marshal.FreeHGlobal(probeBuf);
            }

            CleanupAdl();
        }
        catch
        {
            CleanupAdl();
        }
    }

    // ADL malloc callback — uses process heap (same as btop4win)
    private static IntPtr AdlMalloc(int size) => Marshal.AllocHGlobal(size);

    private static void InitPmlogBuffer(IntPtr buf)
    {
        // Zero the entire struct
        for (int i = 0; i < PMLOG_STRUCT_SIZE; i += 4)
            Marshal.WriteInt32(buf, i, 0);
        // Set size and version
        Marshal.WriteInt32(buf, 0, PMLOG_STRUCT_SIZE);
        Marshal.WriteInt32(buf, 4, 1);
    }

    private static T? GetDelegate<T>(IntPtr lib, string name) where T : Delegate
    {
        var ptr = GetProcAddress(lib, name);
        if (ptr == IntPtr.Zero) return null;
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
        catch { HwCleanup(); }
    }

    // ================================
    // Public API
    // ================================

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
        if (!_adlAvailable || _adlPmlog == null || _adapterIndex < 0) return -1;

        IntPtr buf = IntPtr.Zero;
        try
        {
            buf = Marshal.AllocHGlobal(PMLOG_STRUCT_SIZE);
            InitPmlogBuffer(buf);

            if (_adlPmlog(0, _adapterIndex, buf) < 0) return -1;

            foreach (int id in TempSensorIds)
            {
                if (Marshal.ReadInt32(buf, PMLOG_OFFSET_SUPPORTED + id * 4) != 0)
                {
                    int val = Marshal.ReadInt32(buf, PMLOG_OFFSET_VALUES + id * 4);
                    // Valid range: 0-150°C (= 0-150000 millidegrees)
                    if (val > 0 && val < 150000)
                        return val / 1000;
                }
            }
        }
        catch { }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
        return -1;
    }

    // ================================
    // Read: HWiNFO shared memory (generic)
    // ================================

    private int ReadViaHwInfo(TempLabelFilter filter)
    {
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
        catch { }
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
        try { _adlDestroy?.Invoke(); } catch { }
        if (_adlLib != IntPtr.Zero) { FreeLibrary(_adlLib); _adlLib = IntPtr.Zero; }
        _adlDestroy = null;
        _adlPmlog = null;
        _adlAvailable = false;
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

    // ---- kernel32 P/Invoke ----
    private const uint FILE_MAP_READ = 4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr hModule);

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
