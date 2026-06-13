// Copyright (c) 2026 SysMonCmdPal
// 主页面 — 系统概览列表（CPU / 内存 / 磁盘 / 网络 / 电池 / GPU）

using System.Diagnostics;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>
/// Command Palette 中打开的主页面。
/// 以列表形式展示各子系统，选中后进入详情页。
/// </summary>
internal sealed partial class SysMonMainPage : ListPage
{
    private readonly SystemInfoService _sysInfo = SystemInfoService.Instance;

    public SysMonMainPage()
    {
        Icon = new IconInfo("");       // LightningBolt
        Title = "系统监控";
        Name = "Open";
    }

    public override IListItem[] GetItems()
    {
        _sysInfo.Refresh();
        var info = _sysInfo.Current;

        return [
            new ListItem(new CpuDetailPage())
            {
                Title = $"CPU — {info.CpuUsage:F0}%",
                Subtitle = info.CpuTemperature >= 0
                    ? $"温度 {info.CpuTemperature:F0}°C · {Environment.ProcessorCount} 核心"
                    : $"{Environment.ProcessorCount} 核心",
                Icon = new IconInfo(""),
            },
            new ListItem(new MemoryDetailPage())
            {
                Title = $"内存 — {info.MemoryUsed:F0}%",
                Subtitle = $"{(info.MemoryUsedBytes / (1024.0 * 1024 * 1024)):F1} / {(info.MemoryTotalBytes / (1024.0 * 1024 * 1024)):F1} GB",
                Icon = new IconInfo(""),
            },
            new ListItem(new DiskDetailPage())
            {
                Title = $"磁盘 ({info.Disks.Length} 个驱动器)",
                Subtitle = string.Join(" · ", info.Disks.Select(d => $"{d.Name} {d.UsedPercent:F0}%")),
                Icon = new IconInfo(""),
            },
            new ListItem(new NetworkDetailPage())
            {
                Title = "网络",
                Subtitle = $"↓ {DockFormat.Speed(info.NetDown)}  ↑ {DockFormat.Speed(info.NetUp)}",
                Icon = new IconInfo(""),
            },
            new ListItem(new BatteryDetailPage())
            {
                Title = info.BatteryPercent >= 0
                    ? $"电池 — {info.BatteryPercent:F0}% [{DockFormat.BatteryStatusText(info.BatteryStatus)}]"
                    : "电池 — 不可用",
                Subtitle = "电源状态",
                Icon = new IconInfo(""),
            },
            new ListItem(new GpuDetailPage())
            {
                Title = info.Gpus.Length > 0
                    ? (info.Gpus.Length == 1
                        ? $"GPU — {info.Gpus[0].Name}"
                        : $"GPU — {info.Gpus.Length} 张显卡")
                    : "GPU — 不可用",
                Subtitle = info.Gpus.Length > 0
                    ? string.Join(" | ", info.Gpus.Select(g =>
                        $"{g.Name}: {DockFormat.Temp(g.Temperature)}"))
                    : (info.CpuTemperature >= 0
                        ? $"传感器后端: {BackendStatusText(info.Backend)}"
                        : ""),
                Icon = new IconInfo(""),
            },
            // btop4win 启动器
            new ListItem(new BtopLauncherCommand())
            {
                Title = "启动 btop4win",
                Subtitle = "完整系统监控：进程管理 + 所有传感器",
                Icon = new IconInfo(""),
            },
            // 全量传感器列表（LHM）
            new ListItem(new SensorListPage())
            {
                Title = "传感器列表",
                Subtitle = "浏览所有硬件传感器，选择添加到 Dock 栏",
                Icon = new IconInfo(""),
            },
        ];
    }

    private static string BackendStatusText(SensorBackend b) => b switch
    {
        SensorBackend.Lhm => "LHM (PawnIO) ✓",
        SensorBackend.AmdAdl => "ADL 回退 (仅 CPU)",
        SensorBackend.HwInfo => "HWiNFO 回退",
        SensorBackend.None => "无可用传感器后端",
        _ => "未知",
    };
}
