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
internal sealed partial class SensorListPage : ListPage, IDisposable
{
    private readonly Dictionary<int, SensorCategoryPage> _categoryPages = [];
    private readonly object _categoryPagesLock = new();
    private bool _subscribed;

    public SensorListPage()
    {
        Icon = new IconInfo(SysMonIcons.Sensors);
        Title = Loc.Get("Sensor.PageTitle");
        Name = "Sensors";
    }

    public override IListItem[] GetItems()
    {
        EnsureSubscribed();
        var broker = BrokerPushReceiver.Instance;
        bool isBrokerAvailable = broker.TryGetAvailableSnapshot(out var snap);
        if (!isBrokerAvailable || snap.AllSensors.Count == 0)
        {
            return [PageNavigation.BackListItem(Dispose), .. CreateNoDataItems(snap, isBrokerAvailable)];
        }

        var items = new List<IListItem>(ShmLayout.BrowseCategories.Count + snap.AllSensors.Count);
        items.AddRange(CreateCategoryItems(snap));
        items.AddRange(CreateSensorItems(snap.AllSensors));

        IListItem[] content = items.Count == 0 ? CreateEmptyItems() : items.ToArray();
        return [PageNavigation.BackListItem(Dispose), .. content];
    }

    private void EnsureSubscribed()
    {
        if (_subscribed)
            return;

        _subscribed = true;
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
        SensorDockSettings.Changed += OnSensorDockSettingsChanged;
    }

    private void OnRefresh()
    {
        RaiseItemsChanged();
        SensorCategoryPage[] pages;
        lock (_categoryPagesLock)
            pages = _categoryPages.Values.ToArray();

        foreach (var page in pages)
            page.RequestRefresh();
    }

    private void OnSensorDockSettingsChanged(object? sender, EventArgs e) => OnRefresh();

    public void Dispose()
    {
        if (_subscribed)
        {
            DockBandRefreshCoordinator.Unsubscribe(OnRefresh);
            SensorDockSettings.Changed -= OnSensorDockSettingsChanged;
            _subscribed = false;
        }

        lock (_categoryPagesLock)
            _categoryPages.Clear();
    }

    private IEnumerable<IListItem> CreateCategoryItems(BrokerSensorSnapshot snap)
    {
        return ShmLayout.BrowseCategories.Select(category =>
        {
            int count = snap.AllSensors.Count(sensor => ShmLayout.BrowseCategoryMatchesTag(category, sensor.Tag));
            int representativeTag = ShmLayout.BrowseCategoryRepresentativeTag(category);

            return (IListItem)new ListItem(GetOrCreateCategoryPage(category))
            {
                Title = ShmLayout.BrowseCategoryName(category),
                Subtitle = Loc.Format("Sensor.BrowseCount", count),
                Icon = new IconInfo(GetCategoryIcon(representativeTag)),
            };
        });
    }

    private SensorCategoryPage GetOrCreateCategoryPage(int category)
    {
        lock (_categoryPagesLock)
        {
            if (_categoryPages.TryGetValue(category, out var page))
                return page;

            page = new SensorCategoryPage(category);
            _categoryPages[category] = page;
            return page;
        }
    }

    internal static IListItem[] CreateNoDataItems(
        BrokerSensorSnapshot snap,
        bool isBrokerAvailable)
    {
        return
        [
            new ListItem(new NoOpCommand())
            {
                Title = Loc.Get("Sensor.NoData"),
                Subtitle = GetNoDataSubtitle(snap, isBrokerAvailable),
                Icon = new IconInfo(SysMonIcons.SensorUnavailable),
            }
        ];
    }

    private static string GetNoDataSubtitle(
        BrokerSensorSnapshot snap,
        bool isBrokerAvailable)
    {
        if (isBrokerAvailable)
            return Loc.Get("Sensor.NoDataAlive");

        if (snap.LastPush != DateTime.MinValue)
        {
            int seconds = Math.Max(0, (int)(DateTime.UtcNow - snap.LastPush).TotalSeconds);
            return Loc.Format("Sensor.NoDataStale", seconds);
        }

        var diag = SharedMemoryReader.Diagnostics;
        if (!string.IsNullOrWhiteSpace(diag.LastError))
            return Loc.Format("Sensor.NoDataError", diag.LastError);

        return Loc.Get("Sensor.NoDataUnavailable");
    }

    internal static IListItem[] CreateEmptyItems()
    {
        return
        [
            new ListItem(new NoOpCommand())
            {
                Title = Loc.Get("Sensor.EmptyList"),
                Subtitle = Loc.Get("Sensor.EmptyListDetail"),
                Icon = new IconInfo(SysMonIcons.SensorUnavailable),
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
            : new NoOpCommand())
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
        0 or 1 or 2 or 3 or 4 => SysMonIcons.Cpu,
        5 or 6 or 7 or 8 or 9 or 10 or 11 => SysMonIcons.Gpu,
        12 or 13 or 14 => SysMonIcons.Sensors,
        15 or 16 => SysMonIcons.Disk,
        _ => SysMonIcons.Sensors,
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
        var broker = BrokerPushReceiver.Instance;
        bool isBrokerAvailable = broker.TryGetAvailableSnapshot(out var snap);
        if (!isBrokerAvailable || snap.AllSensors.Count == 0)
        {
            return [PageNavigation.BackListItem(), .. SensorListPage.CreateNoDataItems(snap, isBrokerAvailable)];
        }

        var sensors = snap.AllSensors
            .Where(sensor => ShmLayout.BrowseCategoryMatchesTag(_category, sensor.Tag))
            .ToArray();

        if (sensors.Length == 0)
        {
            return
            [
                PageNavigation.BackListItem(),
                new ListItem(new NoOpCommand())
                {
                    Title = Loc.Get("Sensor.EmptyList"),
                    Subtitle = Loc.Format("Sensor.CategoryEmptyDetail", ShmLayout.BrowseCategoryName(_category)),
                    Icon = new IconInfo(SensorListPage.GetCategoryIcon(
                        ShmLayout.BrowseCategoryRepresentativeTag(_category))),
                }
            ];
        }

        return [PageNavigation.BackListItem(), .. SensorListPage.CreateSensorItems(sensors)];
    }

    internal void RequestRefresh() => RaiseItemsChanged();
}
