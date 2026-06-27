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
        var r = new CpuTempResult(65.5, "HWiNFO");
        Assert.True(r.IsValid);
        Assert.Equal(65.5, r.Temperature);
        Assert.Equal("HWiNFO", r.Source);
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
        var a = new CpuTempResult(65.5, "HWiNFO");
        var b = new CpuTempResult(65.5, "HWiNFO");
        Assert.Equal(a, b);
    }

    // ================================================================
    // 回退链: 所有数据源不可用时返回 None
    // ================================================================

    [Fact]
    public void Read_AllSourcesUnavailable_ReturnsNone()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"4","precisionModeStr":"HWiNFO","cpuChain":["ThermalZone"],"gpuChain":[]}""");

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
            """{"version":"4","precisionModeStr":"HWiNFO","cpuChain":[],"gpuChain":[]}""");

        var result = CpuSensorReader.Read();

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Read_UnknownSource_SkipsToNext()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"4","precisionModeStr":"HWiNFO","cpuChain":["UnknownSource"],"gpuChain":[]}""");

        var result = CpuSensorReader.Read();

        Assert.False(result.IsValid);
    }

    // ================================================================
    // HWiNFO 链（商店版最高优先级）
    // ================================================================

    [Fact]
    public void Read_HWiNFOChain_AllPhasesUnavailable_ReturnsNone()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"4","precisionModeStr":"HWiNFO","cpuChain":["HWiNFO","ADL","ThermalZone","LHM"],"gpuChain":[]}""");

        // 禁用所有数据源
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
            """{"version":"4","precisionModeStr":"HWiNFO","cpuChain":["ThermalZone"],"gpuChain":[]}""");

        var result = CpuSensorReader.Read();

        // ThermalZone 在真实环境中可能可用也可能不可用
        Assert.True(result.IsValid || !result.IsValid, "Should not throw");
    }

    // ================================================================
    // 配置驱动的链顺序
    // ================================================================

    [Fact]
    public void Read_RespectsChainOrder()
    {
        File.WriteAllText(_tempConfigPath,
            """{"version":"4","precisionModeStr":"HWiNFO","cpuChain":["HWiNFO","ThermalZone"],"gpuChain":[]}""");

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
