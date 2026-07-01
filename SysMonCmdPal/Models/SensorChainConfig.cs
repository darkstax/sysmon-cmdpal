// Copyright (c) 2026 SysMonCmdPal
// 精简版传感器配置 — 仅保留精度模式和版本号
// 传感器链和 GPU 模式已移除，CpuSensorReader / GpuSensorReader 独立工作
//
// IMPORTANT: This project uses AOT/Trim in Release. Reflection-based JSON
// serialization is disabled by the trimmer, so we MUST use JsonSerializerContext
// (source generator) for all JSON operations.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SysMonCmdPal;

/// <summary>高精度模式 — 决定最高精度的数据源</summary>
public enum PrecisionMode
{
    /// <summary>不使用高精度源，仅 ACPI ThermalZone</summary>
    None,
    /// <summary>使用 Broker 共享内存推送（最精准，需 SysMonBroker 运行）</summary>
    Broker,
}

/// <summary>
/// 精简版传感器配置。存储在 settings.json 中，与 CmdPal 设置页面同步。
/// </summary>
public sealed class SensorChainConfig
{
    /// <summary>配置版本号</summary>
    public string Version { get; set; } = "4";

    /// <summary>高精度模式（默认 "None"，需手动切换到 Broker）</summary>
    public string PrecisionModeStr { get; set; } = "None";

    [JsonIgnore]
    public PrecisionMode PrecisionMode
    {
        get {
            // 向后兼容：旧版 'HWiNFO' 映射为 None（ACPI 回退）
            if (PrecisionModeStr == "HWiNFO") return PrecisionMode.None;
            return Enum.TryParse<PrecisionMode>(PrecisionModeStr, true, out var m) ? m : PrecisionMode.None;
        }
        set => PrecisionModeStr = value.ToString();
    }

    /// <summary>旧版兼容: HighPrecision = PrecisionMode != None</summary>
    [JsonIgnore]
    public bool HighPrecision => PrecisionMode != PrecisionMode.None;

    /// <summary>配置文件路径</summary>
    internal static string ConfigPath = Path.Combine(
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
                var cfg = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.SensorChainConfig) ?? new();
                cfg.Version = "4";
                return cfg;
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[SensorChainConfig] Load failed: {ex.Message}");
        }
        return new();
    }

    /// <summary>保存配置到文件</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, ConfigJsonContext.Default.SensorChainConfig);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[SensorChainConfig] Save failed: {ex.Message}");
        }
    }
}

/// <summary>
/// JsonSerializerContext for AOT/Trim-safe JSON serialization.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SensorChainConfig))]
internal partial class ConfigJsonContext : JsonSerializerContext;