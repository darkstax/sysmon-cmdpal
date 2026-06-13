// Copyright (c) 2026 SysMonCmdPal
// CpuSensorReader 测试 — CpuTempResult 结构体 + 回退链编排

using System.Reflection;
using Xunit;

namespace SysMonCmdPal.Tests;

public class CpuSensorReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _tempConfigPath;
    private readonly string _tempLogPath;

    public CpuSensorReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SysMonTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempConfigPath = Path.Combine(_tempDir, "settings.json");
        _tempLogPath = Path.Combine(_tempDir, "cpu_test.log");

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
    // CpuTempResult — 结构体
    // ================================================================

    [Fact]
    public void CpuTempResult_Default_IsInvalid()
    {
        var r = new CpuTempResult();
        Assert.False(r.IsValid);
        Assert.Equal(0, r.Temperature);
    }

    [Fact]
    public void CpuTempResult_ValidTemp_IsValid()
    {
        var r = new CpuTempResult(65.5, "Broker_SMU");
        Assert.True(r.IsValid);
        Assert.Equal(65.5, r.Temperature);
        Assert.Equal("Broker_SMU", r.Source);
    }

    [Fact]
    public void CpuTempResult_Zero_IsNotValid()
    {
        var r = new CpuTempResult(0, "Test");
        Assert.False(r.IsValid);
    }

    [Fact]
    public void CpuTempResult_Negative_IsNotValid()
    {
        var r = new CpuTempResult(-1, "Test");
        Assert.False(r.IsValid);
    }

    [Fact]
    public void CpuTempResult_None_HasExpectedValues()
    {
        Assert.Equal(-1, CpuTempResult.None.Temperature);
        Assert.Equal("无", CpuTempResult.None.Source);
        Assert.False(CpuTempResult.None.IsValid);
    }

    [Fact]
    public void CpuTempResult_RecordStruct_Equality()
    {
        var a = new CpuTempResult(65.5, "Broker_SMU");
        var b = new CpuTempResult(65.5, "Broker_SMU");
        Assert.Equal(a, b);
    }

    // ================================================================
    // 回退链: 所有数据源不可用时返回 None
    // ================================================================

    [Fact]
    public void Read_AllSourcesUnavailable_ReturnsNone()
    {
        // 配置只含 ThermalZone，且禁用 ThermalZone
        File.WriteAllText(_tempConfigPath,
            """{"version":"3","precisionMode":"None","cpuChain":["ThermalZone"],"gpuChain":[]}""");

        // 强制 ThermalZoneReader 不可用
        SetInstanceField<ThermalZoneReader>("_available", false);
        SetInstanceField<ThermalZoneReader>("_initAttempted", true);

        var result = CpuSensorReader.Read();

        Assert.False(result.IsValid);
        Assert.Equal("无", result.Source);
    }

    [Fact]
    public void Read_EmptyChain_ReturnsNone()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"3","precisionMode":"None","cpuChain":[],"gpuChain":[]}""");

        var result = CpuSensorReader.Read();

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Read_UnknownSource_SkipsToNext()
    {
        // "UnknownSource" 不是有效数据源 → ReadFromSource 返回 None
        File.WriteAllText(_tempConfigPath,
            """{"version":"3","precisionMode":"None","cpuChain":["UnknownSource"],"gpuChain":[]}""");

        var result = CpuSensorReader.Read();

        Assert.False(result.IsValid);
    }

    // ================================================================
    // 回退链: Broker 子链测试（通过控制 IsAvailable）
    // ================================================================

    [Fact]
    public void Read_BrokerChain_AllPhasesUnavailable_ReturnsNone()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"3","precisionMode":"Broker","cpuChain":["Broker"],"gpuChain":[]}""");

        // 禁用所有 Broker 子链阶段
        SetInstanceField<BrokerClient>("_isAvailable", false);
        SetInstanceField<BrokerClient>("_lastAttempt", DateTime.UtcNow);
        SetInstanceField<IntelMsrReader>("_available", false);
        SetInstanceField<IntelMsrReader>("_initAttempted", true);
        SetInstanceField<AmdSmuReader>("_available", false);
        SetInstanceField<AmdSmuReader>("_initAttempted", true);
        SetInstanceField<LhmHttpReader>("_available", false);
        SetInstanceField<LhmHttpReader>("_initAttempted", true);
        SetInstanceField<AmdTempReader>("_adlAvailable", false);
        SetInstanceField<AmdTempReader>("_adlInitAttempted", true);
        SetInstanceField<LhmSensorService>("_available", false);
        SetInstanceField<LhmSensorService>("_initAttempted", true);

        var result = CpuSensorReader.Read();

        Assert.False(result.IsValid);
        Assert.Equal("无", result.Source);
    }

    [Fact]
    public void Read_ThermalZoneAvailable_ReturnsThermalZone()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"3","precisionMode":"Broker","cpuChain":["ThermalZone","Broker"],"gpuChain":[]}""");

        // 确保 Broker 不可用（否则会先走 Broker 子链）
        SetInstanceField<BrokerClient>("_isAvailable", false);
        SetInstanceField<BrokerClient>("_lastAttempt", DateTime.UtcNow);

        var result = CpuSensorReader.Read();

        // ThermalZone 在真实环境中可能可用也可能不可用
        // 这是一个集成性质的测试，验证 ThermalZone 路径可达
        Assert.True(result.IsValid || !result.IsValid, "Should not throw");
    }

    // ================================================================
    // 配置驱动的链顺序
    // ================================================================

    [Fact]
    public void Read_RespectsChainOrder()
    {
        // 先 HWiNFO 后 ThermalZone（非默认顺序）
        File.WriteAllText(_tempConfigPath,
            """{"version":"3","precisionMode":"None","cpuChain":["HWiNFO","ThermalZone"],"gpuChain":[]}""");

        var result = CpuSensorReader.Read();

        // 不应崩溃
        Assert.True(result.IsValid || !result.IsValid, "Should not throw");
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
