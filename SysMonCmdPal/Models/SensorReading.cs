// Copyright (c) 2026 SysMonCmdPal
// 传感器数据模型 — 按类别组织的全量 LHM 传感器

using System;
using System.Collections.Generic;

namespace SysMonCmdPal;

/// <summary>传感器类别</summary>
public enum SensorCategory
{
    CpuTemp,        // CPU 温度
    CpuLoad,        // CPU 负载
    CpuClock,       // CPU 频率
    CpuPower,       // CPU 功耗
    CpuVoltage,     // CPU 电压
    GpuTemp,        // GPU 温度
    GpuLoad,        // GPU 负载
    GpuClock,       // GPU 频率
    GpuPower,       // GPU 功耗
    GpuFan,         // GPU 风扇
    GpuMemory,      // GPU 显存
    GpuVoltage,     // GPU 电压
    MbTemp,         // 主板温度
    MbFan,          // 主板风扇
    MbVoltage,      // 主板电压
    StorageTemp,    // 存储温度 (NVMe SSD)
    StorageLoad,    // 存储负载
}

/// <summary>单个传感器读数</summary>
public struct SensorReading
{
    /// <summary>硬件名称，如 "AMD Ryzen 7 6800H"（null = 未初始化/未找到）</summary>
    public string? HardwareName;

    /// <summary>传感器名称，如 "CPU Package"（null = 未初始化/未找到）</summary>
    public string? SensorName;

    /// <summary>标签，如 "Temperature"</summary>
    public string? Label;

    /// <summary>数值</summary>
    public double Value;

    /// <summary>单位，如 "°C"、"MHz"、"W"</summary>
    public string? Unit;

    /// <summary>类别</summary>
    public SensorCategory Category;

    /// <summary>唯一标识键（用于配置持久化）</summary>
    public string? UniqueKey;

    /// <summary>显示名（用户可自定义）</summary>
    public string? DisplayName;

    /// <summary>警告阈值（>= 此值显示黄色），0 表示不启用</summary>
    public double WarningThreshold;

    /// <summary>严重阈值（>= 此值显示红色），0 表示不启用</summary>
    public double CriticalThreshold;

    public bool IsTemperature => Category is SensorCategory.CpuTemp or SensorCategory.GpuTemp
        or SensorCategory.MbTemp or SensorCategory.StorageTemp;

    public bool IsLoad => Category is SensorCategory.CpuLoad or SensorCategory.GpuLoad;

    /// <summary>格式化值 + 单位</summary>
    public string FormatValue()
    {
        if (Value >= 1000)
            return $"{Value:F0} {Unit}";
        if (Value >= 100)
            return $"{Value:F1} {Unit}";
        if (Value >= 1)
            return $"{Value:F2} {Unit}";
        return $"{Value:F3} {Unit}";
    }
}

/// <summary>用户传感器配置</summary>
public sealed class SensorConfig
{
    public string Version { get; set; } = "1.0";
    public List<SensorConfigEntry> Sensors { get; set; } = [];
}

/// <summary>单个传感器的配置条目</summary>
public sealed class SensorConfigEntry
{
    /// <summary>匹配键（HardwareName|SensorName|Label）</summary>
    public string Key { get; set; } = "";

    /// <summary>自定义显示名</summary>
    public string? DisplayName { get; set; }

    /// <summary>在 Dock 中的顺序（越小越靠前）</summary>
    public int Order { get; set; }

    /// <summary>是否在 Dock 中显示</summary>
    public bool InDock { get; set; } = true;

    /// <summary>警告阈值</summary>
    public double WarningThreshold { get; set; }

    /// <summary>严重阈值</summary>
    public double CriticalThreshold { get; set; }
}
