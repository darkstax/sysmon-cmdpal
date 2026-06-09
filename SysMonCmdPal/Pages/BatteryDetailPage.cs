// Copyright (c) 2026 SysMonCmdPal
// 电池详情页

using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class BatteryDetailPage : ContentPage
{
    public BatteryDetailPage()
    {
        Icon = new IconInfo("");
        Title = "电池";
        Name = "电池";
    }

    public override IContent[] GetContent()
    {
        var sys = SystemInfoService.Instance;
        sys.Refresh();
        var info = sys.Current;

        if (info.BatteryPercent < 0)
        {
            return [new MarkdownContent("# 🔋 电池\n\n未检测到电池（台式机或虚拟机）。")];
        }

        string statusText = DockFormat.BatteryStatusText(info.BatteryStatus);

        var sb = new StringBuilder();
        sb.AppendLine("# 🔋 电池");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 数值 |");
        sb.AppendLine("|------|------|");
        sb.AppendLine($"| 电量 | **{info.BatteryPercent:F0}%** |");
        sb.AppendLine($"| 状态 | {statusText} |");

        return [new MarkdownContent(sb.ToString())];
    }
}
