// Copyright (c) 2026 SysMonCmdPal
// 磁盘详情页 — 列表展示各分区（IO 读写速度优先 + 使用率 + 卷标）

using System;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class DiskDetailPage : ListPage
{
    public DiskDetailPage()
    {
        Icon = new IconInfo("");
        Title = "磁盘详情";
        Name = "磁盘";
    }

    public override IListItem[] GetItems()
    {
        var sys = SystemInfoService.Instance;
        sys.Refresh();
        var info = sys.Current;

        return info.Disks
            .Select(d =>
            {
                double totalGB = d.TotalBytes / (1024.0 * 1024 * 1024);
                double freeGB = d.FreeBytes / (1024.0 * 1024 * 1024);

                string label = string.IsNullOrEmpty(d.VolumeLabel) ? "" : $" ({d.VolumeLabel})";

                // IO 速度优先
                string ioStr = d.ReadBytesPerSec >= 0 && d.WriteBytesPerSec >= 0
                    ? $"R:{DockFormat.Speed(d.ReadBytesPerSec)}  W:{DockFormat.Speed(d.WriteBytesPerSec)}  |  "
                    : "";

                return new ListItem(new CopyTextCommand($"{d.Name} R:{DockFormat.Speed(d.ReadBytesPerSec)} W:{DockFormat.Speed(d.WriteBytesPerSec)}"))
                {
                    Title = $"{d.Name}{label}",
                    Subtitle = $"{ioStr}已用 {d.UsedPercent:F0}% · 空闲 {freeGB:F1} / {totalGB:F1} GB",
                    Icon = new IconInfo(d.UsedPercent > 90 ? "" : ""),
                };
            })
            .ToArray();
    }

}
