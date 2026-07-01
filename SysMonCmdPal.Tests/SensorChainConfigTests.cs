// Copyright (c) 2026 SysMonCmdPal
// SensorChainConfig 测试 — 向后兼容 + 保存/加载往返

using System.Text.Json;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SensorChainConfigTests
{
    // Always save the real config path to avoid cross-test pollution
    // when multiple tests modify the static ConfigPath field.
    private static readonly string _realConfigPath = SensorChainConfig.ConfigPath;

    /// <summary>
    /// Helper: temporarily redirect ConfigPath to a temp file, run the action,
    /// then restore. Ensures isolation even when tests fail.
    /// </summary>
    private static void WithTempConfig(Action<string> action)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"sysmon_test_{Guid.NewGuid():N}.json");
        SensorChainConfig.ConfigPath = tempPath;
        try
        {
            action(tempPath);
        }
        finally
        {
            SensorChainConfig.ConfigPath = _realConfigPath;
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    // ================================================================
    // PrecisionModeStr → PrecisionMode 映射
    // ================================================================

    [Theory]
    [InlineData("HWiNFO", PrecisionMode.None)]   // explicit backward compat in getter
    [InlineData("Broker", PrecisionMode.Broker)]
    [InlineData("None", PrecisionMode.None)]
    [InlineData("Invalid", PrecisionMode.None)]   // TryParse fails → fallback None
    [InlineData("", PrecisionMode.None)]          // empty → TryParse fails → fallback None
    public void PrecisionModeStr_MapsToPrecisionMode(string str, PrecisionMode expected)
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = str };
        Assert.Equal(expected, cfg.PrecisionMode);
    }

    // ================================================================
    // 设置 PrecisionMode 属性会同步更新 PrecisionModeStr
    // ================================================================

    [Fact]
    public void SetPrecisionMode_UpdatesPrecisionModeStr()
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = "None" };
        Assert.Equal(PrecisionMode.None, cfg.PrecisionMode);

        cfg.PrecisionMode = PrecisionMode.Broker;
        Assert.Equal("Broker", cfg.PrecisionModeStr);
        Assert.Equal(PrecisionMode.Broker, cfg.PrecisionMode);

        cfg.PrecisionMode = PrecisionMode.None;
        Assert.Equal("None", cfg.PrecisionModeStr);
        Assert.Equal(PrecisionMode.None, cfg.PrecisionMode);
    }

    // ================================================================
    // HighPrecision 兼容属性
    // ================================================================

    [Theory]
    [InlineData("Broker", true)]
    [InlineData("None", false)]
    [InlineData("HWiNFO", false)]
    public void HighPrecision_ReflectsPrecisionMode(string str, bool expected)
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = str };
        Assert.Equal(expected, cfg.HighPrecision);
    }

    // ================================================================
    // Save/Load 往返 — 使用临时路径避免污染真实配置
    // ================================================================

    [Fact]
    public void SaveLoad_Roundtrip_PreservesPrecisionModeStr()
    {
        WithTempConfig(tempPath =>
        {
            var cfg = new SensorChainConfig { PrecisionModeStr = "None", Version = "4" };
            cfg.Save();

            Assert.True(File.Exists(tempPath));

            var loaded = SensorChainConfig.Load();
            Assert.Equal("None", loaded.PrecisionModeStr);
            Assert.Equal(PrecisionMode.None, loaded.PrecisionMode);
            Assert.Equal("4", loaded.Version);
        });
    }

    [Fact]
    public void SaveLoad_Roundtrip_PreservesBrokerMode()
    {
        WithTempConfig(tempPath =>
        {
            var cfg = new SensorChainConfig { PrecisionModeStr = "Broker", Version = "4" };
            cfg.Save();

            var loaded = SensorChainConfig.Load();
            Assert.Equal("Broker", loaded.PrecisionModeStr);
            Assert.Equal(PrecisionMode.Broker, loaded.PrecisionMode);
        });
    }

    // ================================================================
    // JSON 反序列化确认 camelCase 字段名
    // ================================================================

    [Fact]
    public void Load_ParsesCamelCaseJson()
    {
        WithTempConfig(tempPath =>
        {
            File.WriteAllText(tempPath, @"{""version"":""4"",""precisionModeStr"":""Broker""}");

            var loaded = SensorChainConfig.Load();
            Assert.Equal("Broker", loaded.PrecisionModeStr);
            Assert.Equal(PrecisionMode.Broker, loaded.PrecisionMode);
        });
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        WithTempConfig(tempPath =>
        {
            // Ensure file does not exist (WithTempConfig already cleans up, but be safe)
            if (File.Exists(tempPath))
                File.Delete(tempPath);

            var cfg = SensorChainConfig.Load();
            Assert.Equal("None", cfg.PrecisionModeStr);   // default
            Assert.Equal(PrecisionMode.None, cfg.PrecisionMode);
            Assert.Equal("4", cfg.Version);
        });
    }

    // ================================================================
    // 旧版兼容: HWiNFO → None (explicit backward-compat in PrecisionMode getter)
    // ================================================================

    [Fact]
    public void Load_LegacyHwInfoJson_MapsToNone()
    {
        WithTempConfig(tempPath =>
        {
            File.WriteAllText(tempPath, @"{""version"":""3"",""precisionModeStr"":""HWiNFO""}");

            var loaded = SensorChainConfig.Load();
            Assert.Equal("HWiNFO", loaded.PrecisionModeStr);
            Assert.Equal(PrecisionMode.None, loaded.PrecisionMode);
        });
    }
}
