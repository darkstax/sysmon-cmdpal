// Copyright (c) 2026 SysMonCmdPal
// CommandsProvider: registers top-level commands and individual dock bands.
// Each dock band is a separate ICommandItem → independently pinnable in CmdPal dock.
// Dynamic sensor dock bands are generated from user config (LhmSensorService).

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

public partial class SysMonCommandsProvider : CommandProvider
{
    // Static dock bands (always present)
    private readonly WrappedDockItem[] _staticBands;

    // Dynamic sensor dock bands (from user config) — rebuilt on each GetDockBands()
    private List<SensorDockBand> _sensorBands = [];

    private readonly ICommandItem _rootCommand;

    public SysMonCommandsProvider()
    {
        Id = "SysMonCmdPal";
        DisplayName = "系统监控";
        Icon = new IconInfo(""); // LightningBolt
        Frozen = false;

        _rootCommand = new CommandItem(new SysMonMainPage())
        {
            Title = "系统监控",
            Subtitle = "CPU · 内存 · 磁盘 · 网络 · 电池 · GPU · 传感器",
        };

        _staticBands = [
            new CpuDockBand(),
            new MemoryDockBand(),
            new DiskDockBand(),
            new NetworkDownDockBand(),
            new NetworkUpDockBand(),
            new BatteryDockBand(),
            new GpuDockBand(),
        ];
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return [_rootCommand];
    }

    /// <summary>
    /// Returns static dock bands + user-configured sensor dock bands.
    /// Each is an independently-pinnable atomic band.
    /// </summary>
    public override ICommandItem[]? GetDockBands()
    {
        // Rebuild dynamic sensor bands from config
        foreach (var band in _sensorBands)
            DockBandRefreshCoordinator.Unsubscribe(band.OnRefresh);
        _sensorBands.Clear();

        var config = LhmSensorService.Instance.Config;
        foreach (var entry in config.Sensors.Where(e => e.InDock).OrderBy(e => e.Order))
        {
            var band = new SensorDockBand(entry.Key, entry.DisplayName ?? entry.Key);
            _sensorBands.Add(band);
        }

        return [.. _staticBands, .. _sensorBands];
    }

    public override ICommandItem? GetCommandItem(string id)
    {
        return _staticBands.FirstOrDefault(b =>
            string.Equals(b.Command?.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? (ICommandItem?)_sensorBands.FirstOrDefault(b =>
            string.Equals(b.Command?.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public override void Dispose()
    {
        foreach (var band in _sensorBands)
            DockBandRefreshCoordinator.Unsubscribe(band.OnRefresh);
        DockBandRefreshCoordinator.Shutdown();
        LhmSensorService.Instance.Shutdown();
        base.Dispose();
    }
}
