// Copyright (c) 2026 SysMonCmdPal
// AMD GPU 读取器 — 通过 ADL PMLOG (atiadlxx.dll) 读取 AMD GPU 温度/使用率。
// 纯用户态 DLL，MSIX 下完全可用。回退链位置: Phase 1 (AMD GPU 最高优先级)
// 支持多 GPU 枚举：遍历所有 ADL 适配器，按 3D 活跃度智能筛选。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace SysMonCmdPal;

/// <summary>
/// 通过 AMD ADL PMLOG 读取所有 AMD GPU 的温度和使用率。
/// 复用 AmdTempReader 的 ADL 初始化和 PMLOG 读取能力。
/// </summary>
internal sealed class AdlGpuReader
{
    public static AdlGpuReader Instance { get; } = new();

    public bool IsAvailable => AmdTempReader.Instance.IsAdlAvailable;

    // 缓存 WMI GPU 名称（只查一次）
    private string[]? _wmiGpuNames;

    private AdlGpuReader() { }

    /// <summary>
    /// 读取所有 ADL 适配器的 GPU 数据，返回 GpuResult 列表。
    /// 如果只有一张卡有 3D 负载（GFX Load > 0），只返回那张；
    /// 如果都有或都没有 3D 负载，返回所有有数据的卡。
    /// </summary>
    public List<GpuResult> ReadAllGpus()
    {
        if (!IsAvailable) return [];

        try
        {
            var adapters = AmdTempReader.Instance.ReadAllGpuAdapters();
            if (adapters.Count == 0) return [];

            // 获取 WMI GPU 名称用于标注
            var names = GetWmiGpuNames();

            // 转换为 GpuResult
            // 去重: 同一物理 GPU 的多个虚拟适配器（每个显示输出一个）只保留第一个。
            // 笔记本电脑通常只有 1 个 AMD iGPU，桌面多卡场景 ADL 物理适配器在前。
            var amdNames = names.Where(n =>
                n.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("Radeon", StringComparison.OrdinalIgnoreCase)).ToArray();
            var results = new List<GpuResult>();
            bool taken = false;
            for (int i = 0; i < adapters.Count; i++)
            {
                var a = adapters[i];
                if (taken)
                {
                    SensorLogger.ForceLog($"ADL GPU: adapter {a.AdapterIndex} 跳过（已有物理适配器，此为虚拟适配器）");
                    continue;
                }

                // 第一个有数据的适配器 = 物理 GPU
                string name = amdNames.Length > 0 ? amdNames[0] : (names.Length > 0 ? names[0] : $"AMD GPU {i}");
                results.Add(new GpuResult(name, a.LoadPercent, a.Temperature, 0, 0, "ADL"));
                taken = true;
                SensorLogger.ForceLog($"ADL GPU: adapter {a.AdapterIndex} = {name} (物理适配器)");
            }

            if (results.Count <= 1) return results;

            // 智能筛选：按 3D 活跃度
            var with3D = results.Where(r => r.UsagePercent > 0).ToList();
            var without3D = results.Where(r => r.UsagePercent <= 0).ToList();

            if (with3D.Count >= 1 && without3D.Count >= 1)
            {
                // 部分卡有 3D 活动、部分没有 → 只返回有活动的
                SensorLogger.ForceLog($"ADL GPU: {with3D.Count} active (3D>0), {without3D.Count} idle — showing active only");
                return with3D;
            }

            // 都有或都没有 3D 活动 → 全部返回
            SensorLogger.ForceLog($"ADL GPU: all {results.Count} adapters returned, 3D activity uniform");
            return results;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"ADL GPU ReadAllGpus 异常: {ex.Message}");
        }
        return [];
    }

    /// <summary>单卡兼容接口：返回最佳单张 GPU 数据</summary>
    public GpuResult ReadGpu()
    {
        var gpus = ReadAllGpus();
        if (gpus.Count == 0) return GpuResult.None;
        // 优先返回有 3D 负载的，否则返回温度最高的
        return gpus.OrderByDescending(g => g.UsagePercent > 0 ? 1 : 0)
                   .ThenByDescending(g => g.Temperature)
                   .First();
    }

    /// <summary>通过 WMI 获取所有 GPU 名称（按设备枚举顺序）</summary>
    private string[] GetWmiGpuNames()
    {
        if (_wmiGpuNames != null) return _wmiGpuNames;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController");
            _wmiGpuNames = searcher.Get()
                .Cast<ManagementObject>()
                .Select(o => o["Name"]?.ToString() ?? "Unknown GPU")
                .ToArray();
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"WMI GPU 名称查询失败: {ex.Message}");
            _wmiGpuNames = [];
        }

        return _wmiGpuNames;
    }
}
