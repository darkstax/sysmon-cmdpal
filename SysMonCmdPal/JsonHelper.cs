// Copyright (c) 2026 SysMonCmdPal
// AOT 安全的 JSON 辅助工具 — 不用反射，手动拼接 Dictionary<string,string> → JSON
// .NET 10 AOT/Trim 模式下 JsonSerializer.Serialize(反射) 被禁用，必须用手动拼接

using System.Collections.Generic;
using System.Text;

namespace SysMonCmdPal;

internal static class JsonHelper
{
    /// <summary>
    /// 将 Dictionary<string,string> 序列化为 JSON 字符串（AOT 安全，无反射）。
    /// 值会被 JSON 转义（引号、反斜杠、控制字符）。
    /// </summary>
    public static string ToJson(Dictionary<string, string> data)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kvp in data)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"');
            sb.Append(Escape(kvp.Key));
            sb.Append("\":\"");
            sb.Append(Escape(kvp.Value ?? ""));
            sb.Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:X4}");
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
