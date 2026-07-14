// Copyright (c) 2026 SysMonCmdPal
// 二级: FormContent hero 布局 + 双图表（读/写 IO 速度趋势）

using System;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

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
          "text": "磁盘",
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
        Icon = new IconInfo(SysMonIcons.Disk);
        Title = disk.Model.ToUpper().Trim();
        Name = disk.Model.ToUpper().Trim();
        Commands = [PageNavigation.BackContextItem(Dispose)];
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
