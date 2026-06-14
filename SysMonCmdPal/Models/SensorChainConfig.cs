// Copyright (c) 2026 SysMonCmdPal
// 传感器链配置 — 用户可自定义 CPU/GPU 数据源优先级链
// 商店安全版: 移除所有 ring-0 / Broker 依赖

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    /// <summary>使用 HWiNFO 共享内存（推荐，精度与驱动级读取相当）</summary>
    HWiNFO,
    /// <summary>[已废弃] 使用 Broker（商店版已移除，向后兼容旧配置）</summary>
    [Obsolete("商店版已移除 Broker。此值仅用于旧配置迁移，自动降级为 HWiNFO。")]
    Broker,
}

/// <summary>
/// 传感器链配置。存储在 settings.json 中，与 CmdPal 设置页面同步。
/// 版本 v4 新增商店安全默认值（移除 Broker）。
/// </summary>
public sealed class SensorChainConfig
{
    /// <summary>配置版本号</summary>
    public string Version { get; set; } = "4";

    /// <summary>高精度模式（v4: 默认 "HWiNFO"，向后兼容旧 "Broker"）</summary>
    public string PrecisionModeStr { get; set; } = "HWiNFO";

    [JsonIgnore]
    public PrecisionMode PrecisionMode
    {
        get => Enum.TryParse<PrecisionMode>(PrecisionModeStr, true, out var m)
            ? (m == PrecisionMode.Broker ? PrecisionMode.HWiNFO : m)
            : PrecisionMode.HWiNFO;
        set => PrecisionModeStr = value.ToString();
    }

    /// <summary>旧版兼容: HighPrecision = PrecisionMode == HWiNFO</summary>
    [JsonIgnore]
    public bool HighPrecision => PrecisionMode != PrecisionMode.None;

    /// <summary>CPU 传感器链（优先级从高到低）。商店版默认: HWiNFO → ADL → ThermalZone → LHM</summary>
    public List<string> CpuChain { get; set; } = ["HWiNFO", "ADL", "ThermalZone", "LHM"];

    /// <summary>根据 PrecisionMode 获取自动推荐的 CPU 链</summary>
    [JsonIgnore]
    public List<string> DefaultCpuChain => PrecisionMode switch
    {
        PrecisionMode.HWiNFO => (List<string>)["HWiNFO", "ADL", "ThermalZone", "LHM"],
        _ => (List<string>)["ThermalZone", "HWiNFO"],
    };

    /// <summary>GPU 传感器链（优先级从高到低）。商店版默认: LHM → ADL → HWiNFO → ThermalZone</summary>
    public List<string> GpuChain { get; set; } = ["LHM", "ADL", "HWiNFO", "ThermalZone"];

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

                // v4+ 有 precisionModeStr 字段
                if (root.TryGetProperty("precisionModeStr", out _))
                {
                    var cfg = JsonSerializer.Deserialize<SensorChainConfig>(json, _jsonOptions) ?? new();
                    // 旧 Broker 配置自动迁移到 HWiNFO
                    if (cfg.PrecisionModeStr == "Broker")
                        cfg.PrecisionModeStr = "HWiNFO";
                    cfg.CpuChain = MigrateOldChain(cfg.CpuChain);
                    cfg.GpuChain = MigrateOldChain(cfg.GpuChain);
                    cfg.Version = "4";
                    return cfg;
                }
                // v3 legacy: precisionMode key + version "3"
                if (root.TryGetProperty("precisionMode", out var pm3))
                {
                    var cfg3 = JsonSerializer.Deserialize<SensorChainConfig>(json, _jsonOptions) ?? new();
                    cfg3.PrecisionModeStr = (pm3.GetString() ?? "HWiNFO") == "Broker" ? "HWiNFO" : (pm3.GetString() ?? "HWiNFO");
                    // 迁移包含 Broker 的旧链
                    cfg3.CpuChain = MigrateOldChain(cfg3.CpuChain);
                    cfg3.GpuChain = MigrateOldChain(cfg3.GpuChain);
                    cfg3.Version = "4";
                    return cfg3;
                }
                // v2: 有 cpuChain / version=="2"
                if (root.TryGetProperty("cpuChain", out _) ||
                    root.TryGetProperty("version", out var verEl) && verEl.GetString() == "2")
                {
                    var config = JsonSerializer.Deserialize<SensorChainConfig>(json, _jsonOptions) ?? new();
                    if (root.TryGetProperty("precisionMode", out var pm))
                        config.PrecisionModeStr = (pm.GetString() ?? "HWiNFO") == "Broker" ? "HWiNFO" : (pm.GetString() ?? "HWiNFO");
                    else if (!config.HighPrecision)
                        config.PrecisionModeStr = "None";
                    config.CpuChain = MigrateOldChain(config.CpuChain);
                    config.GpuChain = MigrateOldChain(config.GpuChain);
                    config.Version = "4";
                    return config;
                }
                // v1: 只有 highPrecision 字段
                var config2 = new SensorChainConfig();
                if (root.TryGetProperty("highPrecision", out var hp))
                {
                    config2.PrecisionModeStr = hp.GetBoolean() ? "HWiNFO" : "None";
                    config2.CpuChain = hp.GetBoolean()
                        ? (List<string>)["HWiNFO", "ADL", "ThermalZone", "LHM"]
                        : (List<string>)["ThermalZone", "HWiNFO"];
                }
                config2.Version = "4";
                return config2;
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[SensorChainConfig] 加载失败: {ex.Message}");
        }
        return new();
    }

    /// <summary>从旧链中移除 Broker 条目（如果存在）</summary>
    private static List<string> MigrateOldChain(List<string> chain)
    {
        return chain.Where(s => s != "Broker").ToList();
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