// Copyright (c) 2026 SysMonCmdPal
// GPU 读取器 — 用户态多层回退
// 分型回退链（§3 约束 1）：GPU = Broker SHM → HWiNFO → D3DKMT → PDH。
// ThermalZone 有意不在 GPU 链中（ACPI 仅有 CPU 热区，冒充 GPU 温度会误导用户）。
// CPU 温度链见 CpuSensorReader（Broker → HWiNFO → ThermalZone）。

using System;
using System.Collections.Generic;
using System.Linq;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

/// <summary>
/// 单个 GPU 采集结果。
/// <paramref name="Kind"/> 来自单一身份来源 <see cref="GpuIdentityService"/>（P1-3），
/// 供主卡竞选与 UI 图标共用；默认 Unknown = 未判定。
/// </summary>
public readonly record struct GpuResult(
    string Name, double UsagePercent, double Temperature,
    double MemoryUsedMB, double MemoryTotalMB, string Source,
    GpuKind Kind = GpuKind.Unknown)
{
    public bool IsValid => !string.IsNullOrEmpty(Name);
    public static GpuResult None => new("", -1, -1, 0, 0, "无");
}

internal static class GpuSensorReader
{
    /// <summary>身份服务注入点（测试 fixture 用；生产走 <see cref="GpuIdentityService.Default"/>）。</summary>
    internal static GpuIdentityService Identities = GpuIdentityService.Default;

    public static List<GpuResult> ReadAll() => ReadAll(hwinfoOverride: null);

    /// <summary>
    /// 测试缝隙：注入已构造好的 HWiNFO 布局快照（buffer 驱动，不依赖活体 HWiNFO/COM/WMI）。
    /// 回退链层级顺序与生产完全一致。
    /// </summary>
    internal static List<GpuResult> ReadAll(HwinfoLayoutSnapshot? hwinfoOverride)
    {
        // 1. Broker 共享内存推送（最高精度）
        var broker = BrokerPushReceiver.Instance;
        if (broker.TryGetAvailableSnapshot(out var brokerSnap))
        {
            if (brokerSnap.Gpus.Count > 0)
            {
                return brokerSnap.Gpus
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => kvp.Value)
                    .Select(g => new GpuResult(g.Name, g.UsagePercent, g.Temperature,
                        g.MemoryUsedMB, g.MemoryTotalMB, "Broker",
                        Identities.Classify(new GpuInfo { Name = g.Name, MemoryTotalMB = g.MemoryTotalMB })))
                    .ToList();
            }
        }

        // 2. HWiNFO 共享内存（用户态，零 COM/零 WMI 路径可达）
        //    T2-1：按 units 分区归属（sensor_index），不再按标签出现顺序分配。
        try
        {
            var snapshot = hwinfoOverride;
            snapshot ??= HwinfoSharedMemoryReader.Instance.TryGetLayoutSnapshot();

            if (snapshot != null)
            {
                var associated = GpuHwinfoAssociation.Associate(snapshot, Identities.GetIdentities());
                if (associated.Results.Count > 0)
                {
                    if (!associated.UnitsParseAvailable)
                        SensorLogger.ForceLog(
                            $"GPU HWiNFO units 面不可用，保守降级: {associated.Note}");
                    return associated.Results;
                }
                SensorLogger.ForceLog(
                    $"GPU HWiNFO 可用但未读到可归属的 GPU 数据（units={associated.UnitsConsidered} matched={associated.UnitsMatchedIdentity} unitsFace={associated.UnitsParseAvailable}），继续回退");
            }
            else
            {
                SensorLogger.ForceLog("GPU HWiNFO 不可用（未连接/布局校验失败），回退下一级");
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"GPU HWiNFO 异常: {ex.GetType().Name}: {Trim(ex.Message)}");
        }

        // 3. D3DKMT API (用户态，无需管理员，无需第三方工具)
        //    gdi32.dll P/Invoke 读取 per-engine RunningTime，delta 计算利用率。
        //    仅提供 UsagePercent，温度/显存不可用。
        try
        {
            var d3dkmt = D3dkmtGpuReader.Instance;
            if (d3dkmt.IsAvailable)
            {
                var results = d3dkmt.ReadAll();
                if (results.Count > 0)
                {
                    SensorLogger.ForceLog("GPU: 使用 D3DKMT API (用户态, 无需管理员)");
                    return WithIdentity(results);
                }
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"GPU D3DKMT 异常: {ex.GetType().Name}: {Trim(ex.Message)}");
        }

        // 4. PDH PerformanceCounter (用户态，Windows 内置 GPU Engine 计数器)
        //    由 DxgKrnl 驱动发布，通过 perflib 读取。仅提供 UsagePercent。
        try
        {
            var pdh = PdhGpuReader.Instance;
            if (pdh.IsAvailable)
            {
                var results = pdh.ReadAll();
                if (results.Count > 0)
                {
                    SensorLogger.ForceLog("GPU: 使用 PDH PerformanceCounter (GPU Engine)");
                    return WithIdentity(results);
                }
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"GPU PDH 异常: {ex.GetType().Name}: {Trim(ex.Message)}");
        }

        SensorLogger.ForceLog("GPU: 所有数据源不可用");
        return [];
    }

    /// <summary>把结果列表的名称/显存交给单一身份源复判 Kind（P1-3 收敛）。</summary>
    private static List<GpuResult> WithIdentity(List<GpuResult> results)
    {
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            results[i] = r with { Kind = Identities.Classify(new GpuInfo { Name = r.Name, MemoryTotalMB = r.MemoryTotalMB }) };
        }
        return results;
    }

    private static string Trim(string s) => s.Length <= 160 ? s : s[..160] + "…";

    // ========================================================================
    // 历史备注（T2-1 / T2-3 前）：
    //   GPU 名称曾由 System.Management(WMI Win32_VideoController) 按 AdapterRAM 排名取得，
    //   温度/负载按 "GPU Temperature" 标签出现顺序硬分配给 iGPU/dGPU。
    //   两条路径均已废弃：WMI 在启用 trimming/AOT 的 Release 产物中整体不可用
    //   （BuiltInCOM 被特性开关禁用），AdapterRAM 亦有 4GB 截断；
    //   身份统一走 GpuIdentityService(SetupDi，零 COM)，归属统一走 units 分区
    //   （GpuHwinfoAssociation）。
    // ========================================================================
}
