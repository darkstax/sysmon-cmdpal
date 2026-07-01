// Copyright (c) 2026 SysMonCmdPal
// 电池健康报告服务 — 通过 powercfg /batteryreport 解析设计容量/满充容量/循环次数
// 非管理员可运行；30 天缓存，后台线程执行不阻塞 UI

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace SysMonCmdPal;

internal sealed class BatteryReportService
{
    public static BatteryReportService Instance { get; } = new();

    private readonly object _lock = new();
    private BatteryReportData? _cached;
    private bool _running;

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysMonCmdPal", "battery_report.json");

    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysMonCmdPal");

    private const int CacheDays = 30;

    /// <summary>获取缓存的电池报告数据。若无缓存或过期，触发后台刷新，本次返回 null。</summary>
    public BatteryReportData? Get()
    {
        lock (_lock)
        {
            if (_cached is { } data) return data;
        }

        var loaded = TryLoadCache();
        if (loaded is { } cached && (DateTime.UtcNow - cached.LastRun).TotalDays < CacheDays)
        {
            lock (_lock) { _cached = cached; }
            return cached;
        }

        // 后台刷新（首次进入页面或过期时触发）
        RefreshAsync();
        return loaded; // 过期缓存也比没有强
    }

    /// <summary>立即触发后台刷新（忽略 30 天缓存检查）。</summary>
    public void RefreshAsync()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var data = GenerateReport();
                if (data is not null)
                {
                    lock (_lock) { _cached = data; }
                    TrySaveCache(data);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BatteryReport] refresh failed: {ex.Message}");
            }
            finally
            {
                lock (_lock) { _running = false; }
            }
        });
    }

    private BatteryReportData? GenerateReport()
    {
        var tempHtml = Path.Combine(Path.GetTempPath(), $"sysmon_battery_{Guid.NewGuid():N}.html");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg",
                Arguments = $"/batteryreport /output \"{tempHtml}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            p.WaitForExit(15000);
            if (p.ExitCode != 0 || !File.Exists(tempHtml)) return null;

            string html = File.ReadAllText(tempHtml);
            return ParseHtml(html);
        }
        finally
        {
            try { if (File.Exists(tempHtml)) File.Delete(tempHtml); } catch { }
        }
    }

    /// <summary>从 battery report HTML 提取电池静态信息。</summary>
    private static BatteryReportData? ParseHtml(string html)
    {
        // Installed batteries 表格格式：<span class="label">DESIGN CAPACITY</span></td><td>90,006 mWh</td>
        string designStr = ExtractLabel(html, "DESIGN CAPACITY");
        string fullStr = ExtractLabel(html, "FULL CHARGE CAPACITY");
        string name = ExtractLabel(html, "NAME").Trim();
        string manufacturer = ExtractLabel(html, "MANUFACTURER").Trim();
        string chemistry = ExtractLabel(html, "CHEMISTRY").Trim();
        string cycleStr = ExtractLabel(html, "CYCLE COUNT").Trim();

        if (!TryParseMWh(designStr, out int designMWh) || !TryParseMWh(fullStr, out int fullMWh))
            return null;

        int cycleCount = int.TryParse(cycleStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? c : -1;

        return new BatteryReportData
        {
            DesignCapacityMWh = designMWh,
            FullChargeCapacityMWh = fullMWh,
            CycleCount = cycleCount,
            Name = name,
            Manufacturer = manufacturer,
            Chemistry = chemistry,
            HealthPercent = designMWh > 0 ? Math.Round(fullMWh * 100.0 / designMWh, 1) : 0,
            LastRun = DateTime.UtcNow,
        };
    }

    /// <summary>提取 label 后第一个 td 的文本内容。</summary>
    private static string ExtractLabel(string html, string label)
    {
        // 匹配 <span class="label">LABEL</span></td><td>VALUE</td>
        var match = Regex.Match(html,
            $@"<span\s+class=""label"">\s*{Regex.Escape(label)}\s*</span></td>\s*<td>(.*?)</td>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    /// <summary>解析 "90,006 mWh" → 90006。</summary>
    private static bool TryParseMWh(string s, out int mwh)
    {
        mwh = 0;
        if (string.IsNullOrWhiteSpace(s) || s.Contains('-')) return false;
        var numMatch = Regex.Match(s, @"([\d,]+)");
        if (!numMatch.Success) return false;
        return int.TryParse(numMatch.Groups[1].Value.Replace(",", ""),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out mwh);
    }

    private static BatteryReportData? TryLoadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            string json = File.ReadAllText(CachePath);
            // 手动解析（AOT 模式禁用反射 JSON）
            return new BatteryReportData
            {
                DesignCapacityMWh = ExtractJsonInt(json, "DesignCapacityMWh"),
                FullChargeCapacityMWh = ExtractJsonInt(json, "FullChargeCapacityMWh"),
                HealthPercent = ExtractJsonDouble(json, "HealthPercent"),
                CycleCount = ExtractJsonInt(json, "CycleCount"),
                Name = ExtractJsonStr(json, "Name"),
                Manufacturer = ExtractJsonStr(json, "Manufacturer"),
                Chemistry = ExtractJsonStr(json, "Chemistry"),
                LastRun = DateTime.TryParse(ExtractJsonStr(json, "LastRun"), null, DateTimeStyles.RoundtripKind, out var d) ? d : DateTime.MinValue,
            };
        }
        catch { return null; }
    }

    private static void TrySaveCache(BatteryReportData data)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            // 手动拼接（AOT 安全）
            string json = $$"""
            {
              "DesignCapacityMWh": {{data.DesignCapacityMWh}},
              "FullChargeCapacityMWh": {{data.FullChargeCapacityMWh}},
              "HealthPercent": {{data.HealthPercent.ToString(CultureInfo.InvariantCulture)}},
              "CycleCount": {{data.CycleCount}},
              "Name": "{{JsonEscape(data.Name)}}",
              "Manufacturer": "{{JsonEscape(data.Manufacturer)}}",
              "Chemistry": "{{JsonEscape(data.Chemistry)}}",
              "LastRun": "{{data.LastRun:O}}"
            }
            """;
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BatteryReport] save cache failed: {ex.Message}");
        }
    }

    private static string ExtractJsonStr(string json, string field)
        => Regex.Match(json, $@"""{field}""\s*:\s*""(.*?)""", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : "";

    private static int ExtractJsonInt(string json, string field)
        => int.TryParse(Regex.Match(json, $@"""{field}""\s*:\s*(-?\d+)", RegexOptions.IgnoreCase) is { Success: true } m2 ? m2.Groups[1].Value : "", out int v) ? v : 0;

    private static double ExtractJsonDouble(string json, string field)
        => double.TryParse(Regex.Match(json, $@"""{field}""\s*:\s*(-?[\d.]+)", RegexOptions.IgnoreCase) is { Success: true } m3 ? m3.Groups[1].Value : "", NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private static string JsonEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

internal sealed class BatteryReportData
{
    public int DesignCapacityMWh { get; set; }
    public int FullChargeCapacityMWh { get; set; }
    public double HealthPercent { get; set; }
    public int CycleCount { get; set; }       // -1 if unavailable
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Chemistry { get; set; } = "";
    public DateTime LastRun { get; set; }
}
