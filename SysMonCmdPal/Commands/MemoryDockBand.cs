// Copyright (c) 2026 SysMonCmdPal

using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>Dock band: Memory usage in % and GB. Links to MemoryDetailPage.</summary>
internal sealed partial class MemoryDockBand : WrappedDockItem
{
    private readonly ListItem _memItem;

    public MemoryDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.memory", "内存")
    {
        _memItem = new ListItem(new MemoryDetailPage())
        {
            Title = Loc.Get("Dock.Memory"),
            Subtitle = Loc.Get("Common.Loading"),
            Icon = new IconInfo(""),
        };
        Items = [_memItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        var used = info.MemoryUsedBytes / (1024.0 * 1024 * 1024);
        var total = info.MemoryTotalBytes / (1024.0 * 1024 * 1024);
        _memItem.Title = Loc.Format("Dock.MemoryTitle", $"{info.MemoryUsed:F0}");
        _memItem.Subtitle = Loc.Format("Dock.MemorySubtitle", $"{used:F1}", $"{total:F1}");
    }
}
