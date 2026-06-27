// Copyright (c) 2026 SysMonCmdPal
// SparklineChart — 实时火花线图生成器。
// 为 CPU/内存/GPU 使用率生成 PNG 火花线，输出为 base64 data URL。
// 纯托管实现，无 System.Drawing 依赖。
//
// 用法:
//   var chart = new SparklineChart(60);
//   chart.Push(value);
//   string url = chart.ToDataUrl();

using System;
using System.Collections.Generic;
using System.IO;

namespace SysMonCmdPal;

public class SparklineChart
{
    private readonly int _maxPoints;
    private readonly LinkedList<float> _history = new();
    private readonly object _lock = new();

    private readonly byte[] _lineColor;
    private readonly byte[] _fillColor;

    public int Width { get; }
    public int Height { get; }

    public SparklineChart(int maxPoints = 60, int width = 340, int height = 100, uint lineColor = 0x4CC2FF)
    {
        _maxPoints = maxPoints;
        Width = width;
        Height = height;
        _lineColor = [(byte)((lineColor >> 16) & 0xFF), (byte)((lineColor >> 8) & 0xFF), (byte)(lineColor & 0xFF), 255];
        _fillColor = [_lineColor[0], _lineColor[1], _lineColor[2], 40];
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

    public int Count { get { lock (_lock) return _history.Count; } }

    public string ToDataUrl()
    {
        byte[] png = ToPng();
        return $"data:image/png;base64,{Convert.ToBase64String(png)}";
    }

    public byte[] ToPng()
    {
        float[] points;
        lock (_lock)
        {
            points = new float[_history.Count];
            _history.CopyTo(points, 0);
        }

        if (points.Length == 0) return CreateEmptyPng();

        using var ms = new MemoryStream();
        RenderPng(ms, points);
        return ms.ToArray();
    }

    private void RenderPng(Stream stream, float[] points)
    {
        int w = Width, h = Height, m = 2, pw = w - m * 2, ph = h - m * 2;

        float min = 100, max = 0;
        foreach (var p in points) { if (p < min) min = p; if (p > max) max = p; }
        float range = max - min;
        if (range < 10) { float mid = (min + max) / 2; min = Math.Max(0, mid - 10); max = Math.Min(100, mid + 10); }
        if (max == min) max = min + 1;

        byte[] px = new byte[w * h * 4];

        // Baseline
        for (int x = m; x < m + pw; x++)
        {
            int idx = ((m + ph - 1) * w + x) * 4;
            px[idx] = 255; px[idx + 1] = 255; px[idx + 2] = 255; px[idx + 3] = 20;
        }

        // Map to pixel coords
        int[] xs = new int[points.Length + 2], ys = new int[points.Length + 2];
        for (int i = 0; i < points.Length; i++)
        {
            xs[i] = m + (int)((float)i / (_maxPoints - 1) * pw);
            ys[i] = m + (int)((1 - (points[i] - min) / (max - min)) * ph);
            ys[i] = Math.Clamp(ys[i], m, m + ph - 1);
        }
        xs[points.Length] = xs[points.Length - 1]; ys[points.Length] = m + ph;
        xs[points.Length + 1] = xs[0]; ys[points.Length + 1] = m + ph;

        FillPolygon(px, w, h, xs, ys, _fillColor);
        for (int i = 1; i < points.Length; i++)
            DrawLine(px, w, h, xs[i - 1], ys[i - 1], xs[i], ys[i], _lineColor);
        if (points.Length > 0)
            FillCircle(px, w, h, xs[points.Length - 1], ys[points.Length - 1], 3, _lineColor);

        WritePngFile(stream, w, h, px);
    }

    private void FillPolygon(byte[] px, int w, int h, int[] xs, int[] ys, byte[] c)
    {
        int n = xs.Length, minY = h, maxY = 0;
        for (int i = 0; i < n; i++) { if (ys[i] < minY) minY = ys[i]; if (ys[i] > maxY) maxY = ys[i]; }
        minY = Math.Max(0, minY); maxY = Math.Min(h - 1, maxY);

        for (int y = minY; y <= maxY; y++)
        {
            var ix = new List<int>();
            for (int i = 0; i < n - 1; i++)
                if ((ys[i] <= y && ys[i + 1] > y) || (ys[i + 1] <= y && ys[i] > y))
                    if (ys[i] != ys[i + 1])
                        ix.Add((int)(xs[i] + (float)(y - ys[i]) / (ys[i + 1] - ys[i]) * (xs[i + 1] - xs[i])));
            ix.Sort();
            for (int i = 0; i < ix.Count - 1; i += 2)
                for (int x = Math.Clamp(ix[i], 0, w - 1); x <= Math.Clamp(ix[i + 1], 0, w - 1); x++)
                {
                    int idx = (y * w + x) * 4;
                    float a = c[3] / 255f;
                    px[idx] = (byte)(c[0] * a + px[idx] * (1 - a));
                    px[idx + 1] = (byte)(c[1] * a + px[idx + 1] * (1 - a));
                    px[idx + 2] = (byte)(c[2] * a + px[idx + 2] * (1 - a));
                    px[idx + 3] = 255;
                }
        }
    }

    private void DrawLine(byte[] px, int w, int h, int x0, int y0, int x1, int y1, byte[] c)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            if (x0 >= 0 && x0 < w && y0 >= 0 && y0 < h)
            {
                int idx = (y0 * w + x0) * 4;
                px[idx] = c[0]; px[idx + 1] = c[1]; px[idx + 2] = c[2]; px[idx + 3] = c[3];
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private void FillCircle(byte[] px, int w, int h, int cx, int cy, int r, byte[] c)
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

    private byte[] CreateEmptyPng()
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
            raw[pos++] = 0;
            Array.Copy(rgba, y * w * 4, raw, pos, w * 4);
            pos += w * 4;
        }

        using var cms = new MemoryStream();
        using (var def = new System.IO.Compression.DeflateStream(cms, System.IO.Compression.CompressionLevel.Optimal, true))
            def.Write(raw, 0, pos);
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
