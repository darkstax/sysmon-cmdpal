// Copyright (c) 2026 SysMonCmdPal
// SysMonBroker GPU 读取器 — 以管理员权限运行，通过 NVAPI/ADL/IGCL 读取所有 GPU 数据。
// Broker 以管理员权限运行，不受 MSIX AppContainer 沙箱限制，可直接访问 NVAPI/ADL/IGCL。
// 回退链: NVAPI(NVIDIA) + ADL(AMD) + IGCL(Intel) 并行枚举 → 统一 3D 活跃度筛选

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace SysMonBroker;

/// <summary>GPU 数据记录</summary>
public struct GpuData
{
    public string Name;
    public double Temperature;    // °C, -1 if unavailable
    public double UsagePercent;   // 0-100, -1 if unavailable
    public double MemoryUsedMB;   // 已用显存 MB
    public double MemoryTotalMB;  // 总显存 MB
    public string Vendor;         // "NVIDIA" / "AMD" / "Intel" / "Unknown"

    public GpuData(string name, double temperature, double usagePercent,
                   double memoryUsedMB, double memoryTotalMB, string vendor)
    {
        Name = name;
        Temperature = temperature;
        UsagePercent = usagePercent;
        MemoryUsedMB = memoryUsedMB;
        MemoryTotalMB = memoryTotalMB;
        Vendor = vendor;
    }

    public readonly bool IsValid => !string.IsNullOrEmpty(Name) && Name != "Unknown GPU";

    public static GpuData None => new("", -1, -1, 0, 0, "Unknown");
}

/// <summary>GPU 厂商常量</summary>
public static class GpuVendor
{
    public const string NVIDIA = "NVIDIA";
    public const string AMD = "AMD";
    public const string Intel = "Intel";
    public const string Unknown = "Unknown";
}

/// <summary>
/// GPU 读取器 — 枚举所有厂商的物理 GPU，按 3D 活跃度智能筛选。
/// Broker 以管理员权限运行，NVAPI/ADL/IGCL 均可正常访问。
/// </summary>
public static class GpuReader
{
    // ================================================================
    // Cached readers
    // ================================================================

    private static readonly NvapiReaderInternal _nvapiCache = new();
    private static readonly AdlReaderInternal _adlCache = new();
    private static readonly IgclReaderInternal _igclCache = new();

    // ================================================================
    // Public API
    // ================================================================

    /// <summary>
    /// 枚举所有 GPU（NVIDIA/AMD/Intel 并行枚举），返回完整列表。
    /// 每个厂商独立读取自己的 GPU，互不依赖。
    /// </summary>
    public static List<GpuData> ReadAllGpus()
    {
        var allGpus = new List<GpuData>();

        // ── NVIDIA — NVAPI ──
        try
        {
            var nvGpus = ReadNvidiaGpus();
            foreach (var g in nvGpus)
                Log($"GPU[NVAPI]: {g.Name}, {g.UsagePercent:F0}%, {g.Temperature:F0}C, VRAM {g.MemoryUsedMB:F0}/{g.MemoryTotalMB:F0}MB");
            allGpus.AddRange(nvGpus);
        }
        catch (Exception ex) { Log($"GPU NVAPI 异常: {ex.Message}"); }

        // ── AMD — ADL PMLOG ──
        try
        {
            var amdGpus = ReadAmdGpus();
            foreach (var g in amdGpus)
                Log($"GPU[ADL]: {g.Name}, {g.UsagePercent:F0}%, {g.Temperature:F0}C");
            allGpus.AddRange(amdGpus);
        }
        catch (Exception ex) { Log($"GPU ADL 异常: {ex.Message}"); }

        // ── Intel — IGCL ──
        try
        {
            var intelGpus = ReadIntelGpus();
            foreach (var g in intelGpus)
                Log($"GPU[IGCL]: {g.Name}, {g.UsagePercent:F0}%, {g.Temperature:F0}C");
            allGpus.AddRange(intelGpus);
        }
        catch (Exception ex) { Log($"GPU IGCL 异常: {ex.Message}"); }

        if (allGpus.Count == 0)
        {
            Log("GPU: 所有数据源不可用");
            return [];
        }

        return allGpus;
    }

    /// <summary>
    /// 3D 活跃度筛选。
    /// 规则:
    ///   - iGPU + dGPU → 两个都保留（除非 iGPU 使用率 = 0%）
    ///   - 多个 dGPU → 有使用率的优先，全无使用率则全部保留
    ///   - 单 GPU → 直接返回
    /// </summary>
    public static List<GpuData> FilterActiveGpus(List<GpuData> gpus)
    {
        if (gpus.Count <= 1) return gpus;

        // 区分 iGPU 和 dGPU
        var igpus = gpus.Where(IsIgpu).ToList();
        var dgpus = gpus.Where(g => !IsIgpu(g)).ToList();

        // ── 场景 1: iGPU + dGPU 混合 ──
        if (igpus.Count >= 1 && dgpus.Count >= 1)
        {
            var activeIgpus = igpus.Where(g => g.UsagePercent > 0).ToList();
            if (activeIgpus.Count == 0)
            {
                // iGPU 使用率 = 0%，只保留 dGPU
                Log($"GPU 筛选: iGPU idle (usage=0%), keeping {dgpus.Count} dGPU(s) only");
                return dgpus.Count > 1 ? FilterActiveDgpus(dgpus) : dgpus;
            }

            // iGPU 有负载，保留全部
            var result = new List<GpuData>();
            result.AddRange(activeIgpus);
            result.AddRange(dgpus.Count > 1 ? FilterActiveDgpus(dgpus) : dgpus);
            Log($"GPU 筛选: iGPU active ({activeIgpus.Count}), dGPU active -> keeping all");
            return result;
        }

        // ── 场景 2: 只有 dGPU（多个） ──
        if (dgpus.Count > 1)
            return FilterActiveDgpus(dgpus);

        // ── 场景 3: 只有 iGPU（多个） ──
        if (igpus.Count > 1)
        {
            var active = igpus.Where(g => g.UsagePercent > 0).ToList();
            if (active.Count >= 1 && active.Count < igpus.Count)
            {
                Log($"GPU 筛选: {active.Count} iGPU active, returning active only");
                return active;
            }
        }

        Log($"GPU 筛选: {gpus.Count} GPUs, no filtering needed");
        return gpus;
    }

    // ================================================================
    // iGPU 识别启发式
    // ================================================================

    /// <summary>
    /// 判断 GPU 是否为 iGPU（集成显卡）。
    /// 启发式规则:
    ///   - Intel: 非 Arc 系列视为 iGPU
    ///   - AMD: "Radeon(TM) Graphics" 或 "Radeon Graphics"（无 RX 前缀）视为 iGPU
    ///   - NVIDIA: 始终视为 dGPU
    /// </summary>
    private static bool IsIgpu(GpuData gpu)
    {
        string name = gpu.Name;
        string vendor = gpu.Vendor;

        if (vendor == GpuVendor.Intel)
        {
            if (name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        if (vendor == GpuVendor.AMD)
        {
            if (name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Vega", StringComparison.OrdinalIgnoreCase))
            {
                if (!name.Contains("RX", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("Pro", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("WX", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        return false;
    }

    /// <summary>多个 dGPU 的筛选: 有使用率的优先</summary>
    private static List<GpuData> FilterActiveDgpus(List<GpuData> dgpus)
    {
        var withUsage = dgpus.Where(g => g.UsagePercent > 0).ToList();
        if (withUsage.Count >= 1 && withUsage.Count < dgpus.Count)
        {
            Log($"GPU 筛选: {withUsage.Count}/{dgpus.Count} dGPU active, returning active only");
            return withUsage;
        }
        Log($"GPU 筛选: {dgpus.Count} dGPUs, 3D activity uniform -> returning all");
        return dgpus;
    }

    // ================================================================
    // NVIDIA — NVAPI
    // ================================================================

    private static List<GpuData> ReadNvidiaGpus()
    {
        if (!_nvapiCache.IsAvailable) return [];

        var results = new List<GpuData>();
        try
        {
            var handles = new IntPtr[64];
            int count = handles.Length;
            if (_nvapiCache.EnumPhysicalGpus(handles, ref count) != NvapiReaderInternal.NVAPI_OK || count == 0)
                return [];

            for (int i = 0; i < count; i++)
            {
                string name = _nvapiCache.GetGpuName(handles[i]);
                // NVAPI struct 版本不兼容 — 温度/使用率/显存返回 -1，
                // 由 MSIX 端 LHM 补充或 ThermalZone/HWiNFO 兜底
                results.Add(new GpuData(name, -1, -1, 0, 0, GpuVendor.NVIDIA));
            }
        }
        catch (Exception ex)
        {
            Log($"NVAPI 读取异常: {ex.Message}");
        }

        return results;
    }

    /// <summary>NVAPI 读取器内部实现（自包含，无外部依赖，无 unsafe 代码）</summary>
    private sealed class NvapiReaderInternal
    {
        public const int NVAPI_OK = 0;

        private IntPtr _lib;
        private bool _initAttempted;
        private bool _available;

        // NVAPI 函数通过 nvapi_QueryInterface(magic_id) 获取
        private NvAPI_QueryInterface? _queryInterface;
        private NvAPI_Initialize? _initialize;
        private NvAPI_EnumPhysicalGPUs? _enumPhysicalGPUs;
        private NvAPI_GPU_GetFullName? _getFullName;

        // NVAPI 函数 magic IDs
        private const uint ID_Initialize = 0x0150E828;
        private const uint ID_EnumPhysicalGPUs = 0xE5AC921F;
        private const uint ID_GPU_GetThermalSettings = 0xE3640A56;
        private const uint ID_GPU_GetDynamicPstatesInfoEx = 0x843C0256;
        private const uint ID_GPU_GetMemoryInfo = 0x774AA982;
        private const uint ID_GPU_GetFullName = 0xCEEE8E9F;

        // — IntPtr-based delegates (avoid unsafe fixed buffers) —
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr NvAPI_QueryInterface(uint functionId);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_Initialize();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_EnumPhysicalGPUs([Out] IntPtr[] gpuHandles, ref int gpuCount);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_GPU_GetFullName(IntPtr gpuHandle, [Out] char[] name);

        // IntPtr-based delegates for struct-heavy calls (avoids fixed-buffer structs)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_GPU_GetThermalSettings_IntPtr(
            IntPtr gpuHandle, int sensorIndex, IntPtr thermalSettings);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_GPU_GetDynamicPstatesInfoEx_IntPtr(
            IntPtr gpuHandle, IntPtr pstatesInfoEx);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_GPU_GetMemoryInfo_IntPtr(
            IntPtr gpuHandle, IntPtr memoryInfo);

        private NvAPI_GPU_GetThermalSettings_IntPtr? _getThermalSettings;
        private NvAPI_GPU_GetDynamicPstatesInfoEx_IntPtr? _getDynamicPstatesInfoEx;
        private NvAPI_GPU_GetMemoryInfo_IntPtr? _getMemoryInfo;

        // — NVAPI struct 尺寸（用于手动 Marshal） —
        // Thermal V3: 4(version) + 4(count) + 64*9*4(sensors) = 2312
        private const int THERMAL_V3_SIZE = 2312;
        private const int THERMAL_V3_SENSOR_COUNT = 64;
        private const int THERMAL_V3_SENSOR_STRIDE = 36; // 9 ints
        // Sensor 0 currentTemp = offset 8 + 0*36 + 3*4 = 20
        private const int THERMAL_V3_COUNT_OFFSET = 4;
        private const int THERMAL_V3_SENSOR_BASE = 8;
        private const int THERMAL_V3_CURRENT_TEMP_INDEX = 3;

        // Thermal V1: 4 + 4 + 64*5*4 = 1288
        private const int THERMAL_V1_SIZE = 1288;
        private const int THERMAL_V1_SENSOR_STRIDE = 20; // 5 ints
        private const int THERMAL_V1_CURRENT_TEMP_INDEX = 3;

        // P-states V3: 4(version) + 4(flags) + 32*6*4(domains) = 776
        private const int PSTATES_V3_SIZE = 776;
        private const int PSTATES_V3_DOMAIN_COUNT = 32;
        private const int PSTATES_V3_DOMAIN_STRIDE = 24; // 6 ints
        // Domain 0 bIsPresent at offset 8, percentage at offset 12
        private const int PSTATES_V3_DOMAIN_BASE = 8;
        private const int PSTATES_V3_B_IS_PRESENT_INDEX = 0;
        private const int PSTATES_V3_PERCENTAGE_INDEX = 1;
        private const int PSTATES_V3_GPU_DOMAIN = 0;

        // P-states V1: 4 + 4 + 8*6*4 = 200
        private const int PSTATES_V1_SIZE = 200;
        private const int PSTATES_V1_DOMAIN_COUNT = 8;
        private const int PSTATES_V1_DOMAIN_STRIDE = 24;

        // Memory V3: 36 bytes (no variable-length arrays, can use struct)
        // Memory V2: 28 bytes
        // Memory V1: 20 bytes

        [StructLayout(LayoutKind.Sequential)]
        private struct NV_MEMORY_INFO_V3
        {
            public uint version;
            public uint flags;
            public uint dedicatedVideoMemory;       // KB
            public uint availableDedicatedVideoMemory; // KB
            public uint systemVideoMemory;
            public uint sharedSystemMemory;
            public uint curAvailableDedicatedVideoMemory;
            public uint dedicatedVideoMemoryEvictionsSize;
            public uint dedicatedVideoMemoryPromotionsSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NV_MEMORY_INFO_V2
        {
            public uint version;
            public uint flags;
            public uint dedicatedVideoMemory;
            public uint availableDedicatedVideoMemory;
            public uint systemVideoMemory;
            public uint sharedSystemMemory;
            public uint curAvailableDedicatedVideoMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NV_MEMORY_INFO_V1
        {
            public uint version;
            public uint flags;
            public uint availableDedicatedVideoMemory; // KB (total)
            public uint systemVideoMemory;
            public uint sharedSystemMemory;
        }

        private const double KB_TO_MB = 1.0 / 1024.0;

        public bool IsAvailable
        {
            get
            {
                if (!_initAttempted) TryInit();
                return _available;
            }
        }

        private void TryInit()
        {
            _initAttempted = true;
            try
            {
                if (!NativeLibrary.TryLoad("nvapi64.dll", out _lib))
                {
                    Log("NVAPI: nvapi64.dll not found");
                    return;
                }

                if (!NativeLibrary.TryGetExport(_lib, "nvapi_QueryInterface", out var qiPtr))
                {
                    Log("NVAPI: nvapi_QueryInterface export not found");
                    return;
                }
                _queryInterface = Marshal.GetDelegateForFunctionPointer<NvAPI_QueryInterface>(qiPtr);

                _initialize = QueryFunc<NvAPI_Initialize>(ID_Initialize, "Initialize");
                _enumPhysicalGPUs = QueryFunc<NvAPI_EnumPhysicalGPUs>(ID_EnumPhysicalGPUs, "EnumPhysicalGPUs");
                _getFullName = QueryFunc<NvAPI_GPU_GetFullName>(ID_GPU_GetFullName, "GetFullName");

                // IntPtr-based variants for struct-heavy calls
                _getThermalSettings = QueryFunc<NvAPI_GPU_GetThermalSettings_IntPtr>(ID_GPU_GetThermalSettings, "GetThermalSettings");
                _getDynamicPstatesInfoEx = QueryFunc<NvAPI_GPU_GetDynamicPstatesInfoEx_IntPtr>(ID_GPU_GetDynamicPstatesInfoEx, "GetDynamicPstatesInfoEx");
                _getMemoryInfo = QueryFunc<NvAPI_GPU_GetMemoryInfo_IntPtr>(ID_GPU_GetMemoryInfo, "GetMemoryInfo");

                if (_initialize == null || _enumPhysicalGPUs == null)
                {
                    Log("NVAPI: core function pointers missing");
                    return;
                }

                int initResult = _initialize();
                if (initResult != NVAPI_OK)
                {
                    Log($"NVAPI: Initialize returned 0x{initResult:X8}");
                    return;
                }

                _available = true;
                Log("NVAPI: init OK");
            }
            catch (Exception ex)
            {
                Log($"NVAPI init exception: {ex.Message}");
            }
        }

        public int EnumPhysicalGpus(IntPtr[] handles, ref int count)
        {
            if (!_available || _enumPhysicalGPUs == null) return -1;
            return _enumPhysicalGPUs(handles, ref count);
        }

        public string GetGpuName(IntPtr gpuHandle)
        {
            if (_getFullName == null) return "NVIDIA GPU";
            try
            {
                char[] nameBuf = new char[64];
                if (_getFullName(gpuHandle, nameBuf) == NVAPI_OK)
                {
                    int end = Array.IndexOf(nameBuf, '\0');
                    if (end > 0)
                        return new string(nameBuf, 0, end).Trim();
                }
            }
            catch { }
            return "NVIDIA GPU";
        }

        public double GetGpuTemperature(IntPtr gpuHandle)
        {
            if (_getThermalSettings == null) return -1;

            IntPtr buf = IntPtr.Zero;
            try
            {
                // Try V3 first
                buf = Marshal.AllocHGlobal(THERMAL_V3_SIZE);
                // Write version field: size | (version << 16)
                Marshal.WriteInt32(buf, 0, (int)((uint)THERMAL_V3_SIZE | (3u << 16)));
                Marshal.WriteInt32(buf, THERMAL_V3_COUNT_OFFSET, 0);

                int result = _getThermalSettings(gpuHandle, 0, buf);
                if (result == NVAPI_OK)
                {
                    int count = Marshal.ReadInt32(buf, THERMAL_V3_COUNT_OFFSET);
                    if (count > 0)
                    {
                        int tempOffset = THERMAL_V3_SENSOR_BASE +
                            PSTATES_V3_GPU_DOMAIN * THERMAL_V3_SENSOR_STRIDE +
                            THERMAL_V3_CURRENT_TEMP_INDEX * 4;
                        int temp = Marshal.ReadInt32(buf, tempOffset);
                        if (temp > 0 && temp < 150)
                            return temp;
                    }
                }

                // Fallback V1
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(THERMAL_V1_SIZE);
                Marshal.WriteInt32(buf, 0, (int)((uint)THERMAL_V1_SIZE | (1u << 16)));
                Marshal.WriteInt32(buf, THERMAL_V3_COUNT_OFFSET, 0);

                // V1 uses the same QueryInterface ID — try V1 delegate (smaller struct)
                var qiV1 = QueryFunc<NvAPI_GPU_GetThermalSettings_IntPtr>(
                    ID_GPU_GetThermalSettings, "GetThermalSettings_V1");
                if (qiV1 != null)
                {
                    result = qiV1(gpuHandle, 0, buf);
                    if (result == NVAPI_OK)
                    {
                        int count = Marshal.ReadInt32(buf, THERMAL_V3_COUNT_OFFSET);
                        if (count > 0)
                        {
                            int tempOffset = THERMAL_V3_SENSOR_BASE +
                                PSTATES_V3_GPU_DOMAIN * THERMAL_V1_SENSOR_STRIDE +
                                THERMAL_V1_CURRENT_TEMP_INDEX * 4;
                            int temp = Marshal.ReadInt32(buf, tempOffset);
                            if (temp > 0 && temp < 150)
                                return temp;
                        }
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

        public double GetGpuUsage(IntPtr gpuHandle)
        {
            if (_getDynamicPstatesInfoEx == null) return -1;

            IntPtr buf = IntPtr.Zero;
            try
            {
                // Try V3 first
                buf = Marshal.AllocHGlobal(PSTATES_V3_SIZE);
                Marshal.WriteInt32(buf, 0, (int)((uint)PSTATES_V3_SIZE | (3u << 16)));
                Marshal.WriteInt32(buf, 4, 0);

                int result = _getDynamicPstatesInfoEx(gpuHandle, buf);
                if (result == NVAPI_OK)
                {
                    int domainOffset = PSTATES_V3_DOMAIN_BASE +
                        PSTATES_V3_GPU_DOMAIN * PSTATES_V3_DOMAIN_STRIDE;
                    int bIsPresent = Marshal.ReadInt32(buf, domainOffset +
                        PSTATES_V3_B_IS_PRESENT_INDEX * 4);
                    if (bIsPresent != 0)
                    {
                        int pct = Marshal.ReadInt32(buf, domainOffset +
                            PSTATES_V3_PERCENTAGE_INDEX * 4);
                        if (pct >= 0 && pct <= 100)
                            return pct;
                    }
                }

                // Fallback V1
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(PSTATES_V1_SIZE);
                Marshal.WriteInt32(buf, 0, (int)((uint)PSTATES_V1_SIZE | (1u << 16)));
                Marshal.WriteInt32(buf, 4, 0);

                var qiV1 = QueryFunc<NvAPI_GPU_GetDynamicPstatesInfoEx_IntPtr>(
                    ID_GPU_GetDynamicPstatesInfoEx, "GetDynamicPstatesInfoEx_V1");
                if (qiV1 != null)
                {
                    result = qiV1(gpuHandle, buf);
                    if (result == NVAPI_OK)
                    {
                        int domainOffset = PSTATES_V3_DOMAIN_BASE +
                            PSTATES_V3_GPU_DOMAIN * PSTATES_V1_DOMAIN_STRIDE;
                        int bIsPresent = Marshal.ReadInt32(buf, domainOffset +
                            PSTATES_V3_B_IS_PRESENT_INDEX * 4);
                        if (bIsPresent != 0)
                        {
                            int pct = Marshal.ReadInt32(buf, domainOffset +
                                PSTATES_V3_PERCENTAGE_INDEX * 4);
                            if (pct >= 0 && pct <= 100)
                                return pct;
                        }
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

        public void GetGpuMemory(IntPtr gpuHandle, out double usedMB, out double totalMB)
        {
            usedMB = 0;
            totalMB = 0;

            if (_getMemoryInfo == null) return;

            IntPtr buf = IntPtr.Zero;
            try
            {
                // Try V3
                buf = Marshal.AllocHGlobal(Marshal.SizeOf<NV_MEMORY_INFO_V3>());
                var v3 = new NV_MEMORY_INFO_V3
                {
                    version = (uint)(Marshal.SizeOf<NV_MEMORY_INFO_V3>() | (3u << 16)),
                    flags = 0,
                };
                Marshal.StructureToPtr(v3, buf, false);

                int result = _getMemoryInfo(gpuHandle, buf);
                if (result == NVAPI_OK)
                {
                    v3 = Marshal.PtrToStructure<NV_MEMORY_INFO_V3>(buf);
                    totalMB = v3.dedicatedVideoMemory * KB_TO_MB;
                    double avail = v3.curAvailableDedicatedVideoMemory * KB_TO_MB;
                    if (totalMB > 0 && avail > 0)
                        usedMB = totalMB - avail;
                    return;
                }

                // Try V2
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(Marshal.SizeOf<NV_MEMORY_INFO_V2>());
                var v2 = new NV_MEMORY_INFO_V2
                {
                    version = (uint)(Marshal.SizeOf<NV_MEMORY_INFO_V2>() | (2u << 16)),
                    flags = 0,
                };
                Marshal.StructureToPtr(v2, buf, false);

                var qiV2 = QueryFunc<NvAPI_GPU_GetMemoryInfo_IntPtr>(
                    ID_GPU_GetMemoryInfo, "GetMemoryInfo_V2");
                if (qiV2 != null)
                {
                    result = qiV2(gpuHandle, buf);
                    if (result == NVAPI_OK)
                    {
                        v2 = Marshal.PtrToStructure<NV_MEMORY_INFO_V2>(buf);
                        totalMB = v2.dedicatedVideoMemory * KB_TO_MB;
                        double avail = v2.curAvailableDedicatedVideoMemory * KB_TO_MB;
                        if (totalMB > 0 && avail > 0)
                            usedMB = totalMB - avail;
                        return;
                    }
                }

                // Try V1
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(Marshal.SizeOf<NV_MEMORY_INFO_V1>());
                var v1 = new NV_MEMORY_INFO_V1
                {
                    version = (uint)(Marshal.SizeOf<NV_MEMORY_INFO_V1>() | (1u << 16)),
                    flags = 0,
                };
                Marshal.StructureToPtr(v1, buf, false);

                var qiV1 = QueryFunc<NvAPI_GPU_GetMemoryInfo_IntPtr>(
                    ID_GPU_GetMemoryInfo, "GetMemoryInfo_V1");
                if (qiV1 != null)
                {
                    result = qiV1(gpuHandle, buf);
                    if (result == NVAPI_OK)
                    {
                        v1 = Marshal.PtrToStructure<NV_MEMORY_INFO_V1>(buf);
                        totalMB = v1.availableDedicatedVideoMemory * KB_TO_MB;
                        usedMB = 0; // V1 can't compute used
                    }
                }
            }
            catch { }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }
        }

        private T? QueryFunc<T>(uint id, string name) where T : Delegate
        {
            IntPtr ptr = _queryInterface!(id);
            if (ptr == IntPtr.Zero)
            {
                Log($"NVAPI: QueryInterface({name}, 0x{id:X8}) null");
                return null;
            }
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }
    }

    // ================================================================
    // AMD — ADL PMLOG
    // ================================================================

    private static List<GpuData> ReadAmdGpus()
    {
        if (!_adlCache.IsAvailable) return [];

        try
        {
            var adapters = _adlCache.ReadAllGpuAdapters();
            if (adapters.Count == 0) return [];

            // ADL 物理适配器筛选：第一个有 GPU 传感器数据的适配器 = 物理 GPU
            // 后续适配器是虚拟适配器（每个显示输出一个），跳过
            var results = new List<GpuData>();
            bool taken = false;
            for (int i = 0; i < adapters.Count; i++)
            {
                if (taken)
                {
                    Log($"ADL GPU: adapter {adapters[i].AdapterIndex} 跳过（虚拟适配器）");
                    continue;
                }

                var a = adapters[i];
                string name = $"AMD GPU {a.AdapterIndex}";
                results.Add(new GpuData(name, a.Temperature, a.LoadPercent, 0, 0, GpuVendor.AMD));
                taken = true;
                Log($"ADL GPU: adapter {a.AdapterIndex} = {name} (物理适配器)");
            }

            return results;
        }
        catch (Exception ex)
        {
            Log($"ADL GPU 读取异常: {ex.Message}");
        }

        return [];
    }

    /// <summary>ADL PMLOG 读取器内部实现（自包含，无外部依赖）</summary>
    private sealed class AdlReaderInternal
    {
        private const int ADL_PMLOG_MAX_SENSORS = 256;
        private const int PMLOG_TEMP_EDGE = 8;
        private const int PMLOG_TEMP_HOTSPOT = 27;
        private const int PMLOG_TEMP_GFX = 28;
        private const int PMLOG_LOAD_GFX = 50;
        private const int PMLOG_STRUCT_SIZE = 2056;
        private const int PMLOG_SENSORS_OFFSET = 4;
        private const int PMLOG_ENTRY_SIZE = 8;

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

        private readonly object _initLock = new();
        private IntPtr _adlLib;
        private bool _available;
        private bool _initAttempted;
        private int _adapterCount;
        private AdlDestroyFn? _adlDestroy;
        private Adl2PmlogFn? _adlPmlog;
        private AdlMallocCb? _adlMallocCb;

        internal readonly record struct AdlGpuAdapterData(
            int AdapterIndex, double Temperature, double LoadPercent, bool HasGpuData);

        public bool IsAvailable
        {
            get
            {
                if (!_initAttempted) InitAdl();
                return _available;
            }
        }

        private void InitAdl()
        {
            lock (_initLock)
            {
                if (_initAttempted) return;
                _initAttempted = true;

                try
                {
                    Log("ADL Init: starting");

                    bool loaded = NativeLibrary.TryLoad("atiadlxx.dll", out _adlLib);
                    if (!loaded)
                        loaded = NativeLibrary.TryLoad(@"C:\Windows\System32\atiadlxx.dll", out _adlLib);
                    if (!loaded)
                        loaded = NativeLibrary.TryLoad("atiadlxy.dll", out _adlLib);
                    if (!loaded)
                    {
                        Log("ADL Init: all DLL load attempts failed");
                        return;
                    }

                    if (!NativeLibrary.TryGetExport(_adlLib, "ADL_Main_Control_Create", out var createPtr))
                    { Log("ADL Init: ADL_Main_Control_Create not found"); Cleanup(); return; }
                    var create = Marshal.GetDelegateForFunctionPointer<AdlCreateFn>(createPtr);

                    _adlMallocCb = AdlMalloc;
                    var cbPtr = Marshal.GetFunctionPointerForDelegate(_adlMallocCb);

                    int createResult = create(cbPtr, 1);
                    Log($"ADL Init: ADL_Main_Control_Create result = {createResult}");
                    if (createResult < 0) { Cleanup(); return; }

                    if (!NativeLibrary.TryGetExport(_adlLib, "ADL_Main_Control_Destroy", out var destroyPtr))
                    { Log("ADL Init: ADL_Main_Control_Destroy not found"); Cleanup(); return; }
                    _adlDestroy = Marshal.GetDelegateForFunctionPointer<AdlDestroyFn>(destroyPtr);

                    if (!NativeLibrary.TryGetExport(_adlLib, "ADL_Adapter_NumberOfAdapters_Get", out var adaptersPtr))
                    { Log("ADL Init: ADL_Adapter_NumberOfAdapters_Get not found"); Cleanup(); return; }
                    var adapters = Marshal.GetDelegateForFunctionPointer<AdlAdapterCountFn>(adaptersPtr);

                    if (!NativeLibrary.TryGetExport(_adlLib, "ADL2_New_QueryPMLogData_Get", out var pmlogPtr))
                    { Log("ADL Init: ADL2_New_QueryPMLogData_Get not found"); Cleanup(); return; }
                    _adlPmlog = Marshal.GetDelegateForFunctionPointer<Adl2PmlogFn>(pmlogPtr);

                    int count = 0;
                    int adapterResult = adapters(ref count);
                    _adapterCount = count;
                    Log($"ADL Init: adapter count = {count}, result = {adapterResult}");
                    if (adapterResult < 0 || count <= 0) { Cleanup(); return; }

                    // Probe PMLOG on each adapter for GPU sensors
                    IntPtr probeBuf = IntPtr.Zero;
                    try
                    {
                        probeBuf = Marshal.AllocHGlobal(PMLOG_STRUCT_SIZE);
                        InitPmlogBuffer(probeBuf);

                        for (int i = 0; i < count; i++)
                        {
                            int pmResult = _adlPmlog(0, i, probeBuf);
                            if (pmResult >= 0)
                            {
                                int edgeSup = ReadSensorSupported(probeBuf, PMLOG_TEMP_EDGE);
                                int edgeVal = ReadSensorValue(probeBuf, PMLOG_TEMP_EDGE);
                                int gfxSup = ReadSensorSupported(probeBuf, PMLOG_LOAD_GFX);
                                Log($"ADL Init: adapter {i}: edge_temp={edgeVal}(sup={edgeSup}), gfx_load_sup={gfxSup}");

                                if (edgeSup != 0 || gfxSup != 0)
                                {
                                    _available = true;
                                    Log($"ADL Init: SUCCESS — adapter {i} has GPU sensors");
                                    return;
                                }
                            }
                            else
                            {
                                Log($"ADL Init: adapter {i}: pmlog={pmResult}");
                            }
                        }

                        Log($"ADL Init: {count} adapters, none support GPU PMLOG");
                        Cleanup();
                    }
                    finally
                    {
                        if (probeBuf != IntPtr.Zero) Marshal.FreeHGlobal(probeBuf);
                    }
                }
                catch (Exception ex)
                {
                    Log($"ADL Init exception: {ex.GetType().Name}: {ex.Message}");
                    Cleanup();
                }
            }
        }

        internal List<AdlGpuAdapterData> ReadAllGpuAdapters()
        {
            var result = new List<AdlGpuAdapterData>();
            if (_adlPmlog == null || _adapterCount <= 0) return result;

            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(PMLOG_STRUCT_SIZE);

                for (int i = 0; i < _adapterCount; i++)
                {
                    InitPmlogBuffer(buf);
                    int pmResult = _adlPmlog(0, i, buf);
                    if (pmResult < 0) continue;

                    double temp = -1;
                    int edgeSup = ReadSensorSupported(buf, PMLOG_TEMP_EDGE);
                    int edgeVal = ReadSensorValue(buf, PMLOG_TEMP_EDGE);
                    if (edgeSup != 0 && edgeVal > 0 && edgeVal < 150)
                        temp = edgeVal;

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

                    double load = -1;
                    int loadSup = ReadSensorSupported(buf, PMLOG_LOAD_GFX);
                    int loadVal = ReadSensorValue(buf, PMLOG_LOAD_GFX);
                    if (loadSup != 0 && loadVal >= 0 && loadVal <= 100)
                        load = loadVal;

                    bool hasGpuData = temp > 0 || load >= 0;
                    Log($"ADL ReadAll: adapter {i}: temp={temp:F0}C, load={load:F0}%, hasGpu={hasGpuData}");

                    if (hasGpuData)
                        result.Add(new AdlGpuAdapterData(i, temp, load, true));
                }
            }
            catch (Exception ex)
            {
                Log($"ADL ReadAllGpuAdapters: {ex.Message}");
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            }

            return result;
        }

        private static void InitPmlogBuffer(IntPtr buf)
        {
            for (int i = 0; i < PMLOG_STRUCT_SIZE; i += 4)
                Marshal.WriteInt32(buf, i, 0);
            Marshal.WriteInt32(buf, 0, PMLOG_STRUCT_SIZE);
        }

        private static int ReadSensorSupported(IntPtr buf, int sensorId)
        {
            if (sensorId < 0 || sensorId >= ADL_PMLOG_MAX_SENSORS) return 0;
            return Marshal.ReadInt32(buf, PMLOG_SENSORS_OFFSET + sensorId * PMLOG_ENTRY_SIZE);
        }

        private static int ReadSensorValue(IntPtr buf, int sensorId)
        {
            if (sensorId < 0 || sensorId >= ADL_PMLOG_MAX_SENSORS) return 0;
            return Marshal.ReadInt32(buf, PMLOG_SENSORS_OFFSET + sensorId * PMLOG_ENTRY_SIZE + 4);
        }

        private static IntPtr AdlMalloc(int size) => Marshal.AllocHGlobal(size);

        private void Cleanup()
        {
            try { _adlDestroy?.Invoke(); } catch { }
            if (_adlLib != IntPtr.Zero) { NativeLibrary.Free(_adlLib); _adlLib = IntPtr.Zero; }
            _adlDestroy = null;
            _adlPmlog = null;
            _adlMallocCb = null;
            _available = false;
            _adapterCount = 0;
        }
    }

    // ================================================================
    // Intel — IGCL
    // ================================================================

    private static List<GpuData> ReadIntelGpus()
    {
        if (!_igclCache.IsAvailable) return [];

        try
        {
            double temp = _igclCache.ReadGpuTemp();
            double usage = _igclCache.ReadGpuUsage();
            if (temp > 0)
            {
                return [new GpuData("Intel GPU (IGCL)", temp, usage, 0, 0, GpuVendor.Intel)];
            }
        }
        catch (Exception ex)
        {
            Log($"IGCL 读取异常: {ex.Message}");
        }

        return [];
    }

    /// <summary>IGCL 读取器内部实现（自包含，无外部依赖）</summary>
    private sealed class IgclReaderInternal
    {
        private IntPtr _lib;
        private bool _initAttempted;
        private bool _available;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int IgclInitFn();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int IgclGetGpuTempFn(out int temperature);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int IgclGetGpuUtilizationFn(out int utilization);

        private IgclInitFn? _init;
        private IgclGetGpuTempFn? _getTemp;
        private IgclGetGpuUtilizationFn? _getUtil;

        private const int IGCL_SUCCESS = 0;

        public bool IsAvailable
        {
            get
            {
                if (!_initAttempted) TryInit();
                return _available;
            }
        }

        private void TryInit()
        {
            _initAttempted = true;
            try
            {
                string[] candidates = [
                    "igcl.dll",
                    @"C:\Windows\System32\igcl.dll",
                    @"C:\Windows\System32\DriverStore\FileRepository\igcl.dll",
                ];

                bool loaded = false;
                foreach (var path in candidates)
                {
                    loaded = NativeLibrary.TryLoad(path, out _lib);
                    if (loaded) break;
                }

                if (!loaded)
                {
                    Log("IGCL: igcl.dll not found");
                    return;
                }

                NativeLibrary.TryGetExport(_lib, "igcl_init", out var initPtr);
                NativeLibrary.TryGetExport(_lib, "igcl_get_gpu_temperature", out var tempPtr);
                NativeLibrary.TryGetExport(_lib, "igcl_get_gpu_utilization", out var utilPtr);

                if (initPtr != IntPtr.Zero)
                    _init = Marshal.GetDelegateForFunctionPointer<IgclInitFn>(initPtr);
                if (tempPtr != IntPtr.Zero)
                    _getTemp = Marshal.GetDelegateForFunctionPointer<IgclGetGpuTempFn>(tempPtr);
                if (utilPtr != IntPtr.Zero)
                    _getUtil = Marshal.GetDelegateForFunctionPointer<IgclGetGpuUtilizationFn>(utilPtr);

                if (_init == null)
                {
                    Log("IGCL: igcl_init not exported");
                    return;
                }

                if (_init() != IGCL_SUCCESS)
                {
                    Log("IGCL: igcl_init failed");
                    return;
                }

                _available = true;
                Log("IGCL: init OK");
            }
            catch (Exception ex)
            {
                Log($"IGCL init exception: {ex.Message}");
            }
        }

        public double ReadGpuTemp()
        {
            if (!IsAvailable || _getTemp == null) return -1;
            try
            {
                if (_getTemp(out int temp) == IGCL_SUCCESS && temp > 0 && temp < 150)
                    return temp;
            }
            catch { }
            return -1;
        }

        public double ReadGpuUsage()
        {
            if (!IsAvailable || _getUtil == null) return -1;
            try
            {
                if (_getUtil(out int util) == IGCL_SUCCESS && util >= 0 && util <= 100)
                    return util;
            }
            catch { }
            return -1;
        }
    }

    // ================================================================
    // Logging
    // ================================================================

    private static void Log(string msg)
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SysMonCmdPal", "broker.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [GpuReader] {msg}\n");
        }
        catch { }
    }
}
