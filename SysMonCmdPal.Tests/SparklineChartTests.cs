using Xunit;

namespace SysMonCmdPal.Tests;

public class SparklineChartTests
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    // ── Push / Count ──

    [Fact]
    public void Push_IncrementsCount()
    {
        var chart = new SparklineChart(maxPoints: 10);
        Assert.Equal(0, chart.Count);

        chart.Push(50);
        Assert.Equal(1, chart.Count);

        chart.Push(60);
        Assert.Equal(2, chart.Count);
    }

    // ── 值域 clamp 0~100 ──

    [Fact]
    public void Push_ClampsAbove100()
    {
        var chart = new SparklineChart(maxPoints: 10);
        chart.Push(150);
        // Count 增加，但内部值被 clamp 到 100
        Assert.Equal(1, chart.Count);
    }

    [Fact]
    public void Push_ClampsBelow0()
    {
        var chart = new SparklineChart(maxPoints: 10);
        chart.Push(-50);
        Assert.Equal(1, chart.Count);
    }

    // ── maxPoints 限制 ──

    [Fact]
    public void Push_ExceedsMaxPoints_DropsOldest()
    {
        var chart = new SparklineChart(maxPoints: 5);

        for (int i = 0; i < 10; i++)
            chart.Push(i);

        Assert.Equal(5, chart.Count);
    }

    [Fact]
    public void Push_MaxPoints1_KeepsOnlyLast()
    {
        var chart = new SparklineChart(maxPoints: 1);
        chart.Push(10);
        chart.Push(20);
        chart.Push(30);
        Assert.Equal(1, chart.Count);
    }

    // ── ToPng — PNG 签名 ──

    [Fact]
    public void ToPng_WithData_ReturnsValidPngSignature()
    {
        var chart = new SparklineChart(maxPoints: 10, width: 100, height: 50);
        chart.Push(10);
        chart.Push(50);
        chart.Push(90);

        byte[] png = chart.ToPng();

        Assert.True(png.Length > 8);
        Assert.Equal(PngSignature, png[..8]);
    }

    // ── ToPng — 空图返回 1×1 PNG ──

    [Fact]
    public void ToPng_EmptyChart_Returns1x1Png()
    {
        var chart = new SparklineChart();

        byte[] png = chart.ToPng();

        Assert.True(png.Length > 8);
        Assert.Equal(PngSignature, png[..8]);
        // 1×1 RGBA PNG (无压缩) 典型大小约 67 字节，不会超过 200
        Assert.True(png.Length < 200, $"1×1 PNG should be small, got {png.Length} bytes");
    }

    // ── ToDataUrl ──

    [Fact]
    public void ToDataUrl_StartsWithDataUri()
    {
        var chart = new SparklineChart(maxPoints: 10, width: 100, height: 50);
        chart.Push(42);

        string url = chart.ToDataUrl();

        Assert.StartsWith("data:image/png;base64,", url);
    }

    [Fact]
    public void ToDataUrl_Base64DecodesToPng()
    {
        var chart = new SparklineChart(maxPoints: 10, width: 100, height: 50);
        chart.Push(10);
        chart.Push(80);

        string url = chart.ToDataUrl();
        string base64 = url["data:image/png;base64,".Length..];
        byte[] bytes = Convert.FromBase64String(base64);

        Assert.Equal(PngSignature, bytes[..8]);
    }

    // ── 自定义尺寸 ──

    [Fact]
    public void Constructor_CustomDimensions()
    {
        var chart = new SparklineChart(width: 200, height: 80);
        Assert.Equal(200, chart.Width);
        Assert.Equal(80, chart.Height);
    }

    [Fact]
    public void ToPng_WithData_ProducesNonTrivialOutput()
    {
        var chart = new SparklineChart(maxPoints: 30, width: 200, height: 60);
        for (int i = 0; i < 30; i++)
            chart.Push(Random.Shared.Next(0, 101));

        byte[] png = chart.ToPng();

        // 含 30 数据点的图应该比空图大得多
        Assert.True(png.Length > 500, $"Expected >500 bytes for 30-point chart, got {png.Length}");
    }
}
