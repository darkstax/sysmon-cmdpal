// Copyright (c) 2026 SysMonCmdPal
// GPU 读取器 — v1.5 三层回退
// 回退链: Broker 共享内存 → HWiNFO 共享内存 → ACPI ThermalZone

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
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
    // GPU 名称缓存（从 Win32_VideoController 读取，按 AdapterRAM 从大到小排序）
    private static List<(string Name, ulong Ram)>? _gpuNamesByRamDesc;

    public static List<GpuResult> ReadAll()
    {
        // 1. Broker 共享内存推送（最高精度）
        var brokerSnap = BrokerPushReceiver.Instance.Snapshot;
        if (brokerSnap.IsFresh && brokerSnap.Gpus.Count > 0)
        {
            return brokerSnap.Gpus
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => kvp.Value)
                .Select(g => new GpuResult(g.Name, g.UsagePercent, g.Temperature,
                    g.MemoryUsedMB, g.MemoryTotalMB, "Broker"))
                .ToList();
        }

        // 2. HWiNFO 共享内存（用户态）
        var hwinfo = HwinfoSharedMemoryReader.Instance;
        if (hwinfo.IsAvailable)
        {
            try
            {
                var hwinfoResults = ReadGpusFromHwinfo(hwinfo);
                if (hwinfoResults.Count > 0)
                    return hwinfoResults;
                SensorLogger.ForceLog("GPU HWiNFO 可用但未读到 GPU 数据，继续回退");
            }
            catch (Exception ex)
            {
                SensorLogger.ForceLog($"GPU HWiNFO 异常: {ex.Message}");
            }
        }

        // 3. ACPI ThermalZone — CPU 热区温度不能代表 GPU 温度，移除误导性回退
        //    ThermalZone 只有 CPU 热区，没有独立 GPU 热区。把 CPU 温度标为 GPU 温度会误导用户。

        // 4. D3DKMT API (用户态，无需管理员，无需第三方工具)
        //    通过 gdi32.dll P/Invoke 读取 per-engine RunningTime，delta 计算 GPU 利用率
        //    仅提供 UsagePercent，温度/显存不可用
        try
        {
            var d3dkmt = D3dkmtGpuReader.Instance;
            if (d3dkmt.IsAvailable)
            {
                var results = d3dkmt.ReadAll();
                if (results.Count > 0)
                {
                    SensorLogger.ForceLog("GPU: 使用 D3DKMT API (用户态, 无需管理员)");
                    return results;
                }
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"GPU D3DKMT 异常: {ex.Message}");
        }

        // 5. PDH PerformanceCounter (用户态，Windows 内置 GPU Engine 计数器)
        //    由 DxgKrnl 驱动发布，通过 perflib 读取
        //    仅提供 UsagePercent，温度/显存不可用
        try
        {
            var pdh = PdhGpuReader.Instance;
            if (pdh.IsAvailable)
            {
                var results = pdh.ReadAll();
                if (results.Count > 0)
                {
                    SensorLogger.ForceLog("GPU: 使用 PDH PerformanceCounter (GPU Engine)");
                    return results;
                }
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"GPU PDH 异常: {ex.Message}");
        }

        SensorLogger.ForceLog("GPU: 所有数据源不可用");
        return [];
    }

    // ========================================================================
    // HWiNFO 回退：从共享内存读取 GPU 数据
    // ========================================================================
    //
    // HWiNFO 把所有 GPU 的传感器混在一个数组里，没有分组字段。
    // 判断集显/独显的唯一可靠标准：**有无独立显存**。
    //
    //   有 GPU Memory Allocated + GPU Memory Available → 独显（有独立 VRAM）
    //   无独立显存读数 → 集显（共享系统内存）
    //
    // 使用率标签也不同：
    //   独显（NVIDIA/AMD 独立卡）：GPU Core Load
    //   集显（AMD APU 集成）：GPU Utilization
    //
    // 温度：两组 GPU 各有一个 "GPU Temperature"，按出现顺序区分。
    //
    // GPU 名称从 Win32_VideoController 取，按 AdapterRAM 从大到小排序。
    // 独显（有独立显存）对应 RAM 最大的 WMI 条目，集显对应第二个。
    // 不靠名字判断集显/独显——Radeon 可能是 7900XT 独显，Intel 可能是 Arc。
    //
    // 返回顺序：独显在前，集显在后。

    private static List<GpuResult> ReadGpusFromHwinfo(HwinfoSharedMemoryReader hwinfo)
    {
        var results = new List<GpuResult>();
        EnsureGpuNames();

        // 一次读取所有 GPU 使用率（Core Load = 独显, Utilization = 集显）
        var (dgpuLoad, igpuLoad, _, _) = hwinfo.ReadGpuUsageAll();

        // 独显显存（GPU Memory Allocated + Available）
        var (dgpuMemUsed, dgpuMemAvail, dgpuMemTotal) = hwinfo.ReadGpuMemoryMB();

        // HWiNFO 只有温度标签顺序，没有可靠分组。双 GPU 时沿用旧约定：
        // 第 1 个温度给集显，第 2 个温度给独显；单 GPU 时第 1 个温度给唯一 GPU。
        var (temp0, _) = hwinfo.ReadGpuTemp(0);
        var (temp1, _) = hwinfo.ReadGpuTemp(1);

        bool hasDgpuSignals = dgpuMemTotal > 1024 || dgpuLoad >= 0;
        bool hasIgpuSignals = igpuLoad >= 0;
        bool dualGpu = hasDgpuSignals && hasIgpuSignals;

        double dgpuTemp = -1;
        double igpuTemp = -1;
        if (dualGpu)
        {
            igpuTemp = temp0 > 0 ? temp0 : -1;
            dgpuTemp = temp1 > 0 ? temp1 : -1;
        }
        else if (hasDgpuSignals)
        {
            dgpuTemp = temp0 > 0 ? temp0 : (temp1 > 0 ? temp1 : -1);
        }
        else if (hasIgpuSignals)
        {
            igpuTemp = temp0 > 0 ? temp0 : -1;
        }
        else if (temp0 > 0)
        {
            // 只有温度、没有负载/显存信号时，不凭温度制造第二块 GPU。
            hasDgpuSignals = true;
            dgpuTemp = temp0;
        }

        // ---- 独显/唯一 GPU（排第一）----
        if (hasDgpuSignals || dgpuTemp > 0)
        {
            string name = GetGpuNameByRamRank(0); // RAM 最大的 = 独显；单 GPU 时也是唯一 GPU
            if (string.IsNullOrEmpty(name)) name = "GPU";
            results.Add(new GpuResult(
                name,
                dgpuLoad >= 0 ? dgpuLoad : -1,
                dgpuTemp > 0 ? dgpuTemp : -1,
                dgpuMemUsed >= 0 ? dgpuMemUsed : 0,
                dgpuMemTotal >= 0 ? dgpuMemTotal : 0,
                "HWiNFO"));
        }

        // ---- 集显（排第二；单集显时也允许使用 rank 0 名称）----
        if (hasIgpuSignals || igpuTemp > 0)
        {
            string name = GetGpuNameByRamRank(results.Count > 0 ? 1 : 0);
            if (string.IsNullOrEmpty(name)) name = "iGPU";
            results.Add(new GpuResult(
                name,
                igpuLoad >= 0 ? igpuLoad : -1,
                igpuTemp > 0 ? igpuTemp : -1,
                0, 0,  // 集显共享系统内存，无独立显存
                "HWiNFO"));
        }

        return results;
    }

    // ========================================================================
    // WMI GPU 名称
    // ========================================================================

    /// <summary>
    /// 从 Win32_VideoController 读取 GPU 名称，按 AdapterRAM 从大到小排序（缓存）。
    /// 过滤掉虚拟显示设备（Virtual/Zako）。
    /// </summary>
    private static void EnsureGpuNames()
    {
        if (_gpuNamesByRamDesc != null) return;
        _gpuNamesByRamDesc = [];
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                try
                {
                    string name = obj["Name"] as string ?? "";
                    ulong ram = 0;
                    if (obj["AdapterRAM"] is uint r) ram = r;
                    if (!string.IsNullOrEmpty(name) &&
                        !name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("Zako", StringComparison.OrdinalIgnoreCase))
                    {
                        _gpuNamesByRamDesc.Add((name, ram));
                    }
                }
                finally { obj.Dispose(); }
            }
            // 按 RAM 从大到小排序——独显通常 RAM 更大
            _gpuNamesByRamDesc.Sort((a, b) => b.Ram.CompareTo(a.Ram));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GpuSensorReader] Win32_VideoController: {ex.Message}");
        }
    }

    /// <summary>
    /// 按 RAM 排名取 GPU 名称。rank=0 是 RAM 最大的（独显），rank=1 是第二（集显）。
    /// 不靠名字判断集显/独显——完全靠 AdapterRAM 大小排序。
    /// </summary>
    private static string GetGpuNameByRamRank(int rank)
    {
        if (_gpuNamesByRamDesc is null || _gpuNamesByRamDesc.Count == 0)
            return rank == 0 ? "GPU" : "iGPU";

        if (rank < _gpuNamesByRamDesc.Count)
            return _gpuNamesByRamDesc[rank].Name;

        // 只有 1 个 GPU 时，第二个返回空
        return "";
    }
}
