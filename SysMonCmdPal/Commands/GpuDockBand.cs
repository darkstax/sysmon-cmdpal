// Copyright (c) 2026 SysMonCmdPal

using System;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>Dock band: GPU usage, temperature. Links to GpuDetailPage.</summary>
internal sealed partial class GpuDockBand : WrappedDockItem
{
    private readonly ListItem _gpuItem;

    public GpuDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.gpu", "GPU")
    {
        _gpuItem = new ListItem(new GpuDetailPage())
        {
            Title = Loc.Get("Dock.Gpu"),
            Subtitle = Loc.Get("Common.Loading"),
            Icon = new IconInfo(""),
        };
        Items = [_gpuItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        var gpus = info.Gpus;
        if (gpus == null || gpus.Length == 0)
        {
            _gpuItem.Title = Loc.Get("Dock.GpuUnavailable");
            _gpuItem.Subtitle = Loc.Get("Dock.GpuSubtitle");
            return;
        }

        var primary = gpus.OrderByDescending(g => g.MemoryTotalMB > 0 ? 1 : 0)
                          .ThenByDescending(g => g.UsagePercent > 0 ? 1 : 0)
                          .ThenByDescending(g => g.Temperature)
                          .First();
        _gpuItem.Title = primary.Temperature >= 0
            ? Loc.Format("Dock.GpuTitle", $"{primary.UsagePercent:F0}", DockFormat.Temp(primary.Temperature))
            : Loc.Format("Dock.GpuTitleNoTemp", DockFormat.Percent(primary.UsagePercent));
        _gpuItem.Subtitle = gpus.Length > 1
            ? $"{primary.Name} (+{gpus.Length - 1})"
            : primary.Name;
    }
}
