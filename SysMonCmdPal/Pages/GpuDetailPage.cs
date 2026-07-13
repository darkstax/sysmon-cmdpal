// Copyright (c) 2026 SysMonCmdPal
// GPU 详情页 — 两级 ListPage
// 一级: GPU 列表（型号/温度/使用率）
// 二级: 单个 GPU 详情（FormContent + 双图表：使用率 + 显存）

using System;
using System.Linq;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>
/// 一级页面: GPU 列表
/// </summary>
internal sealed partial class GpuDetailPage : ListPage, IDisposable
{
    private GpuItemPage[]? _cachedItems;
    private int _cachedCount = -1;
    private readonly CopyTextCommand _copyCommand = new(string.Empty);

    public GpuDetailPage()
    {
        Icon = new IconInfo("");
        Title = Loc.Get("Gpu.PageTitle");
        Name = "GPU";
    }

    public override IListItem[] GetItems()
    {
        // 用已缓存快照，不触发同步 Refresh（避免阻塞 UI）
        var info = SystemInfoService.Instance.Current;
        var gpus = info.Gpus;
        _copyCommand.Text = BuildCopyText(info);

        if (gpus == null || gpus.Length == 0)
        {
            return CreateItemsWithCopy([
                new ListItem(new NoOpPage())
                {
                    Title = Loc.Get("Gpu.NotDetected"),
                    Subtitle = "无可用数据源",
                    Icon = new IconInfo(""),
                }
            ]);
        }

        // 缓存二级页实例 — GPU 数量固定，复用避免 timer 泄漏
        if (_cachedItems == null || _cachedCount != gpus.Length)
        {
            DisposeCachedItems();
            _cachedItems = gpus.Select((gpu, i) => new GpuItemPage(gpu, i)).ToArray();
            _cachedCount = gpus.Length;
        }

        return CreateItemsWithCopy(_cachedItems.Select((page, i) =>
        {
            var gpu = gpus[i];
            string memStr = gpu.MemoryTotalMB > 0
                ? $"{gpu.MemoryUsedMB / 1024:F1}/{gpu.MemoryTotalMB / 1024:F1} GB"
                : "—";
            string tempStr = gpu.Temperature > 0 ? $"{gpu.Temperature:F0}°C" : "—";
            string usageStr = gpu.UsagePercent >= 0 ? $"{gpu.UsagePercent:F0}%" : "—";

            return new ListItem(page)
            {
                Title = string.IsNullOrEmpty(gpu.Name) ? $"GPU {i + 1}" : gpu.Name,
                Subtitle = $"{usageStr} · {tempStr} · {memStr}",
                Icon = new IconInfo(""),
            };
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

    /// <summary>预渲染：用已缓存数据创建二级页实例并启动定时器（不触发 Refresh）</summary>
    public void StartTimer()
    {
        // 用已缓存的 GPU 数据创建二级页（不调 GetItems 避免同步 Refresh 阻塞）
        var gpus = SystemInfoService.Instance.Current.Gpus;
        if (_cachedItems == null && gpus != null && gpus.Length > 0)
        {
            _cachedItems = gpus.Select((gpu, i) => new GpuItemPage(gpu, i)).ToArray();
            _cachedCount = gpus.Length;
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
        var gpu = info.Gpus.Length > 0 ? info.Gpus[0] : info.Gpu;
        string name = string.IsNullOrWhiteSpace(gpu.Name) ? "GPU" : gpu.Name.Trim();
        string usage = gpu.UsagePercent >= 0 ? $"{gpu.UsagePercent:F0}%" : Loc.Get("Common.NA");
        string temp = gpu.Temperature >= 0 ? $"{gpu.Temperature:F0}°C" : Loc.Get("Common.NA");
        string memory = gpu.MemoryTotalMB > 0
            ? $"{gpu.MemoryUsedMB / 1024:F1}/{gpu.MemoryTotalMB / 1024:F1} GB"
            : Loc.Get("Common.NA");

        return $"GPU {name} · {usage} · {temp} · {memory}";
    }
}

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
          "text": "🎮 GPU",
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

        Icon = new IconInfo("");
        Title = string.IsNullOrEmpty(gpu.Name) ? $"GPU {index + 1}" : gpu.Name;
        Name = Title;
        Commands = [new CommandContextItem(_copyCommand) { Title = Loc.Get("Common.CopyCurrentMetrics") }];
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
