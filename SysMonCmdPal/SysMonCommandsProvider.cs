// Copyright (c) 2026 SysMonCmdPal
// CommandsProvider: registers top-level commands and individual dock bands.
// Each dock band is a separate ICommandItem → independently pinnable in CmdPal dock.

using System;
using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

public partial class SysMonCommandsProvider : CommandProvider
{
    // Static dock bands (always present)
    private readonly WrappedDockItem[] _staticBands;

    private readonly ICommandItem _rootCommand;

    public SysMonCommandsProvider()
    {
        Id = "SysMonCmdPal";
        DisplayName = Loc.Get("Provider.DisplayName");
        Icon = new IconInfo(""); // LightningBolt
        Frozen = false;

        // M11: PrecisionMode 设置已移除 — 传感器回退链自动选择最优数据源
        // (Broker → HWiNFO → ThermalZone)，无需用户手动切换。
        Settings = new Settings();

        _rootCommand = new CommandItem(new SysMonMainPage())
        {
            Title = Loc.Get("Provider.Title"),
            Subtitle = Loc.Get("Provider.Subtitle"),
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
    /// Returns static dock bands only.
    /// Each is an independently-pinnable atomic band.
    /// </summary>
    public override ICommandItem[]? GetDockBands()
    {
        return [.. _staticBands];
    }

    public override ICommandItem? GetCommandItem(string id)
    {
        foreach (var band in _staticBands)
        {
            if (string.Equals(band.Command?.Id, id, StringComparison.OrdinalIgnoreCase))
                return band;
        }
        return null;
    }

    public override void Dispose()
    {
        DockBandRefreshCoordinator.Shutdown();
        base.Dispose();
    }
}
