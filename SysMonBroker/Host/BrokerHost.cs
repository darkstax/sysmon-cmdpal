// SysMonBroker/Host/BrokerHost.cs
//
// 采集-发布主循环的宿主（从 Program.Main 提取，P0 盲区 1/3）。
//
// 为什么单独成文件：
//   原 Main 的 163 行里缠着 5 件事（崩溃兜底 / DevMode 分派 / 组件装配 / 主循环 / 拆解与退出码），
//   其中「装配 + 循环 + 拆解」三件构成一条完整的进程生命周期，与「参数分派」「崩溃兜底」无关。
//   本类承接这三件，使 Main 退化为瘦入口。
//
// 可测性收益（本次拆分的真正价值）：
//   原来缠在循环里的「连续错误计数 → 降频日志 → 自杀退出」与「写错误分轨计数」是纯算术决策，
//   却只能靠读 163 行源码确认；现在它们是 <see cref="ShouldLogConsecutive"/> /
//   <see cref="ShouldAbortAfterConsecutiveErrors"/> / <see cref="ClassifyWriteError"/>
//   等 internal static 纯函数，可对任意计数直接断言。
//
// ⚠ 阈值与退出码**不在本类定义**：MaxConsecutiveCycleErrors /
//   WriterConflictExitCode 的唯一真相源仍是 Program，经构造参数注入。
//   两条退出码语义尤其反直觉，必须原样保留：
//     · BrokerAlreadyRunningException → **0**（R04）。计划任务对所有非零退出码一视同仁，
//       返 0 可避免无意义重启并节省 RestartCount 配额。**不要"修正"成非零**。
//     · BrokerWriterConflictException → 2（真双写者，必须立即停止以免内存映像互相踩踏）。

using System.Diagnostics;
using SysMonBroker.IPC;
using SysMonBroker.Logging;
using SysMonBroker.Sensors;

namespace SysMonBroker.Host;

/// <summary>写路径异常的分类（决定「致命退出 / 传播 / 计数后继续」三种处理）。</summary>
internal enum WriteErrorKind
{
    /// <summary>真双写者：致命，必须立即停止（退出码 2），不得计入自杀统计。</summary>
    Conflict,

    /// <summary>取消信号：不外理，原样传播（保持原实现的传播路径）。</summary>
    Cancelled,

    /// <summary>普通写失败：独立计数 + 限频日志 + 睡 2s 后进入下一周期（rebase 自愈）。</summary>
    Transient,
}

/// <summary>组件装配 + 采集-发布主循环 + 拆解与退出码。</summary>
internal sealed class BrokerHost
{
    private const int CycleIntervalMilliseconds = 2000;

    private readonly Action<string> _log;
    private readonly Func<int, string> _shmTagName;
    private readonly Action _markHeartbeat;
    private readonly int _maxConsecutiveCycleErrors;
    private readonly int _writerConflictExitCode;

    public BrokerHost(
        Action<string> log,
        Func<int, string> shmTagName,
        Action markHeartbeat,
        int maxConsecutiveCycleErrors,
        int writerConflictExitCode)
    {
        _log = log;
        _shmTagName = shmTagName;
        _markHeartbeat = markHeartbeat;
        _maxConsecutiveCycleErrors = maxConsecutiveCycleErrors;
        _writerConflictExitCode = writerConflictExitCode;
    }

    // ==================== 纯决策（可脱离真实组件直接测试） ====================

    /// <summary>
    /// 写路径异常分类。三个判据的顺序与 when 过滤器等价：
    /// Conflict 优先于 Cancelled 优先于 Transient。
    /// </summary>
    /// <remarks>
    /// P0-3：<c>shm.Write</c> 的普通异常（非 conflict）由 <see cref="BrokerSharedMemory"/>
    /// 下一周期入口的 rebase 自愈消化，**不得**计入 MaxConsecutiveCycleErrors 自杀统计；
    /// BrokerWriterConflictException 原样上抛，真双写者的退出码 2 防线不削弱。
    /// </remarks>
    public static WriteErrorKind ClassifyWriteError(Exception error) => error switch
    {
        BrokerWriterConflictException => WriteErrorKind.Conflict,
        OperationCanceledException => WriteErrorKind.Cancelled,
        _ => WriteErrorKind.Transient,
    };

    /// <summary>限频日志判据：首次 + 每 10 次（避免持续故障刷屏）。</summary>
    public static bool ShouldLogConsecutive(int consecutiveCount) =>
        consecutiveCount == 1 || consecutiveCount % 10 == 0;

    /// <summary>自杀判据：连续采集错误达上限即退出，交给计划任务重启（R06）。</summary>
    public static bool ShouldAbortAfterConsecutiveErrors(int consecutiveErrors, int max) =>
        consecutiveErrors >= max;

    /// <summary>采样日志判据：前 3 周期 + 此后每 30 周期一次。</summary>
    public static bool ShouldLogSample(int cycle) => cycle <= 3 || cycle % 30 == 1;

    // ==================== 进程生命周期 ====================

    /// <summary>
    /// 装配组件 → 跑主循环 → 拆解，返回进程退出码。本方法内部 finally 兜底，不向外抛。
    /// </summary>
    public int Run(CancellationToken token)
    {
        SensorCollector? collector = null;
        BrokerSharedMemory? shm = null;
        BrokerAdminPipeServer? adminPipe = null;

        try
        {
            // 组件创建顺序 AdminPipe → SHM → Collector（各自的日志行顺序也是契约）
            _log("Creating AdminPipe server...");
            adminPipe = new BrokerAdminPipeServer();
            adminPipe.Start();

            _log("Creating SharedMemory v2 + SMX1...");
            shm = new BrokerSharedMemory();
            _log($"SharedMemory: {BrokerSharedMemory.MapName} (size={BrokerSharedMemory.MapSize}, " +
                $"instance={shm.InstanceId:X16}, writerLease={BrokerSharedMemory.WriterMutexName})");

            _log("Creating SensorCollector (LHM Computer.Open)...");
            collector = new SensorCollector();
            _log($"SensorCollector ready. PawnIo: {(collector.PawnIoInstalled ? "installed" : "not installed — user-mode sensors only")}");

            return RunCycles(collector, shm, token);
        }
        catch (BrokerAlreadyRunningException ex)
        {
            // 已有实例持有写者租约属正常状态（R04）。计划任务无法按退出码区分
            // 重启策略（RestartCount 对所有非零退出码一视同仁），返回 0 可避免
            // 无意义的重启尝试并节省 RestartCount 配额。
            _log($"{ex.Message} (another instance is serving; exiting normally)");
            return 0;
        }
        catch (BrokerWriterConflictException ex)
        {
            _log($"FATAL: {ex.Message}");
            return _writerConflictExitCode;
        }
        catch (Exception ex)
        {
            _log($"FATAL: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
        finally
        {
            Cleanup(adminPipe, shm, collector);
        }
    }

    /// <summary>采集-发布主循环。返回退出码；正常取消时返回 0。</summary>
    private int RunCycles(SensorCollector collector, BrokerSharedMemory shm, CancellationToken token)
    {
        int cycle = 0;
        int lastGpuCount = -1;
        int consecutiveErrors = 0;
        // P0-3 异常分类：写路径异常独立计数，不并入采集错误的
        // MaxConsecutiveCycleErrors 自杀统计（见 ClassifyWriteError 注释）。
        int consecutiveWriteErrors = 0;

        while (!token.IsCancellationRequested)
        {
            cycle++;
            try
            {
                var (cpuTemp, cpuSource, gpus, sensors) = collector.ReadAll();

                try
                {
                    shm.Write(cpuTemp, cpuSource, gpus, sensors);
                }
                catch (Exception writeEx) when (ClassifyWriteError(writeEx) == WriteErrorKind.Transient)
                {
                    consecutiveWriteErrors++;
                    if (ShouldLogConsecutive(consecutiveWriteErrors))
                        _log($"SHM write error ({consecutiveWriteErrors} consecutive; " +
                            $"rebasing next cycle): {writeEx.Message}");
                    // 睡 2s 后 continue ⇒ 跳过循环底部的第二次 Sleep，总周期仍是 2s。
                    // 改成 fallthrough 会变成 4s，白白吃掉一半看门狗余量。
                    Thread.Sleep(CycleIntervalMilliseconds);
                    continue;
                }

                // 顺序与原实现一致：先清零写错误计数，再刷新心跳
                consecutiveWriteErrors = 0;
                _markHeartbeat();

                // 周期成功：清零连续错误计数（R06）
                consecutiveErrors = 0;

                if (lastGpuCount >= 0 && gpus.Count != lastGpuCount)
                    _log($"cycle={cycle} GPU count changed: {lastGpuCount} -> {gpus.Count}");
                lastGpuCount = gpus.Count;

                if (ShouldLogSample(cycle))
                    LogSample(cycle, cpuTemp, cpuSource, gpus, sensors);
            }
            catch (BrokerWriterConflictException ex)
            {
                // Conflict 在循环内即 return，保持「立即停止避免双写者」语义
                _log($"FATAL: {ex.Message} Stopping to avoid multiple shared-memory writers.");
                return _writerConflictExitCode;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 连续错误降频记录 + 持续失败主动退出走计划任务重启（R06）
                consecutiveErrors++;
                if (ShouldLogConsecutive(consecutiveErrors))
                    _log($"Cycle error ({consecutiveErrors} consecutive): {ex.Message}");
                if (ShouldAbortAfterConsecutiveErrors(consecutiveErrors, _maxConsecutiveCycleErrors))
                {
                    _log($"FATAL: {consecutiveErrors} consecutive cycle errors; exiting for task restart");
                    return 1;
                }
            }

            Thread.Sleep(CycleIntervalMilliseconds);
        }

        return 0;
    }

    /// <summary>周期采样日志（前 3 周期记录汇总，前 2 周期追加 GPU/传感器分类明细）。</summary>
    private void LogSample(
        int cycle, double cpuTemp, string cpuSource,
        List<GpuReading> gpus, List<SensorEntry> sensors)
    {
        _log($"cycle={cycle} cpu={cpuTemp:F1}°C [{cpuSource}] gpus={gpus.Count} " +
            $"sensors={sensors.Count}");
        if (cycle > 2)
            return;

        foreach (var g in gpus)
            _log($"  gpu: {g.Name} temp={g.TempCelsius:F1}°C load={g.UsagePercent:F0}%");

        var byCategory = sensors.GroupBy(s => s.Tag)
            .OrderBy(g => g.Key)
            .Select(g => $"{_shmTagName(g.Key)}:{g.Count()}");
        _log($"  sensors: {string.Join(", ", byCategory)}");
    }

    /// <summary>
    /// 拆解：三个 Dispose 各自独立容错（R01：任一失败不得中断 stopped 日志 / Flush / 退出码语义）。
    /// 顺序 AdminPipe.Stop → shm.Dispose → collector.Dispose。
    /// </summary>
    private void Cleanup(
        BrokerAdminPipeServer? adminPipe, BrokerSharedMemory? shm, SensorCollector? collector)
    {
        _log("Shutting down...");
        try { adminPipe?.Stop(); }
        catch (Exception ex) { _log($"WARN: AdminPipe stop failed: {ex.Message}"); }
        try { shm?.Dispose(); }
        catch (Exception ex) { _log($"WARN: SharedMemory dispose failed: {ex.Message}"); }
        try { collector?.Dispose(); }
        catch (Exception ex) { _log($"WARN: SensorCollector dispose failed: {ex.Message}"); }
        _log("=== SysMonBroker stopped ===");
        BrokerLogger.Flush();
    }
}
