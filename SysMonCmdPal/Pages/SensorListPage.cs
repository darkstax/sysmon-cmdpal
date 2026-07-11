// Copyright (c) 2026 SysMonCmdPal
// 全量传感器浏览页 — 显示 Broker 共享内存中的所有 LHM 传感器
// 按分类标签分组，支持实时刷新

using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        if (!snap.IsFresh || snap.AllSensors.Count == 0)
        {
            return [
                new ListItem(NoOpPage.Instance)
                {
                    Title = Loc.Get("Sensor.NoData"),
                    Subtitle = snap.IsAlive
                        ? Loc.Get("Sensor.NoDataAlive")
                        : Loc.Get("Sensor.NoDataDead"),
                    Icon = new IconInfo(""),
                }
            ];
        }

        // 按 tag 分组
        var grouped = snap.AllSensors
            .GroupBy(s => s.Tag)
            .OrderBy(g => g.Key)
            .ToList();

        var items = new List<IListItem>();

        foreach (var group in grouped)
        {
            string categoryName = ShmLayout.TagName(group.Key);
            string categoryUnit = ShmLayout.TagUnit(group.Key);

            foreach (var sensor in group.OrderBy(s => s.Name))
            {
                string valueStr = FormatSensorValue(sensor.Value, sensor.Unit);
                items.Add(new ListItem(NoOpPage.Instance)
                {
                    Title = Loc.Format("Sensor.ItemTitle", categoryName, sensor.Name),
                    Subtitle = valueStr,
                    Icon = new IconInfo(GetCategoryIcon(group.Key)),
                });
            }
        }

        if (items.Count == 0)
        {
            return [
                new ListItem(NoOpPage.Instance)
                {
                    Title = Loc.Get("Sensor.EmptyList"),
                    Subtitle = Loc.Get("Sensor.EmptyListDetail"),
                    Icon = new IconInfo(""),
                }
            ];
        }

        return items.ToArray();
    }

    private static string FormatSensorValue(double value, string unit)
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

    private static string GetCategoryIcon(int tag) => tag switch
    {
        0 or 1 or 2 or 3 or 4 => "",    // CPU
        5 or 6 or 7 or 8 or 9 or 10 or 11 => "",  // GPU
        12 or 13 or 14 => "",             // Motherboard
        15 or 16 => "",                    // Storage
        _ => "",
    };
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
