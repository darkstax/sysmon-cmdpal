using System;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

public sealed partial class SparklineChart
{
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
}
