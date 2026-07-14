// Copyright (c) 2026 SysMonCmdPal
// Dynamic sensor Dock band formatting tests.

using Xunit;

namespace SysMonCmdPal.Tests;

public class SensorDockBandTests
{
    [Theory]
    [InlineData(63.25, "°C", "63.2°C")]
    [InlineData(42.25, "%", "42.2%")]
    [InlineData(3500.0, "MHz", "3.50 GHz")]
    [InlineData(950.0, "MHz", "950 MHz")]
    [InlineData(125.25, "W", "125.2 W")]
    [InlineData(1.125, "V", "1.125 V")]
    [InlineData(2048.0, "MB", "2.0 GB")]
    [InlineData(512.0, "MB", "512 MB")]
    [InlineData(1234.0, "RPM", "1234 RPM")]
    [InlineData(12.345, "", "12.35")]
    [InlineData(12.345, "A", "12.35 A")]
    public void FormatValue_ReturnsPlainDockText(double value, string unit, string expected)
    {
        Assert.Equal(expected, SensorDockBand.FormatValue(value, unit));
    }
}
