// Copyright (c) 2026 SysMonCmdPal
// CommandsProvider: registers top-level commands and individual dock bands.
// Each dock band is a separate ICommandItem → independently pinnable in CmdPal dock.

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
    private readonly Dictionary<string, SensorDockBand> _sensorBands = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sensorBandsLock = new();
    private SensorDockKey[] _configuredSensorBands = [];

    private readonly SysMonSettingsManager _settingsManager;
    private readonly SysMonMainPage _rootPage;
    private readonly ICommandItem _rootCommand;

    public SysMonCommandsProvider()
    {
        Id = "SysMonCmdPal";
        DisplayName = Loc.Get("Provider.DisplayName");
        Icon = new IconInfo(SysMonIcons.App);
        Frozen = false;

        // M11: PrecisionMode 设置已移除 — 传感器回退链自动选择最优数据源（分型，见 §3 约束 1）:
        // GPU=Broker → HWiNFO → D3DKMT → PDH；CPU温度=Broker → HWiNFO → ThermalZone。无需用户手动切换。
        _settingsManager = new SysMonSettingsManager();
        Settings = _settingsManager;

        // 手动切换「主网卡」后失效接口缓存并重播种基线，避免沿用过期的接口集合。
        // 只订阅网卡选择的**实际变化**（而非 Settings.SettingsChanged）—— 后者在保存
        // 任意设置项时都触发，会让无关设置保存也重置网络基线。
        // 注意：这里不主动解析 SystemInfoService.Instance —— 那会把它提到 _rootPage 之前构造，
        // 而源实例化顺序是 PerformanceCounter 时序敏感的（见 SystemInfoService 注释）。
        // 改为事件触发时惰性取用。
        _settingsManager.NicSelectionChanged += OnNicSelectionChanged;

        _rootPage = new SysMonMainPage();
        _rootCommand = new CommandItem(_rootPage)
        {
            Title = Loc.Get("Provider.Title"),
            Subtitle = Loc.Get("Provider.Subtitle"),
        };

        _staticBands = [
            new CpuDockBand(),
            new MemoryDockBand(),
            new DiskDockBand(),
            new GpuDockBand(),
            new NetworkDownDockBand(),
            new NetworkUpDockBand(),
            new BatteryDockBand(),
        ];

        ReloadSensorDockBands(raiseItemsChanged: false);
        SensorDockSettings.Changed += OnSensorDockSettingsChanged;
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return [_rootCommand];
    }

    /// <summary>
    /// Returns static dock bands plus configured custom sensor bands.
    /// Each is an independently-pinnable atomic band.
    /// </summary>
    public override ICommandItem[]? GetDockBands()
    {
        return [.. _staticBands, .. GetSensorBandsSnapshot()];
    }

    public override ICommandItem? GetCommandItem(string id)
    {
        foreach (var band in _staticBands)
        {
            if (string.Equals(band.Command?.Id, id, StringComparison.OrdinalIgnoreCase))
                return band;
        }

        if (TryGetConfiguredSensorBand(id, out var sensorBand))
            return sensorBand;

        return null;
    }

    public override void Dispose()
    {
        SensorDockSettings.Changed -= OnSensorDockSettingsChanged;
        _settingsManager.NicSelectionChanged -= OnNicSelectionChanged;
        _rootPage.Dispose();
        ReleaseSensorDockBands();
        DockBandRefreshCoordinator.Shutdown();
        base.Dispose();
    }

    private void OnNicSelectionChanged(object? sender, EventArgs e)
    {
        try { SystemInfoService.Instance.NetworkMonitorSource?.InvalidateNicSelection(); }
        catch { /* 失效失败不得影响设置保存 */ }
    }

    private void OnSensorDockSettingsChanged(object? sender, EventArgs e)
        => ReloadSensorDockBands(raiseItemsChanged: true);

    private ICommandItem[] GetSensorBandsSnapshot()
    {
        lock (_sensorBandsLock)
        {
            return _configuredSensorBands
                .Select(GetOrCreateSensorBand)
                .Cast<ICommandItem>()
                .ToArray();
        }
    }

    private bool TryGetConfiguredSensorBand(string id, out SensorDockBand? band)
    {
        lock (_sensorBandsLock)
        {
            if (_sensorBands.TryGetValue(id, out band))
                return true;

            if (!SensorDockKey.TryFromDockId(id, out var key))
            {
                band = null;
                return false;
            }

            if (!_configuredSensorBands.Contains(key, SensorDockKeyComparer.Instance))
            {
                band = null;
                return false;
            }

            band = GetOrCreateSensorBand(key);
            return true;
        }
    }

    private SensorDockBand GetOrCreateSensorBand(SensorDockKey key)
    {
        var id = key.DockId;
        if (_sensorBands.TryGetValue(id, out var existing))
            return existing;

        var created = new SensorDockBand(key);
        _sensorBands[id] = created;
        return created;
    }

    private void ReloadSensorDockBands(bool raiseItemsChanged)
    {
        lock (_sensorBandsLock)
        {
            _configuredSensorBands = SensorDockSettings.Load().ToArray();
            var configuredIds = _configuredSensorBands
                .Select(key => key.DockId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var item in _sensorBands.ToArray())
            {
                if (configuredIds.Contains(item.Key))
                    continue;

                item.Value.Release();
                _sensorBands.Remove(item.Key);
            }
        }

        if (raiseItemsChanged)
            RaiseItemsChanged(_staticBands.Length + _configuredSensorBands.Length);
    }

    private void ReleaseSensorDockBands()
    {
        lock (_sensorBandsLock)
        {
            foreach (var band in _sensorBands.Values)
                band.Release();

            _sensorBands.Clear();
            _configuredSensorBands = [];
        }
    }
}
