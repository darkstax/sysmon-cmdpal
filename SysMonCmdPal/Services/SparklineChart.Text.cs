using System;
using System.Text;

namespace SysMonCmdPal;

public sealed partial class SparklineChart
{
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
}
