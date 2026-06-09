// Copyright (c) 2026 SysMonCmdPal
// 传感器类别分组信息 — 排序、中文名、图标（带缓存）

using System.Collections.Generic;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

/// <summary>传感器类别分组信息（排序、中文名、图标）</summary>
internal static class SensorCategoryMeta
{
    public static readonly SensorCategory[] Order =
    [
        SensorCategory.CpuTemp, SensorCategory.CpuLoad, SensorCategory.CpuClock,
        SensorCategory.CpuPower, SensorCategory.CpuVoltage,
        SensorCategory.GpuTemp, SensorCategory.GpuLoad, SensorCategory.GpuClock,
        SensorCategory.GpuPower, SensorCategory.GpuMemory, SensorCategory.GpuFan,
        SensorCategory.GpuVoltage,
        SensorCategory.MbTemp, SensorCategory.MbFan, SensorCategory.MbVoltage,
        SensorCategory.StorageTemp, SensorCategory.StorageLoad,
    ];

    private static readonly Dictionary<SensorCategory, IconInfo> _iconCache = [];

    public static string Name(SensorCategory cat) => cat switch
    {
        SensorCategory.CpuTemp => "CPU 温度",
        SensorCategory.CpuLoad => "CPU 负载",
        SensorCategory.CpuClock => "CPU 频率",
        SensorCategory.CpuPower => "CPU 功耗",
        SensorCategory.CpuVoltage => "CPU 电压",
        SensorCategory.GpuTemp => "GPU 温度",
        SensorCategory.GpuLoad => "GPU 负载",
        SensorCategory.GpuClock => "GPU 频率",
        SensorCategory.GpuPower => "GPU 功耗",
        SensorCategory.GpuMemory => "GPU 显存",
        SensorCategory.GpuFan => "GPU 风扇",
        SensorCategory.GpuVoltage => "GPU 电压",
        SensorCategory.MbTemp => "主板 温度",
        SensorCategory.MbFan => "主板 风扇",
        SensorCategory.MbVoltage => "主板 电压",
        SensorCategory.StorageTemp => "存储 温度",
        SensorCategory.StorageLoad => "存储 负载",
        _ => cat.ToString(),
    };

    public static string IconGlyph(SensorCategory cat) => cat switch
    {
        SensorCategory.CpuTemp or SensorCategory.CpuLoad or SensorCategory.CpuClock
            or SensorCategory.CpuPower or SensorCategory.CpuVoltage => "",   // CPU
        SensorCategory.GpuTemp or SensorCategory.GpuLoad or SensorCategory.GpuClock
            or SensorCategory.GpuPower or SensorCategory.GpuMemory
            or SensorCategory.GpuFan or SensorCategory.GpuVoltage => "",      // GPU
        SensorCategory.MbTemp or SensorCategory.MbFan or SensorCategory.MbVoltage => "",  // Motherboard
        SensorCategory.StorageTemp or SensorCategory.StorageLoad => "",       // Storage
        _ => "",
    };

    /// <summary>获取缓存的 IconInfo（避免频繁分配）</summary>
    public static IconInfo GetIcon(SensorCategory cat)
    {
        if (!_iconCache.TryGetValue(cat, out var icon))
        {
            icon = new IconInfo(IconGlyph(cat));
            _iconCache[cat] = icon;
        }
        return icon;
    }
}
