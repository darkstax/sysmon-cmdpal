// Copyright (c) 2026 SysMonCmdPal
// 磁盘详情页 — 两级 ListPage
// 一级: 物理磁盘列表（型号/总大小/IO 汇总）
// 二级: FormContent hero 布局 + 双图表（读/写 IO 速度趋势）

using System;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>
/// 一级页面: 物理磁盘列表
/// </summary>
internal sealed partial class DiskDetailPage : ListPage, IDisposable
{
    private DiskPartitionsPage[]? _cachedItems;
    private int _cachedCount = -1;
    private readonly CopyTextCommand _copyCommand = new(string.Empty);

    public DiskDetailPage()
    {
        Icon = new IconInfo("");
        Title = Loc.Get("Disk.PageTitle");
        Name = Loc.Get("Dock.Disk");
    }

    public override IListItem[] GetItems()
    {
        // 用已缓存快照，不触发同步 Refresh（避免阻塞 UI）
        var info = SystemInfoService.Instance.Current;
        _copyCommand.Text = BuildCopyText(info);

        var physDisks = info.PhysicalDisks;
        if (physDisks == null || physDisks.Length == 0)
        {
            SensorLogger.ForceLog($"[DiskDetailPage] fallback logical disks: logical={info.Disks.Length}, physical={(physDisks == null ? "null" : "0")}");
            // WMI 失败回退: 直接按分区列出
            return CreateItemsWithCopy(info.Disks.Select(d => CreatePartitionListItem(d)));
        }

        SensorLogger.ForceLog($"[DiskDetailPage] physical disks: count={physDisks.Length}, protocols={string.Join(",", physDisks.Select(d => d.InterfaceType))}");

        // 缓存二级页实例 — 磁盘数量固定，复用避免 timer 泄漏
        if (_cachedItems == null || _cachedCount != physDisks.Length)
        {
            DisposeCachedItems();
            _cachedItems = physDisks.Select(d => new DiskPartitionsPage(d)).ToArray();
            _cachedCount = physDisks.Length;
        }

        return CreateItemsWithCopy(physDisks
            .Select((disk, i) =>
            {
                double totalGB = disk.TotalBytes / (1024.0 * 1024 * 1024);
                var partitions = disk.Partitions;
                long partTotal = partitions.Sum(p => p.TotalBytes);
                long partUsed = partitions.Sum(p => p.TotalBytes - p.FreeBytes);
                double usedPct = partTotal > 0 ? partUsed * 100.0 / partTotal : 0;

                string ioStr = disk.ReadBytesPerSec >= 0 && disk.WriteBytesPerSec >= 0
                    ? $"↓{DockFormat.CompactSpeed(disk.ReadBytesPerSec)} ↑{DockFormat.CompactSpeed(disk.WriteBytesPerSec)}  |  "
                    : "";

                var item = new ListItem(_cachedItems[i])
                {
                    Title = disk.Model.ToUpper().Trim(),
                    Subtitle = $"{ioStr}{usedPct:F0}% 使用 · {partitions.Length} 分区 · {totalGB:F0} GB{(!string.IsNullOrEmpty(disk.InterfaceType) ? $" · {disk.InterfaceType}" : "")}",
                    Icon = new IconInfo(usedPct > 90 ? "" : ""),
                };
                SensorLogger.ForceLog($"[DiskDetailPage] item target=DiskPartitionsPage title={item.Title} subtitle={item.Subtitle}");
                return item;
            }));
    }

    private IListItem[] CreateItemsWithCopy(IEnumerable<IListItem> items)
    {
        return
        [
            new ListItem(_copyCommand)
            {
                Title = Loc.Get("Common.CopyCurrentMetrics"),
                Subtitle = _copyCommand.Text,
                Icon = new IconInfo(""),
            },
            .. items,
        ];
    }

    /// <summary>预渲染：先 GetItems 创建缓存实例，再启动所有二级页定时器</summary>
    /// <summary>预渲染：用已缓存数据创建二级页实例并启动定时器（不触发 Refresh）</summary>
    public void StartTimer()
    {
        // 用已缓存的磁盘数据创建二级页（不调 GetItems 避免同步 Refresh 阻塞）
        var physDisks = SystemInfoService.Instance.Current.PhysicalDisks;
        if (_cachedItems == null && physDisks != null && physDisks.Length > 0)
        {
            _cachedItems = physDisks.Select(d => new DiskPartitionsPage(d)).ToArray();
            _cachedCount = physDisks.Length;
        }

        if (_cachedItems != null)
        {
            foreach (var item in _cachedItems)
                item.StartTimer();
        }
    }

    public void Dispose() => DisposeCachedItems();

    private void DisposeCachedItems()
    {
        if (_cachedItems == null) return;
        foreach (var item in _cachedItems)
            item.Dispose();
        _cachedItems = null;
        _cachedCount = -1;
    }

    private static string BuildCopyText(SystemSnapshot info)
    {
        if (info.PhysicalDisks is { Length: > 0 })
        {
            double totalRead = info.PhysicalDisks.Sum(d => Math.Max(0, d.ReadBytesPerSec));
            double totalWrite = info.PhysicalDisks.Sum(d => Math.Max(0, d.WriteBytesPerSec));
            long totalBytes = info.PhysicalDisks.Sum(d => d.TotalBytes);
            long usedBytes = info.PhysicalDisks.Sum(d =>
            {
                var partitions = d.Partitions ?? [];
                return partitions.Sum(p => p.TotalBytes - p.FreeBytes);
            });
            string usedPct = totalBytes > 0 ? $"{usedBytes * 100.0 / totalBytes:F0}% used" : Loc.Get("Common.NA");
            return $"Disk ↓ {DockFormat.Speed(totalRead)} · ↑ {DockFormat.Speed(totalWrite)} · {usedPct}";
        }

        if (info.Disks is { Length: > 0 })
        {
            double totalRead = info.Disks.Sum(d => Math.Max(0, d.ReadBytesPerSec));
            double totalWrite = info.Disks.Sum(d => Math.Max(0, d.WriteBytesPerSec));
            long totalBytes = info.Disks.Sum(d => d.TotalBytes);
            long usedBytes = info.Disks.Sum(d => d.TotalBytes - d.FreeBytes);
            string usedPct = totalBytes > 0 ? $"{usedBytes * 100.0 / totalBytes:F0}% used" : Loc.Get("Common.NA");
            return $"Disk ↓ {DockFormat.Speed(totalRead)} · ↑ {DockFormat.Speed(totalWrite)} · {usedPct}";
        }

        return $"Disk ↓ {Loc.Get("Common.NA")} · ↑ {Loc.Get("Common.NA")} · {Loc.Get("Common.NA")}";
    }

    internal static ListItem CreatePartitionListItem(DiskInfo d)
    {
        double totalGB = d.TotalBytes / (1024.0 * 1024 * 1024);
        double freeGB = d.FreeBytes / (1024.0 * 1024 * 1024);
        string label = string.IsNullOrEmpty(d.VolumeLabel) ? "" : $" ({d.VolumeLabel})";
        string ioStr = d.ReadBytesPerSec >= 0 && d.WriteBytesPerSec >= 0
            ? $"↓{DockFormat.CompactSpeed(d.ReadBytesPerSec)} ↑{DockFormat.CompactSpeed(d.WriteBytesPerSec)}  |  "
            : "";
        return new ListItem(new CopyTextCommand($"{d.Name} R:{DockFormat.Speed(d.ReadBytesPerSec)} W:{DockFormat.Speed(d.WriteBytesPerSec)}"))
        {
            Title = $"{d.Name}{label}",
            Subtitle = Loc.Format("Disk.Subtitle", ioStr, $"{d.UsedPercent:F0}", $"{freeGB:F1}", $"{totalGB:F1}"),
            Icon = new IconInfo(d.UsedPercent > 90 ? "" : ""),
        };
    }
}

/// <summary>
/// 二级页面: 某物理磁盘的详情（FormContent hero 布局 + 双图表）
/// </summary>
internal sealed partial class DiskPartitionsPage : RefreshingContentPage
{
    private readonly PhysicalDiskInfo _disk;
    private readonly FormContent _form = new();
    private readonly SparklineChart _readChart = new(maxPoints: 60, metric: ChartMetric.Disk);
    private readonly SparklineChart _writeChart = new(maxPoints: 60, metric: ChartMetric.DiskWrite);

    private const string Template = """
    {
      "type": "AdaptiveCard",
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "version": "1.5",
      "body": [
        {
          "type": "TextBlock",
          "text": "💾 磁盘",
          "size": "Large",
          "weight": "Bolder",
          "spacing": "Medium"
        },
        {
          "type": "TextBlock",
          "text": "${diskName}",
          "size": "Small",
          "isSubtle": true,
          "spacing": "None"
        },
        {
          "type": "ColumnSet",
          "separator": true,
          "spacing": "Medium",
          "columns": [
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${readSpeed}",
                  "size": "Large",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center",
                  "spacing": "Small"
                },
                {
                  "type": "TextBlock",
                  "text": "读取速度",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "None"
                }
              ]
            },
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${writeSpeed}",
                  "size": "Large",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center",
                  "spacing": "Small"
                },
                {
                  "type": "TextBlock",
                  "text": "写入速度",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "None"
                }
              ]
            }
          ]
        },
        {
          "type": "ColumnSet",
          "separator": true,
          "spacing": "Medium",
          "columns": [
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${usedPct}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "使用率",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "None"
                }
              ]
            },
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${totalGB}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "总容量",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "None"
                }
              ]
            },
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "TextBlock",
                  "text": "${iface}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "接口",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "None"
                }
              ]
            }
          ]
        },
        {
          "type": "ColumnSet",
          "separator": true,
          "spacing": "Medium",
          "columns": [
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "Image",
                  "url": "${readChartUrl}",
                  "altText": "Disk read speed sparkline",
                  "horizontalAlignment": "Center",
                  "width": "380px",
                  "height": "160px"
                },
                {
                  "type": "TextBlock",
                  "text": "读取速度 (满刻度 ${readScale})",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "Small"
                }
              ]
            },
            {
              "type": "Column",
              "width": "stretch",
              "items": [
                {
                  "type": "Image",
                  "url": "${writeChartUrl}",
                  "altText": "Disk write speed sparkline",
                  "horizontalAlignment": "Center",
                  "width": "380px",
                  "height": "160px"
                },
                {
                  "type": "TextBlock",
                  "text": "写入速度 (满刻度 ${writeScale})",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "Small"
                }
              ]
            }
          ]
        }
      ]
    }
    """;

    public DiskPartitionsPage(PhysicalDiskInfo disk)
    {
        _disk = disk;
        Icon = new IconInfo("");
        Title = disk.Model.ToUpper().Trim();
        Name = disk.Model.ToUpper().Trim();
        _form.TemplateJson = Template;
        _form.DataJson = """{"diskName":"—","readSpeed":"—","writeSpeed":"—","usedPct":"—","totalGB":"—","iface":"—","readScale":"","writeScale":"","readChartUrl":"","writeChartUrl":""}""";
    }

    public override IContent[] GetContent()
    {
        StartTimer();
        return [_form];
    }

    protected override void RefreshContent()
    {
        try
        {
            // P1: 不再调用 sys.Refresh() — 依赖 DockBandRefreshCoordinator 的 1s 统一刷新。
            // 之前在这里直接调 Refresh() 导致与 coordinator 的 Refresh() 并发，WMI 查询翻倍。
            var sys = SystemInfoService.Instance;
            var refreshed = sys.Current.PhysicalDisks;
            var disk = Array.Find(refreshed, d => d.Model == _disk.Model && d.SerialNumber == _disk.SerialNumber);
            if (disk.Model == null && disk.SerialNumber == null) disk = _disk;
            string diskName = string.IsNullOrWhiteSpace(disk.Model)
                ? "DISK"
                : disk.Model.ToUpperInvariant().Trim();

            // Push IO 速度到图表
            _readChart.PushRaw((float)(Math.Max(0, disk.ReadBytesPerSec) / 1_000_000.0));
            _writeChart.PushRaw((float)(Math.Max(0, disk.WriteBytesPerSec) / 1_000_000.0));

            var parts = disk.Partitions;
            long partTotal = parts.Length > 0 ? parts.Sum(p => p.TotalBytes) : 0;
            long partUsed = parts.Length > 0 ? parts.Sum(p => p.TotalBytes - p.FreeBytes) : 0;
            double usedPct = partTotal > 0 ? partUsed * 100.0 / partTotal : 0;
            double totalGB = disk.TotalBytes / (1024.0 * 1024 * 1024);

            // 双图表
            string readChartUrl = _readChart.ToSvgDataUriFixedScale(" MB/s", 320, false) ?? "";
            string writeChartUrl = _writeChart.ToSvgDataUriFixedScale(" MB/s", 320, false) ?? "";
            string readScale = _readChart.GetCurrentScaleLabel();
            string writeScale = _writeChart.GetCurrentScaleLabel();

            var data = new Dictionary<string, string>
            {
                ["diskName"] = diskName,
                ["readSpeed"] = DockFormat.Speed(disk.ReadBytesPerSec),
                ["writeSpeed"] = DockFormat.Speed(disk.WriteBytesPerSec),
                ["usedPct"] = $"{usedPct:F0}%",
                ["totalGB"] = $"{totalGB:F0} GB",
                ["iface"] = string.IsNullOrEmpty(disk.InterfaceType) ? "—" : disk.InterfaceType,
                ["readScale"] = readScale,
                ["writeScale"] = writeScale,
                ["readChartUrl"] = readChartUrl,
                ["writeChartUrl"] = writeChartUrl,
            };

            _form.DataJson = JsonHelper.ToJson(data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiskPartitionsPage] Update failed: {ex.Message}");
        }
    }
}
