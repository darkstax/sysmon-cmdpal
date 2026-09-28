// Copyright (c) 2026 SysMonCmdPal
// 主页面 — 系统概览列表（CPU / 内存 / 磁盘 / GPU / 网络 / 电池 / 传感器 / Broker 诊断）

using System.Diagnostics;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

/// <summary>
/// Command Palette 中打开的主页面。
/// 以列表形式展示各子系统，选中后进入详情页。
///
/// T1-2（路线图 §5 Phase1 / P0-2）：构造函数不再 new 8 个详情页 —— 只创建轻量惰性壳
/// (DeferredListPage / DeferredContentPage)。真实详情页在用户首次进入该页（宿主读取
/// 页内容 / 列表 / 上下文命令）时才创建并此后复用同一实例；Dispose 只释放已创建的实例，
/// 未创建的永不触发。BatteryReportService 等后台工作只在内层电池页真正打开时启动。
/// 新增 ListItem 的 Icon/Title/Subtitle 逻辑保持与重构前完全一致；本地化仍走 Loc.Get。
/// </summary>
internal sealed partial class SysMonMainPage : ListPage, IDisposable
{
    /// <summary>
    /// 详情页工厂（T1-2 测试缝隙）：默认实现 new 真实详情页；
    /// 单元测试注入计次工厂，断言「未进入详情页 → 零实例化」「重复进入 → 复用同一实例」
    /// 「Dispose 未创建页 → 不反向触发创建」。
    /// </summary>
    internal sealed record DetailPageFactories(
        Func<ContentPage> Cpu,
        Func<ContentPage> Memory,
        Func<ListPage> Disk,
        Func<ContentPage> Network,
        Func<ContentPage> Battery,
        Func<ListPage> Gpu,
        Func<ListPage> Sensors,
        Func<ContentPage> Diagnostics)
    {
        public static DetailPageFactories Default { get; } = new(
            static () => new CpuDetailPage(),
            static () => new MemoryDetailPage(),
            static () => new DiskDetailPage(),
            static () => new NetworkDetailPage(),
            static () => new BatteryDetailPage(),
            static () => new GpuDetailPage(),
            static () => new SensorListPage(),
            static () => new BrokerDiagnosticsPage());
    }

    private readonly SystemInfoService _sysInfo;

    // 惰性壳的 name/title/icon 必须与对应详情页 ctor 中的赋值逐字一致：
    // 宿主在进入页面之前就会读取页头 (Icon/Name/Title)，壳必须自带正确镜像值。
    // 【改任一详情页 ctor 的这三项时，必须同步修改这里】
    private readonly DeferredContentPage _cpuPage;
    private readonly DeferredContentPage _memPage;
    private readonly DeferredListPage _diskPage;
    private readonly DeferredContentPage _netPage;
    private readonly DeferredContentPage _batPage;
    private readonly DeferredListPage _gpuPage;
    private readonly DeferredListPage _sensorPage;
    private readonly DeferredContentPage _brokerDiagnosticsPage;
    private readonly BtopLauncherCommand _btopCmd = new();

    public SysMonMainPage()
        : this(DetailPageFactories.Default, SystemInfoService.Instance)
    {
    }

    /// <summary>T1-2 测试构造：注入详情页工厂与采集服务，断言可在零硬件依赖下完成。</summary>
    internal SysMonMainPage(DetailPageFactories factories, SystemInfoService sysInfo)
    {
        _sysInfo = sysInfo;

        Icon = new IconInfo(SysMonIcons.App);
        Title = Loc.Get("MainPage.Title");
        Name = "Open";

        _cpuPage = new DeferredContentPage(factories.Cpu,
            name: "CPU", title: Loc.Get("Cpu.PageTitle"), icon: SysMonIcons.Cpu);
        _memPage = new DeferredContentPage(factories.Memory,
            name: Loc.Get("Dock.Memory"), title: Loc.Get("Memory.PageTitle"), icon: SysMonIcons.Memory);
        _diskPage = new DeferredListPage(factories.Disk,
            name: Loc.Get("Dock.Disk"), title: Loc.Get("Disk.PageTitle"), icon: SysMonIcons.Disk);
        _netPage = new DeferredContentPage(factories.Network,
            name: Loc.Get("MainPage.NetworkTitle"), title: Loc.Get("Network.PageTitle"), icon: SysMonIcons.Network);
        _batPage = new DeferredContentPage(factories.Battery,
            name: Loc.Get("Dock.Battery"), title: Loc.Get("Battery.PageTitle"), icon: SysMonIcons.Battery);
        _gpuPage = new DeferredListPage(factories.Gpu,
            name: "GPU", title: Loc.Get("Gpu.PageTitle"), icon: SysMonIcons.Gpu);
        _sensorPage = new DeferredListPage(factories.Sensors,
            name: "Sensors", title: Loc.Get("Sensor.PageTitle"), icon: SysMonIcons.Sensors);
        _brokerDiagnosticsPage = new DeferredContentPage(factories.Diagnostics,
            name: "BrokerDiagnostics", title: Loc.Get("BrokerDiagnostics.PageTitle"), icon: SysMonIcons.Diagnostics);
        // P2: 不需要 preWarmTimer —— 内层详情页在 GetContent()/GetItems() 时才
        // 自动订阅 DockBandRefreshCoordinator；构造函数零订阅、零后台工作。
    }

    // ---- T1-2 测试缝隙：暴露惰性壳本体（只读引用壳，绝不触发创建） ----
    internal DeferredContentPage CpuShellForTest => _cpuPage;
    internal DeferredContentPage MemoryShellForTest => _memPage;
    internal DeferredListPage DiskShellForTest => _diskPage;
    internal DeferredContentPage NetworkShellForTest => _netPage;
    internal DeferredContentPage BatteryShellForTest => _batPage;
    internal DeferredListPage GpuShellForTest => _gpuPage;
    internal DeferredListPage SensorShellForTest => _sensorPage;
    internal DeferredContentPage DiagnosticsShellForTest => _brokerDiagnosticsPage;

    public override IListItem[] GetItems()
    {
        // 用已缓存快照，不触发同步 Refresh（避免阻塞 UI）；
        // 只读取壳的镜像元数据，不创建任何详情页实例（T1-2 验收 1）。
        var info = _sysInfo.Current;

        return [
            PageNavigation.BackListItem(Dispose),
            new ListItem(_cpuPage)
            {
                Title = Loc.Format("MainPage.CpuTitle", $"{info.CpuUsage:F0}"),
                Subtitle = string.IsNullOrEmpty(SystemInfoService.CpuName)
                    ? (info.CpuTemperature >= 0
                        ? Loc.Format("MainPage.CpuSubtitleTemp", $"{info.CpuTemperature:F0}", Environment.ProcessorCount)
                        : Loc.Format("MainPage.CpuSubtitleNoTemp", Environment.ProcessorCount))
                    : GetNamedCpuSubtitle(info),
                Icon = new IconInfo(SysMonIcons.Cpu),
            },
            new ListItem(_memPage)
            {
                Title = Loc.Format("MainPage.MemoryTitle", $"{info.MemoryUsed:F0}"),
                Subtitle = $"{(info.MemoryUsedBytes / (1024.0 * 1024 * 1024)):F1} / {(info.MemoryTotalBytes / (1024.0 * 1024 * 1024)):F1} GB",
                Icon = new IconInfo(SysMonIcons.Memory),
            },
            new ListItem(_diskPage)
            {
                Title = Loc.Format("MainPage.DiskTitle", GetDiskCount(info)),
                Subtitle = GetDiskSubtitle(info),
                Icon = new IconInfo(SysMonIcons.Disk),
            },
            new ListItem(_gpuPage)
            {
                Title = info.Gpus.Length > 0
                    ? (info.Gpus.Length == 1
                        ? Loc.Format("MainPage.GpuTitleSingle", info.Gpus[0].Name.ToUpper())
                        : Loc.Format("MainPage.GpuTitleMulti", info.Gpus.Length))
                    : Loc.Get("MainPage.GpuUnavailable"),
                Subtitle = info.Gpus.Length > 0
                    ? string.Join(" | ", info.Gpus.Select(g =>
                        $"{g.Name.ToUpper()}: {DockFormat.Temp(g.Temperature)}"))
                    : (info.CpuTemperature >= 0
                        ? Loc.Format("MainPage.GpuSubtitleBackend", BackendStatusText(info.Backend))
                        : ""),
                Icon = new IconInfo(SysMonIcons.Gpu),
            },
            new ListItem(_netPage)
            {
                Title = Loc.Get("MainPage.NetworkTitle"),
                Subtitle = $"↓ {DockFormat.Speed(info.NetDown)}  ↑ {DockFormat.Speed(info.NetUp)}",
                Icon = new IconInfo(SysMonIcons.Network),
            },
            new ListItem(_batPage)
            {
                Title = info.BatteryPercent >= 0
                    ? Loc.Format("MainPage.BatteryTitle", $"{info.BatteryPercent:F0}", DockFormat.BatteryStatusText(info.BatteryStatus))
                    : Loc.Get("MainPage.BatteryUnavailable"),
                Subtitle = Loc.Get("MainPage.BatterySubtitle"),
                Icon = new IconInfo(SysMonIcons.Battery),
            },
            new ListItem(_sensorPage)
            {
                Title = Loc.Get("MainPage.SensorListTitle"),
                Subtitle = GetSensorSubtitle(),
                Icon = new IconInfo(SysMonIcons.Sensors),
            },
            new ListItem(_brokerDiagnosticsPage)
            {
                Title = Loc.Get("MainPage.BrokerDiagnosticsTitle"),
                Subtitle = GetBrokerDiagnosticsSubtitle(),
                Icon = new IconInfo(SysMonIcons.Diagnostics),
            },
            new ListItem(_btopCmd)
            {
                Title = Loc.Get("MainPage.BtopTitle"),
                Subtitle = Loc.Get("MainPage.BtopSubtitle"),
                Icon = new IconInfo(SysMonIcons.Terminal),
            },
        ];
    }

    private static string BackendStatusText(SensorBackend b) => b switch
    {
        SensorBackend.Broker => Loc.Get("Backend.Broker"),
        SensorBackend.HWiNFO => Loc.Get("Backend.Hwinfo"),
        SensorBackend.ThermalZone => Loc.Get("Backend.ThermalZone"),
        SensorBackend.None => Loc.Get("Backend.None"),
        _ => Loc.Get("Backend.Unknown"),
    };

    private static string GetNamedCpuSubtitle(SystemSnapshot info)
    {
        string cpuName = SystemInfoService.CpuName.ToUpperInvariant().Trim();
        string details = info.CpuTemperature >= 0
            ? Loc.Format("MainPage.CpuSubtitleTemp", $"{info.CpuTemperature:F0}", Environment.ProcessorCount)
            : Loc.Format("MainPage.CpuSubtitleNoTemp", Environment.ProcessorCount);

        return $"{cpuName} · {details}";
    }

    private static string GetSensorSubtitle()
    {
        var broker = BrokerPushReceiver.Instance;
        bool isBrokerAvailable = broker.TryGetAvailableSnapshot(out var snap);

        if (isBrokerAvailable)
        {
            return snap.AllSensors.Count > 0
                ? Loc.Format("MainPage.SensorSubtitleConnected", snap.AllSensors.Count)
                : Loc.Get("MainPage.SensorSubtitleNoData");
        }

        if (snap.LastPush != DateTime.MinValue)
        {
            int seconds = Math.Max(0, (int)(DateTime.UtcNow - snap.LastPush).TotalSeconds);
            return Loc.Format("MainPage.SensorSubtitleStale", seconds);
        }

        var diag = SharedMemoryReader.Diagnostics;
        if (!string.IsNullOrWhiteSpace(diag.LastError))
            return Loc.Format("MainPage.SensorSubtitleError", diag.LastError);

        return Loc.Get("MainPage.SensorSubtitleUnavailable");
    }

    private static string GetBrokerDiagnosticsSubtitle()
    {
        var broker = BrokerPushReceiver.Instance;
        bool isBrokerAvailable = broker.TryGetAvailableSnapshot(out var snap);
        var diag = SharedMemoryReader.Diagnostics;
        int pid = BrokerDetector.GetBrokerPid();

        if (isBrokerAvailable)
            return Loc.Format("MainPage.BrokerDiagnosticsConnected", snap.AllSensors.Count);

        if (pid <= 0)
            return Loc.Get("MainPage.BrokerDiagnosticsNotRunning");

        if (!diag.IsConnected)
            return Loc.Get("MainPage.BrokerDiagnosticsShmUnavailable");

        return Loc.Get("MainPage.BrokerDiagnosticsStale");
    }

    private static int GetDiskCount(SystemSnapshot info) =>
        info.PhysicalDisks is { Length: > 0 } ? info.PhysicalDisks.Length : info.Disks.Length;

    private static string GetDiskSubtitle(SystemSnapshot info)
    {
        if (info.PhysicalDisks is { Length: > 0 })
        {
            return string.Join(" · ", info.PhysicalDisks.Select(d =>
            {
                string protocol = string.IsNullOrWhiteSpace(d.InterfaceType) ? "—" : d.InterfaceType;
                var partitions = d.Partitions ?? [];
                long partTotal = partitions.Sum(p => p.TotalBytes);
                long partUsed = partitions.Sum(p => p.TotalBytes - p.FreeBytes);
                double usedPct = partTotal > 0 ? partUsed * 100.0 / partTotal : 0;
                return $"{protocol} {usedPct:F0}%";
            }));
        }

        return string.Join(" · ", info.Disks.Select(d => $"{d.Name} {d.UsedPercent:F0}%"));
    }

    /// <summary>
    /// 释放所有已创建的详情页实例；从未进入过的页面（Lazy 未创建）不触发实例化。
    /// 改造前 Dispose 覆盖 7 页且漏了 BrokerDiagnosticsPage，现在 8 个壳全部覆盖。
    /// </summary>
    public void Dispose()
    {
        _cpuPage.Dispose();
        _memPage.Dispose();
        _diskPage.Dispose();
        _netPage.Dispose();
        _batPage.Dispose();
        _gpuPage.Dispose();
        _sensorPage.Dispose();
        _brokerDiagnosticsPage.Dispose();
    }
}
