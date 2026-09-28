// Copyright (c) 2026 SysMonCmdPal
// T1-2（路线图 P0-2）详情页惰性化测试。用注入工厂 + DockBandRefreshCoordinator
// 订阅计数验证:
//   1) 构造 + 渲染主页列表不实例化任何详情页，也不产生任何刷新订阅（验收 1）;
//   2) 9 个条目 Command 仍是同一批惰性壳（顺序与重构前一致），
//      壳的页头镜像(Name/Title/Icon)与真实详情页 ctor 逐字一致;
//   3) 同一详情页重复进入复用同一实例，不产生额外订阅（验收 2）;
//   4) Dispose 释放全部已创建的刷新页且订阅数归零；
//      从未打开的页不被 Dispose 反向实例化（验收 3 + T1-2 语义）;
//   5) 宿主契约桥接: 内层页 RaiseItemsChanged / PropChanged 必须转发到壳
//      —— SDK 里 ItemsChanged/PropChanged 是字段事件，不跨对象联动，
//      SensorListPage 的每秒刷新依赖这条链（结构性断点，307 用例原本抓不到）;
//   6) 创建前宿主写入的 SearchText 在创建瞬间补灌给内层页;
//   7) 真实电池详情页 ctor 零订阅 —— BatteryReportService 等后台工作
//      只在页面真正打开后才启动（路线图 P0-2 影响项）。

using System;
using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SysMonMainPageLazyPagesTests
{
    // ------------------------------------------------------------------
    // 测试替身
    // ------------------------------------------------------------------

    /// <summary>零硬件采集源：把 GetItems() 读到的引用型字段置为空数组、其余置哨兵。</summary>
    private sealed class SeedSource : ISystemInfoSource
    {
        public void ReadInto(ref SystemSnapshot snapshot)
        {
            snapshot.Disks = [];
            snapshot.PhysicalDisks = [];
            snapshot.Gpus = [];
            snapshot.BatteryStatus = "no battery";
            snapshot.BatteryPercent = -1;
            snapshot.CpuUsage = 0;
            snapshot.MemoryUsed = 0;
            snapshot.MemoryUsedBytes = 0;
            snapshot.MemoryTotalBytes = 0;
            snapshot.NetDown = 0;
            snapshot.NetUp = 0;
            snapshot.CpuTemperature = -1;
            snapshot.CpuFrequency = -1;
            snapshot.Backend = SensorBackend.None;
            snapshot.BackendNote = string.Empty;
        }
    }

    private static SystemInfoService CreateSeededService()
    {
        var svc = new SystemInfoService([new SeedSource()]);
        svc.Refresh();
        return svc;
    }

    private sealed class FakeContentPage : ContentPage
    {
        public int GetContentCalls { get; private set; }

        public override IContent[] GetContent()
        {
            GetContentCalls++;
            return [];
        }
    }

    /// <summary>形态对齐真实详情页：进入页面(GetContent)时才订阅 coordinator。</summary>
    private sealed class FakeRefreshingPage : RefreshingContentPage
    {
        public int GetContentCalls { get; private set; }

        public override IContent[] GetContent()
        {
            GetContentCalls++;
            StartTimer();
            return [];
        }

        protected override void RefreshContent() { }
    }

    private sealed class FakeListPage : ListPage
    {
        public int GetItemsCalls { get; private set; }

        public override IListItem[] GetItems()
        {
            GetItemsCalls++;
            return [];
        }
    }

    /// <summary>字段事件只能由声明类内部 raise，用探针页手动触发。</summary>
    private sealed class ProbeListPage : ListPage
    {
        public override IListItem[] GetItems() => [];
        public void TriggerItemsChanged(int total) => RaiseItemsChanged(total);
        public void TriggerPropChanged(string name) => OnPropertyChanged(name);
    }

    private sealed class ProbeContentPage : ContentPage
    {
        public override IContent[] GetContent() => [];
        public void TriggerItemsChanged(int total) => RaiseItemsChanged(total);
    }

    private sealed class FactoryCounters
    {
        public int Cpu, Memory, Disk, Network, Battery, Gpu, Sensors, Diagnostics;

        public int Total => Cpu + Memory + Disk + Network + Battery + Gpu + Sensors + Diagnostics;

        public SysMonMainPage.DetailPageFactories ToFactories() => new(
            () => { Cpu++; return new FakeContentPage(); },
            () => { Memory++; return new FakeContentPage(); },
            () => { Disk++; return new FakeListPage(); },
            () => { Network++; return new FakeContentPage(); },
            () => { Battery++; return new FakeRefreshingPage(); },
            () => { Gpu++; return new FakeListPage(); },
            () => { Sensors++; return new FakeListPage(); },
            () => { Diagnostics++; return new FakeContentPage(); });
    }

    private static SysMonMainPage.DetailPageFactories SingleSlotFactories(
        Func<ContentPage>? battery = null, Func<ListPage>? disk = null) => new(
            static () => new FakeContentPage(),
            static () => new FakeContentPage(),
            disk ?? (static () => new FakeListPage()),
            static () => new FakeContentPage(),
            battery ?? (static () => new FakeContentPage()),
            static () => new FakeListPage(),
            static () => new FakeListPage(),
            static () => new FakeContentPage());

    // ------------------------------------------------------------------
    // 1: 打开主页未进入任何详情页 → 零实例化、零订阅
    // ------------------------------------------------------------------

    [Fact]
    public void Constructor_And_GetItems_Instantiate_Zero_DetailPages()
    {
        int baseline = DockBandRefreshCoordinator.SubscriberCount;
        var counters = new FactoryCounters();
        using var page = new SysMonMainPage(counters.ToFactories(), CreateSeededService());

        IListItem[] items = page.GetItems();

        // 条目数量与顺序与重构前一致: Back + 8 详情页 + btop。
        Assert.Equal(10, items.Length);

        // 验收 1: 未进入任何详情页 → 工厂一次都没被调用。
        Assert.Equal(0, counters.Total);
        Assert.False(page.CpuShellForTest.IsCreated);
        Assert.False(page.MemoryShellForTest.IsCreated);
        Assert.False(page.DiskShellForTest.IsCreated);
        Assert.False(page.NetworkShellForTest.IsCreated);
        Assert.False(page.BatteryShellForTest.IsCreated);
        Assert.False(page.GpuShellForTest.IsCreated);
        Assert.False(page.SensorShellForTest.IsCreated);
        Assert.False(page.DiagnosticsShellForTest.IsCreated);

        // 构造与列表渲染不产生任何 coordinator 订阅。
        Assert.Equal(baseline, DockBandRefreshCoordinator.SubscriberCount);
    }

    // ------------------------------------------------------------------
    // 2: 条目 Command 仍是同一批惰性壳
    // ------------------------------------------------------------------

    [Fact]
    public void GetItems_ListItem_Commands_Are_The_Lazy_Shells()
    {
        var counters = new FactoryCounters();
        using var page = new SysMonMainPage(counters.ToFactories(), CreateSeededService());

        IListItem[] items = page.GetItems();

        Assert.IsType<GoBackCommand>(((ListItem)items[0]).Command);
        Assert.Same(page.CpuShellForTest, ((ListItem)items[1]).Command);
        Assert.Same(page.MemoryShellForTest, ((ListItem)items[2]).Command);
        Assert.Same(page.DiskShellForTest, ((ListItem)items[3]).Command);
        Assert.Same(page.GpuShellForTest, ((ListItem)items[4]).Command);
        Assert.Same(page.NetworkShellForTest, ((ListItem)items[5]).Command);
        Assert.Same(page.BatteryShellForTest, ((ListItem)items[6]).Command);
        Assert.Same(page.SensorShellForTest, ((ListItem)items[7]).Command);
        Assert.Same(page.DiagnosticsShellForTest, ((ListItem)items[8]).Command);
        Assert.IsType<BtopLauncherCommand>(((ListItem)items[9]).Command);

        Assert.Equal(0, counters.Total);
    }

    // ------------------------------------------------------------------
    // 3: 重复进入同一详情页复用同一实例，不产生额外订阅
    // ------------------------------------------------------------------

    [Fact]
    public void Reopening_ContentPage_Reuses_Single_Instance_And_Single_Subscription()
    {
        int baseline = DockBandRefreshCoordinator.SubscriberCount;
        var created = new List<ContentPage>();
        using var page = new SysMonMainPage(
            SingleSlotFactories(battery: () =>
            {
                var p = new FakeRefreshingPage();
                created.Add(p);
                return p;
            }),
            CreateSeededService());

        page.BatteryShellForTest.GetContent();
        page.BatteryShellForTest.GetContent();
        page.BatteryShellForTest.GetContent();

        Assert.Single(created);
        var inner = (FakeRefreshingPage)created[0];
        Assert.Equal(3, inner.GetContentCalls);
        Assert.Same(inner, page.BatteryShellForTest.CreatedPageOrNull);
        // 三次进入 → 只有 1 个额外订阅（RefreshingContentPage 去重 + 单实例）。
        Assert.Equal(baseline + 1, DockBandRefreshCoordinator.SubscriberCount);

        page.Dispose();
        Assert.Equal(baseline, DockBandRefreshCoordinator.SubscriberCount);
    }

    [Fact]
    public void Reopening_ListPage_Reuses_Single_Instance()
    {
        var created = new List<ListPage>();
        using var page = new SysMonMainPage(
            SingleSlotFactories(disk: () =>
            {
                var p = new FakeListPage();
                created.Add(p);
                return p;
            }),
            CreateSeededService());

        page.DiskShellForTest.GetItems();
        page.DiskShellForTest.GetItems();

        Assert.Single(created);
        Assert.Equal(2, ((FakeListPage)created[0]).GetItemsCalls);
        Assert.Same(created[0], page.DiskShellForTest.CreatedPageOrNull);
    }

    // ------------------------------------------------------------------
    // 4: Dispose —— 释放全部已创建页；未创建页不被反向实例化
    // ------------------------------------------------------------------

    [Fact]
    public void Dispose_Releases_Every_Opened_Refreshing_Page()
    {
        int baseline = DockBandRefreshCoordinator.SubscriberCount;
        var factories = new SysMonMainPage.DetailPageFactories(
            () => new FakeRefreshingPage(),          // CPU
            static () => new FakeContentPage(),      // 内存（不打开）
            static () => new FakeListPage(),         // 磁盘（不打开）
            static () => new FakeContentPage(),      // 网络（不打开）
            () => new FakeRefreshingPage(),          // 电池
            static () => new FakeListPage(),         // GPU（不打开）
            static () => new FakeListPage(),         // 传感器（不打开）
            static () => new FakeContentPage());     // Broker 诊断（不打开）
        using var page = new SysMonMainPage(factories, CreateSeededService());

        page.CpuShellForTest.GetContent();
        Assert.Equal(baseline + 1, DockBandRefreshCoordinator.SubscriberCount);

        page.BatteryShellForTest.GetContent();
        page.BatteryShellForTest.GetContent(); // 重复进入不叠加订阅
        Assert.Equal(baseline + 2, DockBandRefreshCoordinator.SubscriberCount);

        page.Dispose();

        // 验收 3: 两个已创建的刷新页全部退订 → 订阅数回到基线（无泄漏）。
        Assert.Equal(baseline, DockBandRefreshCoordinator.SubscriberCount);
    }

    [Fact]
    public void Dispose_Never_Instantiates_Unopened_Pages()
    {
        var counters = new FactoryCounters();
        using var page = new SysMonMainPage(counters.ToFactories(), CreateSeededService());

        page.GetItems();
        page.CpuShellForTest.GetContent(); // 只打开 CPU 一页
        page.Dispose();

        Assert.Equal(1, counters.Cpu);
        Assert.Equal(1, counters.Total);
        Assert.False(page.BatteryShellForTest.IsCreated);
        Assert.False(page.SensorShellForTest.IsCreated);
        Assert.False(page.DiagnosticsShellForTest.IsCreated);
    }

    // ------------------------------------------------------------------
    // 5: 宿主契约桥接（唯一真断点 SensorListPage 的每秒刷新链）
    // ------------------------------------------------------------------

    [Fact]
    public void DeferredListPage_Bridges_Inner_ItemsChanged_And_PropChanged_To_Shell()
    {
        var inner = new ProbeListPage();
        using var shell = new DeferredListPage(() => inner, "Probe", "ProbeTitle", SysMonIcons.Cpu);

        int receivedTotal = -99;
        string? changedProp = null;
        shell.ItemsChanged += (_, e) => receivedTotal = e!.TotalItems;
        shell.PropChanged += (_, e) => changedProp = e!.PropertyName;

        // 宿主订阅壳本身不触发创建。
        Assert.False(shell.IsCreated);

        shell.GetItems(); // 首次进入 → 工厂创建 + 桥接
        Assert.True(shell.IsCreated);

        // 桥接后，内层页的 RaiseItemsChanged 必须送达宿主订阅的壳。
        inner.TriggerItemsChanged(7);
        Assert.Equal(7, receivedTotal);

        inner.TriggerPropChanged("Title");
        Assert.Equal("Title", changedProp);
    }

    [Fact]
    public void DeferredContentPage_Bridges_Inner_ItemsChanged_To_Shell()
    {
        var inner = new ProbeContentPage();
        using var shell = new DeferredContentPage(() => inner, "Probe", "ProbeTitle", SysMonIcons.Battery);

        int receivedTotal = -99;
        shell.ItemsChanged += (_, e) => receivedTotal = e!.TotalItems;

        Assert.False(shell.IsCreated);
        shell.GetContent();
        Assert.True(shell.IsCreated);

        inner.TriggerItemsChanged(3);
        Assert.Equal(3, receivedTotal);
    }

    // ------------------------------------------------------------------
    // 6: 创建前的宿主写入（SearchText）在创建瞬间补灌给内层
    // ------------------------------------------------------------------

    [Fact]
    public void SearchText_Written_Before_Creation_Is_Forwarded_To_Inner_Page()
    {
        var inner = new ProbeListPage();
        using var shell = new DeferredListPage(() => inner, "Probe", "ProbeTitle", SysMonIcons.Disk);

        shell.SearchText = "fan"; // 创建前写入 → 落在壳上
        Assert.Equal("fan", shell.SearchText);
        Assert.False(shell.IsCreated);
        Assert.Equal(string.Empty, inner.SearchText);

        shell.GetItems(); // 创建 → 补灌
        Assert.Equal("fan", inner.SearchText);
        Assert.Equal("fan", shell.SearchText);

        shell.SearchText = "fan2"; // 创建后 → 读写直达内层
        Assert.Equal("fan2", inner.SearchText);
        Assert.Equal("fan2", shell.SearchText);
    }

    [Fact]
    public void DeferredContentPage_Commands_Read_Stays_Lazy_Before_Creation()
    {
        // 宿主若在进入页面前预读上下文命令，壳不得因此创建详情页（验收 1 的强化）；
        // 创建后读取则直达内层真实页。
        var inner = new ProbeContentPage();
        using var shell = new DeferredContentPage(() => inner, "Probe", "ProbeTitle", SysMonIcons.Battery);

        Assert.Empty(shell.Commands);
        Assert.False(shell.IsCreated);

        shell.GetContent();
        inner.Commands = [new CommandContextItem(new NoOpCommand())];

        Assert.Single(shell.Commands);
        Assert.True(shell.IsCreated);
    }

    [Fact]
    public void Shell_Forwards_Metadata_To_Inner_After_Creation()
    {
        var inner = new ProbeListPage();
        using var shell = new DeferredListPage(() => inner, "ShellName", "ShellTitle", SysMonIcons.Cpu);

        // 创建前：读壳的镜像值。
        Assert.Equal("ShellName", shell.Name);
        Assert.Equal("ShellTitle", shell.Title);

        shell.GetItems();

        // 创建后：读写直达内层真实页（宿主契约不丢失）。
        inner.Name = "InnerName";
        inner.Title = "InnerTitle";
        Assert.Equal("InnerName", shell.Name);
        Assert.Equal("InnerTitle", shell.Title);

        shell.Title = "Changed";
        Assert.Equal("Changed", inner.Title);
    }

    // ------------------------------------------------------------------
    // 7: 壳的页头镜像与真实详情页 ctor 逐字一致（默认工厂路径守护）
    // ------------------------------------------------------------------

    [Fact]
    public void Shell_Preset_Metadata_Matches_Real_DetailPage_Constructors()
    {
        using var page = new SysMonMainPage(
            SysMonMainPage.DetailPageFactories.Default, CreateSeededService());

        AssertMirror(page.CpuShellForTest, new CpuDetailPage());
        AssertMirror(page.MemoryShellForTest, new MemoryDetailPage());
        AssertMirror(page.DiskShellForTest, new DiskDetailPage());
        AssertMirror(page.NetworkShellForTest, new NetworkDetailPage());
        AssertMirror(page.BatteryShellForTest, new BatteryDetailPage());
        AssertMirror(page.GpuShellForTest, new GpuDetailPage());
        AssertMirror(page.SensorShellForTest, new SensorListPage());
        AssertMirror(page.DiagnosticsShellForTest, new BrokerDiagnosticsPage());
    }

    private static void AssertMirror(DeferredContentPage shell, Page real)
    {
        try
        {
            Assert.Equal(real.Name, shell.PresetName);
            Assert.Equal(real.Title, shell.PresetTitle);
            Assert.Equal(real.Icon.Light.Icon, shell.PresetIconGlyph);
            Assert.False(shell.IsCreated);
        }
        finally
        {
            (real as IDisposable)?.Dispose();
        }
    }

    private static void AssertMirror(DeferredListPage shell, Page real)
    {
        try
        {
            Assert.Equal(real.Name, shell.PresetName);
            Assert.Equal(real.Title, shell.PresetTitle);
            Assert.Equal(real.Icon.Light.Icon, shell.PresetIconGlyph);
            Assert.False(shell.IsCreated);
        }
        finally
        {
            (real as IDisposable)?.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // 8: 真实电池页 ctor 零订阅（后台工作只在页面真正打开后启动）
    // ------------------------------------------------------------------

    [Fact]
    public void RealBatteryPage_Ctor_Subscribes_Nothing_And_Starts_No_Background_Work()
    {
        int baseline = DockBandRefreshCoordinator.SubscriberCount;

        using (var battery = new BatteryDetailPage())
        {
            Assert.Equal(baseline, DockBandRefreshCoordinator.SubscriberCount);
        }

        Assert.Equal(baseline, DockBandRefreshCoordinator.SubscriberCount);
    }
}
