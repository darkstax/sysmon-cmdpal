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
        Icon = new IconInfo(SysMonIcons.Gpu);
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
                new ListItem(new NoOpCommand())
                {
                    Title = Loc.Get("Gpu.NotDetected"),
                    Subtitle = "无可用数据源",
                    Icon = new IconInfo(SysMonIcons.Gpu),
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
                Icon = new IconInfo(GpuClassifier.GetIcon(gpu)),
            };
        }));
    }

    private IListItem[] CreateItemsWithCopy(IEnumerable<IListItem> items)
    {
        return
        [
            PageNavigation.BackListItem(Dispose),
            new ListItem(_copyCommand)
            {
                Title = Loc.Get("Common.CopyCurrentMetrics"),
                Subtitle = _copyCommand.Text,
                Icon = new IconInfo(SysMonIcons.Copy),
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
