// Copyright (c) 2026 SysMonCmdPal
// Dynamic Dock band backed by one Broker sensor entry.

using System;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

internal sealed partial class SensorDockBand : WrappedDockItem
{
    private readonly SensorDockKey _key;
    private readonly ListItem _sensorItem;
    private bool _released;

    public SensorDockBand(SensorDockKey key)
        : base(Array.Empty<IListItem>(), key.DockId, key.Name)
    {
        _key = key;
        _sensorItem = new ListItem(new SensorListPage())
        {
            Title = key.Name,
            Subtitle = Loc.Get("Common.Loading"),
            Icon = new IconInfo(SensorListPage.GetCategoryIcon(key.Tag)),
        };
        Items = [_sensorItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
        OnRefresh();
    }

    public SensorDockKey Key => _key;

    public void Release()
    {
        if (_released)
            return;

        DockBandRefreshCoordinator.Unsubscribe(OnRefresh);
        _released = true;
    }

    private void OnRefresh()
    {
        ApplySnapshot(BrokerPushReceiver.Instance.Snapshot);
        Items = [_sensorItem];
    }

    internal void ApplySnapshot(BrokerSensorSnapshot snap)
    {
        if (!(snap.IsAlive && snap.IsFresh))
        {
            _sensorItem.Title = _key.Name;
            _sensorItem.Subtitle = snap.LastPush == DateTime.MinValue
                ? Loc.Get("Dock.CustomSensorWaiting")
                : Loc.Get("Dock.CustomSensorUnavailable");
            return;
        }

        var sensor = snap.AllSensors.FirstOrDefault(_key.Matches);
        if (sensor is null)
        {
            _sensorItem.Title = _key.Name;
            _sensorItem.Subtitle = Loc.Format("Dock.CustomSensorNotFound", _key.Name);
            return;
        }

        _sensorItem.Title = Loc.Format(
            "Dock.CustomSensorTitle",
            sensor.Name,
            FormatValue(sensor.Value, sensor.Unit));
        _sensorItem.Subtitle = Loc.Format(
            "Dock.CustomSensorSubtitle",
            ShmLayout.TagName(sensor.Tag),
            sensor.Name);
    }

    internal static string FormatValue(double value, string unit)
    {
        return unit switch
        {
            "°C" => $"{value:F1}°C",
            "%" => $"{value:F1}%",
            "MHz" => value >= 1000 ? $"{value / 1000:F2} GHz" : $"{value:F0} MHz",
            "W" => $"{value:F1} W",
            "V" => $"{value:F3} V",
            "MB" => value >= 1024 ? $"{value / 1024:F1} GB" : $"{value:F0} MB",
            "RPM" => $"{value:F0} RPM",
            _ => string.IsNullOrWhiteSpace(unit) ? $"{value:F2}" : $"{value:F2} {unit}",
        };
    }
}

internal enum SensorDockCommandMode
{
    Add,
    Remove,
    AlreadyAdded,
}

internal sealed partial class SensorDockCommand : InvokableCommand
{
    private readonly SensorDockKey _key;
    private readonly SensorDockCommandMode _mode;

    public SensorDockCommand(SensorDockKey key, SensorDockCommandMode mode)
    {
        _key = key;
        _mode = mode;
        Id = $"sysmon.sensorDock.{mode}.{key.DockId}";
        Name = mode switch
        {
            SensorDockCommandMode.Remove => Loc.Get("Sensor.RemoveDockBand"),
            SensorDockCommandMode.AlreadyAdded => Loc.Get("Sensor.DockBandAlreadyAdded"),
            _ => Loc.Get("Sensor.AddDockBand"),
        };
        Icon = new IconInfo(mode == SensorDockCommandMode.Remove ? SysMonIcons.Remove : SysMonIcons.Add);
    }

    public override ICommandResult Invoke()
    {
        return _mode switch
        {
            SensorDockCommandMode.Remove => Remove(),
            SensorDockCommandMode.AlreadyAdded => CommandResult.ShowToast(Loc.Get("Sensor.DockBandAlreadyAdded")),
            _ => Add(),
        };
    }

    private ICommandResult Add()
    {
        var changed = SensorDockSettings.Add(_key);
        return CommandResult.ShowToast(Loc.Get(
            changed ? "Sensor.DockBandAdded" : "Sensor.DockBandAlreadyAdded"));
    }

    private ICommandResult Remove()
    {
        var changed = SensorDockSettings.Remove(_key);
        return CommandResult.ShowToast(Loc.Get(
            changed ? "Sensor.DockBandRemoved" : "Sensor.DockBandNotConfigured"));
    }
}
