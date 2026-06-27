using System.Reflection;
using System.Text.Json;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SensorChainConfigTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempPath;

    public SensorChainConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SysMonTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempPath = Path.Combine(_tempDir, "settings.json");

        // 重定向 ConfigPath 到临时文件
        var field = typeof(SensorChainConfig).GetField("ConfigPath",
            BindingFlags.Public | BindingFlags.Static)!;
        field.SetValue(null, _tempPath);
    }

    public void Dispose()
    {
        // 恢复原始路径
        var field = typeof(SensorChainConfig).GetField("ConfigPath",
            BindingFlags.Public | BindingFlags.Static)!;
        field.SetValue(null, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysMonCmdPal", "settings.json"));

        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ── 默认值 (v4) ──

    [Fact]
    public void DefaultValues_VersionIs4()
    {
        var cfg = new SensorChainConfig();
        Assert.Equal("4", cfg.Version);
    }

    [Fact]
    public void DefaultValues_PrecisionModeIsHWiNFO()
    {
        var cfg = new SensorChainConfig();
        Assert.Equal(PrecisionMode.HWiNFO, cfg.PrecisionMode);
        Assert.Equal("HWiNFO", cfg.PrecisionModeStr);
    }

    [Fact]
    public void DefaultValues_GpuModeIsAuto()
    {
        var cfg = new SensorChainConfig();
        Assert.Equal(GpuMode.Auto, cfg.GpuMode);
        Assert.Equal("Auto", cfg.GpuModeStr);
    }

    [Fact]
    public void DefaultCpuChain_HasFourEntries()
    {
        var cfg = new SensorChainConfig();
        Assert.Equal(4, cfg.CpuChain.Count);
    }

    // ── PrecisionMode 解析 ──

    [Theory]
    [InlineData("HWiNFO", PrecisionMode.HWiNFO)]
    [InlineData("None", PrecisionMode.None)]
    [InlineData("hwinfo", PrecisionMode.HWiNFO)]
    public void PrecisionMode_ParsesCorrectly(string str, PrecisionMode expected)
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = str };
        Assert.Equal(expected, cfg.PrecisionMode);
    }

    [Fact]
    public void PrecisionMode_BrokerIsValid()
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = "Broker" };
        Assert.Equal(PrecisionMode.Broker, cfg.PrecisionMode);
    }

    [Fact]
    public void PrecisionMode_InvalidString_FallsBackToHWiNFO()
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = "InvalidValue" };
        Assert.Equal(PrecisionMode.HWiNFO, cfg.PrecisionMode);
    }

    [Fact]
    public void PrecisionMode_Setter_UpdatesString()
    {
        var cfg = new SensorChainConfig();
        cfg.PrecisionMode = PrecisionMode.None;
        Assert.Equal("None", cfg.PrecisionModeStr);
    }

    // ── HighPrecision 兼容性 ──

    [Fact]
    public void HighPrecision_TrueWhenHWiNFO()
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = "HWiNFO" };
        Assert.True(cfg.HighPrecision);
    }

    [Fact]
    public void HighPrecision_FalseWhenNone()
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = "None" };
        Assert.False(cfg.HighPrecision);
    }

    // ── DefaultCpuChain ──

    [Fact]
    public void DefaultCpuChain_HWiNFOMode()
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = "HWiNFO" };
        Assert.Equal(new[] { "HWiNFO", "ADL", "ThermalZone", "LHM" }, cfg.DefaultCpuChain);
    }

    [Fact]
    public void DefaultCpuChain_NoneMode()
    {
        var cfg = new SensorChainConfig { PrecisionModeStr = "None" };
        Assert.Equal(new[] { "ThermalZone", "HWiNFO" }, cfg.DefaultCpuChain);
    }

    // ── GpuMode 解析 ──

    [Theory]
    [InlineData("Auto", GpuMode.Auto)]
    [InlineData("DedicatedOnly", GpuMode.DedicatedOnly)]
    [InlineData("All", GpuMode.All)]
    public void GpuMode_ParsesCorrectly(string str, GpuMode expected)
    {
        var cfg = new SensorChainConfig { GpuModeStr = str };
        Assert.Equal(expected, cfg.GpuMode);
    }

    [Fact]
    public void GpuMode_InvalidString_FallsBackToAuto()
    {
        var cfg = new SensorChainConfig { GpuModeStr = "Something" };
        Assert.Equal(GpuMode.Auto, cfg.GpuMode);
    }

    // ── JSON 序列化 / 反序列化 ──

    [Fact]
    public void Serialize_ProducesCamelCase()
    {
        var cfg = new SensorChainConfig();
        var json = JsonSerializer.Serialize(cfg, GetOptions());
        Assert.Contains("\"precisionModeStr\"", json);
        Assert.Contains("\"cpuChain\"", json);
        Assert.Contains("\"gpuChain\"", json);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var original = new SensorChainConfig
        {
            PrecisionModeStr = "HWiNFO",
            CpuChain = ["HWiNFO", "ADL", "ThermalZone"],
            GpuChain = ["LHM", "HWiNFO"],
            GpuModeStr = "All",
        };
        var json = JsonSerializer.Serialize(original, GetOptions());
        var deserialized = JsonSerializer.Deserialize<SensorChainConfig>(json, GetOptions())!;

        Assert.Equal(original.Version, deserialized.Version);
        Assert.Equal(original.PrecisionModeStr, deserialized.PrecisionModeStr);
        Assert.Equal(original.CpuChain, deserialized.CpuChain);
        Assert.Equal(original.GpuChain, deserialized.GpuChain);
        Assert.Equal(original.GpuModeStr, deserialized.GpuModeStr);
    }

    // ── 版本迁移 ──

    [Fact]
    public void Load_V1_HighPrecisionTrue_MigratesToHWiNFO()
    {
        File.WriteAllText(_tempPath, """{"highPrecision":true}""");

        var cfg = SensorChainConfig.Load();

        Assert.Equal("4", cfg.Version);
        Assert.Equal(PrecisionMode.HWiNFO, cfg.PrecisionMode);
        Assert.Equal(new[] { "HWiNFO", "ADL", "ThermalZone", "LHM" }, cfg.CpuChain);
    }

    [Fact]
    public void Load_V1_HighPrecisionFalse_MigratesToNone()
    {
        File.WriteAllText(_tempPath, """{"highPrecision":false}""");

        var cfg = SensorChainConfig.Load();

        Assert.Equal("4", cfg.Version);
        Assert.Equal(PrecisionMode.None, cfg.PrecisionMode);
        Assert.Equal(new[] { "ThermalZone", "HWiNFO" }, cfg.CpuChain);
    }

    [Fact]
    public void Load_V3_BrokerPrecisionMode_KeepsBroker()
    {
        File.WriteAllText(_tempPath, """
            {"version":"3","precisionModeStr":"Broker","cpuChain":["Broker","ThermalZone"],"gpuChain":["Broker","ThermalZone"]}
            """);

        var cfg = SensorChainConfig.Load();

        Assert.Equal("4", cfg.Version);
        Assert.Equal("Broker", cfg.PrecisionModeStr);
        // Chain 中的 Broker 条目被移除（Broker 通过 COM 推送，不在链中）
        Assert.DoesNotContain("Broker", cfg.CpuChain);
        Assert.DoesNotContain("Broker", cfg.GpuChain);
    }

    [Fact]
    public void Load_V4_DirectDeserialize()
    {
        File.WriteAllText(_tempPath, """
            {"version":"4","precisionModeStr":"HWiNFO","cpuChain":["HWiNFO","ADL"],"gpuChain":["LHM"],"gpuModeStr":"DedicatedOnly"}
            """);

        var cfg = SensorChainConfig.Load();

        Assert.Equal("4", cfg.Version);
        Assert.Equal(PrecisionMode.HWiNFO, cfg.PrecisionMode);
        Assert.Equal(2, cfg.CpuChain.Count);
        Assert.Equal("DedicatedOnly", cfg.GpuModeStr);
    }

    // ── Load / Save round-trip ──

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var original = new SensorChainConfig
        {
            PrecisionModeStr = "HWiNFO",
            CpuChain = ["HWiNFO", "ADL", "ThermalZone"],
            GpuChain = ["LHM", "ADL"],
            GpuModeStr = "All",
        };
        original.Save();

        var loaded = SensorChainConfig.Load();

        Assert.Equal(original.PrecisionModeStr, loaded.PrecisionModeStr);
        Assert.Equal(original.CpuChain, loaded.CpuChain);
        Assert.Equal(original.GpuChain, loaded.GpuChain);
        Assert.Equal(original.GpuModeStr, loaded.GpuModeStr);
    }

    [Fact]
    public void Load_FileNotExist_ReturnsDefault()
    {
        // 临时路径指向不存在的文件
        var field = typeof(SensorChainConfig).GetField("ConfigPath",
            BindingFlags.Public | BindingFlags.Static)!;
        field.SetValue(null, Path.Combine(_tempDir, "nonexistent.json"));

        var cfg = SensorChainConfig.Load();

        Assert.Equal(PrecisionMode.HWiNFO, cfg.PrecisionMode);
        Assert.Equal("4", cfg.Version);
    }

    // ── helpers ──

    private static JsonSerializerOptions GetOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
    }
}
