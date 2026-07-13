using System;
using System.Globalization;
using System.Text;

namespace SysMonCmdPal;

public sealed partial class SparklineChart
{
    /// <summary>
    /// Generate an SVG data URI for the current chart data.
    /// Uses data:image/svg+xml;utf8, format (no base64) — same approach as
    /// official PowerToys PerformanceMonitor ChartHelper.
    /// Lightweight: SVG is plain text XML, no encoding/decoding overhead.
    /// NOTE: cannot use url(#id) gradients — '#' is the data-URI fragment
    /// delimiter and would truncate the payload. Use fill-opacity layering.
    /// Canvas is 2x (680x160); CmdPal shrinks it via Image size:Stretch.
    /// No <text> elements — CmdPal's SVG renderer doesn't render them.
    /// Current value is shown in the AdaptiveCard TextBlock above/below the chart.
    /// </summary>
    public string? ToSvgDataUri(int canvasHeight = 160, int canvasWidth = 380)
    {
        float[]? points = TakeLastRender();
        if (points is null) return null;

        int h = canvasHeight;
        int n = points.Length;
        int[] ys = new int[n];
        for (int i = 0; i < n; i++)
            ys[i] = Math.Clamp((int)((1f - points[i] / 100f) * (h - 1)), 0, h - 1);
        return BuildSvg(ys, canvasHeight, canvasWidth);
    }

    /// <summary>
    /// Generate an SVG data URI with auto-scaled Y range (for non-percentage
    /// metrics like network speed). Gridlines at 25/50/75 of actual range.
    /// No <text> elements — values shown in AdaptiveCard TextBlocks.
    /// Canvas is 2x (680x160); CmdPal shrinks it via Image size:Stretch.
    /// </summary>
    public string? ToSvgDataUriAutoScale(string unit = "", int canvasHeight = 160, int canvasWidth = 380)
    {
        float[]? points = TakeLastRender();
        if (points is null) return null;

        int h = canvasHeight;

        // Auto-scale Y with padding
        float min = float.MaxValue, max = float.MinValue;
        foreach (var p in points) { if (p < min) min = p; if (p > max) max = p; }
        float range = max - min;
        if (range < 0.001f) { min = 0; max = Math.Max(max, 1); }
        float pad = (max - min) * 0.12f;
        if (pad < 0.001f) pad = max * 0.1f;
        min = Math.Max(0, min - pad);
        max = max + pad;
        if (max <= min) max = min + 1;

        int n = points.Length;
        int[] ys = new int[n];
        for (int i = 0; i < n; i++)
            ys[i] = Math.Clamp((int)((1f - (points[i] - min) / (max - min)) * (h - 1)), 0, h - 1);
        return BuildSvg(ys, canvasHeight, canvasWidth);
    }

    /// <summary>
    /// 固定刻度 SVG 生成 —— 根据数据峰值自动选择满刻度档位，
    /// Y 轴固定 0-maxScale，避免自适应范围导致小波动被放大。
    /// 档位: 20/100/200/500/1000/2000/5000/10000。
    /// 用于网络速度、磁盘 IO 等非百分比指标。
    /// </summary>
    public string? ToSvgDataUriFixedScale(string unit = " MB/s", int canvasHeight = 160, bool showMinorGridlines = true, int canvasWidth = 380)
    {
        float[]? points = TakeLastRender();
        if (points is null) return null;

        // 根据数据峰值选择满刻度档位
        float peak = 0;
        foreach (var p in points) if (p > peak) peak = p;
        float maxScale = SelectScale(peak);

        int h = canvasHeight;
        int n = points.Length;
        int[] ys = new int[n];
        for (int i = 0; i < n; i++)
        {
            float ratio = maxScale > 0 ? points[i] / maxScale : 0;
            ys[i] = Math.Clamp((int)((1f - Math.Clamp(ratio, 0, 1)) * (h - 1)), 0, h - 1);
        }
        return BuildSvg(ys, canvasHeight, canvasWidth);
    }

    /// <summary>
    /// Shared SVG builder — transparent background (no flicker), integer grid.
    /// canvasWidth adapts: single-chart pages use larger width, dual-chart pages smaller.
    /// </summary>
    private string BuildSvg(int[] ys, int canvasHeight, int canvasWidth = 380)
    {
        int w = canvasWidth;
        int h = canvasHeight;
        const int maxPts = 34;
        int pxBetween = (w - 4) / maxPts;

        int n = ys.Length;
        int startX = 2 + ((maxPts - n) * pxBetween);

        var sb = new StringBuilder(n * 12);
        int x = startX;
        for (int i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(CultureInfo.InvariantCulture, $"{x},{ys[i]}");
            x += pxBetween;
        }
        string pts = sb.ToString();
        int lastX = x - pxBetween;
        int lastY = ys[n - 1];
        int baseline = h - 2;
        string fillPts = $"{pts} {lastX},{baseline} {startX},{baseline}";
        string fillRgb = $"{_fillColor[0]},{_fillColor[1]},{_fillColor[2]}";
        string lineRgb = $"{_lineColor[0]},{_lineColor[1]},{_lineColor[2]}";

        var svg = $""""
<svg height="{h}" width="{w}" xmlns="http://www.w3.org/2000/svg">
<polyline points="{fillPts}" style="fill:rgb({fillRgb});fill-opacity:1;stroke:none"/>
<polyline points="{pts}" style="fill:none;stroke:rgb({lineRgb});stroke-width:2;stroke-linejoin:round;stroke-linecap:round"/>
<circle cx="{lastX}" cy="{lastY}" r="4" style="fill:rgb({lineRgb})"/>
<circle cx="{lastX}" cy="{lastY}" r="2" style="fill:rgb(255,255,255)"/>
</svg>
"""";

        return "data:image/svg+xml;utf8," + svg;
    }

    /// <summary>Take the last 34 points for rendering (matches official MaxChartValues).</summary>
    private float[]? TakeLastRender()
    {
        lock (_lock)
        {
            if (_history.Count < 2) return null;
            int take = Math.Min(_history.Count, 34);
            float[] points = new float[take];
            var node = _history.Last;
            for (int i = take - 1; i >= 0; i--)
            {
                points[i] = node!.Value;
                node = node.Previous;
            }
            return points;
        }
    }

    /// <summary>根据峰值选择最接近的满刻度档位（MB/s）</summary>
    private static float SelectScale(float peak)
    {
        float[] scales = [20, 100, 200, 500, 1000, 2000, 5000, 10000];
        foreach (var s in scales)
            if (peak < s) return s;
        return 10000;
    }
}
