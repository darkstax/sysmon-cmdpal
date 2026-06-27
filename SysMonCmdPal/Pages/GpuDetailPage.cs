// Copyright (c) 2026 SysMonCmdPal
// GPU 详情页 — 使用率 + 温度 + 显存

using System;
using System.Text;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class GpuDetailPage : ContentPage
{
    public GpuDetailPage()
    {
        Icon = new IconInfo(""); // GPU — SensorShelf
        Title = "GPU 详情";
        Name = "GPU";
    }

    public override IContent[] GetContent()
    {
        var sys = SystemInfoService.Instance;
        sys.Refresh();
        var info = sys.Current;
        var gpu = info.Gpu;

        var sb = new StringBuilder();
        sb.AppendLine("# 🎮 GPU & 温度");
        sb.AppendLine();
        sb.AppendLine("| 指标 | 数值 |");
        sb.AppendLine("|------|------|");

        // GPU
        if (!string.IsNullOrEmpty(gpu.Name))
        {
            sb.AppendLine($"| GPU | **{gpu.Name}** |");
            sb.AppendLine($"| GPU 使用率 | {DockFormat.PercentMd(gpu.UsagePercent)} |");
            sb.AppendLine($"| GPU 温度 | {DockFormat.TempMd(gpu.Temperature)} |");
            sb.AppendLine($"| 显存已用 | {FormatMB(gpu.MemoryUsedMB)} |");
            sb.AppendLine($"| 显存总计 | {FormatMB(gpu.MemoryTotalMB)} |");
        }
        else
        {
            sb.AppendLine("| GPU | *未检测到 GPU* |");
        }

        // CPU 温度（与 GPU 同一个后端）
        sb.AppendLine($"| CPU 温度 | {DockFormat.TempMd(info.CpuTemperature)} |");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"*传感器后端: {BackendLabel(info.Backend)}*");
        if (info.BackendNote != null)
            sb.AppendLine($"*{info.BackendNote}*");

        return [new MarkdownContent(sb.ToString())];
    }

    private static string FormatMB(double v)
        => v > 0 ? $"**{v / 1024:F1} GB** ({v:F0} MB)" : "*不可用*";

    private static string BackendLabel(SensorBackend b) => b switch
    {
        SensorBackend.Broker => "Broker (最精准) ✓",
        SensorBackend.HwInfo => "HWiNFO 共享内存 ✓",
        SensorBackend.Lhm => "LHM 传感器库 ✓",
        SensorBackend.AmdAdl => "AMD ADL",
        SensorBackend.ThermalZone => "ACPI 热区",
        SensorBackend.None => "无可用后端 ✗",
        _ => "未知",
    };
}
