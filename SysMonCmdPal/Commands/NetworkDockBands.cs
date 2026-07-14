// Copyright (c) 2026 SysMonCmdPal

using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>Dock band: Network download speed. Links to NetworkDetailPage.</summary>
internal sealed partial class NetworkDownDockBand : WrappedDockItem
{
    private readonly ListItem _netItem;

    public NetworkDownDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.network.down", "下载")
    {
        _netItem = new ListItem(new NetworkDetailPage())
        {
            Title = Loc.Get("Dock.Download"),
            Subtitle = Loc.Get("Common.Loading"),
            Icon = new IconInfo(SysMonIcons.NetworkDown),
        };
        Items = [_netItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        _netItem.Title = $"↓ {DockFormat.Speed(info.NetDown)}";
        _netItem.Subtitle = Loc.Get("Dock.Download");
    }
}

/// <summary>Dock band: Network upload speed. Links to NetworkDetailPage.</summary>
internal sealed partial class NetworkUpDockBand : WrappedDockItem
{
    private readonly ListItem _netItem;

    public NetworkUpDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.network.up", "上传")
    {
        _netItem = new ListItem(new NetworkDetailPage())
        {
            Title = Loc.Get("Dock.Upload"),
            Subtitle = Loc.Get("Common.Loading"),
            Icon = new IconInfo(SysMonIcons.NetworkUp),
        };
        Items = [_netItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        _netItem.Title = $"↑ {DockFormat.Speed(info.NetUp)}";
        _netItem.Subtitle = Loc.Get("Dock.Upload");
    }
}
