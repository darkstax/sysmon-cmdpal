// Copyright (c) 2026 SysMonCmdPal
// CommandsProvider: registers top-level commands and individual dock bands.
// Each dock band is a separate ICommandItem → independently pinnable in CmdPal dock.
// Dynamic sensor dock bands are generated from user config (LhmSensorService).

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

public partial class SysMonCommandsProvider : CommandProvider
{
    // Static dock bands (always present)
    private readonly WrappedDockItem[] _staticBands;

    // Dynamic sensor dock bands (from user config) — rebuilt on each GetDockBands()
    private List<SensorDockBand> _sensorBands = [];

    private readonly ICommandItem _rootCommand;

    // 高精度模式（商店版: HWiNFO 优先）
    private readonly ChoiceSetSetting _precisionModeSetting;
    // 传感器链设置
    private readonly ChoiceSetSetting _cpuPrimarySource;
    private readonly ChoiceSetSetting _cpuSecondarySource;
    private readonly ChoiceSetSetting _cpuTertiarySource;
    private readonly ChoiceSetSetting _gpuPrimarySource;
    private readonly ChoiceSetSetting _gpuSecondarySource;
    private readonly ChoiceSetSetting _gpuTertiarySource;
    private readonly ChoiceSetSetting _gpuModeSetting;
    private readonly Settings _settingsObj;

    // 传感器源选项（商店版，全部用户态，Broker 按需显示）
    private static List<ChoiceSetSetting.Choice> GetSourceChoices()
    {
        var choices = new List<ChoiceSetSetting.Choice>
        {
            new("HWiNFO (精确, 无需驱动)", "HWiNFO"),
            new("ThermalZone (ACPI)", "ThermalZone"),
            new("ADL (AMD 用户态)", "ADL"),
            new("LHM (传感器库)", "LHM"),
        };

        // 仅当 Broker 进程运行中时显示 Broker 选项
        if (BrokerDetector.IsBrokerRunning() || BrokerPushReceiver.Instance.IsBrokerAvailable)
        {
            choices.Insert(0, new("Broker (最精准)", "Broker"));
        }

        return choices;
    }

    // 高精度模式选项（Broker 按需显示）
    private static List<ChoiceSetSetting.Choice> GetPrecisionChoices()
    {
        var choices = new List<ChoiceSetSetting.Choice>
        {
            new("无 (仅 ACPI)", "None"),
            new("HWiNFO (精确, 无需驱动)", "HWiNFO"),
        };

        if (BrokerDetector.IsBrokerRunning() || BrokerPushReceiver.Instance.IsBrokerAvailable)
        {
            choices.Add(new("Broker (最精准, 需 PawnIO)", "Broker"));
        }

        return choices;
    }

    // GPU 模式选项
    private static readonly List<ChoiceSetSetting.Choice> GpuModeChoices =
    [
        new("Auto (智能筛选)", "Auto"),
        new("DedicatedOnly (仅独显)", "DedicatedOnly"),
        new("All (全部)", "All"),
    ];

    public SysMonCommandsProvider()
    {
        Id = "SysMonCmdPal";
        DisplayName = "系统监控";
        Icon = new IconInfo(""); // LightningBolt
        Frozen = false;

        // 从文件加载完整配置
        var savedConfig = SensorChainConfig.Load();

        // 高精度模式
        _precisionModeSetting = new ChoiceSetSetting(
            "precisionMode",
            "高精度温度源",
            "选择最高精度的 CPU 温度数据源。推荐 HWiNFO。",
            GetPrecisionChoices())
        { Value = savedConfig.PrecisionModeStr, IgnoreUnknownValue = true };

        // CPU 传感器链
        string cpu1 = savedConfig.CpuChain.Count > 0 ? savedConfig.CpuChain[0] : "HWiNFO";
        string cpu2 = savedConfig.CpuChain.Count > 1 ? savedConfig.CpuChain[1] : "ADL";
        string cpu3 = savedConfig.CpuChain.Count > 2 ? savedConfig.CpuChain[2] : "ThermalZone";
        _cpuPrimarySource = new ChoiceSetSetting("cpuPrimary", "CPU 主数据源", "最高优先级的数据源", GetSourceChoices())
            { Value = cpu1, IgnoreUnknownValue = true };
        _cpuSecondarySource = new ChoiceSetSetting("cpuSecondary", "CPU 次级数据源", "主数据源不可用时的回退", GetSourceChoices())
            { Value = cpu2, IgnoreUnknownValue = true };
        _cpuTertiarySource = new ChoiceSetSetting("cpuTertiary", "CPU 三级数据源", "次级数据源不可用时的最终回退", GetSourceChoices())
            { Value = cpu3, IgnoreUnknownValue = true };

        // GPU 传感器链
        string gpu1 = savedConfig.GpuChain.Count > 0 ? savedConfig.GpuChain[0] : "LHM";
        string gpu2 = savedConfig.GpuChain.Count > 1 ? savedConfig.GpuChain[1] : "ADL";
        string gpu3 = savedConfig.GpuChain.Count > 2 ? savedConfig.GpuChain[2] : "HWiNFO";
        _gpuPrimarySource = new ChoiceSetSetting("gpuPrimary", "GPU 主数据源", "最高优先级的数据源", GetSourceChoices())
            { Value = gpu1, IgnoreUnknownValue = true };
        _gpuSecondarySource = new ChoiceSetSetting("gpuSecondary", "GPU 次级数据源", "主数据源不可用时的回退", GetSourceChoices())
            { Value = gpu2, IgnoreUnknownValue = true };
        _gpuTertiarySource = new ChoiceSetSetting("gpuTertiary", "GPU 三级数据源", "次级数据源不可用时的最终回退", GetSourceChoices())
            { Value = gpu3, IgnoreUnknownValue = true };

        // GPU 模式
        _gpuModeSetting = new ChoiceSetSetting("gpuMode", "GPU 模式", "控制 GPU 列表的筛选方式", GpuModeChoices)
            { Value = savedConfig.GpuModeStr, IgnoreUnknownValue = true };

        _settingsObj = new Settings();
        _settingsObj.Add(_precisionModeSetting);
        _settingsObj.Add(_cpuPrimarySource);
        _settingsObj.Add(_cpuSecondarySource);
        _settingsObj.Add(_cpuTertiarySource);
        _settingsObj.Add(_gpuPrimarySource);
        _settingsObj.Add(_gpuSecondarySource);
        _settingsObj.Add(_gpuTertiarySource);
        _settingsObj.Add(_gpuModeSetting);
        _settingsObj.SettingsChanged += OnSettingsChanged;
        Settings = _settingsObj;

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

    // ==================== Settings persistence ====================

    private void OnSettingsChanged(object sender, Settings args)
    {
        var config = new SensorChainConfig
        {
            PrecisionModeStr = _precisionModeSetting.Value ?? "HWiNFO",
            CpuChain =
            [
                _cpuPrimarySource.Value ?? "HWiNFO",
                _cpuSecondarySource.Value ?? "ADL",
                _cpuTertiarySource.Value ?? "ThermalZone",
            ],
            GpuChain =
            [
                _gpuPrimarySource.Value ?? "LHM",
                _gpuSecondarySource.Value ?? "ADL",
                _gpuTertiarySource.Value ?? "HWiNFO",
            ],
            GpuModeStr = _gpuModeSetting.Value ?? "Auto",
        };
        config.Save();

        SensorLogger.ForceLog($"[Settings] 已保存: CPU链=[{string.Join(", ", config.CpuChain)}], " +
            $"GPU链=[{string.Join(", ", config.GpuChain)}], GPU模式={config.GpuModeStr}");
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
