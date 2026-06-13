// Copyright (c) 2026 SysMonCmdPal
// CPU 温度读取器 — 完整回退链，自包含于单个文件。
// 用户可通过设置自定义数据源优先级链:
//   "Broker"      = 高精度子链: Broker → PawnIO MSR → PawnIO SMU → LHM HTTP → ADL → LHM
//   "ThermalZone" = Windows ACPI 热区温度 (PerformanceCounter)
//   "HWiNFO"      = HWiNFO 共享内存
//
// 注:
//   - Broker = SysMonBroker 计划任务进程，通过命名管道提供精准 Tctl/Tdie
//   - PawnIO 需要管理员权限（SDDL 仅授予 SY/BA GENERIC_READ|WRITE）
//   - LHM HTTP = 外部 LHM 独立版 Web 服务器 (localhost:8085)
//   - Thermal Zone = Windows ACPI 热区温度（PerformanceCounter，无需管理员）
//   - ADL PMLOG sensor 32 读数比 Tctl/Tdie 偏低 ~5°C，已应用校准偏移

using System;
using System.Linq;

namespace SysMonCmdPal;

public readonly record struct CpuTempResult(double Temperature, string Source)
{
    public bool IsValid => Temperature > 0;
    public static CpuTempResult None => new(-1, "无");
}

internal static class CpuSensorReader
{
    /// <summary>读取 CPU 温度，按用户配置的传感器链遍历</summary>
    public static CpuTempResult Read()
    {
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
            "Broker" => ReadFromBrokerChain(),
            "ThermalZone" => ReadFromThermalZone(),
            "HWiNFO" => ReadFromHwInfo(),
            _ => CpuTempResult.None,
        };
    }

    /// <summary>
    /// Broker 子链: Broker 命名管道 → PawnIO MSR (Intel) → PawnIO SMU (AMD)
    /// → LHM HTTP → ADL PMLOG (AMD 校准) → LHM NuGet
    /// </summary>
    private static CpuTempResult ReadFromBrokerChain()
    {
        // Phase 0: Broker — 高精度模式，通过命名管道从管理员进程获取精准 Tctl/Tdie
        var broker = ReadFromBroker();
        if (broker.IsValid) return broker;

        // Phase 1: PawnIO — Intel MSR (所有 Intel CPU, 需要管理员)
        var pawnMsr = ReadFromPawnMsr();
        if (pawnMsr.IsValid) return pawnMsr;

        // Phase 2: PawnIO — AMD SMU (所有 AMD CPU, 需要管理员, 准确的 Tctl/Tdie)
        var pawnSmu = ReadFromPawnSmu();
        if (pawnSmu.IsValid) return pawnSmu;

        // Phase 3: LHM HTTP — 外部 LHM 独立版 Web 服务器（准确的 Tctl/Tdie）
        var lhmHttp = ReadFromLhmHttp();
        if (lhmHttp.IsValid) return lhmHttp;

        // Phase 4: AMD ADL PMLOG + 校准（CPU sensor 32，SoC 温度 + 5°C 偏移）
        var adlResult = ReadFromAdl();
        if (adlResult.IsValid) return adlResult;

        // Phase 5: LHM NuGet（嵌入式，AppContainer 下通常返回 0）
        var lhmResult = ReadFromLhm();
        if (lhmResult.IsValid) return lhmResult;

        return CpuTempResult.None;
    }

    private static CpuTempResult ReadFromBroker()
    {
        try
        {
            if (!BrokerClient.Instance.IsAvailable)
                return CpuTempResult.None;

            // AMD: 尝试 SMU Tctl/Tdie
            double amdTemp = BrokerClient.Instance.ReadAmdTctl();
            if (amdTemp > 0)
                return new CpuTempResult(amdTemp, "Broker_SMU");

            // Intel: 尝试 MSR Package Temp
            double intelTemp = BrokerClient.Instance.ReadIntelTemp();
            if (intelTemp > 0)
                return new CpuTempResult(intelTemp, "Broker_MSR");
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"CPU Broker 异常: {ex.Message}");
        }
        return CpuTempResult.None;
    }

    private static CpuTempResult ReadFromPawnMsr()
    {
        try
        {
            if (!IntelMsrReader.Instance.IsAvailable)
                return CpuTempResult.None;

            double temp = IntelMsrReader.Instance.ReadPackageTemp();
            if (temp > 0)
                return new CpuTempResult(temp, "PawnIO_MSR");
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"CPU PawnIO MSR 异常: {ex.Message}");
        }
        return CpuTempResult.None;
    }

    private static CpuTempResult ReadFromPawnSmu()
    {
        try
        {
            if (!AmdSmuReader.Instance.IsAvailable)
                return CpuTempResult.None;

            double temp = AmdSmuReader.Instance.ReadCpuTemp();
            if (temp > 0)
                return new CpuTempResult(temp, "PawnIO_SMU");
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"CPU PawnIO SMU 异常: {ex.Message}");
        }
        return CpuTempResult.None;
    }

    private static CpuTempResult ReadFromLhmHttp()
    {
        try
        {
            if (!LhmHttpReader.Instance.IsAvailable)
                return CpuTempResult.None;

            double temp = LhmHttpReader.Instance.ReadCpuTemp();
            if (temp > 0)
                return new CpuTempResult(temp, "LHM_HTTP");
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"CPU LHM HTTP 异常: {ex.Message}");
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
                // 应用校准偏移使其更接近真实 CPU 温度
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
}
