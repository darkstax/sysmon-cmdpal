// Copyright (c) 2026 SysMonCmdPal
// NVIDIA GPU 读取器 — 通过 NVAPI (nvapi64.dll) 读取 GPU 温度/使用率/显存。
// nvapi64.dll 仅导出 nvapi_QueryInterface，所有函数通过 magic ID 查询获取。
// 纯用户态 API，MSIX 下完全可用。回退链位置: Phase A (NVIDIA GPU 并行枚举)

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SysMonCmdPal;

/// <summary>
/// 通过 NVAPI QueryInterface 模式读取 NVIDIA GPU 数据。
/// nvapi64.dll 随 NVIDIA 驱动安装，纯用户态，无需管理员。
/// </summary>
internal sealed class NvapiReader
{
    public static NvapiReader Instance { get; } = new();

    private IntPtr _lib;
    private bool _initAttempted;
    private bool _available;
    private int _gpuCount;
    private readonly IntPtr[] _gpuHandles = new IntPtr[64];

    // Function pointers obtained via nvapi_QueryInterface
    private NvAPI_QueryInterface? _queryInterface;
    private NvAPI_Initialize? _initialize;
    private NvAPI_EnumPhysicalGPUs? _enumPhysicalGPUs;
    private NvAPI_GPU_GetThermalSettings? _getThermalSettings;
    private NvAPI_GPU_GetDynamicPstatesInfoEx? _getDynamicPstatesInfoEx;
    private NvAPI_GPU_GetMemoryInfo? _getMemoryInfo;
    private NvAPI_GPU_GetFullName? _getFullName;

    // ── NVAPI function magic IDs (from nvapi.h) ──
    private const uint ID_Initialize               = 0x0150E828;
    private const uint ID_EnumPhysicalGPUs         = 0xE5AC921F;
    private const uint ID_GPU_GetThermalSettings    = 0xE3640A56;
    private const uint ID_GPU_GetDynamicPstatesInfoEx = 0x843C0256;
    private const uint ID_GPU_GetMemoryInfo         = 0x774AA982; // V3 capable
    private const uint ID_GPU_GetFullName           = 0xCEEE8E9F;

    // ── Delegate definitions ──
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr NvAPI_QueryInterface(uint functionId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_Initialize();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_EnumPhysicalGPUs(
        [Out] IntPtr[] gpuHandles, ref int gpuCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_GPU_GetThermalSettings(
        IntPtr gpuHandle, int sensorIndex, ref NV_GPU_THERMAL_SETTINGS_V3 thermalSettings);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_GPU_GetDynamicPstatesInfoEx(
        IntPtr gpuHandle, ref NV_GPU_DYNAMIC_PSTATES_INFO_EX_V3 pstatesInfoEx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_GPU_GetMemoryInfo(
        IntPtr gpuHandle, ref NV_DISPLAY_DRIVER_MEMORY_INFO_V3 memoryInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvAPI_GPU_GetFullName(
        IntPtr gpuHandle, [Out] char[] name);

    // ── Struct definitions (fixed-layout, matching NVAPI C headers exactly) ──
    // Multiple versions per struct for fallback on newer drivers.

    // ── Thermal Settings ──
    // V1: 8 + 64*20 = 1288 bytes, version = 0x00010508
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NV_GPU_THERMAL_SETTINGS_V1
    {
        public uint version;
        public uint count;
        public fixed int sensors[64 * 5]; // 5 ints per sensor (20 bytes each)
    }
    // V3: 8 + 64*36 = 2312 bytes, version = 0x00030908
    // V3 adds: target(4), controller(4), defaultMinTemp(4), defaultMaxTemp(4) after currentTemp
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NV_GPU_THERMAL_SETTINGS_V3
    {
        public uint version;
        public uint count;
        public fixed int sensors[64 * 9]; // 9 ints per sensor (36 bytes each)
    }
    // Per-sensor V3 layout (36 bytes):
    //   [0] controller, [1] defaultMinTemp, [2] defaultMaxTemp, [3] currentTemp (°C),
    //   [4] target, [5-8] reserved

    // ── Dynamic P-states ──
    // V1: 8 + 8*24 = 200 bytes
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NV_GPU_DYNAMIC_PSTATES_INFO_EX_V1
    {
        public uint version;
        public uint flags;
        public fixed int domains[8 * 6]; // 8 domains × 6 ints
    }
    // V3: 8 + 32*24 = 776 bytes
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NV_GPU_DYNAMIC_PSTATES_INFO_EX_V3
    {
        public uint version;
        public uint flags;
        public fixed int domains[32 * 6]; // 32 domains × 6 ints
    }

    // ── Memory Info ──
    // V1: 20 bytes (no curAvailable)
    [StructLayout(LayoutKind.Sequential)]
    private struct NV_DISPLAY_DRIVER_MEMORY_INFO_V1
    {
        public uint version;
        public uint flags;
        public uint availableDedicatedVideoMemory; // KB (total)
        public uint systemVideoMemory;             // KB
        public uint sharedSystemMemory;            // KB
    }
    // V2: 28 bytes
    [StructLayout(LayoutKind.Sequential)]
    private struct NV_DISPLAY_DRIVER_MEMORY_INFO_V2
    {
        public uint version;
        public uint flags;
        public uint dedicatedVideoMemory;
        public uint availableDedicatedVideoMemory;
        public uint systemVideoMemory;
        public uint sharedSystemMemory;
        public uint curAvailableDedicatedVideoMemory;
    }
    // V3: 36 bytes — adds eviction/promotion sizes
    [StructLayout(LayoutKind.Sequential)]
    private struct NV_DISPLAY_DRIVER_MEMORY_INFO_V3
    {
        public uint version;
        public uint flags;
        public uint dedicatedVideoMemory;
        public uint availableDedicatedVideoMemory;
        public uint systemVideoMemory;
        public uint sharedSystemMemory;
        public uint curAvailableDedicatedVideoMemory;
        public uint dedicatedVideoMemoryEvictionsSize;
        public uint dedicatedVideoMemoryPromotionsSize;
    }

    // NVAPI version macro: (sizeof(struct) | (version << 16))
    private static uint MakeVersion(int sizeOfStruct, int version) =>
        (uint)sizeOfStruct | ((uint)version << 16);

    private const int NVAPI_OK = 0;

    public bool IsAvailable
    {
        get
        {
            if (!_initAttempted) TryInit();
            return _available;
        }
    }

    private NvapiReader() { }

    private void TryInit()
    {
        _initAttempted = true;
        try
        {
            if (!NativeLibrary.TryLoad("nvapi64.dll", out _lib))
            {
                SensorLogger.ForceLog("NVAPI: nvapi64.dll 未找到 (无 NVIDIA 驱动)");
                return;
            }

            // Step 1: Get the query interface function (exported by name)
            if (!NativeLibrary.TryGetExport(_lib, "nvapi_QueryInterface", out var qiPtr))
            {
                SensorLogger.ForceLog("NVAPI: nvapi_QueryInterface 导出未找到");
                return;
            }
            _queryInterface = Marshal.GetDelegateForFunctionPointer<NvAPI_QueryInterface>(qiPtr);

            // Step 2: Use query interface to obtain all function pointers
            _initialize = QueryFunc<NvAPI_Initialize>(ID_Initialize, "Initialize");
            _enumPhysicalGPUs = QueryFunc<NvAPI_EnumPhysicalGPUs>(ID_EnumPhysicalGPUs, "EnumPhysicalGPUs");
            _getThermalSettings = QueryFunc<NvAPI_GPU_GetThermalSettings>(ID_GPU_GetThermalSettings, "GetThermalSettings");
            _getDynamicPstatesInfoEx = QueryFunc<NvAPI_GPU_GetDynamicPstatesInfoEx>(ID_GPU_GetDynamicPstatesInfoEx, "GetDynamicPstatesInfoEx");
            _getMemoryInfo = QueryFunc<NvAPI_GPU_GetMemoryInfo>(ID_GPU_GetMemoryInfo, "GetMemoryInfo");
            _getFullName = QueryFunc<NvAPI_GPU_GetFullName>(ID_GPU_GetFullName, "GetFullName");

            if (_initialize == null || _enumPhysicalGPUs == null)
            {
                SensorLogger.ForceLog("NVAPI: 核心函数获取失败");
                return;
            }

            // Step 3: Initialize NVAPI
            int initResult = _initialize();
            if (initResult != NVAPI_OK)
            {
                SensorLogger.ForceLog($"NVAPI: Initialize 返回 0x{initResult:X8}");
                return;
            }

            // Step 4: Enumerate physical GPUs
            int count = _gpuHandles.Length;
            int enumResult = _enumPhysicalGPUs(_gpuHandles, ref count);
            if (enumResult != NVAPI_OK || count == 0)
            {
                SensorLogger.ForceLog($"NVAPI: EnumPhysicalGPUs 返回 0x{enumResult:X8}, count={count}");
                return;
            }

            _gpuCount = count;
            _available = true;
            SensorLogger.ForceLog($"NVAPI: 初始化成功, GPU 数 = {count}");
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"NVAPI 初始化异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 尝试通过 NVAPI 读取 GPU 数据。
    /// 由于 NVAPI struct 版本兼容性问题，主要依赖 GetFullName 获取真实 GPU 名称。
    /// 温度/使用率/显存等数据优先由 LHM 提供。
    /// </summary>
    public unsafe List<GpuResult> ReadAllGpus()
    {
        if (!IsAvailable) return [];

        var results = new List<GpuResult>();
        try
        {
            IntPtr[] handles = new IntPtr[64];
            int count = handles.Length;
            if (_enumPhysicalGPUs!(handles, ref count) != NVAPI_OK || count == 0)
                return [];

            for (int i = 0; i < count; i++)
            {
                // ── GPU name (this always works) ──
                string name = "NVIDIA GPU";
                if (_getFullName != null)
                {
                    char[] nameBuf = new char[64];
                    if (_getFullName(handles[i], nameBuf) == NVAPI_OK)
                    {
                        int end = Array.IndexOf(nameBuf, '\0');
                        if (end > 0)
                            name = new string(nameBuf, 0, end).Trim();
                    }
                }

                // 温度/使用率/显存 不再通过 NVAPI 读取（struct 版本不兼容 + MSIX 沙箱限制）
                // 这些数据由 GpuSensorReader 的 LHM 阶段补充
                var result = new GpuResult(name, -1, -1, 0, 0, "NVAPI-Name");
                SensorLogger.ForceLog($"NVAPI[{i}]: {name} (name only, data via LHM)");
                results.Add(result);
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"NVAPI 读取异常: {ex.Message}");
        }

        return results;
    }

    /// <summary>兼容单卡接口：返回第一张 GPU</summary>
    public GpuResult ReadGpu()
    {
        var all = ReadAllGpus();
        return all.Count > 0 ? all[0] : GpuResult.None;
    }

    private T? QueryFunc<T>(uint id, string name) where T : Delegate
    {
        IntPtr ptr = _queryInterface!(id);
        if (ptr == IntPtr.Zero)
        {
            SensorLogger.ForceLog($"NVAPI: QueryInterface({name}, 0x{id:X8}) 返回 null");
            return null;
        }
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }
}
