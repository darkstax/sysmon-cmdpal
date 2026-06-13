// Copyright (c) 2026 SysMonCmdPal
// GPU 读取器 — 用户可配置回退链。
//
// 数据源:
//   "Broker"      = Broker 命名管道 (最优先，数据最全)
//   "ThermalZone" = ACPI 热区温度 (PerformanceCounter，仅温度)
//   "HWiNFO"      = HWiNFO 共享内存 (最后兜底)

using System;
using System.Collections.Generic;
using System.Linq;

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

                // 筛选后为空，继续下一个数据源
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
            "Broker" => ReadFromBroker(),
            "ThermalZone" => ReadFromThermalZone(),
            "HWiNFO" => ReadFromHwInfo(),
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
                // 仅保留独立显卡（过滤掉 Intel/AMD 集显关键词）
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
                // 如果没有独立显卡，回退到全部
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

    /// <summary>
    /// 智能筛选: 部分卡有 3D 活动而另一部分没有 → 只返回有活动的;
    /// 都有或都没有 → 全部返回。
    /// </summary>
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
    // Phase A: Broker 命名管道
    // ================================================================

    /// <summary>通过 Broker 命名管道 (cmd=4) 读取所有 GPU 数据。LHM 补充缺失的温度/使用率/显存。</summary>
    private static List<GpuResult>? ReadFromBroker()
    {
        try
        {
            if (!BrokerClient.Instance.IsAvailable)
                return null;

            var gpus = BrokerClient.Instance.ReadAllGpus();
            if (gpus == null || gpus.Count == 0)
                return null;

            var results = gpus.Select(g => new GpuResult(
                g.Name, g.UsagePercent, g.Temperature,
                g.MemoryUsedMB, g.MemoryTotalMB, "Broker")).ToList();

            // LHM 补充缺失数据（NVAPI 只拿到名称，温度/使用率/显存需要 LHM 补）
            SupplementFromLhm(results);

            return results;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"GPU Broker 异常: {ex.Message}");
            return null;
        }
    }

    // ================================================================
    // LHM 补充（填充 Broker 返回的 GPU 缺失数据）
    // ================================================================

    private static void SupplementFromLhm(List<GpuResult> gpus)
    {
        if (!LhmSensorService.Instance.IsAvailable) return;
        LhmSensorService.Instance.Refresh();

        for (int i = 0; i < gpus.Count; i++)
        {
            var g = gpus[i];
            if (g.Temperature > 0 && g.UsagePercent >= 0 && g.MemoryTotalMB > 0)
                continue; // 数据已完整

            string src = g.Source == "Broker" ? "Broker+LHM" : g.Source;
            gpus[i] = new GpuResult(g.Name,
                g.UsagePercent < 0 ? ReadLhmGpuField(g.Name, SensorCategory.GpuLoad, "Core") : g.UsagePercent,
                g.Temperature <= 0 ? ReadLhmGpuField(g.Name, SensorCategory.GpuTemp, "Core") : g.Temperature,
                g.MemoryTotalMB <= 0 ? ReadLhmGpuMemory(g.Name, true) : g.MemoryTotalMB,
                g.MemoryTotalMB <= 0 ? ReadLhmGpuMemory(g.Name, false) : g.MemoryUsedMB,
                src);
        }
    }

    private static double ReadLhmGpuField(string gpuName, SensorCategory cat, string sensorPattern)
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

    private static double ReadLhmGpuMemory(string gpuName, bool total)
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

    /// <summary>通过 ACPI 热区 (PerformanceCounter) 读取 GPU 近似温度。</summary>
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
    // Phase C: HWiNFO 共享内存（最后兜底）
    // ================================================================

    /// <summary>通过 HWiNFO 共享内存读取 GPU 温度（最后兜底）。</summary>
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
}
