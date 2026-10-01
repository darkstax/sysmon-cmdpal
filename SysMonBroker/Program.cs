// SysMonBroker — LHM thin-shell + Shared Memory IPC + Admin Pipe (v2.5)
// Runs as admin. Provides sensor data via SharedMemory v2 to SysMonCmdPal plugin,
// and an admin-privilege proxy named pipe for btop4win (protocol: ai-code/btop4win-broker-ipc.md).
//
// Usage:
//   SysMonBroker.exe                — normal mode (LHM + SHM + AdminPipe)
//   SysMonBroker.exe --devmode-on   — enable DevMode at runtime (dev build + marker required)
//   SysMonBroker.exe --devmode-off  — disable DevMode at runtime
//
// v2.5: Adds named-pipe admin proxy (AUTH/PING/TERMINATE/SERVICE_CONTROL) +
//       re-adds DevMode gate (compile-time path + marker + runtime flag).
// v2.4: Adds atomic SMX1 commits, instance identity, and a writer lease.
// v2.3: Removed COM Local Server (btop4win no longer reads it).
//       Removed JSON snapshot (only btop4win consumed it).
//       Removed DevMode verifier + hash registration.
//
// 本文件是**进程契约的唯一真相源**：退出码、看门狗阈值、心跳字段都定义在此，
// 由 Host/BrokerHost、Host/WatchdogService、Host/DevModeService 经构造参数消费，
// 它们不复制这些常量。Main 只做「崩溃兜底 → 参数分派 → 启动看门狗 → 交给 BrokerHost」。
//
// ⚠ 下方 9 个成员被 SysMonBrokerProgramTests 按名字反射（Static|NonPublic），
//   改名/改类型/改可见性/搬进别的类都会直接打红 8 条用例：
//   ShmTagName / CycleTimeout / StartupTimeout / MaxConsecutiveCycleErrors /
//   WriterConflictExitCode / WatchdogExitCode / s_lastCycleTimestamp / s_startTimestamp / DevModeToggle。

using System.Threading;
using System.Diagnostics;
using SysMonBroker.Host;
using SysMonBroker.IPC;
using SysMonBroker.Logging;

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

        Log($"=== SysMonBroker v2.5 starting (SHM + AdminPipe mode) ===");

        s_startTimestamp = Stopwatch.GetTimestamp();

        // --devmode-on / --devmode-off: 运行时 DevMode 开关(dev 构建 + marker 才能设置)
        // ⚠ 此分派必须早于看门狗启动：DevMode 是一次性 CLI 开关，若先起看门狗，
        //    它会因永无心跳而在 120s 后自杀退出——把开关变成定时炸弹。
        if (args.Contains("--devmode-on") || args.Contains("--devmode-off"))
            return DevModeToggle(args.Contains("--devmode-on"));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        new WatchdogService(
            Log,
            CycleTimeout,
            StartupTimeout,
            WatchdogExitCode,
            () => Interlocked.Read(ref s_lastCycleTimestamp),
            () => Interlocked.Read(ref s_startTimestamp))
            .StartThread(cts.Token);

        return new BrokerHost(
            Log,
            ShmTagName,
            () => Interlocked.Exchange(ref s_lastCycleTimestamp, Stopwatch.GetTimestamp()),
            MaxConsecutiveCycleErrors,
            WriterConflictExitCode)
            .Run(cts.Token);
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

    // --devmode-on / --devmode-off: 检查 dev build + marker, 然后创建/删除 .devmode_on 文件。
    // 用文件 flag 而非内存变量, 使所有 broker 进程共享状态(与旧版 COM 时代设计一致)。
    // 保留在 Program 上（测试反射其存在与签名）；主体已提取到 DevModeService。
    static int DevModeToggle(bool enable) => DevModeService.Toggle(enable, Log);
}
