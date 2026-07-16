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
using SysMonBroker.IPC;
using SysMonBroker.Logging;
using SysMonBroker.Sensors;

namespace SysMonBroker;

internal static class Program
{
    private const int WriterConflictExitCode = 2;

    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log($"FATAL: {ex?.Message}\nFATAL: {ex?.StackTrace}");
            Thread.Sleep(500);
        };

        Log($"=== SysMonBroker v2.4 starting (standalone SHM mode) ===");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

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

            while (!cts.Token.IsCancellationRequested)
            {
                cycle++;
                try
                {
                    var (cpuTemp, cpuSource, gpus, sensors) = collector.ReadAll();

                    shm.Write(cpuTemp, cpuSource, gpus, sensors);

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
                    Log($"Cycle error: {ex.Message}");
                }

                Thread.Sleep(2000);
            }
        }
        catch (BrokerAlreadyRunningException ex)
        {
            Log($"FATAL: {ex.Message}");
            return WriterConflictExitCode;
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
            shm?.Dispose();
            collector?.Dispose();
            Log("=== SysMonBroker stopped ===");
            BrokerLogger.Flush();
        }

        return 0;
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
