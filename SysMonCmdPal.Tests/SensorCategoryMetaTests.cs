// Copyright (c) 2026 SysMonCmdPal
// SensorCategoryMeta 测试 — 排序、中文名、图标字形、图标缓存

using Xunit;

namespace SysMonCmdPal.Tests;

public class SensorCategoryMetaTests
{
    // ================================================================
    // Order — 排序数组
    // ================================================================

    [Fact]
    public void Order_Has17Categories()
    {
        Assert.Equal(17, SensorCategoryMeta.Order.Length);
    }

    [Fact]
    public void Order_StartsWithCpuTemp()
    {
        Assert.Equal(SensorCategory.CpuTemp, SensorCategoryMeta.Order[0]);
    }

    [Fact]
    public void Order_EndsWithStorageLoad()
    {
        Assert.Equal(SensorCategory.StorageLoad, SensorCategoryMeta.Order[^1]);
    }

    [Fact]
    public void Order_CpuGroupComesFirst()
    {
        // CPU 系列（索引 0-4）应在 GPU 系列之前
        int firstGpu = Array.IndexOf(SensorCategoryMeta.Order, SensorCategory.GpuTemp);
        Assert.True(firstGpu > 4, "GPU 类别应在 CPU 类别之后");
    }

    [Fact]
    public void Order_NoDuplicates()
    {
        var unique = new HashSet<SensorCategory>(SensorCategoryMeta.Order);
        Assert.Equal(SensorCategoryMeta.Order.Length, unique.Count);
    }

    [Fact]
    public void Order_CoversAllEnumValues()
    {
        var all = Enum.GetValues<SensorCategory>();
        Assert.Equal(all.Length, SensorCategoryMeta.Order.Length);
    }

    // ================================================================
    // Name — 中文显示名
    // ================================================================

    [Theory]
    [InlineData(SensorCategory.CpuTemp, "CPU 温度")]
    [InlineData(SensorCategory.CpuLoad, "CPU 负载")]
    [InlineData(SensorCategory.CpuClock, "CPU 频率")]
    [InlineData(SensorCategory.CpuPower, "CPU 功耗")]
    [InlineData(SensorCategory.CpuVoltage, "CPU 电压")]
    [InlineData(SensorCategory.GpuTemp, "GPU 温度")]
    [InlineData(SensorCategory.GpuLoad, "GPU 负载")]
    [InlineData(SensorCategory.GpuClock, "GPU 频率")]
    [InlineData(SensorCategory.GpuPower, "GPU 功耗")]
    [InlineData(SensorCategory.GpuMemory, "GPU 显存")]
    [InlineData(SensorCategory.GpuFan, "GPU 风扇")]
    [InlineData(SensorCategory.GpuVoltage, "GPU 电压")]
    [InlineData(SensorCategory.MbTemp, "主板 温度")]
    [InlineData(SensorCategory.MbFan, "主板 风扇")]
    [InlineData(SensorCategory.MbVoltage, "主板 电压")]
    [InlineData(SensorCategory.StorageTemp, "存储 温度")]
    [InlineData(SensorCategory.StorageLoad, "存储 负载")]
    public void Name_ReturnsCorrectChineseName(SensorCategory cat, string expected)
    {
        Assert.Equal(expected, SensorCategoryMeta.Name(cat));
    }

    [Fact]
    public void Name_EveryCategory_ReturnsNonEmpty()
    {
        foreach (var cat in Enum.GetValues<SensorCategory>())
        {
            string name = SensorCategoryMeta.Name(cat);
            Assert.False(string.IsNullOrEmpty(name), $"Category {cat} has empty name");
        }
    }

    // ================================================================
    // IconGlyph — 图标字形分组
    // ================================================================

    [Theory]
    [InlineData(SensorCategory.CpuTemp)]
    [InlineData(SensorCategory.CpuLoad)]
    [InlineData(SensorCategory.CpuClock)]
    [InlineData(SensorCategory.CpuPower)]
    [InlineData(SensorCategory.CpuVoltage)]
    public void IconGlyph_CpuGroup_ReturnsCpuGlyph(SensorCategory cat)
    {
        Assert.Equal("", SensorCategoryMeta.IconGlyph(cat)); // CPU glyph
    }

    [Theory]
    [InlineData(SensorCategory.GpuTemp)]
    [InlineData(SensorCategory.GpuLoad)]
    [InlineData(SensorCategory.GpuClock)]
    [InlineData(SensorCategory.GpuPower)]
    [InlineData(SensorCategory.GpuMemory)]
    [InlineData(SensorCategory.GpuFan)]
    [InlineData(SensorCategory.GpuVoltage)]
    public void IconGlyph_GpuGroup_ReturnsGpuGlyph(SensorCategory cat)
    {
        Assert.Equal("", SensorCategoryMeta.IconGlyph(cat)); // GPU glyph
    }

    [Theory]
    [InlineData(SensorCategory.MbTemp)]
    [InlineData(SensorCategory.MbFan)]
    [InlineData(SensorCategory.MbVoltage)]
    public void IconGlyph_MbGroup_ReturnsMbGlyph(SensorCategory cat)
    {
        Assert.Equal("", SensorCategoryMeta.IconGlyph(cat)); // Motherboard glyph
    }

    [Theory]
    [InlineData(SensorCategory.StorageTemp)]
    [InlineData(SensorCategory.StorageLoad)]
    public void IconGlyph_StorageGroup_ReturnsStorageGlyph(SensorCategory cat)
    {
        Assert.Equal("", SensorCategoryMeta.IconGlyph(cat)); // Storage glyph
    }

    // ================================================================
    // GetIcon — 图标缓存
    // ================================================================

    [Fact]
    public void GetIcon_ReturnsNonNull()
    {
        var icon = SensorCategoryMeta.GetIcon(SensorCategory.CpuTemp);
        Assert.NotNull(icon);
    }

    [Fact]
    public void GetIcon_SameCategory_ReturnsSameInstance()
    {
        var icon1 = SensorCategoryMeta.GetIcon(SensorCategory.CpuTemp);
        var icon2 = SensorCategoryMeta.GetIcon(SensorCategory.CpuTemp);
        Assert.Same(icon1, icon2); // 缓存命中
    }

    [Fact]
    public void GetIcon_DifferentCategories_ReturnDifferentInstances()
    {
        var cpu = SensorCategoryMeta.GetIcon(SensorCategory.CpuTemp);
        var gpu = SensorCategoryMeta.GetIcon(SensorCategory.GpuTemp);
        Assert.NotSame(cpu, gpu);
    }

    [Fact]
    public void GetIcon_AllCategoriesSucceed()
    {
        foreach (var cat in Enum.GetValues<SensorCategory>())
        {
            var icon = SensorCategoryMeta.GetIcon(cat);
            Assert.NotNull(icon);
        }
    }
}
