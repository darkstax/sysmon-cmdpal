// Copyright (c) 2026 SysMonCmdPal
// DockFormat 格式化工具测试 — 速度/温度/百分比/电池状态/Markdown

using Xunit;

namespace SysMonCmdPal.Tests;

public class DockFormatTests
{
    // ================================================================
    // Speed — 常规速度格式化
    // ================================================================

    [Theory]
    [InlineData(5_000_000, "5.0 MB/s")]
    [InlineData(1_000_000, "1.0 MB/s")]
    [InlineData(1_500_000, "1.5 MB/s")]
    [InlineData(999_999, "1000 KB/s")]  // <1M, 但 >=1000 → KB
    public void Speed_Megabytes(double bps, string expected)
    {
        Assert.Equal(expected, DockFormat.Speed(bps));
    }

    [Theory]
    [InlineData(999_000, "999 KB/s")]
    [InlineData(500_000, "500 KB/s")]
    [InlineData(1_000, "1 KB/s")]
    [InlineData(1_500, "2 KB/s")]   // F0 rounds up
    public void Speed_Kilobytes(double bps, string expected)
    {
        Assert.Equal(expected, DockFormat.Speed(bps));
    }

    [Theory]
    [InlineData(999, "999 B/s")]
    [InlineData(500, "500 B/s")]
    [InlineData(1, "1 B/s")]
    public void Speed_Bytes(double bps, string expected)
    {
        Assert.Equal(expected, DockFormat.Speed(bps));
    }

    [Fact]
    public void Speed_Zero_ReturnsZero()
    {
        Assert.Equal("0 B/s", DockFormat.Speed(0));
    }

    [Fact]
    public void Speed_Negative_ReturnsZero()
    {
        // 负值匹配 _ => "0 B/s" 分支
        Assert.Equal("0 B/s", DockFormat.Speed(-1));
    }

    // ================================================================
    // CompactSpeed — 紧凑速度（单字母单位）
    // ================================================================

    [Theory]
    [InlineData(5_000_000, "5.0M/s")]
    [InlineData(1_000_000, "1.0M/s")]
    [InlineData(2_500_000, "2.5M/s")]
    public void CompactSpeed_Megabytes(double bps, string expected)
    {
        Assert.Equal(expected, DockFormat.CompactSpeed(bps));
    }

    [Theory]
    [InlineData(500_000, "500K/s")]
    [InlineData(1_000, "1K/s")]
    public void CompactSpeed_Kilobytes(double bps, string expected)
    {
        Assert.Equal(expected, DockFormat.CompactSpeed(bps));
    }

    [Theory]
    [InlineData(999, "999B/s")]
    [InlineData(1, "1B/s")]
    public void CompactSpeed_Bytes(double bps, string expected)
    {
        Assert.Equal(expected, DockFormat.CompactSpeed(bps));
    }

    [Fact]
    public void CompactSpeed_Zero_ReturnsZero()
    {
        Assert.Equal("0", DockFormat.CompactSpeed(0));
    }

    // ================================================================
    // Temp — 温度格式化
    // ================================================================

    [Theory]
    [InlineData(45.0, "45°C")]
    [InlineData(0.0, "0°C")]
    [InlineData(99.9, "100°C")]   // F0 rounds
    [InlineData(-1.0, "N/A")]
    [InlineData(-50.0, "N/A")]
    public void Temp_FormatsCorrectly(double c, string expected)
    {
        Assert.Equal(expected, DockFormat.Temp(c));
    }

    // ================================================================
    // Percent — 百分比格式化
    // ================================================================

    [Theory]
    [InlineData(50.0, "50%")]
    [InlineData(0.0, "0%")]
    [InlineData(100.0, "100%")]
    [InlineData(99.4, "99%")]     // F0 rounds
    [InlineData(-1.0, "N/A")]
    [InlineData(-100.0, "N/A")]
    public void Percent_FormatsCorrectly(double p, string expected)
    {
        Assert.Equal(expected, DockFormat.Percent(p));
    }

    // ================================================================
    // BatteryStatusText — 电池状态中文映射
    // ================================================================

    [Theory]
    [InlineData("charging", "充电中")]
    [InlineData("discharging", "放电中")]
    [InlineData("full", "已充满")]
    [InlineData("no battery", "无电池")]
    public void BatteryStatusText_KnownValues(string status, string expected)
    {
        Assert.Equal(expected, DockFormat.BatteryStatusText(status));
    }

    [Fact]
    public void BatteryStatusText_Unknown_ReturnsRaw()
    {
        Assert.Equal("unknown_state", DockFormat.BatteryStatusText("unknown_state"));
    }

    [Fact]
    public void BatteryStatusText_Empty_ReturnsEmpty()
    {
        Assert.Equal("", DockFormat.BatteryStatusText(""));
    }

    // ================================================================
    // TempMd — Markdown 温度
    // ================================================================

    [Theory]
    [InlineData(45.0, "**45°C**")]
    [InlineData(0.0, "**0°C**")]
    public void TempMd_Valid_ReturnsBold(double c, string expected)
    {
        Assert.Equal(expected, DockFormat.TempMd(c));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-50.0)]
    public void TempMd_Invalid_ReturnsItalicNA(double c)
    {
        Assert.Equal("*N/A*", DockFormat.TempMd(c));
    }

    // ================================================================
    // PercentMd — Markdown 百分比
    // ================================================================

    [Theory]
    [InlineData(50.0, "**50.0%**")]
    [InlineData(100.0, "**100.0%**")]
    [InlineData(0.0, "**0.0%**")]
    public void PercentMd_Valid_ReturnsBold(double p, string expected)
    {
        Assert.Equal(expected, DockFormat.PercentMd(p));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-100.0)]
    public void PercentMd_Invalid_ReturnsItalicNA(double p)
    {
        Assert.Equal("*N/A*", DockFormat.PercentMd(p));
    }
}
