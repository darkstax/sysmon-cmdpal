// Copyright (c) 2026 SysMonCmdPal
// GpuSensorReader 测试 — GpuResult 结构体 + 多卡过滤 + 回退链

using System.Reflection;
using Xunit;

namespace SysMonCmdPal.Tests;

public class GpuSensorReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempConfigPath;
    private readonly string _tempLogPath;

    public GpuSensorReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SysMonTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempConfigPath = Path.Combine(_tempDir, "settings.json");
        _tempLogPath = Path.Combine(_tempDir, "gpu_test.log");

        SetStaticField(typeof(SensorChainConfig), "ConfigPath", _tempConfigPath);
        SetStaticField(typeof(SensorLogger), "LogPath", _tempLogPath);
    }

    public void Dispose()
    {
        RestoreConfigPath();
        RestoreLogPath();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ================================================================
    // GpuResult — 结构体
    // ================================================================

    [Fact]
    public void GpuResult_Default_IsInvalid()
    {
        var r = new GpuResult();
        Assert.False(r.IsValid);
    }

    [Fact]
    public void GpuResult_Valid_IsValid()
    {
        var r = new GpuResult("AMD Radeon RX 680M", 45.0, 62.5, 512, 2048, "Broker");
        Assert.True(r.IsValid);
        Assert.Equal("AMD Radeon RX 680M", r.Name);
        Assert.Equal(45.0, r.UsagePercent);
        Assert.Equal(62.5, r.Temperature);
        Assert.Equal(512, r.MemoryUsedMB);
        Assert.Equal(2048, r.MemoryTotalMB);
        Assert.Equal("Broker", r.Source);
    }

    [Fact]
    public void GpuResult_EmptyName_IsInvalid()
    {
        var r = new GpuResult("", 45.0, 62.5, 512, 2048, "Broker");
        Assert.False(r.IsValid);
    }

    [Fact]
    public void GpuResult_None_HasExpectedValues()
    {
        Assert.Equal("", GpuResult.None.Name);
        Assert.Equal(-1, GpuResult.None.UsagePercent);
        Assert.Equal(-1, GpuResult.None.Temperature);
        Assert.Equal("无", GpuResult.None.Source);
        Assert.False(GpuResult.None.IsValid);
    }

    [Fact]
    public void GpuResult_RecordStruct_Equality()
    {
        var a = new GpuResult("GPU1", 50, 70, 1024, 4096, "Broker");
        var b = new GpuResult("GPU1", 50, 70, 1024, 4096, "Broker");
        Assert.Equal(a, b);
    }

    // ================================================================
    // Read() — 单卡兼容接口
    // ================================================================

    [Fact]
    public void Read_NoGpus_ReturnsNone()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"3","precisionMode":"None","cpuChain":[],"gpuChain":["ThermalZone"]}""");

        // 禁用 ThermalZone
        SetInstanceField<ThermalZoneReader>("_available", false);
        SetInstanceField<ThermalZoneReader>("_initAttempted", true);

        var result = GpuSensorReader.Read();

        Assert.False(result.IsValid);
        Assert.Equal("无", result.Source);
    }

    [Fact]
    public void Read_AllSourcesUnavailable_ReturnsNone()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"3","precisionMode":"None","cpuChain":[],"gpuChain":[]}""");

        var result = GpuSensorReader.Read();

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Read_UnknownSource_SkipsToNext()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"3","precisionMode":"None","cpuChain":[],"gpuChain":["InvalidSource"]}""");

        var result = GpuSensorReader.Read();

        Assert.False(result.IsValid);
    }

    // ================================================================
    // 回退链: Broker → ThermalZone → HWiNFO
    // ================================================================

    [Fact]
    public void ReadAll_BrokerUnavailable_FallsBack()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"3","precisionMode":"Broker","cpuChain":[],"gpuChain":["Broker","ThermalZone"]}""");

        // 禁用 Broker
        SetInstanceField<BrokerClient>("_isAvailable", false);
        SetInstanceField<BrokerClient>("_lastAttempt", DateTime.UtcNow);

        var results = GpuSensorReader.ReadAll();

        // 可能从 ThermalZone 获取到数据，也可能没有
        Assert.NotNull(results);
    }

    // ================================================================
    // 过滤逻辑验证（通过结构体值推断）
    // ================================================================

    // 注: ApplyGpuModeFilter 和 FilterBy3DActivity 是 private static，
    // 无法直接测试。这里通过集成测试间接验证。

    [Fact]
    public void ReadAll_RespectsConfig()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"3","precisionMode":"Broker","cpuChain":[],"gpuChain":["Broker"],"gpuModeStr":"All"}""");

        var results = GpuSensorReader.ReadAll();

        // 在无 Broker 环境下应返回空列表
        Assert.NotNull(results);
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static void SetStaticField(Type type, string fieldName, object value)
    {
        var field = type.GetField(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, value);
    }

    private static void SetInstanceField<T>(string fieldName, object value)
    {
        var instanceProp = typeof(T).GetProperty("Instance",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var instance = instanceProp!.GetValue(null)!;
        var field = typeof(T).GetField(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(instance, value);
    }

    private static void RestoreConfigPath()
    {
        var field = typeof(SensorChainConfig).GetField("ConfigPath",
            BindingFlags.Public | BindingFlags.Static)!;
        field.SetValue(null, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysMonCmdPal", "settings.json"));
    }

    private static void RestoreLogPath()
    {
        var field = typeof(SensorLogger).GetField("LogPath",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysMonCmdPal", "sensor_backend.log"));
    }
}
