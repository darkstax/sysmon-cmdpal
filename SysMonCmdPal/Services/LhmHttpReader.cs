// Copyright (c) 2026 SysMonCmdPal
// LHM HTTP API 读取器 — 通过 LibreHardwareMonitor 独立版的 Web 服务器读取传感器数据。
// LHM 独立版开启 "Run Web Server" 后，在 http://localhost:8085/data.json 暴露 JSON 传感器数据。
// MSIX runFullTrust 下 HTTP loopback 请求完全可用，不依赖内核驱动或 WMI。
//
// LHM JSON 结构为嵌套树:
//   Root → Computer → Hardware (HardwareId="/amdcpu/0")
//     → Category ("Temperatures") → Sensor (SensorId, Type, Value="82.0 °C")
//
// 用途: 获取准确的 CPU Tctl/Tdie 温度（ADL PMLOG sensor 32 读数偏低 ~4-5°C）

using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;

namespace SysMonCmdPal;

/// <summary>
/// 通过 HTTP API 读取外部 LHM 独立版的传感器数据（惰性初始化 + 冷却重试）。
/// LHM 独立版需开启 "Options → Run Web Server"。
/// </summary>
internal sealed class LhmHttpReader
{
    public static LhmHttpReader Instance { get; } = new();

    private bool _available;
    private bool _initAttempted;
    private bool _httpClientFailed;
    private DateTime _lastInitAttempt = DateTime.MinValue;
    private static readonly TimeSpan RetryCooldown = TimeSpan.FromSeconds(30);
    private const string DefaultUrl = "http://localhost:8085/data.json";
    private HttpClient? _http;

    // 缓存解析结果，避免每次读取都做 HTTP 请求
    private string? _cachedJson;
    private DateTime _cacheTime = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(2);

    private LhmHttpReader() { }

    private HttpClient? GetHttpClient()
    {
        if (_httpClientFailed) return null;
        if (_http != null) return _http;
        try
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return _http;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"LHM HTTP: HttpClient 创建失败: {ex.GetType().Name}: {ex.Message}");
            _httpClientFailed = true;
            return null;
        }
    }

    /// <summary>Web 服务器是否可达</summary>
    public bool IsAvailable
    {
        get
        {
            if (!_initAttempted)
            {
                _initAttempted = true;
                _available = ProbeHttp();
            }
            else if (!_available && DateTime.UtcNow - _lastInitAttempt > RetryCooldown)
            {
                _lastInitAttempt = DateTime.UtcNow;
                _available = ProbeHttp();
            }
            return _available;
        }
    }

    /// <summary>读取 CPU Tctl/Tdie 或 Package 温度（°C），不可用返回 -1</summary>
    public double ReadCpuTemp()
    {
        if (!IsAvailable) return -1;

        try
        {
            var json = FetchJson();
            if (json == null) return -1;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double bestValue = -1;

            // 递归遍历树，查找 CPU 硬件下的温度传感器
            SearchCpuTemp(root, ref bestValue);

            return bestValue;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] LHM HTTP 读取失败: {ex.Message}");
            return -1;
        }
    }

    /// <summary>递归搜索 CPU 硬件节点下的温度传感器</summary>
    private static void SearchCpuTemp(JsonElement node, ref double bestValue)
    {
        // 检查是否是 CPU 硬件节点
        if (node.TryGetProperty("HardwareId", out var hwIdEl))
        {
            var hwId = hwIdEl.GetString() ?? "";
            if (hwId.Contains("cpu", StringComparison.OrdinalIgnoreCase))
            {
                // 这是 CPU 硬件节点，搜索其子树中的温度传感器
                SearchTempInSubtree(node, ref bestValue);
                return;
            }
        }

        // 递归进入 Children
        if (node.TryGetProperty("Children", out var children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                SearchCpuTemp(child, ref bestValue);
            }
        }
    }

    /// <summary>在硬件节点的子树中查找温度传感器</summary>
    private static void SearchTempInSubtree(JsonElement node, ref double bestValue)
    {
        // 检查当前节点是否是温度传感器
        if (node.TryGetProperty("Type", out var typeEl))
        {
            var type = typeEl.GetString() ?? "";
            if (type.Equals("Temperature", StringComparison.OrdinalIgnoreCase))
            {
                var name = node.TryGetProperty("Text", out var textEl)
                    ? textEl.GetString() ?? "" : "";

                // 解析 Value (格式: "82.0 °C")
                if (node.TryGetProperty("Value", out var valEl))
                {
                    double val = ParseLhmValue(valEl.GetString() ?? "");
                    if (val > 0 && val < 150)
                    {
                        // 优先: Tctl/Tdie (AMD) 或 Package (Intel)
                        if (name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        {
                            bestValue = val;
                            return; // 最高优先级，直接返回
                        }

                        // 记录第一个有效值作为兜底
                        if (bestValue < 0)
                            bestValue = val;
                    }
                }
            }
        }

        // 递归进入 Children
        if (node.TryGetProperty("Children", out var children) &&
            children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                SearchTempInSubtree(child, ref bestValue);
                // 如果已找到最高优先级，提前返回
                if (bestValue > 0) return;
            }
        }
    }

    /// <summary>解析 LHM 传感器值字符串（如 "82.0 °C" → 82.0）</summary>
    private static double ParseLhmValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return -1;

        // 去掉单位后缀: "°C", "MHz", "W", "%", "GB", "V" 等
        // 只取数字部分
        int end = 0;
        while (end < raw.Length && (char.IsDigit(raw[end]) || raw[end] == '.' || raw[end] == '-' || raw[end] == '+'))
            end++;

        if (end == 0) return -1;

        var numStr = raw.AsSpan(0, end);
        if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            return val;

        return -1;
    }

    /// <summary>获取 JSON 数据（带 2 秒缓存）</summary>
    private string? FetchJson()
    {
        var now = DateTime.UtcNow;
        if (_cachedJson != null && (now - _cacheTime) < CacheDuration)
            return _cachedJson;

        var http = GetHttpClient();
        if (http == null) return null;

        try
        {
            var resp = http.GetAsync(DefaultUrl).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            _cachedJson = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            _cacheTime = now;
            return _cachedJson;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] LHM HTTP fetch 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>探测 LHM Web 服务器是否可达</summary>
    private bool ProbeHttp()
    {
        var http = GetHttpClient();
        if (http == null) return false;

        try
        {
            var resp = http.GetAsync(DefaultUrl).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
