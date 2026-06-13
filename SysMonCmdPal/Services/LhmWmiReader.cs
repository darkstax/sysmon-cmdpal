// Copyright (c) 2026 SysMonCmdPal
// LHM WMI Provider 读取器 — 通过 WMI 读取外部 LibreHardwareMonitor 独立版的传感器数据。
// LHM 独立版勾选 "Enable WMI Provider" 后，传感器暴露在 root\LibreHardwareMonitor 命名空间。
// MSIX runFullTrust 下 WMI 查询完全可用，作为 LHM NuGet (PawnIO 被阻止) 的 CPU 温度回退。
//
// 回退链位置: LHM NuGet (GPU via NVAPI) → LHM WMI (CPU 温度) → AMD ADL → HWiNFO → None

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;

namespace SysMonCmdPal;

/// <summary>
/// 通过 WMI 读取外部 LHM 进程的传感器数据（惰性初始化 + 冷却重试）。
/// LHM 独立版需勾选 "Enable WMI Provider"。
/// </summary>
internal sealed class LhmWmiReader
{
    public static LhmWmiReader Instance { get; } = new();

    private bool _available;
    private bool _initAttempted;
    private DateTime _lastInitAttempt = DateTime.MinValue;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);
    private const string WmiNamespace = @"root\LibreHardwareMonitor";
    private const string WmiQuery = "SELECT Name, Value, SensorType, Parent FROM Sensor WHERE Value IS NOT NULL";

    private LhmWmiReader() { }

    /// <summary>WMI 命名空间是否可达（LHM 已安装并启用 WMI Provider）</summary>
    public bool IsAvailable
    {
        get
        {
            if (!_initAttempted)
            {
                _initAttempted = true;
                _available = ProbeWmi();
            }
            else if (!_available && DateTime.UtcNow - _lastInitAttempt > RetryCooldown)
            {
                // 冷却重试：用户可能稍后启动了 LHM
                _lastInitAttempt = DateTime.UtcNow;
                _available = ProbeWmi();
            }
            return _available;
        }
    }

    /// <summary>读取 CPU Package 温度（°C），不可用返回 -1</summary>
    public double ReadCpuPackageTemp()
    {
        if (!IsAvailable) return -1;

        try
        {
            using var searcher = new ManagementObjectSearcher(WmiNamespace, WmiQuery);
            using var results = searcher.Get();

            // 优先选 "CPU Package" 温度，再选 Tctl/Tdie，再选第一个 CPU 温度
            string? bestName = null;
            double bestValue = -1;

            foreach (ManagementObject obj in results)
            {
                var sensorType = obj["SensorType"]?.ToString() ?? "";
                if (!sensorType.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parent = obj["Parent"]?.ToString() ?? "";
                if (!parent.Contains("CPU", StringComparison.OrdinalIgnoreCase) &&
                    !parent.Contains("Processor", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = obj["Name"]?.ToString() ?? "";
                var val = Convert.ToDouble(obj["Value"]);

                if (val <= 0 || val > 150) continue; // sanity

                // CPU Package 最优先
                if (name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                {
                    return val;
                }

                // Tctl/Tdie 次优先
                if (name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                {
                    bestName = name;
                    bestValue = val;
                    continue;
                }

                // 记录第一个有效值作为兜底
                if (bestValue < 0)
                {
                    bestName = name;
                    bestValue = val;
                }
            }

            return bestValue;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] LHM WMI 读取失败: {ex.Message}");
            return -1;
        }
    }

    /// <summary>探测 WMI 命名空间是否可达</summary>
    private static bool ProbeWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(WmiNamespace,
                "SELECT Name FROM Sensor");
            using var results = searcher.Get();
            // 枚举至少一个结果来验证命名空间和类存在
            foreach (ManagementBaseObject _ in results)
                return true;
            return true; // 命名空间可达但无传感器（LHM 刚启动）
        }
        catch (ManagementException)
        {
            // 命名空间不存在 = LHM 未安装或 WMI Provider 未启用
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] LHM WMI 探测异常: {ex.Message}");
            return false;
        }
    }
}
