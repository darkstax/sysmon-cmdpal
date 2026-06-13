// Copyright (c) 2026 SysMonCmdPal
// CPU 温度读取器 — Windows 热区温度 (PerformanceCounter)，
// 无需管理员权限，读取 ACPI 热区温度。
// 回退链位置: LHM HTTP 之后，ADL PMLOG 之前。
// 注: 热区温度通常比 Tctl/Tdie 偏低几度，但无需任何驱动或外部工具。

using System;
using System.Diagnostics;

namespace SysMonCmdPal;

/// <summary>
/// 通过 Windows PerformanceCounter "Thermal Zone Information" 读取 ACPI 热区温度。
/// 无需管理员权限。返回值为开尔文，转换为摄氏度。
/// 复刻 G-Helper HardwareControl 中 PerformanceCounter 回退路径。
/// </summary>
internal sealed class ThermalZoneReader
{
    public static ThermalZoneReader Instance { get; } = new();

    private PerformanceCounter? _counter;
    private bool _initAttempted;
    private bool _available;
    private int _failCount;
    private const int MaxFailures = 5;

    private ThermalZoneReader() { }

    public bool IsAvailable
    {
        get
        {
            if (!_initAttempted) InitCounter();
            return _available;
        }
    }

    private void InitCounter()
    {
        _initAttempted = true;
        try
        {
            // 尝试常见的 ACPI 热区实例名
            string[] instanceNames = [
                @"\_TZ.THRM",     // 常见 ACPI 热区
                @"\_TZ.TZ00",     // 某些主板
                @"\_TZ.TZ01",     // 某些主板
                @"\_TZ.CPUZ",     // CPU 专用热区
            ];

            foreach (var name in instanceNames)
            {
                try
                {
                    var counter = new PerformanceCounter("Thermal Zone Information", "Temperature", name, true);
                    // 尝试读取一次，确认实例有效
                    float val = counter.NextValue();
                    if (val > 200 && val < 500) // 合理的开尔文温度范围
                    {
                        _counter = counter;
                        _available = true;
                        Log($"InitCounter: SUCCESS — instance '{name}', value={val}K ({val - 273.15:F1}°C)");
                        return;
                    }
                    counter.Dispose();
                }
                catch
                {
                    // 实例名不存在，继续尝试下一个
                }
            }

            // 如果常见实例名都不行，尝试枚举所有实例
            try
            {
                var category = new PerformanceCounterCategory("Thermal Zone Information");
                var instances = category.GetInstanceNames();
                foreach (var inst in instances)
                {
                    try
                    {
                        var counter = new PerformanceCounter("Thermal Zone Information", "Temperature", inst, true);
                        float val = counter.NextValue();
                        if (val > 200 && val < 500)
                        {
                            _counter = counter;
                            _available = true;
                            Log($"InitCounter: SUCCESS (enumerated) — instance '{inst}', value={val}K ({val - 273.15:F1}°C)");
                            return;
                        }
                        counter.Dispose();
                    }
                    catch { /* skip */ }
                }
            }
            catch (Exception ex)
            {
                Log($"InitCounter: category enumeration failed — {ex.Message}");
            }

            Log("InitCounter: no valid thermal zone instance found");
        }
        catch (Exception ex)
        {
            Log($"InitCounter: EXCEPTION — {ex.Message}");
        }
    }

    /// <summary>
    /// 读取 CPU 热区温度（°C）。
    /// PerformanceCounter 返回开尔文值，转换为摄氏度。
    /// </summary>
    public double ReadCpuTemp()
    {
        if (!IsAvailable || _counter == null) return -1;

        try
        {
            float kelvin = _counter.NextValue();
            if (kelvin > 200 && kelvin < 500)
            {
                _failCount = 0;
                double celsius = kelvin - 273.15;
                return celsius;
            }

            // 值不合理
            if (++_failCount >= MaxFailures)
            {
                Log($"ReadCpuTemp: {MaxFailures} consecutive bad readings, marking unavailable");
                _available = false;
            }
        }
        catch (Exception ex)
        {
            Log($"ReadCpuTemp: EXCEPTION — {ex.Message}");
            if (++_failCount >= MaxFailures)
                _available = false;
        }

        return -1;
    }

    private static void Log(string msg)
    {
        try
        {
            SensorLogger.ForceLog($"[ThermalZone] {msg}");
        }
        catch { /* ignore */ }
    }
}
