// Copyright (c) 2026 SysMonCmdPal
// 主页面 — 系统概览列表（CPU / 内存 / 磁盘 / 网络 / 电池 / GPU）

using System.Diagnostics;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

/// <summary>
/// Command Palette 中打开的主页面。
/// 以列表形式展示各子系统，选中后进入详情页。
/// </summary>
internal sealed partial class SysMonMainPage : ListPage
{
    private readonly SystemInfoService _sysInfo = SystemInfoService.Instance;

    // 缓存详情页实例 — 避免每次 GetItems() 创建新实例导致 timer 泄漏和页面不可复用
    private readonly CpuDetailPage _cpuPage = new();
    private readonly MemoryDetailPage _memPage = new();
    private readonly DiskDetailPage _diskPage = new();
    private readonly NetworkDetailPage _netPage = new();
    private readonly BatteryDetailPage _batPage = new();
    private readonly GpuDetailPage _gpuPage = new();
    private readonly SensorListPage _sensorPage = new();
    private readonly BrokerDiagnosticsPage _brokerDiagnosticsPage = new();
    private readonly BtopLauncherCommand _btopCmd = new();

    public SysMonMainPage()
    {
        Icon = new IconInfo("");       // LightningBolt
        Title = Loc.Get("MainPage.Title");
        Name = "Open";
        // P2: 不再需要 preWarmTimer — 详情页在 GetContent() 时自动订阅 DockBandRefreshCoordinator
    }

    public override IListItem[] GetItems()
    {
        // 用已缓存快照，不触发同步 Refresh（避免阻塞 UI）
        var info = _sysInfo.Current;

        return [
            new ListItem(_cpuPage)
            {
                Title = Loc.Format("MainPage.CpuTitle", $"{info.CpuUsage:F0}"),
                Subtitle = string.IsNullOrEmpty(SystemInfoService.CpuName)
                    ? (info.CpuTemperature >= 0
                        ? Loc.Format("MainPage.CpuSubtitleTemp", $"{info.CpuTemperature:F0}", Environment.ProcessorCount)
                        : Loc.Format("MainPage.CpuSubtitleNoTemp", Environment.ProcessorCount))
                    : GetNamedCpuSubtitle(info),
                Icon = new IconInfo(""),
            },
            new ListItem(_memPage)
            {
                Title = Loc.Format("MainPage.MemoryTitle", $"{info.MemoryUsed:F0}"),
                Subtitle = $"{(info.MemoryUsedBytes / (1024.0 * 1024 * 1024)):F1} / {(info.MemoryTotalBytes / (1024.0 * 1024 * 1024)):F1} GB",
                Icon = new IconInfo(""),
            },
            new ListItem(_diskPage)
            {
                Title = Loc.Format("MainPage.DiskTitle", GetDiskCount(info)),
                Subtitle = GetDiskSubtitle(info),
                Icon = new IconInfo(""),
            },
            new ListItem(_netPage)
            {
                Title = Loc.Get("MainPage.NetworkTitle"),
                Subtitle = $"↓ {DockFormat.Speed(info.NetDown)}  ↑ {DockFormat.Speed(info.NetUp)}",
                Icon = new IconInfo(""),
            },
            new ListItem(_batPage)
            {
                Title = info.BatteryPercent >= 0
                    ? Loc.Format("MainPage.BatteryTitle", $"{info.BatteryPercent:F0}", DockFormat.BatteryStatusText(info.BatteryStatus))
                    : Loc.Get("MainPage.BatteryUnavailable"),
                Subtitle = Loc.Get("MainPage.BatterySubtitle"),
                Icon = new IconInfo(""),
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
                Icon = new IconInfo(""),
            },
            new ListItem(_sensorPage)
            {
                Title = Loc.Get("MainPage.SensorListTitle"),
                Subtitle = GetSensorSubtitle(),
                Icon = new IconInfo(""),
            },
            new ListItem(_brokerDiagnosticsPage)
            {
                Title = Loc.Get("MainPage.BrokerDiagnosticsTitle"),
                Subtitle = GetBrokerDiagnosticsSubtitle(),
                Icon = new IconInfo(""),
            },
            new ListItem(_btopCmd)
            {
                Title = Loc.Get("MainPage.BtopTitle"),
                Subtitle = Loc.Get("MainPage.BtopSubtitle"),
                Icon = new IconInfo(""),
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
        var snap = BrokerPushReceiver.Instance.Snapshot;
        var diag = SharedMemoryReader.Diagnostics;

        if (snap.IsAlive && snap.IsFresh)
        {
            return snap.AllSensors.Count > 0
                ? Loc.Format("MainPage.SensorSubtitleConnected", snap.AllSensors.Count)
                : Loc.Get("MainPage.SensorSubtitleNoData");
        }

        if (diag.IsConnected && snap.LastPush != DateTime.MinValue)
        {
            int seconds = Math.Max(0, (int)(DateTime.UtcNow - snap.LastPush).TotalSeconds);
            return Loc.Format("MainPage.SensorSubtitleStale", seconds);
        }

        if (!string.IsNullOrWhiteSpace(diag.LastError))
            return Loc.Format("MainPage.SensorSubtitleError", diag.LastError);

        return Loc.Get("MainPage.SensorSubtitleUnavailable");
    }

    private static string GetBrokerDiagnosticsSubtitle()
    {
        var snap = BrokerPushReceiver.Instance.Snapshot;
        var diag = SharedMemoryReader.Diagnostics;
        int pid = BrokerDetector.GetBrokerPid();

        if (pid <= 0)
            return Loc.Get("MainPage.BrokerDiagnosticsNotRunning");

        if (!diag.IsConnected)
            return Loc.Get("MainPage.BrokerDiagnosticsShmUnavailable");

        if (!snap.IsFresh)
            return Loc.Get("MainPage.BrokerDiagnosticsStale");

        return Loc.Format("MainPage.BrokerDiagnosticsConnected", diag.LastSensorCount);
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
}
