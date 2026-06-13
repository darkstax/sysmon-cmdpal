// Copyright (c) 2026 SysMonCmdPal
// 传感器列表页 — 按类别展示所有 LHM 传感器，可点击添加/移除 Dock

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>
/// 全量传感器列表页。按类别分组，每行一个传感器。
/// 可点击添加/移除 Dock，带传感器实时读数。
/// </summary>
internal sealed partial class SensorListPage : ListPage
{
    private readonly LhmSensorService _sensorSvc = LhmSensorService.Instance;

    public SensorListPage()
    {
        Icon = new IconInfo(""); // Grid/Sensors
        Title = "传感器列表";
        Name = "传感器";
    }

    public override IListItem[] GetItems()
    {
        _sensorSvc.Refresh();
        var catalog = _sensorSvc.Catalog;
        var items = new List<IListItem>();

        foreach (var cat in SensorCategoryMeta.Order)
        {
            if (!catalog.TryGetValue(cat, out var readings) || readings.Count == 0)
                continue;

            // Section header — 使用缓存的图标
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = $"▸ {SensorCategoryMeta.Name(cat)} ({readings.Count})",
                Subtitle = "",
                Icon = SensorCategoryMeta.GetIcon(cat),
            });

            // Individual sensors
            foreach (var r in readings.OrderBy(r => r.SensorName))
            {
                bool inDock = _sensorSvc.IsInConfig(r.UniqueKey ?? "");
                // CmdPal re-queries GetItems() on page navigation; the callback logs the action
                // for diagnostics. Visual state (pin icon) updates when the page is next entered.
                var cmd = new ToggleSensorCommand(r, inDock, () =>
                {
                    Debug.WriteLine($"[SysMon] Sensor toggled: {r.DisplayName} (now {(inDock ? "removed" : "added")})");
                });
                items.Add(new ListItem(cmd)
                {
                    Title = $"{r.DisplayName}  =  {r.FormatValue()}",
                    Subtitle = $"{r.HardwareName}  |  {r.Label}",
                    Icon = new IconInfo(inDock ? "" : ""), // Pin / Add
                });
            }
        }

        if (items.Count == 0)
        {
            return [new ListItem(new NoOpCommand())
            {
                Title = "无传感器数据",
                Subtitle = LhmSensorService.Instance.IsAvailable
                    ? "LHM 已连接，但未检测到传感器"
                    : "LHM 不可用 — 需要 PawnIO 驱动 + 管理员权限",
                Icon = new IconInfo(""),
            }];
        }

        return items.ToArray();
    }
}
