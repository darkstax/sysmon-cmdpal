// Copyright (c) 2026 SysMonCmdPal
// 二级: 单个 GPU 详情（FormContent + 双图表：使用率 + 显存）

using System;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>
/// 二级页面: 单个 GPU 详情（FormContent + 双图表）
/// </summary>
internal sealed partial class GpuItemPage : RefreshingContentPage
{
    private readonly int _gpuIndex;
    private readonly FormContent _form = new();
    private readonly CopyTextCommand _copyCommand = new(string.Empty);
    private readonly SparklineChart _usageChart;
    private readonly SparklineChart _memChart;

    private const string Template = """
    {
      "type": "AdaptiveCard",
      "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
      "version": "1.5",
      "body": [
        {
          "type": "TextBlock",
          "text": "GPU",
          "size": "Large",
          "weight": "Bolder",
          "spacing": "Medium"
        },
        {
          "type": "TextBlock",
          "text": "${gpuName}",
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
              "text": "${gpuUsage}",
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
                  "text": "${gpuTemp}",
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
                  "text": "${gpuMem}",
                  "size": "Medium",
                  "weight": "Bolder",
                  "horizontalAlignment": "Center"
                },
                {
                  "type": "TextBlock",
                  "text": "显存",
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
                  "url": "${chartUrl}",
                  "altText": "GPU usage sparkline",
                  "horizontalAlignment": "Center",
                  "width": "380px",
                  "height": "160px"
                },
                {
                  "type": "TextBlock",
                  "text": "GPU 占用率",
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
                  "url": "${gpuMemChartUrl}",
                  "altText": "GPU memory sparkline",
                  "horizontalAlignment": "Center",
                  "width": "380px",
                  "height": "160px"
                },
                {
                  "type": "TextBlock",
                  "text": "显存占用率",
                  "size": "Small",
                  "isSubtle": true,
                  "horizontalAlignment": "Center",
                  "spacing": "Small"
                }
              ]
            }
          ]
        },
        {
          "type": "TextBlock",
          "text": "CPU 温度: ${cpuTemp}",
          "size": "Small",
          "isSubtle": true,
          "spacing": "Medium"
        }
      ]
    }
    """;

    public GpuItemPage(GpuInfo gpu, int index)
    {
        _gpuIndex = index;
        _usageChart = new SparklineChart(maxPoints: 34, metric: ChartMetric.Gpu);
        _memChart = new SparklineChart(maxPoints: 34, metric: ChartMetric.GpuMemory);

        Icon = new IconInfo(GpuClassifier.GetIcon(gpu));
        Title = string.IsNullOrEmpty(gpu.Name) ? $"GPU {index + 1}" : gpu.Name;
        Name = Title;
        Commands =
        [
            PageNavigation.BackContextItem(Dispose),
            new CommandContextItem(_copyCommand) { Title = Loc.Get("Common.CopyCurrentMetrics") },
        ];
        _form.TemplateJson = Template;
        _form.DataJson = """{"gpuName":"—","gpuUsage":"—","gpuTemp":"—","gpuMem":"—","cpuTemp":"—","backend":"—","chartUrl":"","gpuMemChartUrl":""}""";
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
            var gpus = info.Gpus;

            // 按 index 取对应 GPU
            GpuInfo gpu = (_gpuIndex < gpus.Length) ? gpus[_gpuIndex] : info.Gpu;

            // 推送到图表
            if (gpu.UsagePercent >= 0)
                _usageChart.Push((float)gpu.UsagePercent);
            if (gpu.MemoryTotalMB > 0)
                _memChart.Push((float)(gpu.MemoryUsedMB * 100.0 / gpu.MemoryTotalMB));

            string chartUrl = _usageChart.ToSvgDataUri() ?? "";
            string gpuMemChartUrl = _memChart.ToSvgDataUri() ?? "";

            var data = new Dictionary<string, string>
            {
                ["gpuName"] = string.IsNullOrEmpty(gpu.Name)
                    ? Loc.Get("Gpu.NotDetected")
                    : gpu.Name.ToUpper().Trim(),
                ["gpuUsage"] = DockFormat.PercentMd(gpu.UsagePercent),
                ["gpuTemp"] = DockFormat.TempMd(gpu.Temperature),
                ["gpuMem"] = gpu.MemoryTotalMB > 0
                    ? $"{gpu.MemoryUsedMB / 1024:F1} / {gpu.MemoryTotalMB / 1024:F1} GB"
                    : Loc.Get("Common.Unavailable"),
                ["cpuTemp"] = DockFormat.TempMd(info.CpuTemperature),
                ["backend"] = BackendLabel(info.Backend),
                ["chartUrl"] = chartUrl,
                ["gpuMemChartUrl"] = gpuMemChartUrl,
            };

            _copyCommand.Text = BuildCopyText(gpu);
            _form.DataJson = JsonHelper.ToJson(data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GpuItemPage] Update failed: {ex.Message}");
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

    private static string BuildCopyText(GpuInfo gpu)
    {
        string name = string.IsNullOrWhiteSpace(gpu.Name) ? "GPU" : gpu.Name.Trim();
        string usage = gpu.UsagePercent >= 0 ? $"{gpu.UsagePercent:F0}%" : Loc.Get("Common.NA");
        string temp = gpu.Temperature >= 0 ? $"{gpu.Temperature:F0}°C" : Loc.Get("Common.NA");
        string memory = gpu.MemoryTotalMB > 0
            ? $"{gpu.MemoryUsedMB / 1024:F1}/{gpu.MemoryTotalMB / 1024:F1} GB"
            : Loc.Get("Common.NA");

        return $"GPU {name} · {usage} · {temp} · {memory}";
    }
}
