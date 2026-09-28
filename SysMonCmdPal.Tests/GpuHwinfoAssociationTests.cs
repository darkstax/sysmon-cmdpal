// Copyright (c) 2026 SysMonCmdPal
// T2-1（P1-1）：HWiNFO GPU 归属 = units 分区（sensor_index），取代「标签出现顺序」硬分配。
// 全部用例 buffer 驱动（HwinfoTestData），不依赖活体 HWiNFO / COM / WMI。
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SysMonCmdPal.Tests;

public sealed class GpuHwinfoAssociationTests
{
    private static IReadOnlyList<GpuDeviceIdentity> LocalIdentities() =>
        new GpuIdentityService(HwinfoTestData.FakeGpuIdentityProvider.LocalMachineLike()).GetIdentities();

    private static List<GpuResult> ReadAll(HwinfoLayoutSnapshot snapshot, GpuIdentityService? svc = null)
    {
        var previous = GpuSensorReader.Identities;
        GpuSensorReader.Identities = svc ?? new GpuIdentityService(HwinfoTestData.FakeGpuIdentityProvider.LocalMachineLike());
        try
        {
            return GpuSensorReader.ReadAll(snapshot);
        }
        finally
        {
            GpuSensorReader.Identities = previous;
        }
    }

    // ============ 验收 1：双 GPU 标签乱序/交换时，温度与负载归属仍正确 ============

    [Fact]
    public void DualGpu_EntryOrderSwapped_AttributionStaysPerUnit()
    {
        // 本机真实形状：两个 unit 各自都有一条同名 'GPU Temperature' 标签。
        // 旧实现按出现顺序把 temp0 判给集显、temp1 判给独显 —— 交换条目即张冠李戴。
        var units = new List<HwinfoUnitSpec>
        {
            HwinfoTestData.Unit(HwinfoTestData.IgpuUnitName),
            HwinfoTestData.Unit(HwinfoTestData.DgpuUnitName),
        };

        var igpuFirst = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(sensorIndex: 0, 64.8),
            HwinfoTestData.Load(0, 88, "GPU Utilization"),
            HwinfoTestData.Memory(0, 459.92, "GPU D3D Memory Dedicated"),
            HwinfoTestData.Temp(sensorIndex: 1, 57.23),
            HwinfoTestData.Load(1, 0, "GPU Core Load"),
            HwinfoTestData.Memory(1, 1700, "GPU D3D Memory Dedicated"),
            HwinfoTestData.Memory(1, 2705, "GPU D3D Memory Dynamic"),
        };

        // 同一批条目，仅把两个 unit 的条目整体对调（模拟 HWiNFO 顺序变化）
        var dgpuFirst = igpuFirst.OrderBy(e => e.SensorIndex == 0 ? 1 : 0).ToList();
        Assert.NotEqual(igpuFirst[0].SensorIndex, dgpuFirst[0].SensorIndex);

        var a = Associate(units, igpuFirst);
        var b = Associate(units, dgpuFirst);

        // 归属与顺序无关：每张卡拿到自己 unit 的读数
        Assert.Equal(a.Select(g => (g.Name, g.Temperature, g.UsagePercent)),
                     b.Select(g => (g.Name, g.Temperature, g.UsagePercent)));

        GpuResult igpu = Assert.Single(a, g => g.Name == HwinfoTestData.IgpuOsName);
        GpuResult dgpu = Assert.Single(a, g => g.Name == HwinfoTestData.DgpuOsName);

        Assert.Equal(64.8, igpu.Temperature, 3);
        Assert.Equal(88, igpu.UsagePercent, 3);
        Assert.Equal(57.23, dgpu.Temperature, 3);
        Assert.Equal(0, dgpu.UsagePercent, 3);
    }

    [Fact]
    public void DualGpu_UnitNameOrderSwapped_StillUsesCorrectLabelRole()
    {
        // unit 数组顺序颠倒（HWiNFO 枚举顺序变化）：角色取自 unit 名前缀，不取位置
        var unitsSwapped = new List<HwinfoUnitSpec>
        {
            HwinfoTestData.Unit(HwinfoTestData.DgpuUnitName),
            HwinfoTestData.Unit(HwinfoTestData.IgpuUnitName),
        };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 57.0), HwinfoTestData.Load(0, 42, "GPU Core Load"),
            HwinfoTestData.Temp(1, 64.0), HwinfoTestData.Load(1, 88, "GPU Utilization"),
        };

        var results = Associate(unitsSwapped, entries);

        GpuResult dgpu = Assert.Single(results, g => g.Kind == GpuKind.Discrete);
        GpuResult igpu = Assert.Single(results, g => g.Kind == GpuKind.Integrated);
        Assert.Equal(57.0, dgpu.Temperature, 3);
        Assert.Equal(42, dgpu.UsagePercent, 3);
        Assert.Equal(64.0, igpu.Temperature, 3);
        Assert.Equal(88, igpu.UsagePercent, 3);
        // 独显排在列表首位（消费端 gpuResults[0] 的旧约定保持）
        Assert.Equal(GpuKind.Discrete, results[0].Kind);
    }

    // ============ 验收 3：单 GPU / 纯集显 / 纯独显 / 三 GPU ============

    [Fact]
    public void SingleGpu_OnlyOneUnitWithSignals_ReturnsSingleResult()
    {
        var units = new List<HwinfoUnitSpec>
        {
            HwinfoTestData.Unit(HwinfoTestData.DgpuUnitName),
            HwinfoTestData.Unit("CPU [#0]: AMD Ryzen 7 7735H"),   // 非 GPU unit，必须被忽略
        };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 55.0), HwinfoTestData.Load(0, 33, "GPU Core Load"),
            HwinfoTestData.Temp(sensorIndex: 1, 71.0, label: "CPU Package"),  // 非 GPU 标签
        };

        List<GpuResult> results = Associate(units, entries);
        GpuResult only = Assert.Single(results);
        Assert.Equal(55.0, only.Temperature, 3);
        Assert.Equal(33, only.UsagePercent, 3);
    }

    [Fact]
    public void IntegratedOnly_NoVramSignals_MemoryFieldsStayZero()
    {
        var units = new List<HwinfoUnitSpec> { HwinfoTestData.Unit(HwinfoTestData.IgpuUnitName) };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 64.8), HwinfoTestData.Load(0, 88, "GPU Utilization"),
        };

        GpuResult igpu = Assert.Single(Associate(units, entries));
        Assert.Equal(GpuKind.Integrated, igpu.Kind);
        Assert.Equal(0, igpu.MemoryTotalMB, 3);
        Assert.Equal(0, igpu.MemoryUsedMB, 3);
    }

    [Fact]
    public void IntegratedUnit_WithWddmDedicatedSegment_ReportsNoVram()
    {
        // 本机活体证据：集显 unit 带 'GPU D3D Memory Dedicated'=459.92（WDDM 预留分段）。
        // 旧语义「集显共享系统内存，无独立显存 → 0」必须保持，否则 UI 会显示
        // 误导性的「0.4/0.4 GB 显存」。
        var units = new List<HwinfoUnitSpec> { HwinfoTestData.Unit(HwinfoTestData.IgpuUnitName) };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 64.8),
            HwinfoTestData.Load(0, 88, "GPU Utilization"),
            HwinfoTestData.Memory(0, 459.92, "GPU D3D Memory Dedicated"),
            HwinfoTestData.Memory(0, 2705.43, "GPU D3D Memory Dynamic"),
        };

        GpuResult igpu = Assert.Single(Associate(units, entries));
        Assert.Equal(GpuKind.Integrated, igpu.Kind);
        Assert.Equal(0, igpu.MemoryUsedMB, 3);
        Assert.Equal(0, igpu.MemoryTotalMB, 3);
        Assert.Equal(HwinfoTestData.IgpuOsName, igpu.Name);
    }

    [Fact]
    public void DiscreteOnly_VramFromDedicatedPlusDynamic()
    {
        var units = new List<HwinfoUnitSpec> { HwinfoTestData.Unit(HwinfoTestData.DgpuUnitName) };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 57.23), HwinfoTestData.Load(0, 12, "GPU Core Load"),
            HwinfoTestData.Memory(0, 1700, "GPU D3D Memory Dedicated"),
            HwinfoTestData.Memory(0, 2705, "GPU D3D Memory Dynamic"),
            HwinfoTestData.Load(0, 35.66, "GPU Memory Usage"),
        };

        GpuResult dgpu = Assert.Single(Associate(units, entries));
        Assert.Equal(GpuKind.Discrete, dgpu.Kind);
        Assert.Equal(1700, dgpu.MemoryUsedMB, 3);
        Assert.Equal(4405, dgpu.MemoryTotalMB, 3);
        Assert.Equal("HWiNFO", dgpu.Source);
    }

    [Fact]
    public void ThreeGpus_AllAttributedToOwnUnits()
    {
        var units = new List<HwinfoUnitSpec>
        {
            HwinfoTestData.Unit("dGPU [#0]: NVIDIA GeForce RTX 4060 Laptop"),
            HwinfoTestData.Unit("dGPU [#1]: AMD Radeon RX 7900 XTX"),
            HwinfoTestData.Unit(HwinfoTestData.IgpuUnitName),
        };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 51.0), HwinfoTestData.Load(0, 10, "GPU Core Load"),
            HwinfoTestData.Memory(0, 8000, "GPU D3D Memory Dedicated"),
            HwinfoTestData.Temp(1, 61.0), HwinfoTestData.Load(1, 20, "GPU Core Load"),
            HwinfoTestData.Memory(1, 24000, "GPU D3D Memory Dedicated"),
            HwinfoTestData.Temp(2, 71.0), HwinfoTestData.Load(2, 30, "GPU Utilization"),
        };

        List<GpuResult> results = Associate(units, entries);
        Assert.Equal(3, results.Count);
        Assert.All(results, g => Assert.Equal("HWiNFO", g.Source));
        // 独显在前（两张 NVIDIA/AMD dGPU 按温度降序），集显最后
        Assert.Equal(GpuKind.Integrated, results[^1].Kind);
        Assert.Equal(71.0, results[^1].Temperature, 3);
        Assert.Equal(2, results.Count(g => g.Kind == GpuKind.Discrete));
        // 每张卡拿到自己 unit 的读数，无跨 unit 串味
        Assert.Equal(new[] { 61.0, 51.0, 71.0 }, results.Select(g => g.Temperature).ToArray());
    }

    // ============ 验收 6：只有温度、无负载 ⇒ 不凭温度造第二块卡 ============

    [Fact]
    public void SingleTemperatureReading_NoLoadNoVram_YieldsOneCardNotFabricatedPair()
    {
        // 全链只有一条 'GPU Temperature'（无负载、无显存、无角色）：
        // 旧码在此分支会造出 "GPU" 一张；新实现同样只产出**一张**，绝不因顺序约定凭空配对第二块。
        var units = new List<HwinfoUnitSpec>
        {
            HwinfoTestData.Unit("Some Sensor Group"),
            HwinfoTestData.Unit("Another Group"),   // 该 unit 无任何 GPU 标签
        };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 60.0),
        };

        var result = GpuHwinfoAssociation.Associate(
            HwinfoTestData.Snapshot(units, entries), identities: []);

        GpuResult only = Assert.Single(result.Results);
        Assert.Equal(60.0, only.Temperature, 3);
        Assert.Equal(-1, only.UsagePercent, 3);     // 无负载证据 ⇒ 不猜
        Assert.Equal(GpuKind.Unknown, only.Kind);   // 不凭温度翻独显
        Assert.Equal(0, only.MemoryTotalMB, 3);
    }

    [Fact]
    public void TwoTemperatureOnlyUnits_DoNotFabricateSecondCard()
    {
        // 契约 fixture 清单第 6 项「只有温度无负载（不得凭温度造第二块卡）」：
        // 两个 unit 各有一条 'GPU Temperature'，但全链无任何负载/显存/角色证据
        // ⇒ 无从判断这些分组是否都是真 GPU，保守只产出温度最高的一张。
        // （旧码在此处会按出现顺序把 temp0 判给集显、temp1 判给独显 —— 正是 T2-1 要消灭的缺陷。）
        var units = new List<HwinfoUnitSpec>
        {
            HwinfoTestData.Unit("Sensor Group A"),
            HwinfoTestData.Unit("Sensor Group B"),
        };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 60.0),
            HwinfoTestData.Temp(1, 70.0),
        };

        var result = GpuHwinfoAssociation.Associate(
            HwinfoTestData.Snapshot(units, entries), identities: []);

        GpuResult only = Assert.Single(result.Results);
        Assert.Equal(70.0, only.Temperature, 3);      // 保留证据最强（最热）的一张
        Assert.Equal(GpuKind.Unknown, only.Kind);     // 不猜独显
        Assert.Equal(-1, only.UsagePercent, 3);
        Assert.Contains("不凭温度造第二块卡", result.Note);
    }

    [Fact]
    public void TemperaturePlusLoadOnOneUnit_BothUnitsAttributed_PerUnitReadings()
    {
        // 一旦存在任一负载证据，units 归属即逐张产出（每张只带自己 unit 的读数），
        // 且无负载证据的那张 UsagePercent 保持 -1（不借用别人的读数）。
        var units = new List<HwinfoUnitSpec>
        {
            HwinfoTestData.Unit("Sensor Group A"),
            HwinfoTestData.Unit("Sensor Group B"),
        };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 60.0),
            HwinfoTestData.Temp(1, 70.0),
            HwinfoTestData.Load(1, 25, "GPU Core Load"),
        };

        var result = GpuHwinfoAssociation.Associate(
            HwinfoTestData.Snapshot(units, entries), identities: []);

        Assert.Equal(2, result.Results.Count);
        Assert.DoesNotContain(result.Results, g => g.Kind == GpuKind.Integrated);
        GpuResult loaded = Assert.Single(result.Results, g => g.UsagePercent >= 0);
        Assert.Equal(70.0, loaded.Temperature, 3);    // 负载与温度同属 unit B
        Assert.Equal(25, loaded.UsagePercent, 3);

        GpuResult quiet = Assert.Single(result.Results, g => g.UsagePercent < 0);
        Assert.Equal(60.0, quiet.Temperature, 3);      // unit A 只拿到自己的温度
        Assert.Equal(-1, quiet.UsagePercent, 3);       // 绝不借用其它 unit 的负载读数
    }

    // ============ 验收 7：显存标签缺失 ⇒ 保守降级（不造假、不猜独显） ============

    [Fact]
    public void MissingVramLabels_MemoryStaysZero_NotGuessedDiscrete()
    {
        var units = new List<HwinfoUnitSpec> { HwinfoTestData.Unit("Unnamed GPU Group") };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 63.0),
            HwinfoTestData.Load(0, 17, "GPU Core Load"),
            // 无 'GPU D3D Memory *' 且无 'GPU Memory Allocated/Available'（本机实测后者不存在）
        };

        GpuResult only = Assert.Single(Associate(units, entries));
        Assert.Equal(0, only.MemoryTotalMB, 3);
        Assert.Equal(0, only.MemoryUsedMB, 3);
        Assert.NotEqual(GpuKind.Discrete, only.Kind);   // 不凭缺失数据判独显
    }

    [Fact]
    public void ApuWddmDedicatedSegment_DoesNotFlipDiscreteWithoutRole()
    {
        // 本机活体证据：APU 集显 unit 也带 'GPU D3D Memory Dedicated'=459.92MB（WDDM 预留分段）。
        // 旧主卡竞选按 MemoryTotalMB>0 判独显 → 集显冒充独显。新判据须低于阈值时不翻独显。
        var units = new List<HwinfoUnitSpec> { HwinfoTestData.Unit("Some AMD Graphics Device") };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 64.8),
            HwinfoTestData.Load(0, 88, "GPU Utilization"),
            HwinfoTestData.Memory(0, 459.92, "GPU D3D Memory Dedicated"),
        };

        var result = GpuHwinfoAssociation.Associate(
            HwinfoTestData.Snapshot(units, entries),
            identities: [HwinfoTestData.Identity(0, "Some AMD Graphics Device", HwinfoTestData.PhysicalIdAmd)]);
        GpuResult only = Assert.Single(result.Results);
        Assert.NotEqual(GpuKind.Discrete, only.Kind);
    }

    // ============ units 面不可用：保守降级（绝不复活顺序猜测） ============

    [Fact]
    public void UnitsFaceDisabled_DuplicateLabels_ReturnsEmptyAndFallsBack()
    {
        var units = new List<HwinfoUnitSpec>
        {
            HwinfoTestData.Unit(HwinfoTestData.IgpuUnitName),
            HwinfoTestData.Unit(HwinfoTestData.DgpuUnitName),
        };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 64.8), HwinfoTestData.Load(0, 88, "GPU Utilization"),
            HwinfoTestData.Temp(1, 57.2), HwinfoTestData.Load(1, 5, "GPU Core Load"),
        };

        // unitSize 不足 ⇒ units 面被禁用（entries 面仍可用）
        var snapshot = HwinfoTestData.Snapshot(units, entries, unitSize: 100);
        Assert.False(snapshot.UnitsAvailable);

        var result = GpuHwinfoAssociation.Associate(snapshot, LocalIdentities());
        Assert.Empty(result.Results);           // 标签重复 ⇒ 无法归属 ⇒ 保守放弃
        Assert.False(result.UnitsParseAvailable);
        Assert.Contains("无法归属", result.Note);
    }

    [Fact]
    public void UnitsFaceDisabled_UniqueLabels_ConservativelySingleCard()
    {
        var units = new List<HwinfoUnitSpec>
        {
            HwinfoTestData.Unit(HwinfoTestData.DgpuUnitName),
        };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 57.2), HwinfoTestData.Load(0, 42, "GPU Core Load"),
            HwinfoTestData.Memory(0, 8000, "GPU D3D Memory Dedicated"),
        };

        var snapshot = HwinfoTestData.Snapshot(units, entries, unitSize: 100);
        var result = GpuHwinfoAssociation.Associate(snapshot, LocalIdentities());

        GpuResult only = Assert.Single(result.Results);
        Assert.Equal(57.2, only.Temperature, 3);
        Assert.Equal(42, only.UsagePercent, 3);
        Assert.Contains("保守判定单卡", result.Note);
    }

    // ============ 层级顺序不变（验收 4）：Broker 优先、无 Broker 才吃 HWiNFO ============

    [Fact]
    public void ReadAll_HwinfoLayer_KeepsChainOrderAndSourceLabels()
    {
        var units = new List<HwinfoUnitSpec> { HwinfoTestData.Unit(HwinfoTestData.DgpuUnitName) };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 57.0), HwinfoTestData.Load(0, 44, "GPU Core Load"),
            HwinfoTestData.Memory(0, 8000, "GPU D3D Memory Dedicated"),
        };
        var snapshot = HwinfoTestData.Snapshot(units, entries);

        // 测试宿主 Broker 快照默认不可用 ⇒ 应落到 HWiNFO 层
        List<GpuResult> results = ReadAll(snapshot);

        GpuResult only = Assert.Single(results);
        Assert.Equal("HWiNFO", only.Source);
        Assert.Equal(HwinfoTestData.DgpuOsName, only.Name);   // 身份表给出的 OS 设备名
        Assert.Equal(57.0, only.Temperature, 3);
        Assert.Equal(44, only.UsagePercent, 3);
    }

    [Fact]
    public void ReadAll_NullSnapshotAndNoBroker_ReturnsEmptyWithoutThrowing()
    {
        // 全链不可用（测试宿主：Broker 无、HWiNFO 传 null、D3DKMT/PDH 反射禁用不在本用例范围）
        // ⇒ 行为与旧实现一致：返回空列表，不抛。
        var svc = new GpuIdentityService(new HwinfoTestData.FakeGpuIdentityProvider());
        var previous = GpuSensorReader.Identities;
        GpuSensorReader.Identities = svc;
        try
        {
            // 活体机器可能有 D3DKMT/PDH；本用例只断言不抛异常且来源标签一致
            List<GpuResult> results = GpuSensorReader.ReadAll((HwinfoLayoutSnapshot?)null);
            Assert.All(results, g => Assert.False(string.IsNullOrEmpty(g.Source)));
        }
        finally
        {
            GpuSensorReader.Identities = previous;
        }
    }

    [Fact]
    public void Association_WritesBackHwinfoRole_IntoIdentityTable_ForUiIcon()
    {
        // 本机关键场景：OS 设备名 "AMD Radeon(TM) Graphics" 不含型号（680M），
        // 名称判据可判集显；但 "Mystery Accelerator" 这类名字只能靠 HWiNFO 的 iGPU/dGPU 前缀。
        // 关联阶段把角色回写身份 ⇒ UI 图标（GpuIdentityService.Classify）与主卡竞选吃到同一证据。
        var provider = new HwinfoTestData.FakeGpuIdentityProvider(
            new GpuDisplayDevice(HwinfoTestData.PhysicalIdNvidia, "Mystery Accelerator"));
        var svc = new GpuIdentityService(provider);

        var units = new List<HwinfoUnitSpec> { HwinfoTestData.Unit(HwinfoTestData.DgpuUnitName) };
        var entries = new List<HwinfoEntrySpec>
        {
            HwinfoTestData.Temp(0, 57.0), HwinfoTestData.Load(0, 42, "GPU Core Load"),
            HwinfoTestData.Memory(0, 8000, "GPU D3D Memory Dedicated"),
        };

        GpuSensorReader.Identities = svc;
        try
        {
            var results = GpuSensorReader.ReadAll(HwinfoTestData.Snapshot(units, entries));
            GpuResult only = Assert.Single(results);
            Assert.Equal(GpuKind.Discrete, only.Kind);
            Assert.Equal("Mystery Accelerator", only.Name);
            Assert.Equal(SysMonIcons.GpuDiscrete, svc.ResolveIcon(new GpuInfo { Name = only.Name }));
            Assert.True(svc.LooksDiscrete(new GpuInfo { Name = only.Name }));
        }
        finally
        {
            GpuSensorReader.Identities = GpuIdentityService.Default;
        }
    }

    private static List<GpuResult> Associate(List<HwinfoUnitSpec> units, List<HwinfoEntrySpec> entries) =>
        GpuHwinfoAssociation.Associate(
            HwinfoTestData.Snapshot(units, entries), LocalIdentities()).Results;
}
