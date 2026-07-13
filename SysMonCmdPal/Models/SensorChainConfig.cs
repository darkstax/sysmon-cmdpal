// Copyright (c) 2026 SysMonCmdPal
// 精简版传感器配置 — 仅保留版本号和旧 PrecisionMode 兼容字段
// 运行时数据源选择已改为自动回退链；这里只负责兼容历史 settings.json
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

/// <summary>旧 PrecisionMode 配置值的兼容枚举；运行时不再提供手动切换。</summary>
public enum PrecisionMode
{
    /// <summary>历史上的非高精度模式；当前仅用于兼容旧配置。</summary>
    None,
    /// <summary>历史上的 Broker 偏好；当前自动回退链会自行检测 Broker。</summary>
    Broker,
}

/// <summary>
/// 精简版传感器配置。存储在 settings.json 中。
/// PrecisionMode 相关字段仅用于兼容历史配置，当前没有 CmdPal 手动切换入口。
/// </summary>
public sealed class SensorChainConfig
{
    /// <summary>配置版本号</summary>
    public string Version { get; set; } = "4";

    /// <summary>
    /// 历史序列化字段。保留以便继续读取/写回旧 settings.json，
    /// 但运行时数据源选择始终走自动回退链。
    /// </summary>
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

    /// <summary>旧版兼容投影: HighPrecision = PrecisionMode != None。</summary>
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
