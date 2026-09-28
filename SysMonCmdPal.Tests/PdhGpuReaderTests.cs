// Copyright (c) 2026 SysMonCmdPal
// T2-2：PDH 结果组装的**纯函数**测试（零 PerformanceCounter、零 COM、零硬件）。
// 覆盖 t12 定为真 bug 的「空闲 0% 整卡消失」修复，与「未归属 LUID 不命名/不填显存」。
using System.Collections.Generic;
using Xunit;

namespace SysMonCmdPal.Tests;

public sealed class PdhGpuReaderTests
{
    private static GpuDxgkAdapter Adapter(string name, GpuKind kind, double memTotalMB, double memUsedMB) =>
        new()
        {
            LuidLow = 1,
            LuidHigh = 0,
            Name = name,
            Kind = kind,
            MemoryTotalBytes = (ulong)(memTotalMB * 1024 * 1024),
            MemoryUsedBytes = (ulong)(memUsedMB * 1024 * 1024),
        };

    [Fact]
    public void BuildResults_IdleZeroPercent_StillProducesCard()
    {
        // 旧实现 `if(cooked>0)` ⇒ 空闲 0% 的卡不进 perGpuUsage ⇒ 整卡从结果消失（t12 真 bug）。
        // 修正：只要已建立 delta 基线（在 hasDelta 内）即产出，利用率如实为 0。
        var luid = (low: 1u, high: 0);
        var map = new Dictionary<(uint, int), GpuDxgkAdapter> { [luid] = Adapter(HwinfoTestData.DgpuOsName, GpuKind.Discrete, 8192, 3243) };
        var hasDelta = new HashSet<(uint, int)> { (luid.low, luid.high) };
        var maxByGpu = new Dictionary<(uint, int), float> { [(luid.low, luid.high)] = 0f };

        var results = PdhGpuReader.BuildResults(hasDelta, maxByGpu, map);

        GpuResult only = Assert.Single(results);
        Assert.Equal(0, only.UsagePercent, 3);
        Assert.Equal(HwinfoTestData.DgpuOsName, only.Name);
        Assert.Equal("PDH", only.Source);
        Assert.Equal(GpuKind.Discrete, only.Kind);
        Assert.Equal(3243.0, only.MemoryUsedMB, 0);    // 显存来自 COM-free 映射，非 "GPU" 兜底
        Assert.Equal(8192.0, only.MemoryTotalMB, 0);
    }

    [Fact]
    public void BuildResults_DeltaBaselineWithoutPositiveUsage_StillProducesZero()
    {
        // 某卡全部引擎都未算出正值 ⇒ maxByGpu 缺失该 LUID ⇒ 利用率 0（仍产出，不蒸发）
        var key = (1u, 0);
        var map = new Dictionary<(uint, int), GpuDxgkAdapter> { [key] = Adapter("Some GPU", GpuKind.Discrete, 4096, 0) };
        var hasDelta = new HashSet<(uint, int)> { key };
        var maxByGpu = new Dictionary<(uint, int), float>();   // 空

        GpuResult only = Assert.Single(PdhGpuReader.BuildResults(hasDelta, maxByGpu, map));
        Assert.Equal(0, only.UsagePercent, 3);
    }

    [Fact]
    public void BuildResults_UnattributableLuid_IsSkipped()
    {
        // LUID 不在 COM-free 身份映射里（虚拟/渲染节点副本）⇒ 不产出、不命名、不填显存
        var key = (9u, 0);
        var map = new Dictionary<(uint, int), GpuDxgkAdapter> { [(1u, 0)] = Adapter("Mapped", GpuKind.Discrete, 1000, 10) };
        var hasDelta = new HashSet<(uint, int)> { key };
        var maxByGpu = new Dictionary<(uint, int), float> { [key] = 50f };

        Assert.Empty(PdhGpuReader.BuildResults(hasDelta, maxByGpu, map));
    }

    [Fact]
    public void BuildResults_NullMap_ProducesNothing()
    {
        var hasDelta = new HashSet<(uint, int)> { (1u, 0) };
        Assert.Empty(PdhGpuReader.BuildResults(hasDelta, new(), null));
    }

    [Fact]
    public void BuildResults_NegativeOrNaNUsage_ClampsToZero()
    {
        // 计数器复位算出负值/NaN ⇒ 保守 0，不丢弃该卡
        var key = (1u, 0);
        var map = new Dictionary<(uint, int), GpuDxgkAdapter> { [key] = Adapter("GPU", GpuKind.Discrete, 1000, 10) };
        var hasDelta = new HashSet<(uint, int)> { key };

        GpuResult neg = Assert.Single(PdhGpuReader.BuildResults(hasDelta, new() { [key] = -3f }, map));
        Assert.Equal(0, neg.UsagePercent, 3);
        GpuResult nan = Assert.Single(PdhGpuReader.BuildResults(hasDelta, new() { [key] = float.NaN }, map));
        Assert.Equal(0, nan.UsagePercent, 3);
    }

    [Fact]
    public void BuildResults_CapsAt100()
    {
        var key = (1u, 0);
        var map = new Dictionary<(uint, int), GpuDxgkAdapter> { [key] = Adapter("GPU", GpuKind.Discrete, 1000, 10) };
        var hasDelta = new HashSet<(uint, int)> { key };

        GpuResult only = Assert.Single(PdhGpuReader.BuildResults(hasDelta, new() { [key] = 250f }, map));
        Assert.Equal(100, only.UsagePercent, 3);
    }
}
