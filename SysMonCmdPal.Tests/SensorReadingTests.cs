using Xunit;

namespace SysMonCmdPal.Tests;

public class SensorReadingTests
{
    // ── FormatValue() — 不同值域的格式化精度 ──

    [Theory]
    [InlineData(1500.0, "°C", "1500 °C")]
    [InlineData(1000.0, "MHz", "1000 MHz")]
    [InlineData(3456.78, "RPM", "3457 RPM")]
    public void FormatValue_1000OrAbove_NoDecimal(double value, string unit, string expected)
    {
        var r = new SensorReading { Value = value, Unit = unit };
        Assert.Equal(expected, r.FormatValue());
    }

    [Theory]
    [InlineData(100.0, "°C", "100.0 °C")]
    [InlineData(250.5, "MHz", "250.5 MHz")]
    [InlineData(999.9, "W", "999.9 W")]
    public void FormatValue_100To999_OneDecimal(double value, string unit, string expected)
    {
        var r = new SensorReading { Value = value, Unit = unit };
        Assert.Equal(expected, r.FormatValue());
    }

    [Theory]
    [InlineData(1.0, "°C", "1.00 °C")]
    [InlineData(42.5, "%", "42.50 %")]
    [InlineData(99.99, "V", "99.99 V")]
    public void FormatValue_1To100_TwoDecimals(double value, string unit, string expected)
    {
        var r = new SensorReading { Value = value, Unit = unit };
        Assert.Equal(expected, r.FormatValue());
    }

    [Theory]
    [InlineData(0.0, "°C", "0.000 °C")]
    [InlineData(0.5, "V", "0.500 V")]
    [InlineData(0.999, "W", "0.999 W")]
    public void FormatValue_Below1_ThreeDecimals(double value, string unit, string expected)
    {
        var r = new SensorReading { Value = value, Unit = unit };
        Assert.Equal(expected, r.FormatValue());
    }

    // ── IsTemperature ──

    [Theory]
    [InlineData(SensorCategory.CpuTemp, true)]
    [InlineData(SensorCategory.GpuTemp, true)]
    [InlineData(SensorCategory.MbTemp, true)]
    [InlineData(SensorCategory.StorageTemp, true)]
    [InlineData(SensorCategory.CpuLoad, false)]
    [InlineData(SensorCategory.GpuClock, false)]
    [InlineData(SensorCategory.MbFan, false)]
    public void IsTemperature_ReturnsCorrectly(SensorCategory category, bool expected)
    {
        var r = new SensorReading { Category = category };
        Assert.Equal(expected, r.IsTemperature);
    }

    // ── IsLoad ──

    [Theory]
    [InlineData(SensorCategory.CpuLoad, true)]
    [InlineData(SensorCategory.GpuLoad, true)]
    [InlineData(SensorCategory.CpuTemp, false)]
    [InlineData(SensorCategory.GpuClock, false)]
    [InlineData(SensorCategory.StorageLoad, false)]
    public void IsLoad_ReturnsCorrectly(SensorCategory category, bool expected)
    {
        var r = new SensorReading { Category = category };
        Assert.Equal(expected, r.IsLoad);
    }
}
