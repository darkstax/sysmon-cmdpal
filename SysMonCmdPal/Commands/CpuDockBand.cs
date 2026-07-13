// Copyright (c) 2026 SysMonCmdPal

using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>Dock band: CPU usage + temperature. Links to CpuDetailPage.</summary>
internal sealed partial class CpuDockBand : WrappedDockItem
{
    private readonly ListItem _cpuItem;

    public CpuDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.cpu", "CPU")
    {
        _cpuItem = new ListItem(new CpuDetailPage())
        {
            Title = Loc.Get("Dock.Cpu"),
            Subtitle = Loc.Get("Common.Loading"),
            Icon = new IconInfo(""),
        };
        Items = [_cpuItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        _cpuItem.Title = info.CpuTemperature >= 0
            ? Loc.Format("Dock.CpuTitle", $"{info.CpuUsage:F0}", DockFormat.Temp(info.CpuTemperature))
            : Loc.Format("Dock.CpuTitleNoTemp", $"{info.CpuUsage:F0}");
        _cpuItem.Subtitle = info.CpuUsage switch
        {
            >= 90 => Loc.Get("Dock.CpuHighLoad"),
            >= 70 => Loc.Get("Dock.CpuElevated"),
            >= 40 => Loc.Get("Dock.CpuMedium"),
            _ => Loc.Get("Dock.CpuIdle"),
        };
    }
}
