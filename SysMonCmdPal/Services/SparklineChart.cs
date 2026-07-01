// Copyright (c) 2026 SysMonCmdPal
// SparklineChart — 实时火花线图生成器。
// 纯托管 PNG 编码，无 System.Drawing 依赖。
// 通过 Markdown data URI 嵌入 CmdPal ContentPage。
//
// 渲染策略：整数像素 Bresenham 画线 + 半透明填充。
// 质量 vs 性能权衡：不使用超采样（避免 COM 线程阻塞），
// 但用 Wu 抗锯齿画线 + 垂直渐变填充保证基本视觉效果。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Storage.Streams;

namespace SysMonCmdPal;

public enum ChartMetric
{
    Cpu,
    Memory,
    Gpu,
    GpuMemory,
    Network,
    NetworkUp,
    Disk,
    DiskWrite,
    Battery,
}

public sealed class SparklineChart
{
    private readonly int _maxPoints;
    private readonly LinkedList<float> _history = new();
    private readonly object _lock = new();

    private readonly byte[] _fillColor;   // 淡色，用于折线下方填充
    private readonly byte[] _lineColor;   // 深色，用于折线 + 端点
    private readonly byte[] _dotColor;

    public int Width { get; }
    public int Height { get; }

    public SparklineChart(int maxPoints = 60, int width = 360, int height = 100, ChartMetric metric = ChartMetric.Cpu)
    {
        _maxPoints = maxPoints;
        Width = width;
        Height = height;

        // (fillRgb, lineRgb) — 填充用淡色，折线用深色
        (uint fill, uint line) = metric switch
        {
            ChartMetric.Cpu       => (0xC3ECFAu, 0x2F8CA8u),
            ChartMetric.Memory    => (0xC9E1FFu, 0x095BDEu),
            ChartMetric.Gpu       => (0xE4D0FFu, 0x7E13F0u),
            ChartMetric.GpuMemory => (0xE4D0FFu, 0x7E13F0u),
            ChartMetric.Network   => (0xFBD3DEu, 0xCB406Eu),
            ChartMetric.NetworkUp => (0xFBD3DEu, 0xCB406Eu),
            ChartMetric.Disk      => (0xFCE3C8u, 0xE07A00u),
            ChartMetric.DiskWrite => (0xD4F0DCu, 0x1F9D4Au),
            ChartMetric.Battery   => (0xFFF3B0u, 0xCC9F00u),
            _ => (0xC3ECFAu, 0x2F8CA8u),
        };
        _fillColor = [(byte)((fill >> 16) & 0xFF), (byte)((fill >> 8) & 0xFF), (byte)(fill & 0xFF), 255];
        _lineColor = [(byte)((line >> 16) & 0xFF), (byte)((line >> 8) & 0xFF), (byte)(line & 0xFF), 255];
        _dotColor = [255, 255, 255, 255];
    }

    public void Push(float value)
    {
        value = Math.Clamp(value, 0, 100);
        lock (_lock)
        {
            _history.AddLast(value);
            while (_history.Count > _maxPoints)
                _history.RemoveFirst();
        }
    }

    /// <summary>
    /// Push a raw value without clamping to 0-100. For non-percentage metrics
    /// like network speed (MB/s). Use with ToSvgDataUriAutoScale().
    /// </summary>
    public void PushRaw(float value)
    {
        value = Math.Max(0, value);
        lock (_lock)
        {
            _history.AddLast(value);
            while (_history.Count > _maxPoints)
                _history.RemoveFirst();
        }
    }

    /// <summary>
    /// 返回当前固定刻度档位（MB/s），用于页面显示"满刻度: XXX MB/s"。
    /// 根据当前数据峰值自动选择档位。
    /// </summary>
    public string GetCurrentScaleLabel()
    {
        lock (_lock)
        {
            if (_history.Count < 1) return "20 MB/s";
            float peak = 0;
            foreach (var v in _history) if (v > peak) peak = v;
            float scale = SelectScale(peak);
            return scale >= 1000 ? $"{scale / 1000:F0} GB/s" : $"{scale:F0} MB/s";
        }
    }

    /// <summary>
    /// 返回当前固定刻度档位（MB/s），用于页面显示"满刻度: X MB/s"。
    /// 根据历史数据峰值自动选择。
    /// </summary>
    public float GetCurrentScaleMB()
    {
        lock (_lock)
        {
            if (_history.Count < 1) return 20;
            float peak = 0;
            foreach (var v in _history) if (v > peak) peak = v;
            return SelectScale(peak);
        }
    }

    public int Count
    {
        get { lock (_lock) return _history.Count; }
    }

    /// <summary>
    /// Pre-render the PNG from current data and cache it.
    /// Called from the 1s refresh loop so GetContent() never blocks on rendering.
    /// </summary>
    private byte[]? _cachedPng;
    private int _cachedCount = -1;

    public void Prerender()
    {
        int count = Count;
        // Only re-render if new data arrived
        if (count == _cachedCount && _cachedPng != null) return;
        _cachedCount = count;
        _cachedPng = ToPng();
    }

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

    /// <summary>根据峰值选择最接近的满刻度档位（MB/s）</summary>
    private static float SelectScale(float peak)
    {
        float[] scales = [20, 100, 200, 500, 1000, 2000, 5000, 10000];
        foreach (var s in scales)
            if (peak < s) return s;
        return 10000;
    }

    private static readonly char[] _blocks = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    /// <summary>
    /// Return a Unicode text sparkline string (no image, no base64).
    /// Uses block characters ▁▂▃▄▅▆▇█ for 8-level height visualization.
    /// Suitable for direct embedding in MarkdownContent — zero image decode cost.
    /// </summary>
    public string? ToSparklineText()
    {
        float[] points;
        lock (_lock)
        {
            if (_history.Count < 2) return null;
            points = new float[_history.Count];
            _history.CopyTo(points, 0);
        }

        float min = float.MaxValue, max = float.MinValue;
        foreach (var p in points) { if (p < min) min = p; if (p > max) max = p; }
        if (max == min) max = min + 1;

        var sb = new StringBuilder(points.Length);
        foreach (var p in points)
        {
            int idx = (int)Math.Floor((p - min) / (max - min) * 7);
            idx = Math.Clamp(idx, 0, 7);
            sb.Append(_blocks[idx]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Return cached PNG as a markdown data URI string (legacy fallback, base64 — can be slow).
    /// </summary>
    public string? ToMarkdownBody()
    {
        byte[]? png = _cachedPng;
        if (png == null || png.Length <= 80) return null;
        string b64 = Convert.ToBase64String(png);
        return $"![chart](data:image/png;base64,{b64})";
    }

    /// <summary>
    /// Return cached PNG as a MarkdownContent (legacy, creates new object each call).
    /// </summary>
    public MarkdownContent? ToMarkdownContent()
    {
        string? body = ToMarkdownBody();
        return body == null ? null : new MarkdownContent(body);
    }

    public byte[] ToPng()
    {
        float[] points;
        lock (_lock)
        {
            if (_history.Count < 2) return CreateEmptyPng();
            points = new float[_history.Count];
            _history.CopyTo(points, 0);
        }

        byte[] px = new byte[Width * Height * 4];
        Render(px, Width, Height, points);

        using var ms = new MemoryStream();
        WritePngFile(ms, Width, Height, px);
        return ms.ToArray();
    }

    // ========================================================================
    // Rendering
    // ========================================================================

    private void Render(byte[] px, int w, int h, float[] points)
    {
        int m = 4; // margin
        int pw = w - m * 2;
        int ph = h - m * 2;

        // Auto-scale Y range with padding
        float min = float.MaxValue, max = float.MinValue;
        foreach (var p in points) { if (p < min) min = p; if (p > max) max = p; }
        float range = max - min;
        if (range < 5) { float mid = (min + max) / 2; min = Math.Max(0, mid - 5); max = Math.Min(100, mid + 5); }
        if (max == min) max = min + 1;
        float pad = range * 0.12f;
        min = Math.Max(0, min - pad);
        max = Math.Min(100, max + pad);
        if (max == min) max = min + 1;

        // Map data points to pixel coords
        int n = points.Length;
        int[] xs = new int[n];
        int[] ys = new int[n];
        for (int i = 0; i < n; i++)
        {
            float t = n == 1 ? 0 : (float)i / (n - 1);
            xs[i] = m + (int)(t * pw);
            ys[i] = m + (int)((1 - (points[i] - min) / (max - min)) * ph);
            ys[i] = Math.Clamp(ys[i], m, m + ph - 1);
        }

        int baseline = m + ph - 1;

        // 1. Gradient fill under the curve
        FillGradientUnderCurve(px, w, h, xs, ys, baseline, _lineColor);

        // 2. Draw anti-aliased line segments
        for (int i = 1; i < n; i++)
            DrawWuLine(px, w, h, xs[i - 1], ys[i - 1], xs[i], ys[i], _lineColor);

        // 3. Draw endpoint dot
        if (n > 0)
        {
            FillCircle(px, w, h, xs[n - 1], ys[n - 1], 3, _lineColor);
            FillCircle(px, w, h, xs[n - 1], ys[n - 1], 2, _dotColor);
        }
    }

    /// <summary>
    /// Fill the area under the curve with a vertical gradient.
    /// Top (at curve): ~25% line color. Bottom (baseline): transparent.
    /// </summary>
    private static void FillGradientUnderCurve(byte[] px, int w, int h,
        int[] xs, int[] ys, int baseline, byte[] lineColor)
    {
        int n = xs.Length;
        if (n < 2) return;

        // For each x column between first and last point, find curve Y
        for (int x = xs[0]; x <= xs[n - 1]; x++)
        {
            // Find curve Y at this x by interpolating
            int curveY = baseline;
            for (int i = 0; i < n - 1; i++)
            {
                if (x >= xs[i] && x <= xs[i + 1])
                {
                    if (xs[i + 1] == xs[i])
                        curveY = ys[i];
                    else
                    {
                        float t = (float)(x - xs[i]) / (xs[i + 1] - xs[i]);
                        curveY = (int)(ys[i] * (1 - t) + ys[i + 1] * t);
                    }
                    break;
                }
            }

            int fillHeight = baseline - curveY;
            if (fillHeight <= 0) continue;

            for (int y = curveY; y <= baseline && y < h; y++)
            {
                if (y < 0) continue;
                float dist = fillHeight > 0 ? (float)(y - curveY) / fillHeight : 0;
                float alpha = (1 - dist) * 0.25f;
                int idx = (y * w + x) * 4;
                px[idx]     = (byte)(lineColor[0] * alpha + px[idx]     * (1 - alpha));
                px[idx + 1] = (byte)(lineColor[1] * alpha + px[idx + 1] * (1 - alpha));
                px[idx + 2] = (byte)(lineColor[2] * alpha + px[idx + 2] * (1 - alpha));
                px[idx + 3] = (byte)Math.Max(px[idx + 3], alpha * 255);
            }
        }
    }

    /// <summary>
    /// Wu's anti-aliased line algorithm. Draws a 1px AA line.
    /// </summary>
    private static void DrawWuLine(byte[] px, int w, int h,
        int x0, int y0, int x1, int y1, byte[] c)
    {
        bool steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
        if (steep) { (x0, y0) = (y0, x0); (x1, y1) = (y1, x1); }
        if (x0 > x1) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); }

        int dx = x1 - x0;
        int dy = y1 - y0;
        float gradient = dx == 0 ? 1 : (float)dy / dx;

        // Handle first endpoint
        float xEnd = x0;
        float yEnd = y0 + gradient * (xEnd - x0);
        int xpxl1 = (int)xEnd;
        int ypxl1 = (int)yEnd;
        float xfract = yEnd - ypxl1;

        if (steep)
        {
            Plot(px, w, h, ypxl1, xpxl1, c, 1 - xfract);
            Plot(px, w, h, ypxl1 + 1, xpxl1, c, xfract);
        }
        else
        {
            Plot(px, w, h, xpxl1, ypxl1, c, 1 - xfract);
            Plot(px, w, h, xpxl1, ypxl1 + 1, c, xfract);
        }

        float intery = yEnd + gradient;

        // Handle second endpoint
        xEnd = x1;
        yEnd = y1 + gradient * (xEnd - x1);
        int xpxl2 = (int)xEnd;
        int ypxl2 = (int)yEnd;
        xfract = yEnd - ypxl2;

        if (steep)
        {
            Plot(px, w, h, ypxl2, xpxl2, c, 1 - xfract);
            Plot(px, w, h, ypxl2 + 1, xpxl2, c, xfract);
        }
        else
        {
            Plot(px, w, h, xpxl2, ypxl2, c, 1 - xfract);
            Plot(px, w, h, xpxl2, ypxl2 + 1, c, xfract);
        }

        // Main loop
        for (int x = xpxl1 + 1; x < xpxl2; x++)
        {
            int yi = (int)intery;
            float fract = intery - yi;
            if (steep)
            {
                Plot(px, w, h, yi, x, c, 1 - fract);
                Plot(px, w, h, yi + 1, x, c, fract);
            }
            else
            {
                Plot(px, w, h, x, yi, c, 1 - fract);
                Plot(px, w, h, x, yi + 1, c, fract);
            }
            intery += gradient;
        }
    }

    private static void Plot(byte[] px, int w, int h, int x, int y, byte[] c, float alpha)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        int idx = (y * w + x) * 4;
        float a = alpha;
        px[idx]     = (byte)(c[0] * a + px[idx]     * (1 - a));
        px[idx + 1] = (byte)(c[1] * a + px[idx + 1] * (1 - a));
        px[idx + 2] = (byte)(c[2] * a + px[idx + 2] * (1 - a));
        px[idx + 3] = (byte)Math.Max(px[idx + 3], a * 255);
    }

    private static void FillCircle(byte[] px, int w, int h, int cx, int cy, int r, byte[] c)
    {
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (dx * dx + dy * dy <= r * r)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < w && y >= 0 && y < h)
                    {
                        int idx = (y * w + x) * 4;
                        px[idx] = c[0]; px[idx + 1] = c[1]; px[idx + 2] = c[2]; px[idx + 3] = c[3];
                    }
                }
    }

    // ========================================================================
    // PNG encoding
    // ========================================================================

    private static byte[] CreateEmptyPng()
    {
        using var ms = new MemoryStream();
        WritePngFile(ms, 1, 1, [0, 0, 0, 0]);
        return ms.ToArray();
    }

    private static void WritePngFile(Stream s, int w, int h, byte[] rgba)
    {
        s.Write([137, 80, 78, 71, 13, 10, 26, 10], 0, 8);
        using (var ms = new MemoryStream())
        {
            WriteBE(ms, (uint)w); WriteBE(ms, (uint)h);
            ms.WriteByte(8); ms.WriteByte(6); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
            WriteChunk(s, "IHDR", ms.ToArray());
        }

        byte[] raw = new byte[1 + h * (1 + w * 4)];
        int pos = 0;
        for (int y = 0; y < h; y++)
        {
            raw[pos++] = 0; // filter type: None
            Array.Copy(rgba, y * w * 4, raw, pos, w * 4);
            pos += w * 4;
        }

        using var cms = new MemoryStream();
        // ZLibStream produces proper zlib format (2-byte header + deflate + 4-byte Adler32)
        // which PNG IDAT requires. DeflateStream produces raw deflate without the wrapper,
        // causing PNG decoders to fail or hang.
        using (var zlib = new System.IO.Compression.ZLibStream(cms, System.IO.Compression.CompressionLevel.Optimal, true))
            zlib.Write(raw, 0, pos);
        WriteChunk(s, "IDAT", cms.ToArray());
        WriteChunk(s, "IEND", []);
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        WriteBE(s, (uint)data.Length);
        byte[] tb = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(tb, 0, 4); s.Write(data, 0, data.Length);
        WriteBE(s, Crc32(tb, data));
    }

    private static void WriteBE(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }

    private static readonly uint[] CrcTable = BuildCrc();

    private static uint[] BuildCrc()
    {
        uint[] t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
