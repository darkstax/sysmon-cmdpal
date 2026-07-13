// Copyright (c) 2026 SysMonCmdPal
// 网络详情页 — FormContent + AdaptiveCards + SVG 图表

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class NetworkDetailPage : RefreshingContentPage
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
          "text": "🌐 网络",
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
          "type": "TextBlock",
          "text": "${ssid}",
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
                  "text": "${netDown}",
                  "size": "Large",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center",
                  "spacing": "Small"
                },
                {
                  "type": "TextBlock",
                  "text": "下载",
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
                  "text": "${netUp}",
                  "size": "Large",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center",
                  "spacing": "Small"
                },
                {
                  "type": "TextBlock",
                  "text": "上传",
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
                  "url": "${downChartUrl}",
                  "altText": "Download sparkline",
                  "horizontalAlignment": "Center",
                  "width": "380px",
                  "height": "160px"
                },
                {
                  "type": "TextBlock",
                  "text": "下载速度 (满刻度 ${downScale})",
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
                  "url": "${upChartUrl}",
                  "altText": "Upload sparkline",
                  "horizontalAlignment": "Center",
                  "width": "380px",
                  "height": "160px"
                },
                {
                  "type": "TextBlock",
                  "text": "上传速度 (满刻度 ${upScale})",
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

    public NetworkDetailPage()
    {
        Icon = new IconInfo("");
        Title = Loc.Get("Network.PageTitle");
        Name = Loc.Get("MainPage.NetworkTitle");
        Commands = [new CommandContextItem(_copyCommand) { Title = Loc.Get("Common.CopyCurrentMetrics") }];
        _form.TemplateJson = Template;
        _form.DataJson = """{"netDown":"—","netUp":"—","ssid":"","downScale":"","upScale":"","downChartUrl":"","upChartUrl":""}""";
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

            // 双图表
            string downUrl = SystemInfoService.Instance.NetDownChart.ToSvgDataUriFixedScale(" MB/s", 320, false) ?? "";
            string upUrl = SystemInfoService.Instance.NetUpChart.ToSvgDataUriFixedScale(" MB/s", 320, false) ?? "";
            string downScale = SystemInfoService.Instance.NetDownChart.GetCurrentScaleLabel();
            string upScale = SystemInfoService.Instance.NetUpChart.GetCurrentScaleLabel();

            var ssid = SystemInfoService.Instance.GetWifiSsid();
            var data = new Dictionary<string, string>
            {
                ["netDown"] = DockFormat.Speed(info.NetDown),
                ["netUp"] = DockFormat.Speed(info.NetUp),
                ["ssid"] = string.IsNullOrEmpty(ssid)
                    ? "未连接 Wi-Fi"
                    : $"SSID: {ssid}",
                ["downScale"] = downScale,
                ["upScale"] = upScale,
                ["downChartUrl"] = downUrl,
                ["upChartUrl"] = upUrl,
            };

            _copyCommand.Text = $"Network ↓ {FormatSpeedOrNA(info.NetDown)} · ↑ {FormatSpeedOrNA(info.NetUp)}";
            _form.DataJson = JsonHelper.ToJson(data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NetworkDetailPage] Update failed: {ex.Message}");
        }
    }

    private static string FormatSpeedOrNA(double value) => value >= 0 ? DockFormat.Speed(value) : Loc.Get("Common.NA");
}
