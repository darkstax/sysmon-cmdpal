// Copyright (c) 2026 SysMonCmdPal
// Intel GPU 读取器 — 通过 IGCL (Intel Graphics Control Library) 读取 Intel Arc GPU 温度。
// IGCL DLL 随 Intel 显卡驱动安装。回退链位置: Phase 1 (Intel GPU 最高优先级)

using System;
using System.Runtime.InteropServices;

namespace SysMonCmdPal;

/// <summary>
/// 通过 IGCL (Intel Graphics Control Library) 读取 Intel Arc GPU 数据。
/// igcl.dll 随 Intel 显卡驱动安装。
/// </summary>
internal sealed class IgclReader
{
    public static IgclReader Instance { get; } = new();

    private IntPtr _lib;
    private bool _initAttempted;
    private bool _available;

    // Delegates for IGCL API
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

    private IgclReader() { }

    private void TryInit()
    {
        _initAttempted = true;
        try
        {
            // Try loading igcl.dll from standard locations
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
                SensorLogger.ForceLog("IGCL: igcl.dll 未找到");
                return;
            }

            // Try to get exports
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
                SensorLogger.ForceLog("IGCL: igcl_init 未导出，可能不支持此 API");
                return;
            }

            // Verify IGCL is available (returns 0 on success)
            if (_init() != IGCL_SUCCESS)
            {
                SensorLogger.ForceLog("IGCL: igcl_init 失败");
                return;
            }

            _available = true;
            SensorLogger.ForceLog("IGCL: 初始化成功");
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"IGCL 初始化异常: {ex.Message}");
        }
    }

    /// <summary>读取 Intel GPU 温度 (°C)，不可用返回 -1</summary>
    public double ReadGpuTemp()
    {
        if (!IsAvailable || _getTemp == null) return -1;

        try
        {
            if (_getTemp(out int temp) == IGCL_SUCCESS && temp > 0 && temp < 150)
                return temp;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"IGCL 温度读取异常: {ex.Message}");
        }
        return -1;
    }

    /// <summary>读取 Intel GPU 使用率 (0-100)，不可用返回 -1</summary>
    public double ReadGpuUsage()
    {
        if (!IsAvailable || _getUtil == null) return -1;

        try
        {
            if (_getUtil(out int util) == IGCL_SUCCESS && util >= 0 && util <= 100)
                return util;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"IGCL 使用率读取异常: {ex.Message}");
        }
        return -1;
    }
}
