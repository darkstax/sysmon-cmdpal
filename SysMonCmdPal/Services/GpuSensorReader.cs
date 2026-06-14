// Copyright (c) 2026 SysMonCmdPal
// GPU 读取器 — 商店安全版回退链，所有数据源均为用户态。
//
// 数据源:
//   "HWiNFO"      = HWiNFO 共享内存 (最后兜底)
//   "ThermalZone" = ACPI 热区温度 (PerformanceCounter，仅温度)
//   "ADL"         = AMD ADL GPU 数据 (用户态 DLL)
//   "LHM"         = LibreHardwareMonitor NuGet

using System;
using System.Collections.Generic;
using System.Linq;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

public readonly record struct GpuResult(
    string Name, double UsagePercent, double Temperature,
    double MemoryUsedMB, double MemoryTotalMB, string Source)
{
    public bool IsValid => !string.IsNullOrEmpty(Name);
    public static GpuResult None => new("", -1, -1, 0, 0, "无");
}

internal static class GpuSensorReader
{
    /// <summary>
    /// 枚举所有 GPU，按用户配置的传感器链遍历。
    /// 每个数据源返回后应用 GpuMode 筛选规则。
    /// </summary>
    public static List<GpuResult> ReadAll()
    {
        // 优先使用 Broker COM 推送的数据
        var brokerSnap = BrokerPushReceiver.Instance.Snapshot;
        if (brokerSnap.IsFresh && brokerSnap.Gpus.Count > 0)
        {
            return brokerSnap.Gpus.Values
                .Select(g => new GpuResult(g.Name, g.UsagePercent, g.Temperature,
                    g.MemoryUsedMB, g.MemoryTotalMB, "Broker"))
                .ToList();
        }

        // 回退到配置的传感器链
        var config = SensorChainConfig.Load();

        foreach (var source in config.GpuChain)
        {
            var result = ReadAllFromSource(source);
            if (result != null && result.Count > 0)
            {
                SensorLogger.ForceLog($"GPU: [{source}] {result.Count} GPUs");
                foreach (var g in result)
                    SensorLogger.ForceLog($"GPU[{source}]: {g.Name}, {g.UsagePercent:F0}%, {g.Temperature:F0}°C");

                // 应用 GpuMode 筛选
                var filtered = ApplyGpuModeFilter(result, config.GpuMode);
                if (filtered.Count > 0)
                    return filtered;

                SensorLogger.ForceLog($"GPU: [{source}] GpuMode 筛选后无 GPU，继续回退");
            }
        }

        SensorLogger.ForceLog("GPU: 所有数据源不可用");
        return [];
    }

    /// <summary>兼容单卡接口：返回最佳单张 GPU</summary>
    public static GpuResult Read()
    {
        var all = ReadAll();
        if (all.Count == 0) return GpuResult.None;
        return all.OrderByDescending(g => g.UsagePercent > 0 ? 1 : 0)
                  .ThenByDescending(g => g.Temperature)
                  .First();
    }

    /// <summary>从指定数据源读取 GPU 数据</summary>
    private static List<GpuResult>? ReadAllFromSource(string source)
    {
        return source switch
        {
            "HWiNFO" => ReadFromHwInfo(),
            "ThermalZone" => ReadFromThermalZone(),
            "ADL" => ReadFromAdl(),
            "LHM" => ReadFromLhm(),
            _ => null,
        };
    }

    /// <summary>根据 GpuMode 筛选 GPU 列表</summary>
    private static List<GpuResult> ApplyGpuModeFilter(List<GpuResult> gpus, GpuMode mode)
    {
        if (gpus.Count <= 1) return gpus;

        switch (mode)
        {
            case GpuMode.DedicatedOnly:
                var dedicated = gpus.Where(g =>
                    g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    g.Name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                    g.Name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                    g.Name.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase) ||
                    g.Name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                    g.Name.Contains("GTX", StringComparison.OrdinalIgnoreCase) ||
                    (g.Name.Contains("AMD", StringComparison.OrdinalIgnoreCase) &&
                     !g.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase) &&
                     !g.Name.Contains("Radeon(TM) 6", StringComparison.OrdinalIgnoreCase) &&
                     !g.Name.Contains("Radeon(TM) 7", StringComparison.OrdinalIgnoreCase) &&
                     !g.Name.Contains("Radeon(TM) 8", StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (dedicated.Count > 0)
                {
                    SensorLogger.ForceLog($"GPU GpuMode.DedicatedOnly: {gpus.Count} → {dedicated.Count}");
                    return dedicated;
                }
                return gpus;

            case GpuMode.Auto:
                return FilterBy3DActivity(gpus);

            case GpuMode.All:
            default:
                return gpus;
        }
    }

    // ================================================================
    // 3D 活跃度筛选
    // ================================================================

    private static List<GpuResult> FilterBy3DActivity(List<GpuResult> gpus)
    {
        if (gpus.Count <= 1) return gpus;

        var with3D = gpus.Where(g => g.UsagePercent > 0).ToList();
        var without3D = gpus.Where(g => g.UsagePercent <= 0).ToList();

        if (with3D.Count >= 1 && without3D.Count >= 1)
        {
            SensorLogger.ForceLog($"GPU 筛选: {with3D.Count} active (3D>0), {without3D.Count} idle → showing active");
            return with3D;
        }

        SensorLogger.ForceLog($"GPU 筛选: {gpus.Count} GPUs, 3D activity uniform → showing all");
        return gpus;
    }

    // ================================================================
    // Phase A: LHM NuGet — 嵌入式传感器库
    // ================================================================

    private static List<GpuResult>? ReadFromLhm()
    {
        try
        {
            if (!LhmSensorService.Instance.IsAvailable)
                return null;

            LhmSensorService.Instance.Refresh();

            var gpuNames = new HashSet<string>();
            var results = new List<GpuResult>();

            foreach (var reading in LhmSensorService.Instance.AllReadings)
            {
                if (reading.HardwareName == null) continue;

                bool isGpu = reading.Category is SensorCategory.GpuTemp or SensorCategory.GpuLoad
                    or SensorCategory.GpuClock or SensorCategory.GpuPower
                    or SensorCategory.GpuMemory or SensorCategory.GpuFan
                    or SensorCategory.GpuVoltage;

                if (!isGpu) continue;

                gpuNames.Add(reading.HardwareName);
            }

            foreach (var name in gpuNames)
            {
                double temp = ReadLhmField(name, SensorCategory.GpuTemp);
                double load = ReadLhmField(name, SensorCategory.GpuLoad);
                double memTotal = ReadLhmMemField(name, total: true);
                double memUsed = ReadLhmMemField(name, total: false);

                results.Add(new GpuResult(name, load, temp, memUsed, memTotal, "LHM"));
            }

            return results.Count > 0 ? results : null;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"GPU LHM 异常: {ex.Message}");
            return null;
        }
    }

    private static double ReadLhmField(string gpuName, SensorCategory cat)
    {
        try
        {
            if (!LhmSensorService.Instance.Catalog.TryGetValue(cat, out var list) || list.Count == 0)
                return -1;
            var match = list.FirstOrDefault(r =>
                r.HardwareName != null && gpuName.Contains(r.HardwareName, StringComparison.OrdinalIgnoreCase));
            if (match.SensorName != null)
                return match.Value;
            return list.FirstOrDefault(r => r.Value > 0).Value > 0
                ? list.First(r => r.Value > 0).Value : -1;
        }
        catch { return -1; }
    }

    private static double ReadLhmMemField(string gpuName, bool total)
    {
        try
        {
            if (!LhmSensorService.Instance.Catalog.TryGetValue(SensorCategory.GpuMemory, out var list) || list.Count == 0)
                return 0;
            var pattern = total ? "Total" : "Used";
            var match = list.FirstOrDefault(r =>
                r.HardwareName != null && gpuName.Contains(r.HardwareName, StringComparison.OrdinalIgnoreCase)
                && r.SensorName != null && r.SensorName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            return match.SensorName != null ? match.Value : 0;
        }
        catch { return 0; }
    }

    // ================================================================
    // Phase B: ACPI 热区温度
    // ================================================================

    private static List<GpuResult>? ReadFromThermalZone()
    {
        try
        {
            if (!ThermalZoneReader.Instance.IsAvailable)
                return null;

            double temp = ThermalZoneReader.Instance.ReadCpuTemp();
            if (temp > 0)
            {
                return
                [
                    new GpuResult("ACPI GPU", -1, temp, 0, 0, "ThermalZone"),
                ];
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"GPU ThermalZone 异常: {ex.Message}");
        }

        return null;
    }

    // ================================================================
    // Phase C: HWiNFO 共享内存
    // ================================================================

    private static List<GpuResult>? ReadFromHwInfo()
    {
        try
        {
            int temp = AmdTempReader.Instance.ReadGpuTempViaHwInfo();
            if (temp > 0)
            {
                return
                [
                    new GpuResult("HWiNFO GPU", -1, temp, 0, 0, "HWiNFO ⚠ 每12h重置"),
                ];
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"GPU HWiNFO 异常: {ex.Message}");
        }

        return null;
    }

    // ================================================================
    // Phase D: AMD ADL GPU
    // ================================================================

    private static List<GpuResult>? ReadFromAdl()
    {
        try
        {
            if (!AmdTempReader.Instance.IsAdlAvailable)
                return null;

            int temp = AmdTempReader.Instance.ReadGpuTempViaAdl();
            if (temp > 0)
            {
                return
                [
                    new GpuResult("AMD GPU (ADL)", -1, temp, 0, 0, "ADL"),
                ];
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"GPU ADL 异常: {ex.Message}");
        }

        return null;
    }
}
