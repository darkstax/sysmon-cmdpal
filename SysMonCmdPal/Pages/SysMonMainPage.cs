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
    private readonly BtopLauncherCommand _btopCmd = new();

    public SysMonMainPage()
    {
        Icon = new IconInfo("");       // LightningBolt
        Title = Loc.Get("MainPage.Title");
        Name = "Open";

        // 启动 10 秒后预渲染详情页 — 用户点进去就能立刻看到数据
        var preWarmTimer = new System.Timers.Timer(10000) { AutoReset = false };
        preWarmTimer.Elapsed += (_, _) =>
        {
            try
            {
                _cpuPage.StartTimer();
                _memPage.StartTimer();
                _batPage.StartTimer();
                _netPage.StartTimer();
                _gpuPage.StartTimer();
                _diskPage.StartTimer();
            }
            catch { }
        };
        preWarmTimer.Start();
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
                    : $"{SystemInfoService.CpuName.ToUpper().Trim()} · {(info.CpuTemperature >= 0 ? $"{info.CpuTemperature:F0}°C · " : "")}{Environment.ProcessorCount} 核心",
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
                Title = Loc.Format("MainPage.DiskTitle", info.Disks.Length),
                Subtitle = string.Join(" · ", info.Disks.Select(d => $"{d.Name} {d.UsedPercent:F0}%")),
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

    private static string GetSensorSubtitle()
    {
        var snap = BrokerPushReceiver.Instance.Snapshot;
        return snap.IsAlive && snap.IsFresh
            ? Loc.Format("MainPage.SensorSubtitleConnected", snap.AllSensors.Count)
            : Loc.Get("MainPage.SensorSubtitleDisconnected");
    }
}
