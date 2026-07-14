// Copyright (c) 2026 SysMonCmdPal
// 全量传感器浏览页 — 显示 Broker 共享内存中的所有 LHM 传感器
// 按分类标签分组，支持实时刷新

using System.Collections.Generic;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

/// <summary>
/// 显示来自 Broker 的全量 LHM 传感器数据，按类别分组。
/// 仅在 Broker 可用时显示有效数据。
/// </summary>
internal sealed partial class SensorListPage : ListPage
{
    public SensorListPage()
    {
        Icon = new IconInfo("");
        Title = Loc.Get("Sensor.PageTitle");
        Name = "Sensors";
    }

    public override IListItem[] GetItems()
    {
        var snap = BrokerPushReceiver.Instance.Snapshot;
        if (!(snap.IsAlive && snap.IsFresh) || snap.AllSensors.Count == 0)
        {
            return CreateNoDataItems(snap);
        }

        var items = new List<IListItem>(ShmLayout.BrowseCategories.Count + snap.AllSensors.Count);
        items.AddRange(CreateCategoryItems(snap));
        items.AddRange(CreateSensorItems(snap.AllSensors));

        return items.Count == 0 ? CreateEmptyItems() : items.ToArray();
    }

    private static IEnumerable<IListItem> CreateCategoryItems(BrokerSensorSnapshot snap)
    {
        return ShmLayout.BrowseCategories.Select(category =>
        {
            int count = snap.AllSensors.Count(sensor => ShmLayout.BrowseCategoryMatchesTag(category, sensor.Tag));
            int representativeTag = ShmLayout.BrowseCategoryRepresentativeTag(category);

            return (IListItem)new ListItem(new SensorCategoryPage(category))
            {
                Title = ShmLayout.BrowseCategoryName(category),
                Subtitle = Loc.Format("Sensor.BrowseCount", count),
                Icon = new IconInfo(GetCategoryIcon(representativeTag)),
            };
        });
    }

    internal static IListItem[] CreateNoDataItems(BrokerSensorSnapshot snap)
    {
        return
        [
            new ListItem(NoOpPage.Instance)
            {
                Title = Loc.Get("Sensor.NoData"),
                Subtitle = GetNoDataSubtitle(snap),
                Icon = new IconInfo(""),
            }
        ];
    }

    private static string GetNoDataSubtitle(BrokerSensorSnapshot snap)
    {
        var diag = SharedMemoryReader.Diagnostics;

        if (snap.IsAlive && snap.IsFresh)
            return Loc.Get("Sensor.NoDataAlive");

        if (diag.IsConnected && snap.LastPush != DateTime.MinValue)
        {
            int seconds = Math.Max(0, (int)(DateTime.UtcNow - snap.LastPush).TotalSeconds);
            return Loc.Format("Sensor.NoDataStale", seconds);
        }

        if (!string.IsNullOrWhiteSpace(diag.LastError))
            return Loc.Format("Sensor.NoDataError", diag.LastError);

        return Loc.Get("Sensor.NoDataUnavailable");
    }

    internal static IListItem[] CreateEmptyItems()
    {
        return
        [
            new ListItem(NoOpPage.Instance)
            {
                Title = Loc.Get("Sensor.EmptyList"),
                Subtitle = Loc.Get("Sensor.EmptyListDetail"),
                Icon = new IconInfo(""),
            }
        ];
    }

    internal static IListItem[] CreateSensorItems(IEnumerable<BrokerSensorEntry> sensors)
    {
        var dockedKeys = SensorDockSettings.Load();
        return sensors
            .GroupBy(sensor => sensor.Tag)
            .OrderBy(group => group.Key)
            .SelectMany(group => group.OrderBy(sensor => sensor.Name))
            .Select(sensor => CreateSensorItem(sensor, dockedKeys))
            .ToArray();
    }

    internal static IListItem CreateSensorItem(BrokerSensorEntry sensor)
        => CreateSensorItem(sensor, SensorDockSettings.Load());

    private static IListItem CreateSensorItem(
        BrokerSensorEntry sensor,
        IReadOnlyList<SensorDockKey> dockedKeys)
    {
        string categoryName = ShmLayout.TagName(sensor.Tag);
        string valueStr = FormatSensorValue(sensor.Value, sensor.Unit);
        var dockKey = SensorDockKey.FromSensor(sensor);
        var isDocked = dockKey.IsValid && dockedKeys.Contains(dockKey, SensorDockKeyComparer.Instance);

        var item = new ListItem(dockKey.IsValid
            ? new SensorDockCommand(dockKey, isDocked ? SensorDockCommandMode.AlreadyAdded : SensorDockCommandMode.Add)
            : NoOpPage.Instance)
        {
            Title = Loc.Format("Sensor.ItemTitle", categoryName, sensor.Name),
            Subtitle = valueStr,
            Icon = new IconInfo(GetCategoryIcon(sensor.Tag)),
        };

        if (dockKey.IsValid)
        {
            item.MoreCommands =
            [
                new CommandContextItem(new SensorDockCommand(
                    dockKey,
                    isDocked ? SensorDockCommandMode.Remove : SensorDockCommandMode.Add))
            ];
        }

        return item;
    }

    internal static string FormatSensorValue(double value, string unit)
    {
        if (unit == "°C")
            return $"**{value:F1}°C**";
        if (unit == "%")
            return $"**{value:F1}%**";
        if (unit == "MHz")
            return value >= 1000 ? $"**{value / 1000:F2} GHz**" : $"**{value:F0} MHz**";
        if (unit == "W")
            return $"**{value:F1} W**";
        if (unit == "V")
            return $"**{value:F3} V**";
        if (unit == "MB")
            return value >= 1024 ? $"**{value / 1024:F1} GB**" : $"**{value:F0} MB**";
        if (unit == "RPM")
            return $"**{value:F0} RPM**";
        return $"**{value:F2}** {unit}";
    }

    internal static string GetCategoryIcon(int tag) => tag switch
    {
        0 or 1 or 2 or 3 or 4 => "",    // CPU
        5 or 6 or 7 or 8 or 9 or 10 or 11 => "",  // GPU
        12 or 13 or 14 => "",             // Motherboard
        15 or 16 => "",                    // Storage
        _ => "",
    };
}

internal sealed partial class SensorCategoryPage : ListPage
{
    private readonly int _category;

    public SensorCategoryPage(int category)
    {
        _category = category;
        int representativeTag = ShmLayout.BrowseCategoryRepresentativeTag(category);

        Icon = new IconInfo(SensorListPage.GetCategoryIcon(representativeTag));
        Title = ShmLayout.BrowseCategoryName(category);
        Name = $"SensorCategory:{category}";
    }

    public override IListItem[] GetItems()
    {
        var snap = BrokerPushReceiver.Instance.Snapshot;
        if (!(snap.IsAlive && snap.IsFresh) || snap.AllSensors.Count == 0)
        {
            return SensorListPage.CreateNoDataItems(snap);
        }

        var sensors = snap.AllSensors
            .Where(sensor => ShmLayout.BrowseCategoryMatchesTag(_category, sensor.Tag))
            .ToArray();

        if (sensors.Length == 0)
        {
            return
            [
                new ListItem(NoOpPage.Instance)
                {
                    Title = Loc.Get("Sensor.EmptyList"),
                    Subtitle = Loc.Format("Sensor.CategoryEmptyDetail", ShmLayout.BrowseCategoryName(_category)),
                    Icon = new IconInfo(SensorListPage.GetCategoryIcon(
                        ShmLayout.BrowseCategoryRepresentativeTag(_category))),
                }
            ];
        }

        return SensorListPage.CreateSensorItems(sensors);
    }
}

/// <summary>空页面 — 传感器条目不需要跳转</summary>
internal sealed partial class NoOpPage : ContentPage
{
    public static NoOpPage Instance { get; } = new();

    public NoOpPage()
    {
        Title = Loc.Get("Sensor.NoOpTitle");
        Name = "Sensor";
    }

    public override IContent[] GetContent()
    {
        return [new MarkdownContent(Loc.Get("Sensor.NoOpContent"))];
    }
}
