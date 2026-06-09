// Copyright (c) 2026 SysMonCmdPal
// 内存详情页

using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class MemoryDetailPage : ContentPage
{
    public MemoryDetailPage()
    {
        Icon = new IconInfo(""); // RAM — SensorShelf
        Title = "内存详情";
        Name = "内存";
    }

    public override IContent[] GetContent()
    {
        var sys = SystemInfoService.Instance;
        sys.Refresh();
        var info = sys.Current;

        double totalGB = info.MemoryTotalBytes / (1024.0 * 1024 * 1024);
        double usedGB = info.MemoryUsedBytes / (1024.0 * 1024 * 1024);
        double freeGB = totalGB - usedGB;

        var sb = new StringBuilder();
        sb.AppendLine("# 🧠 内存");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 数值 |");
        sb.AppendLine("|------|------|");
        sb.AppendLine($"| 已用 | **{usedGB:F1} GB** ({info.MemoryUsed:F0}%) |");
        sb.AppendLine($"| 空闲 | {freeGB:F1} GB |");
        sb.AppendLine($"| 总计 | {totalGB:F1} GB |");

        return [new MarkdownContent(sb.ToString())];
    }
}
