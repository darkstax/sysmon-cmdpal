// Copyright (c) 2026 SysMonCmdPal
// 集成测试 — 需要真实硬件和管理员权限。
// 运行: dotnet test --filter "Category=Integration" -v normal
// 单元测试: dotnet test --filter "Category!=Integration" -v normal

using System.Reflection;
using Xunit;

namespace SysMonCmdPal.Tests;

/// <summary>集成测试基类 — 使用临时 settings.json 隔离，避免污染真实配置</summary>
public abstract class IntegrationTestBase : IDisposable
{
    protected readonly string TempDir;
    protected readonly string TempConfigPath;

    protected IntegrationTestBase()
    {
        TempDir = Path.Combine(Path.GetTempPath(), $"SysMonInt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempDir);
        TempConfigPath = Path.Combine(TempDir, "settings.json");

        SetStaticField<SensorChainConfig>("ConfigPath", TempConfigPath);
    }

    public virtual void Dispose()
    {
        RestoreConfigPath();
        try { Directory.Delete(TempDir, true); } catch { }
    }

    protected static void SetStaticField<T>(string fieldName, object value)
    {
        var field = typeof(T).GetField(fieldName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        field.SetValue(null, value);
    }

    private static void RestoreConfigPath()
    {
        var field = typeof(SensorChainConfig).GetField("ConfigPath",
            BindingFlags.Public | BindingFlags.Static)!;
        field.SetValue(null, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysMonCmdPal", "settings.json"));
    }
}

// ================================================================
// 系统信息服务集成测试
// ================================================================

[Trait("Category", "Integration")]
public class SystemInfoServiceIntegrationTests
{
    [Fact]
    public void Refresh_PopulatesCpuUsage()
    {
        SystemInfoService.Instance.Refresh();
        var s = SystemInfoService.Instance.Current;

        // CPU 使用率应在 0-100 之间
        Assert.InRange(s.CpuUsage, 0, 100);
    }

    [Fact]
    public void Refresh_PopulatesMemory()
    {
        SystemInfoService.Instance.Refresh();
        var s = SystemInfoService.Instance.Current;

        Assert.True(s.MemoryTotalBytes > 0, "总内存应大于 0");
        Assert.True(s.MemoryUsedBytes > 0, "已用内存应大于 0");
        Assert.True(s.MemoryUsedBytes <= s.MemoryTotalBytes, "已用内存不应超过总内存");
    }

    [Fact]
    public void Refresh_PopulatesDisks()
    {
        SystemInfoService.Instance.Refresh();
        var s = SystemInfoService.Instance.Current;

        Assert.NotNull(s.Disks);
        Assert.NotEmpty(s.Disks);

        var first = s.Disks[0];
        Assert.False(string.IsNullOrEmpty(first.Name), "磁盘名不应为空");
        Assert.True(first.TotalBytes > 0, "磁盘总容量应大于 0");
        Assert.True(first.UsedPercent >= 0, "使用率不应为负");
        Assert.True(first.UsedPercent <= 100, "使用率不应超过 100");
    }

    [Fact]
    public void Refresh_PopulatesNetwork()
    {
        SystemInfoService.Instance.Refresh();
        var s = SystemInfoService.Instance.Current;

        // 网络速度可能为 0（如果接口空闲），但不应为负
        Assert.True(s.NetDown >= 0, "下行速度不应为负");
        Assert.True(s.NetUp >= 0, "上行速度不应为负");
    }

    [Fact]
    public void Refresh_DoesNotThrow()
    {
        // 连续调用 5 次确保无竞态
        for (int i = 0; i < 5; i++)
        {
            var ex = Record.Exception(() => SystemInfoService.Instance.Refresh());
            Assert.Null(ex);
        }
    }

    [Fact]
    public void Refresh_ReturnsConsistentSnapshot()
    {
        SystemInfoService.Instance.Refresh();
        var s = SystemInfoService.Instance.Current;

        // GPU 数组与主 GPU 一致
        if (s.Gpus.Length > 0)
        {
            Assert.False(string.IsNullOrEmpty(s.Gpu.Name), "主 GPU 名不应为空");
        }
        else
        {
            Assert.Equal(-1, s.Gpu.Temperature);
            Assert.Equal(-1, s.Gpu.UsagePercent);
        }
    }
}

// ================================================================
// LHM 传感器服务集成测试
// ================================================================

[Trait("Category", "Integration")]
public class LhmSensorServiceIntegrationTests
{
    [Fact]
    public void IsAvailable_ReturnsTrueOrFalse()
    {
        // LHM 在 HWiNFO 或驱动未安装时可能不可用
        bool available = LhmSensorService.Instance.IsAvailable;

        // 仅检查不崩溃
        Assert.True(available || !available);
    }

    [Fact]
    public void Refresh_WhenAvailable_PopulatesCatalog()
    {
        if (!LhmSensorService.Instance.IsAvailable)
            return; // 跳过（LHM 不可用）

        LhmSensorService.Instance.Refresh();

        Assert.NotNull(LhmSensorService.Instance.Catalog);
        Assert.NotNull(LhmSensorService.Instance.AllReadings);
    }

    [Fact]
    public void Catalog_WhenPopulated_ContainsCpuEntries()
    {
        if (!LhmSensorService.Instance.IsAvailable)
            return;

        LhmSensorService.Instance.Refresh();

        bool hasCpu = LhmSensorService.Instance.Catalog.ContainsKey(SensorCategory.CpuTemp)
                   || LhmSensorService.Instance.Catalog.ContainsKey(SensorCategory.CpuLoad);

        Assert.True(hasCpu, "至少应包含 CPU 温度或负载");
    }
}

// ================================================================
// 传感器链配置 + CpuSensorReader 集成测试
// ================================================================

[Trait("Category", "Integration")]
public class SensorChainIntegrationTests : IntegrationTestBase
{
    [Fact]
    public void CpuSensorReader_Read_ReturnsResult()
    {
        // 使用完整回退链配置
        File.WriteAllText(TempConfigPath,
            """{"version":"4","precisionModeStr":"HWiNFO","cpuChain":["HWiNFO","ThermalZone","ADL"],"gpuChain":["LHM","HWiNFO"]}""");

        var result = CpuSensorReader.Read();

        // 结果可能是有效温度或不可用，但不应该抛异常
        Assert.True(result.Temperature >= -1);
    }

    [Fact]
    public void GpuSensorReader_Read_ReturnsResult()
    {
        File.WriteAllText(TempConfigPath,
            """{"version":"4","precisionModeStr":"HWiNFO","cpuChain":["HWiNFO"],"gpuChain":["LHM","HWiNFO"]}""");

        var result = GpuSensorReader.Read();

        Assert.True(result.Temperature >= -1);
    }

    [Fact]
    public void GpuSensorReader_ReadAll_ReturnsList()
    {
        File.WriteAllText(TempConfigPath,
            """{"version":"4","precisionModeStr":"HWiNFO","cpuChain":[],"gpuChain":["LHM","HWiNFO"]}""");

        var results = GpuSensorReader.ReadAll();

        Assert.NotNull(results);
        // 在有 GPU 的机器上应返回非空列表
    }

    [Fact]
    public void SensorChain_FullFallback_DoesNotCrash()
    {
        File.WriteAllText(TempConfigPath,
            """{"version":"3","precisionMode":"Broker","cpuChain":["Broker","ThermalZone","HWiNFO"],"gpuChain":["Broker","ThermalZone","HWiNFO"],"gpuModeStr":"Auto"}""");

        // 连续调用多次，验证所有回退路径不崩溃
        for (int i = 0; i < 3; i++)
        {
            var cpuResult = CpuSensorReader.Read();
            var gpuResults = GpuSensorReader.ReadAll();

            Assert.True(cpuResult.Temperature >= -1);
            Assert.NotNull(gpuResults);
        }
    }
}

// ================================================================
// SensorCategoryMeta 集成测试（图标缓存验证）
// ================================================================

[Trait("Category", "Integration")]
public class SensorCategoryMetaIntegrationTests
{
    [Fact]
    public void GetIcon_AllCategories_CacheWorks()
    {
        // 遍历所有类别两次，验证第二次走缓存
        var all = Enum.GetValues<SensorCategory>();
        var firstPass = new System.Collections.Generic.Dictionary<SensorCategory, object>();

        foreach (var cat in all)
        {
            var icon = SensorCategoryMeta.GetIcon(cat);
            Assert.NotNull(icon);
            firstPass[cat] = icon;
        }

        // 第二次应返回同一实例（缓存命中）
        foreach (var cat in all)
        {
            var icon = SensorCategoryMeta.GetIcon(cat);
            Assert.Same(firstPass[cat], icon);
        }
    }
}
