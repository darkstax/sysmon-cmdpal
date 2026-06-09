// Copyright (c) 2026 SysMonCmdPal
// 网络详情页

using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class NetworkDetailPage : ContentPage
{
    public NetworkDetailPage()
    {
        Icon = new IconInfo(""); // Network — SensorShelf
        Title = "网络详情";
        Name = "网络";
    }

    public override IContent[] GetContent()
    {
        var sys = SystemInfoService.Instance;
        sys.Refresh();
        var info = sys.Current;

        var sb = new StringBuilder();
        sb.AppendLine("# 🌐 网络");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 数值 |");
        sb.AppendLine("|------|------|");
        sb.AppendLine($"| 下载 | **{DockFormat.Speed(info.NetDown)}** |");
        sb.AppendLine($"| 上传 | **{DockFormat.Speed(info.NetUp)}** |");

        // 活跃网络接口
        var activeIfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .ToArray();

        if (activeIfaces.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 活跃接口");
            sb.AppendLine();
            sb.AppendLine("| 接口 | 速度 |");
            sb.AppendLine("|------|------|");
            foreach (var ni in activeIfaces)
            {
                string speed = ni.Speed > 0
                    ? (ni.Speed >= 1_000_000_000 ? $"{ni.Speed / 1_000_000_000:F0} Gbps" : $"{ni.Speed / 1_000_000:F0} Mbps")
                    : "—";
                sb.AppendLine($"| {ni.Name} | {speed} |");
            }
        }

        return [new MarkdownContent(sb.ToString())];
    }

}
