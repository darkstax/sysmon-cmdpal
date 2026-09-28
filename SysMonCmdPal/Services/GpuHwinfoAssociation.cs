// Copyright (c) 2026 SysMonCmdPal
// T2-1（P1-1）：HWiNFO GPU 归属由「标签出现顺序」改为 **units 分区归属**。
//
// 如实措辞 —— 本实现**不是** PNPDeviceID 硬关联：
//   本机逐字节解码实测，HWiNFO 共享内存记录内不存在任何 PNPDeviceID/总线/序列号类
//   可连接键（unit=392B{id, instance, 名称×3 副本}；entry=460B{type, sensor_index,
//   entryId, label×2~3, 单位串@268, value/min/max/avg}。entry@268 携带的是单位文本
//   （'MB'/'%'/'°C'），**不是** unit id ⇒ entry→unit 的唯一通路是偏移 4 的 sensor_index）。
//   ⇒ 实现口径 = units 归属（sensor_index）+ 尽力而为的名称/角色启发式。
//     归属主依据是 unit 分区本身：每张卡只从**自己 unit** 的条目按精确标签取
//     温度/负载/显存。即使名称启发式全部落空，也不会再现「温度张冠李戴」。
//
// 取代的旧缺陷（均有本机活体证据）：
//   - 旧码把「第 1 个 GPU Temperature 给集显、第 2 个给独显」按出现顺序硬分配；
//   - 旧码依赖的 "GPU Memory Allocated/Available" 标签在本机不存在 ⇒ 独显显存判据恒 false；
//   - 跨 GPU 全局 Contains 匹配 "GPU Memory Usage" 会把集显读数记到独显头上；
//   - 本机 APU 集显 unit 也携带 "GPU D3D Memory Dedicated"（WDDM 预留 459.92MB），
//     故「有 dedicated 读数 = 独显」会误判 ⇒ dedicated 信号只在超过旧阈值(1024MB)
//     时才参与独显判定。

using System;
using System.Collections.Generic;
using System.Linq;

namespace SysMonCmdPal;

/// <summary>单个 GPU unit 从自身条目提取的信号集（-1/0 = 无读数，不造假）。</summary>
internal struct HwinfoGpuSignals
{
    public double Temperature;        // type=1 label=="GPU Temperature"
    public double CoreLoad;           // type=7 label=="GPU Core Load"
    public double Utilization;        // type=7 label=="GPU Utilization"
    public double D3DUsage;           // type=7 label=="GPU D3D Usage"
    public double MemoryUsagePercent; // type=7 label=="GPU Memory Usage"
    public double DedicatedMB;        // type=8 label=="GPU D3D Memory Dedicated"
    public double DynamicMB;          // type=8 label=="GPU D3D Memory Dynamic"

    public readonly bool HasLoadSignal => CoreLoad >= 0 || Utilization >= 0 || D3DUsage >= 0;
    public readonly bool HasVramSignal => DedicatedMB >= 0 || DynamicMB >= 0;
    public readonly bool HasAnySignal => HasLoadSignal || HasVramSignal || Temperature > 0;

    /// <summary>独立显存总量（Dedicated+Dynamic；任一缺失则只取在场的那个，都缺失为 0）。</summary>
    public readonly double VramTotalMB =>
        DedicatedMB >= 0 && DynamicMB >= 0 ? DedicatedMB + DynamicMB
      : DedicatedMB >= 0 ? DedicatedMB
      : DynamicMB >= 0 ? DynamicMB
      : 0;
}

/// <summary>一个被判定携带 GPU 信号的 HWiNFO unit 分组。</summary>
internal sealed class HwinfoGpuUnit
{
    public required int UnitIndex { get; init; }
    public required string UnitName { get; init; }
    public required GpuRoleHint Role { get; init; }
    public required HwinfoGpuSignals Signals { get; init; }
}

/// <summary>归属结果 + 可判定性诊断。</summary>
internal sealed class GpuHwinfoAssociationResult
{
    public required List<GpuResult> Results { get; init; }
    public int UnitsConsidered { get; init; }
    public int UnitsMatchedIdentity { get; init; }
    public bool UnitsParseAvailable { get; init; }
    public string Note { get; init; } = "";
}

internal static class GpuHwinfoAssociation
{
    // 精确标签（整串相等、大小写不敏感）—— 本机活体解码所见写法。
    private const string TempLabel = "GPU Temperature";
    private const string CoreLoadLabel = "GPU Core Load";
    private const string UtilizationLabel = "GPU Utilization";
    private const string D3DUsageLabel = "GPU D3D Usage";
    private const string MemUsageLabel = "GPU Memory Usage";
    private const string MemDedicatedLabel = "GPU D3D Memory Dedicated";
    private const string MemDynamicLabel = "GPU D3D Memory Dynamic";

    private const int TypeTemperature = 1;
    private const int TypeUsage = 7;
    private const int TypeOther = 8;

    /// <summary>旧码沿用至今的"独显显存"阈值（本机 APU WDDM 预留远低于此值）。</summary>
    internal const double DiscreteVramThresholdMB = 1024;

    /// <summary>
    /// 主入口。identities 来自 <see cref="GpuIdentityService"/>（SetupDi，零 COM/零 WMI）；
    /// 身份表为空只影响显示名与名称判据，**不影响归属正确性**（读数仍锁在各自 unit 内）。
    /// </summary>
    public static GpuHwinfoAssociationResult Associate(
        HwinfoLayoutSnapshot snapshot,
        IReadOnlyList<GpuDeviceIdentity> identities) =>
        snapshot.UnitsAvailable
            ? AssociateByUnits(snapshot, identities)
            : AssociateWithoutUnits(snapshot, identities);

    // ==================== units 可用：正常路径 ====================

    private static GpuHwinfoAssociationResult AssociateByUnits(
        HwinfoLayoutSnapshot snapshot,
        IReadOnlyList<GpuDeviceIdentity> identities)
    {
        var gpuUnits = new List<HwinfoGpuUnit>();
        foreach (var unit in snapshot.Units)
        {
            var s = EmptySignals();
            foreach (var e in snapshot.Entries)
            {
                if (e.SensorIndex != unit.Index) continue;   // ← units 归属（取代出现顺序）
                ConsumeSignal(e, ref s);
            }
            if (!s.HasAnySignal) continue;                   // 该 unit 不携带 GPU 信号面
            gpuUnits.Add(new HwinfoGpuUnit
            {
                UnitIndex = unit.Index,
                UnitName = unit.Name ?? "",
                Role = GpuIdentityService.UnitNameRole(unit.Name),
                Signals = s,
            });
        }

        // 保守护栏（保持旧语义「不凭温度造第二块 GPU」）：
        // 若整个快照里没有任何负载/显存/角色证据，只有温度读数，说明我们无从判断这些
        // 分组是否都是真 GPU ⇒ 至多产出温度最高的一张，其余抑制并记日志。
        // 只要存在任一负载/显存/角色证据，即按 units 归属逐张产出（每张只带自己 unit 的读数）。
        string suppressNote = "";
        bool anyEvidence = gpuUnits.Any(static u =>
            u.Role != GpuRoleHint.None || u.Signals.HasLoadSignal || u.Signals.HasVramSignal);
        if (!anyEvidence && gpuUnits.Count > 1)
        {
            int dropped = gpuUnits.Count - 1;
            gpuUnits = [gpuUnits.OrderByDescending(static u => u.Signals.Temperature).First()];
            suppressNote = $" 纯温度证据 ⇒ 抑制 {dropped} 个无负载/显存分组（不凭温度造第二块卡）";
        }

        var results = new List<GpuResult>(gpuUnits.Count);
        var taken = new HashSet<int>();
        int matched = 0;
        foreach (var g in gpuUnits)
        {
            var s = g.Signals;
            var identity = MatchIdentity(g, identities, taken);
            GpuDeviceIdentity? hit = identity;
            if (hit != null)
            {
                taken.Add(hit.Serial);
                matched++;
            }

            var kind = ResolveKind(g, hit);
            // 把本周期判定出的角色回写身份，使 UI 图标/主卡竞选也能吃到
            // HWiNFO 的 iGPU/dGPU 证据（OS 设备名不含型号时唯一可用信号）。
            if (hit != null && kind != GpuKind.Unknown)
                hit.HwinfoRole = kind == GpuKind.Integrated ? GpuRoleHint.Integrated : GpuRoleHint.Discrete;
            string name = hit?.Name ?? DisplayName(g.UnitName, g.Role);

            // 集显显存保持旧语义 = 0（共享系统内存，无独立 VRAM）：
            // 本机活体实测 APU unit 也携带 "GPU D3D Memory Dedicated"=459.92MB（WDDM 预留分段），
            // 若如实上报会在 UI 上显示成「0.4/0.4 GB 显存」这种误导读数。
            double memUsed = kind == GpuKind.Integrated ? 0
                : s.DedicatedMB >= 0 ? s.DedicatedMB : (s.DynamicMB >= 0 ? s.DynamicMB : 0);
            double memTotal = kind == GpuKind.Integrated ? 0 : s.VramTotalMB;

            results.Add(new GpuResult(
                name,
                PickUsage(g.Role, s),
                s.Temperature > 0 ? s.Temperature : -1,
                memUsed,
                memTotal,
                "HWiNFO",
                kind));
        }

        // 稳定顺序：独显 → 集显 → 未知；组内「有负载优先、温度降序」
        //（保持旧「独显在前」约定：消费端用 gpuResults[0].Source 做 BackendNote）
        results.Sort(static (a, b) =>
        {
            int c = KindRank(a.Kind).CompareTo(KindRank(b.Kind));
            if (c != 0) return c;
            c = (b.UsagePercent > 0 ? 1 : 0).CompareTo(a.UsagePercent > 0 ? 1 : 0);
            if (c != 0) return c;
            return b.Temperature.CompareTo(a.Temperature);
        });

        return new GpuHwinfoAssociationResult
        {
            Results = results,
            UnitsConsidered = gpuUnits.Count,
            UnitsMatchedIdentity = matched,
            UnitsParseAvailable = true,
            Note = $"units 归属: gpuUnits={gpuUnits.Count} identityMatched={matched}{suppressNote}",
        };
    }

    private static int KindRank(GpuKind k) => k switch
    {
        GpuKind.Discrete => 0,
        GpuKind.Integrated => 1,
        _ => 2,
    };

    private static HwinfoGpuSignals EmptySignals() => new()
    {
        Temperature = 0, CoreLoad = -1, Utilization = -1, D3DUsage = -1,
        MemoryUsagePercent = -1, DedicatedMB = -1, DynamicMB = -1,
    };

    /// <summary>把一条 entry 按精确标签并入信号集（只在调用方限定 sensor_index 归属后调用）。</summary>
    private static bool ConsumeSignal(HwinfoEntryDescriptor e, ref HwinfoGpuSignals s)
    {
        switch (e.Type)
        {
            case TypeTemperature:
                if (!LabelEquals(e.Label, TempLabel)) return false;
                if (e.Value <= 0 || e.Value > 150) return false;     // 与既有读取端同范围
                s.Temperature = e.Value;
                return true;
            case TypeUsage:
                if (e.Value < 0 || e.Value > 100) return false;
                if (LabelEquals(e.Label, CoreLoadLabel)) { s.CoreLoad = e.Value; return true; }
                if (LabelEquals(e.Label, UtilizationLabel)) { s.Utilization = e.Value; return true; }
                if (LabelEquals(e.Label, D3DUsageLabel)) { s.D3DUsage = e.Value; return true; }
                if (LabelEquals(e.Label, MemUsageLabel)) { s.MemoryUsagePercent = e.Value; return true; }
                return false;
            case TypeOther:
                if (e.Value < 0) return false;
                if (LabelEquals(e.Label, MemDedicatedLabel)) { s.DedicatedMB = e.Value; return true; }
                if (LabelEquals(e.Label, MemDynamicLabel)) { s.DynamicMB = e.Value; return true; }
                return false;
            default:
                return false;
        }
    }

    private static bool LabelEquals(string? label, string expected) =>
        !string.IsNullOrWhiteSpace(label) &&
        string.Equals(label.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>负载读数选择：独显优先 Core Load，集显优先 Utilization（旧语义保留，但按角色而非全局标签）。</summary>
    private static double PickUsage(GpuRoleHint role, in HwinfoGpuSignals s) => role switch
    {
        GpuRoleHint.Discrete => FirstNonNegative(s.CoreLoad, s.D3DUsage, s.Utilization),
        GpuRoleHint.Integrated => FirstNonNegative(s.Utilization, s.D3DUsage, s.CoreLoad),
        _ => FirstNonNegative(s.CoreLoad, s.Utilization, s.D3DUsage),
    };

    private static double FirstNonNegative(double a, double b, double c)
    {
        if (a >= 0) return a;
        if (b >= 0) return b;
        if (c >= 0) return c;
        return -1;
    }

    // ============ 名称/角色启发式（三级：unit 名→身份表；不确定即保守不猜） ============

    /// <summary>
    /// ① 全名（或剥掉 'iGPU [#n]: ' 前缀后的名）与身份表某项相等且未被消费 → 直接归属；
    /// ② unit 名厂商 token（AMD/NVIDIA/Intel/Qualcomm，映射 VEN_xxxx）唯一候选 → 归属；
    /// ③ 多候选时仅当"其中恰一项的类别判据与本次角色一致"才敢绑定，否则保守返回 null（不猜）。
    /// </summary>
    private static GpuDeviceIdentity? MatchIdentity(
        HwinfoGpuUnit g,
        IReadOnlyList<GpuDeviceIdentity> identities,
        HashSet<int> taken)
    {
        if (identities.Count == 0) return null;

        string unitName = (g.UnitName ?? "").Trim();
        string bare = StripRolePrefix(unitName);

        foreach (var id in identities)
        {
            if (taken.Contains(id.Serial)) continue;
            if (string.Equals(id.Name, unitName, StringComparison.OrdinalIgnoreCase) ||
                (bare.Length > 0 && string.Equals(id.Name, bare, StringComparison.OrdinalIgnoreCase)))
                return id;
        }

        string? vendor = GpuIdentityService.UnitNameVendorToken(unitName);
        if (vendor == null) return null;

        var candidates = new List<GpuDeviceIdentity>();
        foreach (var id in identities)
        {
            if (taken.Contains(id.Serial)) continue;
            if (string.Equals(id.VendorToken, vendor, StringComparison.OrdinalIgnoreCase))
                candidates.Add(id);
        }

        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1 && g.Role != GpuRoleHint.None)
        {
            // 用 HWiNFO 自带角色 + 身份名称判据消歧；仍不唯一则不猜
            var filtered = new List<GpuDeviceIdentity>();
            foreach (var id in candidates)
                if (GpuIdentityService.ClassifyName(id.Name, 0) ==
                    (g.Role == GpuRoleHint.Integrated ? GpuKind.Integrated : GpuKind.Discrete))
                    filtered.Add(id);
            if (filtered.Count == 1) return filtered[0];
        }
        return null;
    }

    /// <summary>
    /// 类别判定优先级：① HWiNFO 自带 iGPU/dGPU 角色（COM-free，本机实测稳定）
    /// ② 匹配到的身份名判据 ③ 显存读数超旧阈值（防 APU WDDM 预留误判）④ Unknown。
    /// </summary>
    private static GpuKind ResolveKind(HwinfoGpuUnit g, GpuDeviceIdentity? identity)
    {
        if (g.Role != GpuRoleHint.None)
            return g.Role == GpuRoleHint.Integrated ? GpuKind.Integrated : GpuKind.Discrete;

        if (identity != null)
        {
            var byName = GpuIdentityService.ClassifyName(identity.Name, 0);
            if (byName != GpuKind.Unknown) return byName;
        }

        if (g.Signals.VramTotalMB > DiscreteVramThresholdMB) return GpuKind.Discrete;
        return GpuKind.Unknown;
    }

    /// <summary>剥掉 'iGPU [#1]: ' / 'dGPU [#0]: ' 角色前缀取可读名。</summary>
    internal static string StripRolePrefix(string unitName)
    {
        if (string.IsNullOrEmpty(unitName)) return "";
        int colon = unitName.IndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && colon <= 12) return unitName[(colon + 2)..].Trim();
        return unitName.Trim();
    }

    private static string DisplayName(string unitName, GpuRoleHint role)
    {
        string bare = StripRolePrefix(unitName);
        if (!string.IsNullOrWhiteSpace(bare)) return bare;
        return role == GpuRoleHint.Integrated ? "iGPU" : "GPU";
    }

    // ==================== units 面不可用：保守降级 ====================

    /// <summary>
    /// units 解析面被 fail-fast 禁用时的兜底：只有当 GPU 信号标签在全局**唯一**出现
    /// 才能确定"只有一块 GPU"；任何重复 ⇒ 无从归属 ⇒ 保守返回空列表
    /// （退回下一级 D3DKMT/PDH），**绝不按出现顺序猜归属**（即不复活 T2-1 要消灭的旧缺陷）。
    /// </summary>
    private static GpuHwinfoAssociationResult AssociateWithoutUnits(
        HwinfoLayoutSnapshot snapshot,
        IReadOnlyList<GpuDeviceIdentity> identities)
    {
        var s = EmptySignals();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool duplicate = false;
        int hits = 0;

        foreach (var e in snapshot.Entries)
        {
            if (!ConsumeSignal(e, ref s)) continue;
            hits++;
            if (!seen.Add(NormalizeLabel(e.Label))) { duplicate = true; break; }
        }

        if (duplicate)
        {
            return new GpuHwinfoAssociationResult
            {
                Results = [],
                UnitsConsidered = 0,
                UnitsMatchedIdentity = 0,
                UnitsParseAvailable = false,
                Note = "GPU 标签重复且 units 面不可用 ⇒ 无法归属，保守放弃 HWiNFO 层（不猜顺序）",
            };
        }
        if (hits == 0)
        {
            return new GpuHwinfoAssociationResult
            {
                Results = [],
                UnitsConsidered = 0,
                UnitsMatchedIdentity = 0,
                UnitsParseAvailable = false,
                Note = "units 面不可用且无 GPU 信号",
            };
        }

        var unit = new HwinfoGpuUnit { UnitIndex = -1, UnitName = "", Role = GpuRoleHint.None, Signals = s };
        var identity = MatchIdentity(unit, identities, new HashSet<int>());
        var kind = ResolveKind(unit, identity);
        var result = new GpuResult(
            identity?.Name ?? "GPU",
            PickUsage(GpuRoleHint.None, s),
            s.Temperature > 0 ? s.Temperature : -1,
            s.DedicatedMB >= 0 ? s.DedicatedMB : 0,
            s.VramTotalMB,
            "HWiNFO",
            kind);
        return new GpuHwinfoAssociationResult
        {
            Results = [result],
            UnitsConsidered = 1,
            UnitsMatchedIdentity = identity != null ? 1 : 0,
            UnitsParseAvailable = false,
            Note = "units 面不可用，GPU 标签全局唯一 ⇒ 保守判定单卡",
        };
    }

    private static string NormalizeLabel(string? label)
    {
        foreach (var known in new[] { TempLabel, CoreLoadLabel, UtilizationLabel, D3DUsageLabel, MemUsageLabel, MemDedicatedLabel, MemDynamicLabel })
            if (LabelEquals(label, known)) return known;
        return (label ?? "").Trim().ToUpperInvariant();
    }
}
