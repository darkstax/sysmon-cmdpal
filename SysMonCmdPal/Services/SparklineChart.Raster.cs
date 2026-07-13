using System;

namespace SysMonCmdPal;

public sealed partial class SparklineChart
{
    private void Render(byte[] px, int w, int h, float[] points)
    {
        int m = 4; // margin
        int pw = w - m * 2;
        int ph = h - m * 2;

        // Auto-scale Y range with padding
        float min = float.MaxValue, max = float.MinValue;
        foreach (var p in points) { if (p < min) min = p; if (p > max) max = p; }
        float range = max - min;
        if (range < 5) { float mid = (min + max) / 2; min = Math.Max(0, mid - 5); max = mid + 5; }
        if (max == min) max = min + 1;
        float pad = range * 0.12f;
        min = Math.Max(0, min - pad);
        max = max + pad;
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
}
