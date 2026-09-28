// Copyright (c) 2026 SysMonCmdPal
// D3DKMT GPU 读取器 — 用户态，不需要管理员，不需要第三方工具，**零 COM**。
// 通过 gdi32!D3DKMT 读取 per-engine RunningTime，delta 计算 GPU 利用率；
// 并填 GPU 显存（T2-2：本地非 aperture 显存段）。
//
// adapter 来源：GpuDxgkrnlAdapters（gdi32 纯 P/Invoke 枚举 + SetupDi 身份归属），
//   **不再**依赖 DXGI/COM 枚举器（出货 trim 产物 BuiltInCOM 被禁，DXGI 恒 0 adapter）。
//   调用链: D3DKMTEnumAdapters2 → LUID → (身份 JOIN) → D3DKMTQueryStatistics(NODE) RunningTime。
//   结构体布局与偏移修正详见 GpuDxgkrnlAdapters 文件头（SDK d3dkmthk.h 权威）。

using System.Runtime.InteropServices;
using System.Diagnostics;

namespace SysMonCmdPal;

internal sealed class D3dkmtGpuReader : IDisposable
{
    public static D3dkmtGpuReader Instance { get; } = new();

    private const int MaxNodes = 16;

    // 每个 adapter 的上一次采样状态
    private sealed class AdapterState
    {
        public uint LuidLow;
        public int LuidHigh;
        public string Name = "";
        public GpuKind Kind;
        public double MemUsedMB;
        public double MemTotalMB;
        public ulong[] PrevRunningTimes = new ulong[MaxNodes];
        public long PrevTimestamp;
        public bool HasPrevious;
        public int NodeCount;
        public (uint, int) LuidKey => (LuidLow, LuidHigh);
    }

    private List<AdapterState>? _adapters;
    private bool _initAttempted;
    private readonly object _initLock = new();

    private void Init()
    {
        _initAttempted = true;
        try
        {
            var enumerated = GpuDxgkrnlAdapters.GetAdapters();   // COM-free + SetupDi 身份
            _adapters = new List<AdapterState>(enumerated.Count);
            foreach (var a in enumerated)
            {
                var state = new AdapterState
                {
                    LuidLow = a.LuidLow,
                    LuidHigh = a.LuidHigh,
                    Name = a.Name,
                    Kind = a.Kind,
                    MemUsedMB = a.MemoryUsedMB,
                    MemTotalMB = a.MemoryTotalMB,
                };

                // 探测 node 数量。空闲 GPU 的 RunningTime 可能为 0，
                // 因此以 QueryStatistics 成功与否判断 node 是否存在。
                for (int n = 0; n < MaxNodes; n++)
                {
                    if (GpuDxgkrnlAdapters.TryReadNodeRunningTime(state.LuidLow, state.LuidHigh, n, out _))
                        state.NodeCount = n + 1;
                    else break;
                }
                if (state.NodeCount > 0)
                    _adapters.Add(state);
            }
            Debug.WriteLine($"[D3DKMT] Initialized: {_adapters.Count} adapters");
            // 诊断（T2-1 必做项）：区分「枚举 0 adapter」与「枚举 N 但 node-probe 全失败」。
            SensorLogger.ForceLog(
                $"[GPU-D3DKMT] adapters={enumerated.Count} after node-probe={_adapters.Count}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3DKMT] Init failed: {ex.Message}");
            SensorLogger.ForceLog($"[GPU-D3DKMT] Init failed: {ex.GetType().Name}: {Trim(ex.Message)}");
        }
    }

    public bool IsAvailable
    {
        get
        {
            lock (_initLock)
            {
                if (!_initAttempted) Init();
                return _adapters != null && _adapters.Count > 0;
            }
        }
    }

    private static string Trim(string s) => s.Length <= 160 ? s : s[..160] + "…";

    /// <summary>
    /// 读取所有 GPU 的利用率 + 显存（T2-2）。温度仍不可用（-1）。
    /// 集显显存如实为 0（共享系统内存，无独立 VRAM）。
    /// </summary>
    public List<GpuResult> ReadAll()
    {
        var results = new List<GpuResult>();
        List<AdapterState>? adapters;
        lock (_initLock)
        {
            if (!IsAvailable || _adapters == null) return results;
            adapters = _adapters;
        }

        // 显存每次刷新：Init 只跑一次，AdapterState 里的内存快照若不重取会永久冻结在首帧。
        // 重取 COM-free 枚举（内部 30s 缓存）刷新各 adapter 的显存/Kind；LUID/Name 稳定不变。
        var freshMem = new Dictionary<(uint, int), GpuDxgkAdapter>();
        try
        {
            foreach (var a in GpuDxgkrnlAdapters.GetAdapters())
                freshMem[a.LuidKey] = a;
        }
        catch { /* 刷新失败则沿用 Init 快照，不阻断利用率读取 */ }

        foreach (var state in adapters)
        {
            try
            {
                if (freshMem.TryGetValue(state.LuidKey, out var fresh))
                {
                    state.MemUsedMB = fresh.MemoryUsedMB;
                    state.MemTotalMB = fresh.MemoryTotalMB;
                    state.Kind = fresh.Kind;
                }

                var current = new ulong[state.NodeCount];
                var valid = new bool[state.NodeCount];
                for (int n = 0; n < state.NodeCount; n++)
                {
                    if (GpuDxgkrnlAdapters.TryReadNodeRunningTime(state.LuidLow, state.LuidHigh, n, out ulong rt))
                    {
                        current[n] = rt;
                        valid[n] = true;
                    }
                }
                long nowTicks = Stopwatch.GetTimestamp();

                double usage = -1;
                bool resetBaseline = false;
                if (state.HasPrevious)
                {
                    double wallMs = (nowTicks - state.PrevTimestamp) * 1000.0 / Stopwatch.Frequency;
                    double maxUtil = 0;
                    for (int n = 0; n < state.NodeCount; n++)
                    {
                        if (!valid[n]) continue;
                        if (current[n] < state.PrevRunningTimes[n])
                        {
                            resetBaseline = true;
                            continue;
                        }
                        double busyMs = (double)(current[n] - state.PrevRunningTimes[n]) / 10000.0;
                        double util = wallMs > 0 ? busyMs / wallMs * 100.0 : 0;
                        if (util > maxUtil) maxUtil = util;
                    }
                    usage = resetBaseline ? -1 : Math.Min(maxUtil, 100);
                }

                Array.Copy(current, state.PrevRunningTimes, state.NodeCount);
                state.PrevTimestamp = nowTicks;
                state.HasPrevious = true;

                results.Add(new GpuResult(
                    state.Name, usage, -1,
                    state.MemUsedMB, state.MemTotalMB, "D3DKMT", state.Kind));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[D3DKMT] ReadAll adapter {state.Name}: {ex.Message}");
            }
        }
        return results;
    }

    public void Dispose()
    {
        // D3DKMT adapters are opened/closed per-query, nothing to dispose
    }
}
