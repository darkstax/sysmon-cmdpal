using System;
using System.Diagnostics;
using System.Text;

namespace SysMonCmdPal;

public partial class SystemInfoService
{
    private static string? _cachedSsid;
    private static DateTime _ssidLastQuery = DateTime.MinValue;
    private static readonly object _ssidLock = new();

    public string GetWifiSsid()
    {
        lock (_ssidLock)
        {
            if (DateTime.UtcNow - _ssidLastQuery < TimeSpan.FromSeconds(15) && _cachedSsid != null)
                return _cachedSsid;
            _ssidLastQuery = DateTime.UtcNow;
        }

        string ssid = QueryWifiSsid();
        lock (_ssidLock)
        {
            _cachedSsid = ssid;
            return _cachedSsid;
        }
    }

    private static string QueryWifiSsid()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "wlan show interfaces",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p == null) return "";

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(entireProcessTree: true); }
                catch { }
                return "";
            }

            var output = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            return ParseWifiSsid(output);
        }
        catch { return ""; }
    }

    internal static string ParseWifiSsid(string output)
    {
        // Parse "    SSID : MyNetwork" and ignore "BSSID".
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                continue;
            if (trimmed.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(":"))
            {
                var val = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
                if (!string.IsNullOrEmpty(val))
                    return val;
            }
        }
        return "";
    }
}
