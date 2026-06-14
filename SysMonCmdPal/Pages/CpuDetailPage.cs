// Copyright (c) 2026 SysMonCmdPal
// CPU 详情页 — 使用率 + 温度 + 核心数

using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class CpuDetailPage : ContentPage
{
    public CpuDetailPage()
    {
        Icon = new IconInfo("");
        Title = "CPU 详情";
        Name = "CPU";
    }

    public override IContent[] GetContent()
    {
        var sys = SystemInfoService.Instance;
        sys.Refresh();
        var info = sys.Current;

        var sb = new StringBuilder();
        sb.AppendLine("# 🖥 CPU");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 数值 |");
        sb.AppendLine("|------|------|");
        sb.AppendLine($"| 使用率 | **{info.CpuUsage:F1}%** |");
        sb.AppendLine($"| 逻辑核心 | {Environment.ProcessorCount} |");

        if (info.CpuTemperature >= 0)
            sb.AppendLine($"| 温度 | **{info.CpuTemperature:F0}°C** |");
        else
            sb.AppendLine($"| 温度 | *{info.BackendNote ?? "不可用"}* |");

        // 后端状态
        sb.AppendLine();
        sb.AppendLine($"### 传感器后端: {BackendLabel(info.Backend)}");
        if (info.BackendNote != null)
            sb.AppendLine($"*{info.BackendNote}*");

        return [new MarkdownContent(sb.ToString())];
    }

    private static string BackendLabel(SensorBackend b) => b switch
    {
        SensorBackend.HwInfo => "HWiNFO 共享内存 ✓",
        SensorBackend.Lhm => "LHM 传感器库 ✓",
        SensorBackend.AmdAdl => "AMD ADL (回退模式)",
        SensorBackend.ThermalZone => "ACPI 热区",
        SensorBackend.None => "无可用后端 ✗",
        _ => "未知",
    };
}
