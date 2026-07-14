// Copyright (c) 2026 SysMonCmdPal

using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>Dock band: Battery percentage and status. Links to BatteryDetailPage.</summary>
internal sealed partial class BatteryDockBand : WrappedDockItem
{
    private readonly ListItem _batItem;

    public BatteryDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.battery", "电池")
    {
        _batItem = new ListItem(new BatteryDetailPage())
        {
            Title = Loc.Get("Dock.Battery"),
            Subtitle = Loc.Get("Common.Loading"),
            Icon = new IconInfo(SysMonIcons.Battery),
        };
        Items = [_batItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        if (info.BatteryPercent < 0)
        {
            _batItem.Title = Loc.Get("Dock.BatteryNoBattery");
            _batItem.Subtitle = Loc.Get("Dock.BatteryDesktop");
            return;
        }
        _batItem.Title = Loc.Format("Dock.BatteryTitle", $"{info.BatteryPercent:F0}");
        _batItem.Subtitle = DockFormat.BatteryStatusText(info.BatteryStatus);
    }
}
