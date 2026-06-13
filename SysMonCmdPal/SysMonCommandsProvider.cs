// Copyright (c) 2026 SysMonCmdPal
// CommandsProvider: registers top-level commands and individual dock bands.
// Each dock band is a separate ICommandItem → independently pinnable in CmdPal dock.
// Dynamic sensor dock bands are generated from user config (LhmSensorService).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.ApplicationModel;

namespace SysMonCmdPal;

public partial class SysMonCommandsProvider : CommandProvider
{
    // Static dock bands (always present)
    private readonly WrappedDockItem[] _staticBands;

    // Dynamic sensor dock bands (from user config) — rebuilt on each GetDockBands()
    private List<SensorDockBand> _sensorBands = [];

    private readonly ICommandItem _rootCommand;

    // 高精度模式（v3: 下拉选择无/HWiNFO/Broker）
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

    // 传感器源选项
    private static readonly List<ChoiceSetSetting.Choice> SourceChoices =
    [
        new("Broker (高精度)", "Broker"),
        new("ThermalZone (ACPI)", "ThermalZone"),
        new("HWiNFO (回退)", "HWiNFO"),
    ];

    // 高精度模式选项
    private static readonly List<ChoiceSetSetting.Choice> PrecisionChoices =
    [
        new("无 (仅 ACPI)", "None"),
        new("HWiNFO (精确, 无需驱动)", "HWiNFO"),
        new("Broker (最精准, 需 PawnIO)", "Broker"),
    ];

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

        // 高精度模式（v3: 下拉选择 无/HWiNFO/Broker）
        _precisionModeSetting = new ChoiceSetSetting(
            "precisionMode",
            "高精度温度源",
            "选择最高精度的 CPU 温度数据源。\nBroker 需要 PawnIO 驱动；HWiNFO 无需额外驱动。",
            PrecisionChoices)
        { Value = savedConfig.PrecisionModeStr, IgnoreUnknownValue = true };

        // CPU 传感器链
        string cpu1 = savedConfig.CpuChain.Count > 0 ? savedConfig.CpuChain[0] : "Broker";
        string cpu2 = savedConfig.CpuChain.Count > 1 ? savedConfig.CpuChain[1] : "ThermalZone";
        string cpu3 = savedConfig.CpuChain.Count > 2 ? savedConfig.CpuChain[2] : "HWiNFO";
        _cpuPrimarySource = new ChoiceSetSetting("cpuPrimary", "CPU 主数据源", "最高优先级的数据源", SourceChoices)
            { Value = cpu1, IgnoreUnknownValue = true };
        _cpuSecondarySource = new ChoiceSetSetting("cpuSecondary", "CPU 次级数据源", "主数据源不可用时的回退", SourceChoices)
            { Value = cpu2, IgnoreUnknownValue = true };
        _cpuTertiarySource = new ChoiceSetSetting("cpuTertiary", "CPU 三级数据源", "次级数据源不可用时的最终回退", SourceChoices)
            { Value = cpu3, IgnoreUnknownValue = true };

        // GPU 传感器链
        string gpu1 = savedConfig.GpuChain.Count > 0 ? savedConfig.GpuChain[0] : "Broker";
        string gpu2 = savedConfig.GpuChain.Count > 1 ? savedConfig.GpuChain[1] : "ThermalZone";
        string gpu3 = savedConfig.GpuChain.Count > 2 ? savedConfig.GpuChain[2] : "HWiNFO";
        _gpuPrimarySource = new ChoiceSetSetting("gpuPrimary", "GPU 主数据源", "最高优先级的数据源", SourceChoices)
            { Value = gpu1, IgnoreUnknownValue = true };
        _gpuSecondarySource = new ChoiceSetSetting("gpuSecondary", "GPU 次级数据源", "主数据源不可用时的回退", SourceChoices)
            { Value = gpu2, IgnoreUnknownValue = true };
        _gpuTertiarySource = new ChoiceSetSetting("gpuTertiary", "GPU 三级数据源", "次级数据源不可用时的最终回退", SourceChoices)
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

        // 根据高精度模式决定 Broker 安装/卸载
        var precisionMode = _precisionModeSetting.Value ?? "None";
        if (precisionMode == "Broker")
        {
            _ = EnsureBrokerOnStartupAsync();
        }

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
        // 从 UI 设置收集当前值
        var config = new SensorChainConfig
        {
            PrecisionModeStr = _precisionModeSetting.Value ?? "Broker",
            CpuChain =
            [
                _cpuPrimarySource.Value ?? "Broker",
                _cpuSecondarySource.Value ?? "ThermalZone",
                _cpuTertiarySource.Value ?? "HWiNFO",
            ],
            GpuChain =
            [
                _gpuPrimarySource.Value ?? "Broker",
                _gpuSecondarySource.Value ?? "ThermalZone",
                _gpuTertiarySource.Value ?? "HWiNFO",
            ],
            GpuModeStr = _gpuModeSetting.Value ?? "Auto",
        };
        config.Save();

        SensorLogger.ForceLog($"[Settings] 已保存: CPU链=[{string.Join(", ", config.CpuChain)}], " +
            $"GPU链=[{string.Join(", ", config.GpuChain)}], GPU模式={config.GpuModeStr}");

        // 根据高精度模式决定 Broker 安装/卸载
        if (config.PrecisionMode == PrecisionMode.Broker)
        {
            _ = SetupBrokerAsync();
        }
        else if (config.PrecisionMode == PrecisionMode.HWiNFO)
        {
            // HWiNFO 模式: 卸载 Broker（如果已安装），用户选 HWiNFO 则不需要 Broker
            _ = UninstallBrokerAsync();
        }
        else
        {
            // None 模式: 卸载 Broker
            _ = UninstallBrokerAsync();
        }
    }

    /// <summary>
    /// On startup, if high-precision was already ON, check whether broker is running.
    /// If not, trigger setup automatically.
    /// </summary>
    private static async Task EnsureBrokerOnStartupAsync()
    {
        try
        {
            // Wait for extension host to settle
            await Task.Delay(3000);

            if (BrokerClient.Instance.Probe())
            {
                SensorLogger.ForceLog("Startup: broker pipe already active, skip setup");
                BrokerClient.Instance.ResetAvailable();
                return;
            }

            SensorLogger.ForceLog("Startup: high-precision ON but broker not running, triggering setup");
            await SetupBrokerAsync();
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"Startup broker check error: {ex.Message}");
        }
    }

    private static async Task SetupBrokerAsync()
    {
        try
        {
            var installDir = Package.Current.InstalledLocation.Path;
            var scriptPath = Path.Combine(installDir, "Broker", "setup-broker.ps1");
            var brokerExePath = Path.Combine(installDir, "Broker", "SysMonBroker.exe");

            if (!File.Exists(scriptPath))
            {
                SensorLogger.ForceLog($"Broker setup script not found: {scriptPath}");
                return;
            }

            // WindowsApps 目录 ACL 严格，管理员也读不了。
            // 先把文件复制到 %LOCALAPPDATA%（MSIX 有写权限），再从那里安装。
            var stagingDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SysMonCmdPal", "broker-staging");
            Directory.CreateDirectory(stagingDir);

            var stagedExe = Path.Combine(stagingDir, "SysMonBroker.exe");
            var stagedScript = Path.Combine(stagingDir, "setup-broker.ps1");

            File.Copy(brokerExePath, stagedExe, overwrite: true);
            File.Copy(scriptPath, stagedScript, overwrite: true);
            SensorLogger.ForceLog($"Staged broker files to: {stagingDir}");

            SensorLogger.ForceLog($"Running broker setup from staging: {stagedScript}");

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{stagedScript}\" -Action Install -SourceDir \"{stagingDir}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                await Task.Run(() => process.WaitForExit(30_000));

                if (process.ExitCode == 0)
                {
                    SensorLogger.ForceLog("Broker setup completed, waiting for pipe...");

                    // 等待管道就绪（最多 8 秒）
                    for (int i = 0; i < 8; i++)
                    {
                        if (BrokerClient.Instance.Probe())
                        {
                            BrokerClient.Instance.ResetAvailable();
                            SensorLogger.ForceLog("Broker pipe is ready!");
                            return;
                        }
                        await Task.Delay(1000);
                    }

                    // 管道未就绪，但仍标记为可用以便后续重试
                    BrokerClient.Instance.ResetAvailable();
                    SensorLogger.ForceLog("Broker pipe not yet ready, will retry on next read");
                }
                else
                {
                    SensorLogger.ForceLog($"Broker setup exited with code {process.ExitCode}");
                }
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"Broker setup error: {ex.Message}");
        }
    }

    private static async Task UninstallBrokerAsync()
    {
        try
        {
            // 立即标记 Broker 不可用，停止读取
            BrokerClient.Instance.MarkUnavailable();

            var stagingDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SysMonCmdPal", "broker-staging");
            var stagedScript = Path.Combine(stagingDir, "setup-broker.ps1");

            if (!File.Exists(stagedScript))
            {
                SensorLogger.ForceLog("Staged setup script not found, skipping uninstall");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{stagedScript}\" -Action Uninstall",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                await Task.Run(() => process.WaitForExit(15_000));
                SensorLogger.ForceLog("Broker uninstall completed");
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"Broker uninstall error: {ex.Message}");
        }
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
