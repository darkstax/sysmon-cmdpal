// Copyright (c) 2026 SysMonCmdPal

namespace SysMonCmdPal;

internal static class DockFormat
{
    /// <summary>格式化速度（字节/秒）。>=1MB/s 显示 MB/s，>=1KB/s 显示 KB/s。</summary>
    public static string Speed(double bytesPerSec) => bytesPerSec switch
    {
        >= 1_000_000 => $"{bytesPerSec / 1_000_000:F1} MB/s",
        >= 1_000 => $"{bytesPerSec / 1_000:F0} KB/s",
        >= 1 => $"{bytesPerSec:F0} B/s",
        _ => "0 B/s",
    };

    /// <summary>紧凑速度（Dock 栏用），单位缩写成单个字母以节省宽度。输入：字节/秒。</summary>
    public static string CompactSpeed(double bytesPerSec) => bytesPerSec switch
    {
        >= 1_000_000 => $"{bytesPerSec / 1_000_000:F1}M/s",
        >= 1_000 => $"{bytesPerSec / 1_000:F0}K/s",
        >= 1 => $"{bytesPerSec:F0}B/s",
        _ => "0",
    };

    /// <summary>CPU/GPU 温度。>=0 显示数值，-1 表示不可用。</summary>
    public static string Temp(double c) => c >= 0 ? $"{c:F0}°C" : "N/A";

    /// <summary>百分比。>=0 显示数值，负值表示不可用。</summary>
    public static string Percent(double p) => p >= 0 ? $"{p:F0}%" : "N/A";

    /// <summary>电池状态文本。</summary>
    public static string BatteryStatusText(string status) => status switch
    {
        "charging" => Loc.Get("BatteryStatus.Charging"),
        "discharging" => Loc.Get("BatteryStatus.Discharging"),
        "dual" => Loc.Get("BatteryStatus.Dual"),
        "full" => Loc.Get("BatteryStatus.Full"),
        "no battery" => Loc.Get("BatteryStatus.NoBattery"),
        _ => status,
    };

    /// <summary>Markdown 格式化：温度或斜体 N/A。</summary>
    public static string TempMd(double c) => c >= 0 ? $"**{c:F0}°C**" : "*N/A*";

    /// <summary>Markdown 格式化：百分比或斜体 N/A。</summary>
    public static string PercentMd(double p) => p >= 0 ? $"**{p:F1}%**" : "*N/A*";
}
