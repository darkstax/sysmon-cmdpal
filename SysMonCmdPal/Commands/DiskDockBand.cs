// Copyright (c) 2026 SysMonCmdPal

using System;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>Dock band: Disk IO speed (primary) + usage. Links to DiskDetailPage.</summary>
internal sealed partial class DiskDockBand : WrappedDockItem
{
    private readonly ListItem _diskItem;

    public DiskDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.disk", "磁盘")
    {
        _diskItem = new ListItem(new DiskDetailPage())
        {
            Title = Loc.Get("Dock.Disk"),
            Subtitle = Loc.Get("Common.Loading"),
            Icon = new IconInfo(""),
        };
        Items = [_diskItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        if (info.Disks.Length == 0)
        {
            _diskItem.Title = Loc.Get("Dock.DiskNoDrive");
            _diskItem.Subtitle = "";
            return;
        }

        double totalRead = 0, totalWrite = 0;
        foreach (var d in info.Disks)
        {
            if (d.ReadBytesPerSec > 0) totalRead += d.ReadBytesPerSec;
            if (d.WriteBytesPerSec > 0) totalWrite += d.WriteBytesPerSec;
        }

        _diskItem.Title = $"↓{DockFormat.CompactSpeed(totalRead)} ↑{DockFormat.CompactSpeed(totalWrite)}";
        _diskItem.Subtitle = FormatDiskSubtitle(info);
    }

    private static string FormatDiskSubtitle(SystemSnapshot info)
    {
        if (info.PhysicalDisks is { Length: > 0 })
        {
            return string.Join("  ", info.PhysicalDisks.Select(d =>
            {
                string protocol = string.IsNullOrWhiteSpace(d.InterfaceType) ? "—" : d.InterfaceType;
                var partitions = d.Partitions ?? [];
                long partTotal = partitions.Sum(p => p.TotalBytes);
                long partUsed = partitions.Sum(p => p.TotalBytes - p.FreeBytes);
                double usedPct = partTotal > 0 ? partUsed * 100.0 / partTotal : 0;
                return $"{protocol} {usedPct:F0}%";
            }));
        }

        return string.Join("  ", info.Disks.Select(d => $"{d.Name} {d.UsedPercent:F0}%"));
    }
}
