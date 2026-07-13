// Copyright (c) 2026 SysMonCmdPal
// D3DKMT GPU 利用率读取器 — 用户态，不需要管理员，不需要第三方工具
// 通过 D3DKMT API 读取 per-engine RunningTime，delta 计算 GPU 利用率
// 这是 Task Manager / LibreHardwareMonitor / Process Hacker 使用的同一套 API
//
// 调用链: DXGI EnumAdapters1 → LUID → D3DKMTOpenAdapterFromLuid → D3DKMTQueryStatistics(NODE)
//         → RunningTime (100ns ticks) → delta / wallClock = 利用率 %

using System.Runtime.InteropServices;
using System.Diagnostics;

namespace SysMonCmdPal;

internal sealed class D3dkmtGpuReader : IDisposable
{
    public static D3dkmtGpuReader Instance { get; } = new();
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMLUID
    {
        public LUID AdapterLuid;
        public IntPtr hAdapter;
        public uint WDDMVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public IntPtr hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYSTATISTICS
    {
        public int Type;            // D3DKMT_QUERYSTATISTICS_TYPE (5 = NODE)
        public LUID AdapterLuid;
        public IntPtr ProcessHandle; // 0 = global
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 800)]
        public byte[] QueryResult;   // union, 前 8 字节 = RunningTime (ulong)
        public uint NodeId;          // QueryElement.NodeId
    }

    private const int D3DKMT_QUERYSTATISTICS_NODE = 5;
    private const int MaxNodes = 16;

    [DllImport("gdi32.dll")]
    private static extern uint D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID Arg);

    [DllImport("gdi32.dll")]
    private static extern uint D3DKMTQueryStatistics(ref D3DKMT_QUERYSTATISTICS Arg);

    [DllImport("gdi32.dll")]
    private static extern uint D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER Arg);

    // 每个 adapter 的上一次采样状态
    private sealed class AdapterState
    {
        public LUID Luid;
        public string Name = "";
        public ulong[] PrevRunningTimes = new ulong[MaxNodes];
        public long PrevTimestamp;
        public bool HasPrevious;
        public int NodeCount;
    }

    private List<AdapterState>? _adapters;
    private bool _initAttempted;

    private void Init()
    {
        _initAttempted = true;
        try
        {
            var dxgiAdapters = GpuAdapterEnumerator.GetAdapters();
            _adapters = new List<AdapterState>();
            foreach (var a in dxgiAdapters)
            {
                var state = new AdapterState
                {
                    Luid = new LUID { LowPart = a.LuidLow, HighPart = a.LuidHigh },
                    Name = a.Name,
                };

                // 探测 node 数量。空闲 GPU 的 RunningTime 可能为 0，
                // 因此以 QueryStatistics 成功与否判断 node 是否存在。
                for (int n = 0; n < MaxNodes; n++)
                {
                    var stat = MakeQuery(stat: default, state.Luid, n);
                    if (D3DKMTQueryStatistics(ref stat) == 0)
                        state.NodeCount = n + 1;
                    else break;
                }
                if (state.NodeCount > 0)
                    _adapters.Add(state);
            }
            Debug.WriteLine($"[D3DKMT] Initialized: {_adapters.Count} adapters");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[D3DKMT] Init failed: {ex.Message}");
        }
    }

    public bool IsAvailable
    {
        get { if (!_initAttempted) Init(); return _adapters != null && _adapters.Count > 0; }
    }

    /// <summary>读取所有 GPU 的利用率。返回 GpuResult 列表（仅 UsagePercent，温度/显存=-1/0）</summary>
    public List<GpuResult> ReadAll()
    {
        var results = new List<GpuResult>();
        if (!IsAvailable || _adapters == null) return results;

        foreach (var state in _adapters)
        {
            try
            {
                // 采样当前 RunningTime
                var current = new ulong[state.NodeCount];
                var valid = new bool[state.NodeCount];
                for (int n = 0; n < state.NodeCount; n++)
                {
                    var stat = MakeQuery(default, state.Luid, n);
                    if (D3DKMTQueryStatistics(ref stat) == 0)
                    {
                        current[n] = BitConverter.ToUInt64(stat.QueryResult, 0);
                        valid[n] = true;
                    }
                }
                long nowTicks = Stopwatch.GetTimestamp();

                double usage = -1;
                bool resetBaseline = false;
                if (state.HasPrevious)
                {
                    double wallMs = (nowTicks - state.PrevTimestamp) * 1000.0 / Stopwatch.Frequency;
                    // 取所有 engine 中的最大利用率 (Task Manager 行为)
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

                // 保存当前采样供下次 delta 计算
                Array.Copy(current, state.PrevRunningTimes, state.NodeCount);
                state.PrevTimestamp = nowTicks;
                state.HasPrevious = true;

                results.Add(new GpuResult(
                    state.Name, usage, -1, 0, 0, "D3DKMT"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[D3DKMT] ReadAll adapter {state.Name}: {ex.Message}");
            }
        }
        return results;
    }

    private static D3DKMT_QUERYSTATISTICS MakeQuery(D3DKMT_QUERYSTATISTICS stat, LUID luid, int nodeId)
    {
        stat.Type = D3DKMT_QUERYSTATISTICS_NODE;
        stat.AdapterLuid = luid;
        stat.ProcessHandle = IntPtr.Zero;
        stat.QueryResult = new byte[800];
        stat.NodeId = (uint)nodeId;
        return stat;
    }

    public void Dispose()
    {
        // D3DKMT adapters are opened/closed per-query, nothing to dispose
    }
}
