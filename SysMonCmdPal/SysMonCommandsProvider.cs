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
    private readonly ICommandItem _rootCommand;

    public SysMonCommandsProvider()
    {
        Id = "SysMonCmdPal";
        DisplayName = Loc.Get("Provider.DisplayName");
        Icon = new IconInfo(SysMonIcons.App);
        Frozen = false;

        // M11: PrecisionMode 设置已移除 — 传感器回退链自动选择最优数据源
        // (Broker → HWiNFO → ThermalZone)，无需用户手动切换。
        _settingsManager = new SysMonSettingsManager();
        Settings = _settingsManager.Settings;

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
        ReleaseSensorDockBands();
        DockBandRefreshCoordinator.Shutdown();
        base.Dispose();
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
