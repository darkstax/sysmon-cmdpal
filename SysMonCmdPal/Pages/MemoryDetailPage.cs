// Copyright (c) 2026 SysMonCmdPal
// 内存详情页 — FormContent + AdaptiveCards + SVG 图表

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class MemoryDetailPage : RefreshingContentPage
{
    private readonly FormContent _form = new();
    private readonly CopyTextCommand _copyCommand = new(string.Empty);

    private const string Template = """
    {
      "type": "AdaptiveCard",
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "version": "1.5",
      "body": [
        {
          "type": "TextBlock",
          "text": "🧠 内存",
          "size": "Large",
          "weight": "Bolder",
          "spacing": "Medium"
        },
        {
          "type": "TextBlock",
          "text": "实时监控",
          "size": "Small",
          "isSubtle": true,
          "spacing": "None"
        },
        {
          "type": "Container",
          "separator": true,
          "spacing": "Medium",
          "items": [
            {
              "type": "TextBlock",
              "text": "${memUsagePct}",
              "size": "Large",
              "weight": "Bolder",
              "horizontalAlignment": "Center",
              "spacing": "Small"
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
                  "text": "${memUsedGB}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "已用",
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
                  "text": "${memFreeGB}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "可用",
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
                  "text": "${memTotalGB}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "总计",
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
          "type": "Image",
          "url": "${chartUrl}",
          "altText": "Memory sparkline",
          "horizontalAlignment": "Center",
          "width": "500px",
          "height": "160px",
          "separator": true,
          "spacing": "Medium"
        }
      ]
    }
    """;

    public MemoryDetailPage()
    {
        Icon = new IconInfo(SysMonIcons.Memory);
        Title = Loc.Get("Memory.PageTitle");
        Name = Loc.Get("Dock.Memory");
        Commands = [new CommandContextItem(_copyCommand) { Title = Loc.Get("Common.CopyCurrentMetrics") }];
        _form.TemplateJson = Template;
        _form.DataJson = """{"memUsagePct":"—","memUsedGB":"—","memFreeGB":"—","memTotalGB":"—","chartUrl":""}""";
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
            var info = SystemInfoService.Instance.Current;

            double totalGB = info.MemoryTotalBytes / (1024.0 * 1024 * 1024);
            double usedGB = info.MemoryUsedBytes / (1024.0 * 1024 * 1024);
            double freeGB = totalGB - usedGB;

            string chartUrl = SystemInfoService.Instance.MemChart.ToSvgDataUri(canvasWidth: 500) ?? "";

            var data = new Dictionary<string, string>
            {
                ["memUsagePct"] = $"{info.MemoryUsed:F0}%",
                ["memUsedGB"] = $"{usedGB:F1} GB",
                ["memFreeGB"] = $"{freeGB:F1} GB",
                ["memTotalGB"] = $"{totalGB:F1} GB",
                ["chartUrl"] = chartUrl,
            };

            _copyCommand.Text = $"Memory {usedGB:F1}/{totalGB:F1} GB · {FormatPercentOrNA(info.MemoryUsed)}";
            _form.DataJson = JsonHelper.ToJson(data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemoryDetailPage] Update failed: {ex.Message}");
        }
    }

    private static string FormatPercentOrNA(double value) => value >= 0 ? $"{value:F0}%" : Loc.Get("Common.NA");
}
