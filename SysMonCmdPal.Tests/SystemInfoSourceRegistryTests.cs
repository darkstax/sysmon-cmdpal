// Copyright (c) 2026 SysMonCmdPal
// T1-1 采集源注册制测试 — 用 mock 源验证:
//   1) SystemInfoService 可通过 internal 构造注入采集源;
//   2) Refresh() 严格按注册顺序遍历调用;
//   3) 单个源抛异常不影响其他源执行,快照仍正常发布;
//   4) 无人写入的字段保持重构前的哨兵默认值(快照字段语义不变);
//   5) 默认注册表顺序 = 重构前 Refresh() 的采集顺序(网络 → CPU → 磁盘 → 内存 → 电池 → 传感器 → 频率);
//   6) public static Instance 兼容入口仍在; 并发/重入守卫仍生效。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SystemInfoSourceRegistryTests
{
    // ------------------------------------------------------------------
    // Mock 采集源
    // ------------------------------------------------------------------

    private sealed class MockSource : ISystemInfoSource
    {
        private readonly List<string> _trace;
        private int _readCount;

        public MockSource(string name, List<string> trace)
        {
            Name = name;
            _trace = trace;
        }

        public string Name { get; }
        public bool ThrowOnRead { get; init; }
        public double? CpuUsage { get; init; }
        public double? NetDown { get; init; }
        public double? MemoryUsed { get; init; }
        public int ReadCount => _readCount;

        /// <summary>重入用: ReadInto 内部再次调用同一服务的 Refresh(),验证并发守卫仍然跳过。</summary>
        public SystemInfoService? ReentrantTarget { get; set; }

        public void ReadInto(ref SystemSnapshot snapshot)
        {
            _readCount++;
            _trace.Add(Name);

            if (ThrowOnRead)
                throw new InvalidOperationException($"mock source {Name} failed");

            if (CpuUsage is double cpu) snapshot.CpuUsage = cpu;
            if (NetDown is double down) snapshot.NetDown = down;
            if (MemoryUsed is double mem) snapshot.MemoryUsed = mem;

            ReentrantTarget?.Refresh();
        }
    }

    private static SystemInfoService CreateService(params ISystemInfoSource[] sources)
        => new(sources);

    // ------------------------------------------------------------------
    // 1 + 2: 注入 + 顺序执行
    // ------------------------------------------------------------------

    [Fact]
    public void Refresh_InvokesInjectedSourcesInRegisteredOrder()
    {
        var trace = new List<string>();
        var a = new MockSource("A", trace);
        var b = new MockSource("B", trace);
        var c = new MockSource("C", trace);
        var svc = CreateService(a, b, c);

        svc.Refresh();

        Assert.Equal(new[] { "A", "B", "C" }, trace);
        Assert.Equal(1, a.ReadCount);
        Assert.Equal(1, b.ReadCount);
        Assert.Equal(1, c.ReadCount);

        // 第二次刷新: 顺序不变,每个源各再调用一次
        trace.Clear();
        svc.Refresh();
        Assert.Equal(new[] { "A", "B", "C" }, trace);
        Assert.Equal(2, c.ReadCount);
    }

    [Fact]
    public void Refresh_WritesSourceValuesIntoPublishedSnapshot()
    {
        var trace = new List<string>();
        var svc = CreateService(
            new MockSource("cpu", trace) { CpuUsage = 42.5 },
            new MockSource("net", trace) { NetDown = 1234.5 },
            new MockSource("mem", trace) { MemoryUsed = 77 });

        svc.Refresh();

        var snap = svc.Current;
        Assert.Equal(42.5, snap.CpuUsage);
        Assert.Equal(1234.5, snap.NetDown);
        Assert.Equal(77, snap.MemoryUsed);
    }

    // ------------------------------------------------------------------
    // 3: 单源异常不影响其他源
    // ------------------------------------------------------------------

    [Fact]
    public void Refresh_SingleSourceThrowing_DoesNotAffectOtherSources()
    {
        var trace = new List<string>();
        var before = new MockSource("before", trace) { CpuUsage = 11 };
        var bad = new MockSource("bad", trace) { ThrowOnRead = true };
        var after = new MockSource("after", trace) { NetDown = 999 };

        var svc = CreateService(before, bad, after);

        // 不应向外抛出
        var ex = Record.Exception(() => svc.Refresh());
        Assert.Null(ex);

        Assert.Equal(new[] { "before", "bad", "after" }, trace);
        Assert.Equal(1, bad.ReadCount);

        // 异常源前后的源结果都进入了同一份快照并正常发布
        var snap = svc.Current;
        Assert.Equal(11, snap.CpuUsage);
        Assert.Equal(999, snap.NetDown);
    }

    // ------------------------------------------------------------------
    // 4: 快照字段哨兵语义不变(与重构前 Refresh() 初始化器一致)
    // ------------------------------------------------------------------

    [Fact]
    public void Refresh_NoSourceWritesSentinelFields_LegacyDefaultsPreserved()
    {
        var trace = new List<string>();
        var svc = CreateService(new MockSource("noop", trace));

        svc.Refresh();

        var snap = svc.Current;
        // 重构前 Refresh() 的快照初始化器:
        //   CpuTemperature = -1, Gpu = { UsagePercent = -1, Temperature = -1 }, Gpus = [], Backend = None
        Assert.Equal(-1, snap.CpuTemperature);
        Assert.Equal(-1, snap.Gpu.UsagePercent);
        Assert.Equal(-1, snap.Gpu.Temperature);
        Assert.Empty(snap.Gpus);
        Assert.Equal(SensorBackend.None, snap.Backend);
        Assert.Null(snap.BackendNote);
        // R(t1) F1: 已发布快照的集合字段恒非 null —— 重构前发布路径 never 产生 null 集合，
        // 消费端(DiskDockBand/DiskDetailPage/SysMonMainPage 回退分支)裸解引用无防护。
        // 断言面由「=null(旧 t1 误固化的契约)」加强为「空集合」，恢复构造期不变式。
        // (Gpus 的 Assert.Empty 见上方既有断言)
        Assert.Empty(snap.Disks);
        Assert.Empty(snap.PhysicalDisks);
        // 未被任何源写入的字段保持 struct 默认值
        Assert.Equal(0, snap.CpuFrequency);
        Assert.Equal(0, snap.NetUp);
    }

    [Fact]
    public void Refresh_PublishedSnapshotCollectionsNeverNull_EvenWhenSourcesThrowOrWriteNothing()
    {
        // R(t1) F1 守护用例: 任何源抛异常/完全不写集合，发布的三个集合字段永不为 null。
        var trace = new List<string>();

        // 场景 1: 唯一的源什么都不写
        var emptySvc = CreateService(new MockSource("noop", trace));
        emptySvc.Refresh();
        Assert.Empty(emptySvc.Current.Disks);
        Assert.Empty(emptySvc.Current.PhysicalDisks);
        Assert.Empty(emptySvc.Current.Gpus);

        // 场景 2: 源在写入前后抛异常
        var throwingSvc = CreateService(
            new MockSource("ok", trace),
            new MockSource("boom", trace) { ThrowOnRead = true },
            new MockSource("boom2", trace) { ThrowOnRead = true });
        var ex = Record.Exception(() => throwingSvc.Refresh());
        Assert.Null(ex); // 服务层隔离异常,不外抛
        Assert.Empty(throwingSvc.Current.Disks);
        Assert.Empty(throwingSvc.Current.PhysicalDisks);
        Assert.Empty(throwingSvc.Current.Gpus);
    }

    [Fact]
    public void Refresh_WithZeroSources_PublishedSnapshotCollectionsStillNonNull()
    {
        // F1 收口位于快照初始化器(源头),与注册表内容无关:
        // 极端退化场景(一个源都没注册)发布的三个集合字段同样非 null。
        var svc = CreateService();

        svc.Refresh();
        var snap = svc.Current;

        Assert.NotNull(snap.Disks);
        Assert.NotNull(snap.PhysicalDisks);
        Assert.NotNull(snap.Gpus);
        Assert.Empty(snap.Disks);
        Assert.Empty(snap.PhysicalDisks);
        Assert.Empty(snap.Gpus);
    }

    [Fact]
    public void Refresh_PushesChartAfterSources_AndConcurrencyGuardSkipsReentrantRefresh()
    {
        var trace = new List<string>();
        var reentrant = new MockSource("reentrant", trace) { CpuUsage = 55 };
        var svc = CreateService(reentrant);
        reentrant.ReentrantTarget = svc; // 源持有服务自身,ReadInto 时触发重入 Refresh

        svc.Refresh();

        // 源内部再次调用 Refresh(): Interlocked 守卫必须跳过重入,源总共只执行一次
        Assert.Equal(1, reentrant.ReadCount);
        // 图表推送逻辑仍在(每次有效刷新各推一个点)
        Assert.Equal(1, svc.CpuChart.Count);
        Assert.Equal(1, svc.NetDownChart.Count);
    }

    // ------------------------------------------------------------------
    // 5: 默认注册表顺序 = 重构前采集顺序
    // ------------------------------------------------------------------

    [Fact]
    public void DefaultSourceRegistry_MatchesLegacyCollectionOrder()
    {
        // R(t1) F2: BuildDefaultSources 不再是测试专用符号——生产构造函数
        // (SystemInfoService.cs) 直接以它作为唯一注册表数据源,因此本守护验证的
        // 就是生产路径实际使用的同一份数据(影子注册表已消灭)。
        // 只创建源对象,不触发任何硬件读取。
        var sources = SystemInfoService.BuildDefaultSources(out var network);
        Assert.NotNull(network);

        Assert.Equal(
            new[]
            {
                nameof(NetworkMonitor),
                nameof(CpuUsageReader),
                nameof(DiskMonitor),
                nameof(MemoryInfoSource),
                nameof(BatteryInfoSource),
                nameof(SensorInfoSource),
                nameof(CpuFrequencyReader),
            },
            sources.Select(s => s.GetType().Name).ToArray());

        // 采集器仍直接实现接口(而非仅靠包装类)
        Assert.Contains(sources, s => s is NetworkMonitor);
        Assert.Contains(sources, s => s is DiskMonitor);
        Assert.Contains(sources, s => s is CpuUsageReader);
        Assert.Contains(sources, s => s is CpuFrequencyReader);

        // F2 硬约束: 注册顺序(network→cpuUsage→disk→…→cpuFreq)与实例化时序
        // (network→disk→cpuFreq→cpuUsage, PerformanceCounter 创建时序敏感)是解耦的两件事。
        // 用引用同一性证明: 注册表首项就是最先实例化并交给 ctor 播种的那个 NetworkMonitor,
        // 而 CpuUsageReader 虽在注册表第 2 位、却是最后实例化的(见 BuildDefaultSources 内注释①/②)。
        Assert.Same(network, sources[0]);
        var usage = Assert.IsType<CpuUsageReader>(sources[1]);
        Assert.NotNull(usage);
        var freq = Assert.IsType<CpuFrequencyReader>(sources[^1]);
        Assert.NotNull(freq);
    }

    // ------------------------------------------------------------------
    // 6: public static Instance 兼容入口保留
    // ------------------------------------------------------------------

    [Fact]
    public void StaticInstanceCompatibilityEntryPoint_StillExposed()
    {
        var prop = typeof(SystemInfoService).GetProperty(
            "Instance", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(prop);
        Assert.True(prop!.CanRead);
        Assert.Equal(typeof(SystemInfoService), prop.PropertyType);
    }
}
