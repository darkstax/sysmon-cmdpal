// Copyright (c) 2026 SysMonCmdPal
// 传感器链配置 — 用户可自定义 CPU/GPU 数据源优先级链
// 存储路径: %LOCALAPPDATA%\SysMonCmdPal\settings.json

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SysMonCmdPal;

/// <summary>GPU 模式</summary>
public enum GpuMode
{
    /// <summary>智能筛选: 部分卡有 3D 活动则只显示活动的（默认）</summary>
    Auto,
    /// <summary>仅显示独立显卡（不显示集显）</summary>
    DedicatedOnly,
    /// <summary>显示所有检测到的 GPU</summary>
    All,
}

/// <summary>
/// 传感器链配置。存储在 settings.json 中，与 CmdPal 设置页面同步。
/// 版本 v2 新增链配置，向后兼容 v1（仅含 highPrecision 键）。
/// </summary>
public sealed class SensorChainConfig
{
    /// <summary>配置版本号</summary>
    public string Version { get; set; } = "2";

    /// <summary>高精度模式（旧版兼容字段，等价于 CpuChain 包含 "Broker"）</summary>
    public bool HighPrecision { get; set; } = true;

    /// <summary>CPU 传感器链（优先级从高到低）</summary>
    public List<string> CpuChain { get; set; } = ["Broker", "ThermalZone", "HWiNFO"];

    /// <summary>GPU 传感器链（优先级从高到低）</summary>
    public List<string> GpuChain { get; set; } = ["Broker", "ThermalZone", "HWiNFO"];

    /// <summary>GPU 模式字符串（序列化友好）</summary>
    public string GpuModeStr { get; set; } = "Auto";

    /// <summary>GPU 模式</summary>
    [JsonIgnore]
    public GpuMode GpuMode
    {
        get => Enum.TryParse<GpuMode>(GpuModeStr, true, out var m) ? m : GpuMode.Auto;
        set => GpuModeStr = value.ToString();
    }

    /// <summary>配置文件路径</summary>
    public static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysMonCmdPal", "settings.json");

    /// <summary>从文件加载配置，不存在则返回默认值</summary>
    public static SensorChainConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                // 检测版本: 如果是 v2 格式（有 cpuChain 字段），直接反序列化
                if (root.TryGetProperty("cpuChain", out _) ||
                    root.TryGetProperty("version", out var verEl) && verEl.GetString() == "2")
                {
                    return JsonSerializer.Deserialize<SensorChainConfig>(json, _jsonOptions) ?? new();
                }

                // 向后兼容 v1: 只有 highPrecision 字段
                var config = new SensorChainConfig();
                if (root.TryGetProperty("highPrecision", out var hp))
                {
                    config.HighPrecision = hp.GetBoolean();
                }
                // 如果高精度关闭，CpuChain 默认跳过 Broker
                if (!config.HighPrecision)
                {
                    config.CpuChain = ["ThermalZone", "HWiNFO"];
                }
                return config;
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[SensorChainConfig] 加载失败: {ex.Message}");
        }
        return new();
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>保存配置到文件</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, _jsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[SensorChainConfig] 保存失败: {ex.Message}");
        }
    }
}
