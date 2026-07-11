// Copyright (c) 2026 SysMonCmdPal
// CPU 详情页 — FormContent + AdaptiveCards + SVG 图表
// 模板只解析一次，DataJson INPC 更新只刷新数据

using System.Timers;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class CpuDetailPage : ContentPage
{
    private readonly FormContent _form = new();
    private bool _subscribed;

    private const string Template = """
    {
      "type": "AdaptiveCard",
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "version": "1.5",
      "body": [
        {
          "type": "TextBlock",
          "text": "🖥 CPU",
          "size": "Large",
          "weight": "Bolder",
          "spacing": "Medium"
        },
        {
          "type": "TextBlock",
          "text": "${cpuName}",
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
              "type": "ColumnSet",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${cpuUsage}",
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
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    {
                      "type": "TextBlock",
                      "text": "${cpuFreq}",
                      "size": "Large",
                      "weight": "Bolder",
                      "horizontalAlignment": "Center",
                      "spacing": "Small"
                    },
                    {
                      "type": "TextBlock",
                      "text": "频率 (MHz)",
                      "size": "Small",
                      "isSubtle": true,
                      "horizontalAlignment": "Center",
                      "spacing": "None"
                    }
                  ]
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
                  "text": "${cpuTemp}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "温度",
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
                  "text": "${cpuCores}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "逻辑核心",
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
                  "text": "${backend}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "后端",
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
          "altText": "CPU sparkline",
          "horizontalAlignment": "Center",
          "width": "500px",
          "height": "160px",
          "separator": true,
          "spacing": "Medium"
        }
      ]
    }
    """;

    public CpuDetailPage()
    {
        Icon = new IconInfo("");
        Title = Loc.Get("Cpu.PageTitle");
        Name = "CPU";
        _form.TemplateJson = Template;
        // Initial placeholder data so FormContent has valid DataJson before first Update
        _form.DataJson = """{"cpuName":"—","cpuUsage":"—","cpuCores":"—","cpuTemp":"—","backend":"—","chartUrl":"","cpuFreq":"—"}""";
    }

    public void StartTimer()
    {
        if (_subscribed) return;
        _subscribed = true;
        DockBandRefreshCoordinator.Subscribe(Update);
        ThreadPool.QueueUserWorkItem(_ => Update());
    }

    public override IContent[] GetContent()
    {
        StartTimer();
        return [_form];
    }

    private void Update()
    {
        try
        {
            var sys = SystemInfoService.Instance;
            var info = sys.Current;

            var data = new Dictionary<string, string>
            {
                ["cpuName"] = string.IsNullOrEmpty(SystemInfoService.CpuName)
                    ? "CPU"
                    : SystemInfoService.CpuName.ToUpper().Trim(),
                ["cpuUsage"] = $"{info.CpuUsage:F1}%",
                ["cpuCores"] = $"{Environment.ProcessorCount}",
                ["cpuTemp"] = info.CpuTemperature >= 0
                    ? $"{info.CpuTemperature:F0}°C"
                    : (info.BackendNote ?? Loc.Get("Common.Unavailable")),
                ["backend"] = BackendLabel(info.Backend),
                ["chartUrl"] = SystemInfoService.Instance.CpuChart.ToSvgDataUri(canvasWidth: 500) ?? "",
                ["cpuFreq"] = info.CpuFrequency >= 0
                    ? $"{info.CpuFrequency:F0}"
                    : "—",
            };

            _form.DataJson = JsonHelper.ToJson(data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CpuDetailPage] Update failed: {ex.Message}");
        }
    }

    private static string BackendLabel(SensorBackend b) => b switch
    {
        SensorBackend.Broker => Loc.Get("Backend.Broker"),
        SensorBackend.HWiNFO => Loc.Get("Backend.HwinfoShm"),
        SensorBackend.ThermalZone => Loc.Get("Backend.ThermalZone"),
        SensorBackend.None => Loc.Get("Backend.NoneFail"),
        _ => Loc.Get("Backend.Unknown"),
    };
}
