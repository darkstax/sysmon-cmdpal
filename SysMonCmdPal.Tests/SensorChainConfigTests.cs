// Copyright (c) 2026 SysMonCmdPal
// SensorChainConfig 测试 — 旧 PrecisionMode 兼容 + 保存/加载往返

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
    // 旧 PrecisionMode 字段 -> 兼容投影
    // ================================================================

    [Theory]
    [InlineData("HWiNFO", PrecisionMode.None)]   // explicit backward compat in getter
    [InlineData("Broker", PrecisionMode.Broker)]
    [InlineData("None", PrecisionMode.None)]
    [InlineData("Invalid", PrecisionMode.None)]   // TryParse fails → fallback None
    [InlineData("", PrecisionMode.None)]          // empty → TryParse fails → fallback None
    public void LegacyPrecisionModeStr_MapsToCompatibilityProjection(string str, PrecisionMode expected)
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = str };
        Assert.Equal(expected, cfg.PrecisionMode);
    }

    // ================================================================
    // 兼容投影 setter 仍会同步更新底层序列化字段
    // ================================================================

    [Fact]
    public void CompatibilityProjectionSetter_UpdatesStoredPrecisionModeStr()
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
    public void HighPrecision_ReflectsCompatibilityProjection(string str, bool expected)
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = str };
        Assert.Equal(expected, cfg.HighPrecision);
    }

    // ================================================================
    // Save/Load 往返 — 保留旧字段但不引入手动切换语义
    // ================================================================

    [Fact]
    public void SaveLoad_Roundtrip_PreservesLegacyPrecisionModeStr()
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
    public void SaveLoad_Roundtrip_PreservesLegacyBrokerPreferenceString()
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
    public void Load_ParsesCamelCaseLegacyPrecisionField()
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
    public void Load_MissingFile_ReturnsCompatibilityDefaults()
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
    // 旧版兼容: HWiNFO -> None，同时保留原始字符串
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

    [Fact]
    public void Save_AfterLoadingLegacyHwInfoJson_PreservesStoredLegacyString()
    {
        WithTempConfig(tempPath =>
        {
            File.WriteAllText(tempPath, @"{""version"":""3"",""precisionModeStr"":""HWiNFO""}");

            var loaded = SensorChainConfig.Load();
            loaded.Save();

            var roundTripped = JsonSerializer.Deserialize(
                File.ReadAllText(tempPath),
                ConfigJsonContext.Default.SensorChainConfig);

            Assert.NotNull(roundTripped);
            Assert.Equal("HWiNFO", roundTripped!.PrecisionModeStr);
            Assert.Equal(PrecisionMode.None, roundTripped.PrecisionMode);
        });
    }
}
