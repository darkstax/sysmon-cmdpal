// Copyright (c) 2026 SysMonCmdPal
// CPU 温度读取器 — 商店安全版回退链，所有数据源均为用户态。
// 用户可通过设置自定义数据源优先级链:
//   "HWiNFO"      = HWiNFO 共享内存 (最精准, 需 HWiNFO 运行中)
//   "ThermalZone" = Windows ACPI 热区温度 (PerformanceCounter)
//   "ADL"         = AMD ADL PMLOG CPU sensor 32 (仅 AMD CPU)
//   "LHM"         = LibreHardwareMonitor NuGet (免驱动, 精度较低)
//
// 注:
//   - HWiNFO 共享内存提供精确 Tctl/Tdie
//   - ADL PMLOG sensor 32 读数比 Tctl/Tdie 偏低 ~5°C，已应用校准偏移
//   - Thermal Zone 无需管理员权限，始终可用

using System;
using System.Linq;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

public readonly record struct CpuTempResult(double Temperature, string Source)
{
    public bool IsValid => Temperature > 0;
    public static CpuTempResult None => new(-1, "无");
}

internal static class CpuSensorReader
{
    /// <summary>读取 CPU 温度，优先检查 Broker COM 推送缓存</summary>
    public static CpuTempResult Read()
    {
        // 优先使用 Broker COM 推送的数据（最精准）
        var brokerSnap = BrokerPushReceiver.Instance.Snapshot;
        if (brokerSnap.IsFresh && brokerSnap.CpuTemperature > 0)
        {
            return new CpuTempResult(brokerSnap.CpuTemperature, brokerSnap.CpuSource);
        }

        // 回退到配置的传感器链
        var config = SensorChainConfig.Load();

        foreach (var source in config.CpuChain)
        {
            var result = ReadFromSource(source);
            if (result.IsValid)
            {
                SensorLogger.ForceLog($"CPU: [{source}] {result.Temperature:F1}°C ({result.Source})");
                return result;
            }
        }

        SensorLogger.ForceLog("CPU: 所有数据源不可用");
        return CpuTempResult.None;
    }

    /// <summary>从指定数据源读取 CPU 温度</summary>
    private static CpuTempResult ReadFromSource(string source)
    {
        return source switch
        {
            "HWiNFO" => ReadFromHwInfo(),
            "ThermalZone" => ReadFromThermalZone(),
            "ADL" => ReadFromAdl(),
            "LHM" => ReadFromLhm(),
            _ => CpuTempResult.None,
        };
    }

    private static CpuTempResult ReadFromHwInfo()
    {
        try
        {
            int temp = AmdTempReader.Instance.ReadCpuTempViaHwInfoOnly();
            if (temp > 0)
                return new CpuTempResult(temp, "HWiNFO");
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"CPU HWiNFO 异常: {ex.Message}");
        }
        return CpuTempResult.None;
    }

    private static CpuTempResult ReadFromThermalZone()
    {
        try
        {
            if (!ThermalZoneReader.Instance.IsAvailable)
                return CpuTempResult.None;

            double temp = ThermalZoneReader.Instance.ReadCpuTemp();
            if (temp > 0)
                return new CpuTempResult(temp, "ThermalZone");
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"CPU Thermal Zone 异常: {ex.Message}");
        }
        return CpuTempResult.None;
    }

    private static CpuTempResult ReadFromAdl()
    {
        try
        {
            int rawTemp = AmdTempReader.Instance.ReadCpuTempViaAdlOnly();
            if (rawTemp > 0)
            {
                // ADL sensor 32 读取 SoC 域温度，通常比 Tctl/Tdie 偏低 ~5°C
                const double adlCalibrationOffset = 5.0;
                double calibrated = rawTemp + adlCalibrationOffset;
                return new CpuTempResult(calibrated, $"ADL+{adlCalibrationOffset:F0}°C");
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"CPU ADL 异常: {ex.Message}");
        }
        return CpuTempResult.None;
    }

    private static CpuTempResult ReadFromLhm()
    {
        try
        {
            if (!LhmSensorService.Instance.IsAvailable)
                return CpuTempResult.None;

            LhmSensorService.Instance.Refresh();

            if (!LhmSensorService.Instance.Catalog.TryGetValue(SensorCategory.CpuTemp, out var cpuTemps) ||
                cpuTemps.Count == 0)
                return CpuTempResult.None;

            var pkg = cpuTemps.FirstOrDefault(r =>
                r.SensorName?.Contains("Package", StringComparison.OrdinalIgnoreCase) == true ||
                r.SensorName?.Contains("Tctl", StringComparison.OrdinalIgnoreCase) == true ||
                r.SensorName?.Contains("Tdie", StringComparison.OrdinalIgnoreCase) == true);

            if (pkg.SensorName != null && pkg.Value > 0)
                return new CpuTempResult(pkg.Value, "LHM");

            if (cpuTemps[0].Value > 0)
                return new CpuTempResult(cpuTemps[0].Value, "LHM");
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"CPU LHM 异常: {ex.Message}");
        }
        return CpuTempResult.None;
    }
}
