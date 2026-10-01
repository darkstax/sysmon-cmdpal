// SysMonBroker/Host/WatchdogService.cs
//
// 看门狗线程的**判定与副作用**（从 Program.WatchdogLoop 提取，P0 盲区 1/3）。
//
// 为什么单独成文件：
//   原 WatchdogLoop 是「取时间 → 三级判定 → Environment.Exit」的混合体，判定部分
//   （启动挂起 / 周期停滞 / 健康）完全可由三个时间戳决定，却被无限循环与进程退出
//   副作用包住，只能靠读源码验证。提取 <see cref="Evaluate"/> 后，构造一个不启动线程的
//   实例即可对任意时间戳组合断言（含 120s/20s 边界）。
//
// ⚠ 硬约束（测试按名字反射 Program，见 SysMonBrokerProgramTests）：
//   本类**不在 Program 里复制任何阈值常量**。CycleTimeout / StartupTimeout / WatchdogExitCode
//   的唯一真相源仍是 Program；此处经构造参数注入，避免「两处定义同一常量」破坏
//   Program 头部那段看门狗阈值层次推导（LHM 8s + 慢主板 5s + 2s ≈ 15s → 20s 余量）的单一性。

using System.Diagnostics;
using SysMonBroker.Logging;

namespace SysMonBroker.Host;

/// <summary>看门狗对当前进程健康度的判定。</summary>
internal enum WatchdogVerdict
{
    /// <summary>心跳正常（或尚未越过任一超时）。</summary>
    Healthy,

    /// <summary>从未拍到过周期心跳（lastCycle==0），且启动已超过 StartupTimeout。</summary>
    StartupHang,

    /// <summary>拍到过心跳，但距上次心跳已超过 CycleTimeout。</summary>
    CycleStall,
}

/// <summary>
/// 看门狗：独立后台线程周期性评估主循环心跳，判定不健康即
/// <c>Flush()</c> 后 <c>Environment.Exit(WatchdogExitCode)</c>，交给计划任务重启。
/// </summary>
internal sealed class WatchdogService
{
    private const int PollIntervalMilliseconds = 5000;

    private readonly Action<string> _log;
    private readonly TimeSpan _cycleTimeout;
    private readonly TimeSpan _startupTimeout;
    private readonly int _exitCode;
    private readonly Func<long> _readLastCycleTimestamp;
    private readonly Func<long> _readStartTimestamp;

    public WatchdogService(
        Action<string> log,
        TimeSpan cycleTimeout,
        TimeSpan startupTimeout,
        int exitCode,
        Func<long> readLastCycleTimestamp,
        Func<long> readStartTimestamp)
    {
        _log = log;
        _cycleTimeout = cycleTimeout;
        _startupTimeout = startupTimeout;
        _exitCode = exitCode;
        _readLastCycleTimestamp = readLastCycleTimestamp;
        _readStartTimestamp = readStartTimestamp;
    }

    /// <summary>
    /// 纯判定：由两个心跳时间戳推出健康度（不读时钟、不产生副作用）。
    /// </summary>
    /// <remarks>
    /// ⚠ 判定顺序与短路即语义：
    /// 1. <paramref name="lastCycle"/> == 0 表示「主循环从未成功发布过一帧」（哨兵），
    ///    此时**只看启动超时**，不看周期超时——否则刚启动就会因「距时间戳 0 已远超 20s」误判停滞。
    ///    <paramref name="start"/> == 0 表示启动时间戳尚未写入（Main 早期），一律 Healthy。
    /// 2. 时间比较是**严格大于**：恰好等于阈值不触发（与门禁 WatchdogLoop_CycleTimeoutBoundary
    ///    断言的 `TimeSpan.FromSeconds(20) > timeout == false` 语义一致）。
    /// </remarks>
    public WatchdogVerdict Evaluate(long lastCycle, long start, long nowTimestamp)
    {
        if (lastCycle == 0)
        {
            if (start > 0 && Stopwatch.GetElapsedTime(start, nowTimestamp) > _startupTimeout)
                return WatchdogVerdict.StartupHang;

            return WatchdogVerdict.Healthy;
        }

        return Stopwatch.GetElapsedTime(lastCycle, nowTimestamp) > _cycleTimeout
            ? WatchdogVerdict.CycleStall
            : WatchdogVerdict.Healthy;
    }

    /// <summary>启动看门狗线程（后台线程：计划任务终止进程时不留挂线程）。</summary>
    public Thread StartThread(CancellationToken token)
    {
        var thread = new Thread(() => Loop(token))
        {
            IsBackground = true,
            Name = "BrokerWatchdog",
        };
        thread.Start();
        return thread;
    }

    /// <summary>看门狗主循环：先睡 5s 再判定；不健康即落盘 FATAL 并退出进程。</summary>
    private void Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Sleep 在判定之前（原实现顺序）：避免刚启动就评估，给主循环首帧留出时间
                Thread.Sleep(PollIntervalMilliseconds);
                long lastCycle = _readLastCycleTimestamp();
                long start = _readStartTimestamp();

                WatchdogVerdict verdict = Evaluate(lastCycle, start, Stopwatch.GetTimestamp());
                if (verdict == WatchdogVerdict.StartupHang)
                {
                    _log($"FATAL: watchdog detected startup hang (>{(int)_startupTimeout.TotalSeconds}s); exiting for task restart");
                    BrokerLogger.Flush(); // R03: Exit 前显式落盘 FATAL 行
                    Environment.Exit(_exitCode);
                }
                else if (verdict == WatchdogVerdict.CycleStall)
                {
                    _log($"FATAL: watchdog detected stalled sensor cycle (>{(int)_cycleTimeout.TotalSeconds}s); exiting for task restart");
                    BrokerLogger.Flush(); // R03: Exit 前显式落盘 FATAL 行
                    Environment.Exit(_exitCode);
                }
            }
            catch (Exception ex)
            {
                // A-F12: 看门狗自身异常不得静默死亡（否则防护失效且无日志）；
                // 记录后按停滞处理退出，交给计划任务重启。
                try
                {
                    _log($"FATAL: watchdog loop failure: {ex.Message}");
                    BrokerLogger.Flush();
                }
                catch { }
                Environment.Exit(_exitCode);
            }
        }
    }
}
