// Copyright (c) 2026 SysMonCmdPal
// PDH GPU 利用率读取器 — 用户态，不需要管理员，**零 COM**
// 通过 PerformanceCounter("GPU Engine", "Utilization Percentage") 读取 GPU 利用率
// 这是 Windows 内置的性能计数器，由 DxgKrnl 驱动发布，通过 perflib 读取
// 实例名格式: pid_<pid>_luid_0x<high>_0x<low>_phys_<n>_eng_<id>_engtype_<Type>
// Type 包括: 3D, Compute_0, Compute_1, Copy, VideoDecode, VideoEncode, VideoProcessing 等
//
// T2-2 修复两条已证问题：
//   1. LUID→名称/显存映射**不再**依赖已死的 DXGI/COM 枚举器（旧兜底写 "GPU"），
//      改用 GpuDxgkrnlAdapters（gdi32 纯 P/Invoke + SetupDi 身份），与 D3DKMT 层共用
//      同一个 adapter 枚举出口（不建第二套名称过滤、不建第二条 COM interop 链）。
//   2. 空闲 0% 的卡**不再整卡消失**（旧 `if(cooked>0)` 只累计正利用率 ⇒ 0% 卡不进
//      perGpuUsage ⇒ 从结果里蒸发，t12 定为真 bug）。修正为：只要该 LUID 本周期已建立
//      delta 基线（有 prev 采样），即产出该卡，利用率如实可为 0。
//      （首帧无 delta 仍不产出，属设计使然——PDH 需两次采样才能算 delta，非 bug。）

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

    // LUID → COM-free 枚举归属的 adapter（含名称/显存/Kind）。替代旧依赖 DXGI/COM 的映射。
    private Dictionary<(uint, int), GpuDxgkAdapter>? _luidAdapterMap;

    private readonly object _lock = new();

    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                if (!_initAttempted) Init();
                return _available;
            }
        }
    }

    private void Init()
    {
        _initAttempted = true;
        try
        {
            _available = PerformanceCounterCategory.Exists("GPU Engine");
            // 入口诊断（t4 必做项 / t12 依据）：PDH 不吃 COM，其"归零"只能靠这条定论。
            int instanceCount = -1;
            if (_available)
            {
                try
                {
                    _category = new PerformanceCounterCategory("GPU Engine");
                    instanceCount = _category.GetInstanceNames().Length;
                }
                catch (Exception ex)
                {
                    SensorLogger.ForceLog($"[GPU-PDH] instance probe failed: {ex.GetType().Name}");
                }
            }
            SensorLogger.ForceLog(
                $"[GPU-PDH] category exists={_available} instances={(instanceCount < 0 ? "n/a" : instanceCount.ToString())}");
            if (_available)
            {
                _category ??= new PerformanceCounterCategory("GPU Engine");
                // 构建 LUID → adapter 映射（COM-free；仅可归属的物理卡）
                _luidAdapterMap = new();
                foreach (var a in GpuDxgkrnlAdapters.GetAdapters())
                    _luidAdapterMap[a.LuidKey] = a;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PDH-GPU] Init failed: {ex.Message}");
            SensorLogger.ForceLog($"[GPU-PDH] Init failed: {ex.GetType().Name}: " +
                                  $"{(ex.Message.Length <= 160 ? ex.Message : ex.Message[..160] + "…")}");
        }
    }

    /// <summary>
    /// 读取所有 GPU 的利用率 + 显存（T2-2）。空闲 0% 的卡仍产出（利用率 0）；
    /// 不可归属（虚拟/副本）的 LUID 不命名、不填显存。温度不可用（-1）。
    /// </summary>
    public List<GpuResult> ReadAll()
    {
        var results = new List<GpuResult>();
        PerformanceCounterCategory? category;
        Dictionary<(uint, int), GpuDxgkAdapter>? map;
        lock (_lock)
        {
            if (!IsAvailable || _category == null) return results;
            category = _category;
            map = _luidAdapterMap;
        }

        try
        {
            var categoryData = category!.ReadCategory();
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

            // 按可归属 LUID 分组：max cooked（含 0）+ 是否已建立 delta 基线
            var perGpuMax = new Dictionary<(uint, int), float>();
            var perGpuHasDelta = new HashSet<(uint, int)>();
            var currentNames = new HashSet<string>();

            foreach (InstanceData data in utilData.Values)
            {
                string instanceName = data.InstanceName;
                currentNames.Add(instanceName);
                var sample = data.Sample;

                var (luidLow, luidHigh) = ParseLuid(instanceName);
                if (luidLow == 0 && luidHigh == 0) continue;
                var key = (luidLow, luidHigh);

                // 仅累计可归属物理卡（不在 COM-free 身份映射里的 LUID = 虚拟/副本，跳过命名与显存）
                if (map is null || !map.ContainsKey(key))
                {
                    _prevSamples[instanceName] = sample;
                    continue;
                }

                if (_prevSamples.TryGetValue(instanceName, out var prevSample))
                {
                    float cooked = CounterSampleCalculator.ComputeCounterValue(prevSample, sample);
                    if (float.IsNaN(cooked) || cooked < 0) cooked = 0;   // 计数器复位等异常 ⇒ 保守 0，不丢弃该卡
                    perGpuHasDelta.Add(key);
                    if (!perGpuMax.TryGetValue(key, out float existing) || cooked > existing)
                        perGpuMax[key] = cooked;
                }

                _prevSamples[instanceName] = sample;
            }

            if (_prevSamples.Count > 200)
            {
                var toRemove = _prevSamples.Keys.Where(k => !currentNames.Contains(k)).ToList();
                foreach (var k in toRemove) _prevSamples.Remove(k);
            }

            foreach (var r in BuildResults(perGpuHasDelta, perGpuMax, map))
                results.Add(r);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PDH-GPU] ReadAll: {ex.Message}");
        }
        return results;
    }

    /// <summary>
    /// 纯函数（测试缝隙）：由「本周期已建立 delta 基线的 LUID 集合 + 各 LUID 的 max cooked + COM-free
    /// 身份映射」组装 GpuResult 列表。规则（T2-2）：
    ///   · 只要在 hasDelta 内即产出该卡 —— 利用率如实可为 0（修掉旧 `cooked>0` 使空闲卡整卡消失的真 bug）；
    ///   · maxByGpu 缺失该 LUID（全引擎都未算出正值）⇒ 利用率 0；
    ///   · 不在身份映射里的 LUID（虚拟/渲染节点副本）⇒ 不产出、不命名、不填显存；
    ///   · 温度不可用（-1）；显存/Kind 来自映射。
    /// </summary>
    internal static List<GpuResult> BuildResults(
        HashSet<(uint low, int high)> hasDelta,
        Dictionary<(uint low, int high), float> maxByGpu,
        IReadOnlyDictionary<(uint low, int high), GpuDxgkAdapter>? map)
    {
        var results = new List<GpuResult>();
        if (map is null) return results;
        foreach (var key in hasDelta)
        {
            if (!map.TryGetValue(key, out var adapter)) continue;
            float usage = maxByGpu.TryGetValue(key, out float u) ? u : 0f;
            if (float.IsNaN(usage) || usage < 0) usage = 0f;
            results.Add(new GpuResult(
                adapter.Name,
                Math.Min(usage, 100),
                -1,
                adapter.MemoryUsedMB,
                adapter.MemoryTotalMB,
                "PDH",
                adapter.Kind));
        }
        return results;
    }

    /// <summary>从实例名解析 LUID。格式: ..._luid_0x&lt;high&gt;_0x&lt;low&gt;_...</summary>
    private static (uint low, int high) ParseLuid(string instanceName)
    {
        // 格式: pid_0_luid_0x00000000_0x00013AB9_phys_0_eng_0_engtype_3D
        var parts = instanceName.Split('_');
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == "luid" && i + 2 < parts.Length)
            {
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
