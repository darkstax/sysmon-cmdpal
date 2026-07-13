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

public sealed partial class SparklineChart
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
    // M8: thread-safe Prerender — use volatile + lock to prevent torn reads
    private volatile byte[]? _cachedPng;
    private int _cachedCount = -1;
    private readonly object _prerenderLock = new();

    public void Prerender()
    {
        int count = Count;
        // Only re-render if new data arrived
        if (count == _cachedCount && _cachedPng != null) return;
        lock (_prerenderLock)
        {
            if (count == _cachedCount && _cachedPng != null) return;
            _cachedCount = count;
            _cachedPng = ToPng();
        }
    }
}
