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

    // 高精度模式（简化为 Broker / 无 二选一）
    private readonly ChoiceSetSetting _precisionModeSetting;

    // 精度模式选项（仅 Broker / 无）
    private static readonly List<ChoiceSetSetting.Choice> PrecisionChoices =
    [
        new(Loc.Get("Provider.PrecisionChoiceBroker"), "Broker"),
        new(Loc.Get("Provider.PrecisionChoiceNone"), "None"),
    ];

    public SysMonCommandsProvider()
    {
        Id = "SysMonCmdPal";
        DisplayName = Loc.Get("Provider.DisplayName");
        Icon = new IconInfo(""); // LightningBolt
        Frozen = false;

        // 从文件加载完整配置
        var savedConfig = SensorChainConfig.Load();

        // 高精度模式（仅 Broker/None）
        _precisionModeSetting = new ChoiceSetSetting(
            "precisionMode",
            Loc.Get("Provider.PrecisionModeLabel"),
            Loc.Get("Provider.PrecisionModeDescription"),
            PrecisionChoices)
        { Value = savedConfig.PrecisionModeStr, IgnoreUnknownValue = true };

        var settingsObj = new Settings();
        settingsObj.Add(_precisionModeSetting);
        settingsObj.SettingsChanged += OnSettingsChanged;
        Settings = settingsObj;

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

    // ==================== Settings persistence ====================

    private void OnSettingsChanged(object sender, Settings args)
    {
        var config = SensorChainConfig.Load();
        config.PrecisionModeStr = _precisionModeSetting.Value ?? "Broker";
        config.Save();

        SensorLogger.ForceLog($"[Settings] 已保存: 精度模式={config.PrecisionModeStr}");
    }

    public override void Dispose()
    {
        DockBandRefreshCoordinator.Shutdown();
        base.Dispose();
    }
}
