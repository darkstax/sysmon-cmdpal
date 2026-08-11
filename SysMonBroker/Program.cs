// SysMonBroker — LHM thin-shell + Shared Memory IPC (v2.4)
// Runs as admin. Provides sensor data via SharedMemory v2 to SysMonCmdPal plugin.
//
// Usage:
//   SysMonBroker.exe   — normal mode (LHM + SHM)
//
// v2.4: Adds atomic SMX1 commits, instance identity, and a writer lease.
// v2.3: Removed COM Local Server (btop4win no longer reads it).
//       Removed JSON snapshot (only btop4win consumed it).
//       Removed DevMode verifier + hash registration.

using System.Threading;
using System.Diagnostics;
using SysMonBroker.IPC;
using SysMonBroker.Logging;
using SysMonBroker.Sensors;

namespace SysMonBroker;

internal static class Program
{
    private const int WriterConflictExitCode = 2;
    private const int WatchdogExitCode = 3;
    private const int MaxConsecutiveCycleErrors = 30;
    // 看门狗阈值层次（审计 A-F3/E-F6）：LHM 单硬件更新超时 HardwareUpdateTimeout=8s，
    // 超时即 break 跳过剩余硬件 → 单轮最坏 ≈ 8s（超时）+ 正常硬件更新（慢主板可达 5s）+ 2s 周期 ≈ 15s。
    // CycleTimeout=20s 为其留出余量，避免慢系统+单 wedged 硬件组合被误杀；
    // 真实停滞（进程 wedged >20s）与启动挂起（>120s）仍由看门狗兜底重启。
    private static readonly TimeSpan CycleTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(120);
    private static long s_lastCycleTimestamp;
    private static long s_startTimestamp;

    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log($"FATAL: {ex?.Message}\nFATAL: {ex?.StackTrace}");
            // 进程即将终止：显式落盘，确保 FATAL 行不丢（R02）
            BrokerLogger.Flush();
            Thread.Sleep(500);
        };

        Log($"=== SysMonBroker v2.4 starting (standalone SHM mode) ===");

        s_startTimestamp = Stopwatch.GetTimestamp();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var watchdog = new Thread(() => WatchdogLoop(cts.Token))
        {
            IsBackground = true,
            Name = "BrokerWatchdog",
        };
        watchdog.Start();

        SensorCollector? collector = null;
        BrokerSharedMemory? shm = null;

        try
        {
            Log("Creating SharedMemory v2 + SMX1...");
            shm = new BrokerSharedMemory();
            Log($"SharedMemory: {BrokerSharedMemory.MapName} (size={BrokerSharedMemory.MapSize}, " +
                $"instance={shm.InstanceId:X16}, writerLease={BrokerSharedMemory.WriterMutexName})");

            Log("Creating SensorCollector (LHM Computer.Open)...");
            collector = new SensorCollector();
            Log($"SensorCollector ready. PawnIO: {(collector.PawnIoInstalled ? "installed" : "not installed — user-mode sensors only")}");

            int cycle = 0;
            int lastGpuCount = -1;
            int consecutiveErrors = 0;

            while (!cts.Token.IsCancellationRequested)
            {
                cycle++;
                try
                {
                    var (cpuTemp, cpuSource, gpus, sensors) = collector.ReadAll();

                    shm.Write(cpuTemp, cpuSource, gpus, sensors);
                    Interlocked.Exchange(ref s_lastCycleTimestamp, Stopwatch.GetTimestamp());

                    // 周期成功：清零连续错误计数（R06）
                    consecutiveErrors = 0;

                    if (lastGpuCount >= 0 && gpus.Count != lastGpuCount)
                        Log($"cycle={cycle} GPU count changed: {lastGpuCount} -> {gpus.Count}");
                    lastGpuCount = gpus.Count;

                    if (cycle <= 3 || cycle % 30 == 1)
                    {
                        Log($"cycle={cycle} cpu={cpuTemp:F1}°C [{cpuSource}] gpus={gpus.Count} " +
                            $"sensors={sensors.Count}");
                        if (cycle <= 2)
                        {
                            foreach (var g in gpus)
                                Log($"  gpu: {g.Name} temp={g.TempCelsius:F1}°C load={g.UsagePercent:F0}%");
                            var byCategory = sensors.GroupBy(s => s.Tag)
                                .OrderBy(g => g.Key)
                                .Select(g => $"{ShmTagName(g.Key)}:{g.Count()}");
                            Log($"  sensors: {string.Join(", ", byCategory)}");
                        }
                    }
                }
                catch (BrokerWriterConflictException ex)
                {
                    Log($"FATAL: {ex.Message} Stopping to avoid multiple shared-memory writers.");
                    return WriterConflictExitCode;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 连续错误降频记录 + 持续失败主动退出走计划任务重启（R06）
                    consecutiveErrors++;
                    if (consecutiveErrors == 1 || consecutiveErrors % 10 == 0)
                        Log($"Cycle error ({consecutiveErrors} consecutive): {ex.Message}");
                    if (consecutiveErrors >= MaxConsecutiveCycleErrors)
                    {
                        Log($"FATAL: {consecutiveErrors} consecutive cycle errors; exiting for task restart");
                        return 1;
                    }
                }

                Thread.Sleep(2000);
            }
        }
        catch (BrokerAlreadyRunningException ex)
        {
            // 已有实例持有写者租约属正常状态（R04）。计划任务无法按退出码区分
            // 重启策略（RestartCount 对所有非零退出码一视同仁），返回 0 可避免
            // 无意义的重启尝试并节省 RestartCount 配额。
            Log($"{ex.Message} (another instance is serving; exiting normally)");
            return 0;
        }
        catch (BrokerWriterConflictException ex)
        {
            Log($"FATAL: {ex.Message}");
            return WriterConflictExitCode;
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
        finally
        {
            Log("Shutting down...");
            // 每个 Dispose 独立容错：任一失败不得中断 stopped 日志 / Flush / 退出码语义（R01）
            try { shm?.Dispose(); }
            catch (Exception ex) { Log($"WARN: SharedMemory dispose failed: {ex.Message}"); }
            try { collector?.Dispose(); }
            catch (Exception ex) { Log($"WARN: SensorCollector dispose failed: {ex.Message}"); }
            Log("=== SysMonBroker stopped ===");
            BrokerLogger.Flush();
        }

        return 0;
    }

    private static void WatchdogLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Thread.Sleep(5000);
                long lastCycle = Interlocked.Read(ref s_lastCycleTimestamp);
                long start = Interlocked.Read(ref s_startTimestamp);

                if (lastCycle == 0)
                {
                    if (start > 0 && Stopwatch.GetElapsedTime(start) > StartupTimeout)
                    {
                        Log($"FATAL: watchdog detected startup hang (>{(int)StartupTimeout.TotalSeconds}s); exiting for task restart");
                        BrokerLogger.Flush(); // R03: Exit 前显式落盘 FATAL 行
                        Environment.Exit(WatchdogExitCode);
                    }
                    continue;
                }

                if (Stopwatch.GetElapsedTime(lastCycle) > CycleTimeout)
                {
                    Log($"FATAL: watchdog detected stalled sensor cycle (>{(int)CycleTimeout.TotalSeconds}s); exiting for task restart");
                    BrokerLogger.Flush(); // R03: Exit 前显式落盘 FATAL 行
                    Environment.Exit(WatchdogExitCode);
                }
            }
            catch (Exception ex)
            {
                // A-F12: 看门狗自身异常不得静默死亡（否则防护失效且无日志）；
                // 记录后按停滞处理退出，交给计划任务重启。
                try
                {
                    Log($"FATAL: watchdog loop failure: {ex.Message}");
                    BrokerLogger.Flush();
                }
                catch { }
                Environment.Exit(WatchdogExitCode);
            }
        }
    }

    private static string ShmTagName(int tag) => tag switch
    {
        0 => "CpuTemp", 1 => "CpuLoad", 2 => "CpuClock", 3 => "CpuPower", 4 => "CpuVoltage",
        5 => "GpuTemp", 6 => "GpuLoad", 7 => "GpuClock", 8 => "GpuPower",
        9 => "GpuMemory", 10 => "GpuFan", 11 => "GpuVoltage",
        12 => "MbTemp", 13 => "MbFan", 14 => "MbVoltage",
        15 => "StorageTemp", 16 => "StorageLoad",
        _ => $"Unknown({tag})",
    };

    static void Log(string msg) => BrokerLogger.Log(msg);
}
