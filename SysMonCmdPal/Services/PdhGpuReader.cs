// Copyright (c) 2026 SysMonCmdPal
// PDH GPU 利用率读取器 — 用户态，不需要管理员
// 通过 PerformanceCounter("GPU Engine", "Utilization Percentage") 读取 GPU 利用率
// 这是 Windows 内置的性能计数器，由 DxgKrnl 驱动发布
// 实例名格式: pid_<pid>_luid_0x<high>_0x<low>_phys_<n>_eng_<id>_engtype_<Type>
// Type 包括: 3D, Compute_0, Compute_1, Copy, VideoDecode, VideoEncode, VideoProcessing 等

using System.Diagnostics;

namespace SysMonCmdPal;

internal sealed class PdhGpuReader
{
    public static PdhGpuReader Instance { get; } = new();
    private PerformanceCounterCategory? _category;
    private bool _initAttempted;
    private bool _available;

    // 上一次采样的 CounterSample（按实例名索引）— 用于计算 delta
    private Dictionary<string, CounterSample> _prevSamples = new();

    // LUID → GPU 名称映射
    private Dictionary<(uint, int), string>? _luidNameMap;

    public bool IsAvailable
    {
        get
        {
            if (!_initAttempted) Init();
            return _available;
        }
    }

    private void Init()
    {
        _initAttempted = true;
        try
        {
            _available = PerformanceCounterCategory.Exists("GPU Engine");
            if (_available)
            {
                _category = new PerformanceCounterCategory("GPU Engine");
                // 构建 LUID → 名称映射
                _luidNameMap = new();
                foreach (var a in GpuAdapterEnumerator.GetAdapters())
                    _luidNameMap[(a.LuidLow, a.LuidHigh)] = a.Name;
                Debug.WriteLine("[PDH-GPU] GPU Engine category available");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PDH-GPU] Init failed: {ex.Message}");
        }
    }

    /// <summary>读取所有 GPU 的利用率。返回 GpuResult 列表（仅 UsagePercent）</summary>
    public List<GpuResult> ReadAll()
    {
        var results = new List<GpuResult>();
        if (!IsAvailable || _category == null) return results;

        try
        {
            // 批量读取所有实例（一次内核调用）
            var categoryData = _category.ReadCategory();
            // InstanceDataCollectionCollection 索引器按计数器名返回 InstanceDataCollection
            InstanceDataCollection? utilData = null;
            foreach (string key in categoryData.Keys)
            {
                if (key == "Utilization Percentage")
                {
                    utilData = (InstanceDataCollection)categoryData[key];
                    break;
                }
            }
            if (utilData == null) return results;

            // 按 LUID 分组，每组取所有 engine 的最大利用率
            var perGpuUsage = new Dictionary<(uint, int), float>();
            var currentNames = new HashSet<string>();

            foreach (InstanceData data in utilData.Values)
            {
                string instanceName = data.InstanceName;
                currentNames.Add(instanceName);
                var sample = data.Sample;

                // 解析实例名: pid_*_luid_0x<high>_0x<low>_phys_*_eng_*_engtype_*
                var (luidLow, luidHigh) = ParseLuid(instanceName);
                if (luidLow == 0 && luidHigh == 0) continue;

                // 计算利用率（需要前一次采样）
                if (_prevSamples.TryGetValue(instanceName, out var prevSample))
                {
                    float cooked = CounterSampleCalculator.ComputeCounterValue(prevSample, sample);
                    if (cooked > 0)
                    {
                        var key = (luidLow, luidHigh);
                        if (!perGpuUsage.TryGetValue(key, out float existing) || cooked > existing)
                            perGpuUsage[key] = cooked;
                    }
                }

                // 保存当前采样供下次计算
                _prevSamples[instanceName] = sample;
            }

            // 清理已消失的实例（防止字典无限增长）
            if (_prevSamples.Count > 200)
            {
                var toRemove = _prevSamples.Keys.Where(k => !currentNames.Contains(k)).ToList();
                foreach (var k in toRemove) _prevSamples.Remove(k);
            }

            // 构建 GpuResult
            foreach (var (luid, usage) in perGpuUsage)
            {
                string name = _luidNameMap?.GetValueOrDefault(luid) ?? "GPU";
                results.Add(new GpuResult(
                    name,
                    Math.Min(usage, 100),
                    -1, 0, 0, "PDH"));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PDH-GPU] ReadAll: {ex.Message}");
        }
        return results;
    }

    /// <summary>从实例名解析 LUID。格式: ..._luid_0x<high>_0x<low>_...</summary>
    private static (uint low, int high) ParseLuid(string instanceName)
    {
        // 格式: pid_0_luid_0x00000000_0x00013AB9_phys_0_eng_0_engtype_3D
        var parts = instanceName.Split('_');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == "luid" && i + 2 < parts.Length)
            {
                // parts[i+1] = "0x<high>", parts[i+2] = "0x<low>"
                // 注意: 实际格式是 luid_0x<high>_0x<low>
                if (parts[i + 1].StartsWith("0x") && parts[i + 2].StartsWith("0x"))
                {
                    int high = ParseHex(parts[i + 1]);
                    uint low = (uint)ParseHex(parts[i + 2]);
                    return (low, high);
                }
            }
        }
        return (0, 0);
    }

    private static int ParseHex(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v) ? v : 0;
    }
}
