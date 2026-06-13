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
    Auto,
    DedicatedOnly,
    All,
}

/// <summary>高精度模式 — 决定最高精度的数据源</summary>
public enum PrecisionMode
{
    /// <summary>不使用高精度源，仅 ACPI ThermalZone</summary>
    None,
    /// <summary>使用 HWiNFO 共享内存（无需 PawnIO 驱动）</summary>
    HWiNFO,
    /// <summary>使用 Broker（PawnIO 驱动，最精准的 Tctl/Tdie）</summary>
    Broker,
}

/// <summary>
/// 传感器链配置。存储在 settings.json 中，与 CmdPal 设置页面同步。
/// 版本 v2 新增链配置，向后兼容 v1（仅含 highPrecision 键）。
/// </summary>
public sealed class SensorChainConfig
{
    /// <summary>配置版本号</summary>
    public string Version { get; set; } = "3";

    /// <summary>高精度模式（v3: "None" / "HWiNFO" / "Broker"，向后兼容 v1/v2 bool）</summary>
    public string PrecisionModeStr { get; set; } = "Broker";

    [JsonIgnore]
    public PrecisionMode PrecisionMode
    {
        get => Enum.TryParse<PrecisionMode>(PrecisionModeStr, true, out var m) ? m : PrecisionMode.Broker;
        set => PrecisionModeStr = value.ToString();
    }

    /// <summary>旧版兼容: HighPrecision = PrecisionMode == Broker</summary>
    [JsonIgnore]
    public bool HighPrecision => PrecisionMode == PrecisionMode.Broker;

    /// <summary>CPU 传感器链（优先级从高到低）</summary>
    public List<string> CpuChain { get; set; } = ["Broker", "ThermalZone", "HWiNFO"];

    /// <summary>根据 PrecisionMode 获取自动推荐的 CPU 链</summary>
    [JsonIgnore]
    public List<string> DefaultCpuChain => PrecisionMode switch
    {
        PrecisionMode.Broker => (List<string>)["Broker", "ThermalZone", "HWiNFO"],
        PrecisionMode.HWiNFO => (List<string>)["HWiNFO", "ThermalZone", "Broker"],
        _ => (List<string>)["ThermalZone", "HWiNFO"],
    };

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
    public static string ConfigPath = Path.Combine(
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

                // v3+ 有 precisionModeStr 字段（camelCase naming policy）
                if (root.TryGetProperty("precisionModeStr", out _))
                {
                    return JsonSerializer.Deserialize<SensorChainConfig>(json, _jsonOptions) ?? new();
                }
                // v3+ legacy: precisionMode (old key) + version "3"
                if (root.TryGetProperty("precisionMode", out var pm3) &&
                    root.TryGetProperty("version", out var verCheck) && verCheck.GetString() == "3")
                {
                    var cfg3 = JsonSerializer.Deserialize<SensorChainConfig>(json, _jsonOptions) ?? new();
                    cfg3.PrecisionModeStr = pm3.GetString() ?? "Broker";
                    cfg3.Version = "3";
                    return cfg3;
                }
                // v2: 有 cpuChain / version=="2"
                if (root.TryGetProperty("cpuChain", out _) ||
                    root.TryGetProperty("version", out var verEl) && verEl.GetString() == "2")
                {
                    var config = JsonSerializer.Deserialize<SensorChainConfig>(json, _jsonOptions) ?? new();
                    // 从旧版 precisionMode 键迁移（v2 序列化为 precisionMode 而非 precisionModeStr）
                    if (root.TryGetProperty("precisionMode", out var pm))
                        config.PrecisionModeStr = pm.GetString() ?? "Broker";
                    // 从旧版 highPrecision bool 迁移到 PrecisionMode
                    else if (!config.HighPrecision)
                        config.PrecisionModeStr = "None";
                    config.Version = "3";
                    return config;
                }
                // v1: 只有 highPrecision 字段
                var config2 = new SensorChainConfig();
                if (root.TryGetProperty("highPrecision", out var hp))
                {
                    config2.PrecisionModeStr = hp.GetBoolean() ? "Broker" : "None";
                    config2.CpuChain = hp.GetBoolean()
                        ? (List<string>)["Broker", "ThermalZone", "HWiNFO"]
                        : (List<string>)["ThermalZone", "HWiNFO"];
                }
                return config2;
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
