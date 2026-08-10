// SysMonBroker/Sensors/SensorCollector.cs
// LHM thin-shell: collects ALL sensors from LibreHardwareMonitor
// No custom ring-0 code — LHM handles PawnIO internally.

using LibreHardwareMonitor.Hardware;
using SysMonBroker.IPC;

namespace SysMonBroker.Sensors;

/// <summary>GPU reading snapshot from LHM</summary>
public sealed record GpuReading(string Name, double TempCelsius, double UsagePercent,
    double MemUsedMB, double MemTotalMB);

/// <summary>
/// Thin wrapper around LibreHardwareMonitorLib.
/// Opens hardware on construction; polls sensors via Update().
/// Thread-safety: NOT safe — all calls must be from a single thread.
/// </summary>
public sealed partial class SensorCollector : IDisposable
{
    private readonly Computer _computer;
    private readonly string _cpuSource;

    private readonly object _hardwareLock = new();
    private readonly HashSet<IHardware> _suspendedHardware = new();
    private static readonly TimeSpan HardwareUpdateTimeout = TimeSpan.FromSeconds(8);

    // ---- R07: single in-flight LHM Update guard ----
    // LHM is not thread-safe: at most one Update() may run at any time. Updates run on
    // thread-pool tasks so the main loop is never blocked beyond the timeout. A task
    // that outlives the timeout (wedged hardware) is tracked: while it is still running
    // no new Update is started and the cycle keeps publishing last-known values.
    private Task? _inFlightUpdate;
    private IHardware? _inFlightHardware;
    private long _leakedUpdateCount;
    private int _lastLeakLogCycle;

    // ---- R08: periodic recovery probe for suspended hardware ----
    private int _cycleCount;
    private const int SuspendRetryInterval = 60; // ~2 minutes at the 2s main loop
    private static readonly TimeSpan SuspendRetryTimeout = TimeSpan.FromSeconds(1);

    // ---- R09: rate-limited update error logging ----
    private long _updateErrorCount;
    private long _lastUpdateErrorLogTicks;
    private const long UpdateErrorLogIntervalMs = 30_000;

    public bool PawnIoInstalled { get; }

    public SensorCollector()
    {
        PawnIoInstalled = CheckPawnIoInstalled();
        _cpuSource = DetermineCpuSource();

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
        };
        _computer.Open();
    }

    // ---- Public API ----

    /// <summary>
    /// Single-pass read: updates all hardware once and returns CPU temp, GPUs, and all sensors.
    /// Replaces the triple-Update pattern of calling ReadCpuTemp + ReadGpus + ReadAllSensors separately.
    /// </summary>
    public (double CpuTemp, string CpuSource, List<GpuReading> Gpus, List<SensorEntry> Sensors) ReadAll()
    {
        double cpuTemp = -1;
        var gpus = new List<GpuReading>();
        var sensors = new List<SensorEntry>();
        var instanceByType = new Dictionary<int, int>();

        _cycleCount++;
        ProbeSuspendedHardware();

        foreach (var hw in _computer.Hardware)
        {
            if (IsSuspended(hw))
                continue;

            var ht = hw.HardwareType;
            bool isCpu = ht == HardwareType.Cpu;
            bool isGpu = ht == HardwareType.GpuNvidia || ht == HardwareType.GpuAmd || ht == HardwareType.GpuIntel;

            // A wedged hardware (e.g. a dGPU disabled while polling) must not stall the
            // whole cycle. On timeout it is suspended and the rest of this cycle is skipped
            // to avoid concurrent LHM calls; remaining hardware keeps publishing.
            if (!UpdateHardwareWithTimeout(hw))
                break;

            int hwTag = HardwareTypeTag(ht);
            if (hwTag < 0) continue;
            instanceByType.TryGetValue(hwTag, out int typeInstance);
            instanceByType[hwTag] = typeInstance + 1;
            int packedHwTag = PackHardwareTag(hwTag, typeInstance);

            // Collect all categorized sensors from this hardware (and sub-hardware)
            CollectSensorsFromHardware(hw, sensors, packedHwTag);

            if (isCpu)
            {
                cpuTemp = ExtractCpuTemp(hw);
            }

            if (isGpu)
            {
                var gpu = ExtractGpuReading(hw);
                if (gpu != null) gpus.Add(gpu);
            }
        }

        return (cpuTemp, cpuTemp > 0 ? _cpuSource : "None", gpus, sensors);
    }

    private bool IsSuspended(IHardware hw)
    {
        lock (_hardwareLock) return _suspendedHardware.Contains(hw);
    }

    /// <summary>
    /// Calls hw.Update() with a bounded wait. A wedged hardware (such as a dGPU removed
    /// or disabled while the broker is running) is suspended so CPU/iGPU/storage sensors
    /// keep publishing without blocking the whole cycle.
    ///
    /// R07: at most one LHM Update may be in flight at any time. If a previous update
    /// task is still running (it outlived its timeout and is therefore leaked), no new
    /// Update is started; the cycle continues publishing last-known values and the leak
    /// is counted and logged (rate-limited).
    /// </summary>
    private bool UpdateHardwareWithTimeout(IHardware hw)
    {
        if (InFlightUpdateBusy())
        {
            long leaked = Interlocked.Increment(ref _leakedUpdateCount);
            if (_cycleCount - _lastLeakLogCycle >= 30)
            {
                _lastLeakLogCycle = _cycleCount;
                SysMonBroker.Logging.BrokerLogger.Log(
                    $"sensor: previous update of {_inFlightHardware?.Name ?? "unknown"} is still running " +
                    $"after timeout; skipping new updates (leaked updates={leaked}) — publishing last known values");
            }
            return true; // keep publishing this cycle with last-known (stale) values
        }

        var update = StartHardwareUpdate(hw);
        if (update.Wait(HardwareUpdateTimeout))
            return true;

        lock (_hardwareLock) _suspendedHardware.Add(hw);
        SysMonBroker.Logging.BrokerLogger.Log(
            $"sensor: {hw.Name} ({hw.HardwareType}) update exceeded " +
            $"{(int)HardwareUpdateTimeout.TotalSeconds}s; suspending it for this process");
        return false;
    }

    /// <summary>
    /// Launches one LHM Update on a thread-pool task and tracks it as the single
    /// in-flight update. Callers must have confirmed the channel is free.
    /// </summary>
    private Task StartHardwareUpdate(IHardware hw)
    {
        var update = Task.Run(() =>
        {
            try { hw.Update(); }
            catch (Exception ex) { LogUpdateError(hw, ex); }
        });
        _inFlightUpdate = update;
        _inFlightHardware = hw;
        return update;
    }

    /// <summary>True while the tracked update task is still running (leaked or not).</summary>
    private bool InFlightUpdateBusy()
    {
        var t = _inFlightUpdate;
        return t != null && !t.IsCompleted;
    }

    /// <summary>
    /// R08: every ~2 minutes, try a short update on suspended hardware so a hardware
    /// that recovered from a transient stall is re-armed instead of staying suspended
    /// for the whole process lifetime. Probing obeys the single in-flight rule: if the
    /// channel is occupied by a leaked task, probing is skipped this round.
    /// </summary>
    private void ProbeSuspendedHardware()
    {
        if (_cycleCount % SuspendRetryInterval != 0) return;
        if (InFlightUpdateBusy()) return;

        IHardware[] suspended;
        lock (_hardwareLock) suspended = _suspendedHardware.ToArray();
        if (suspended.Length == 0) return;

        foreach (var hw in suspended)
        {
            if (InFlightUpdateBusy()) return; // a probe itself timed out; keep the rest suspended

            var probe = StartHardwareUpdate(hw);
            if (probe.Wait(SuspendRetryTimeout))
            {
                lock (_hardwareLock) _suspendedHardware.Remove(hw);
                SysMonBroker.Logging.BrokerLogger.Log(
                    $"sensor: {hw.Name} ({hw.HardwareType}) recovered after suspension; resuming collection");
            }
            else
            {
                SysMonBroker.Logging.BrokerLogger.Log(
                    $"sensor: {hw.Name} recovery probe exceeded {(int)SuspendRetryTimeout.TotalSeconds}s; keeping it suspended");
            }
        }
    }

    /// <summary>R09: rate-limited logging of Update() exceptions (hardware removal races, etc.).</summary>
    private void LogUpdateError(IHardware hw, Exception ex)
    {
        long count = Interlocked.Increment(ref _updateErrorCount);
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastUpdateErrorLogTicks);
        if (now - last >= UpdateErrorLogIntervalMs &&
            Interlocked.CompareExchange(ref _lastUpdateErrorLogTicks, now, last) == last)
        {
            SysMonBroker.Logging.BrokerLogger.Log(
                $"sensor: {hw.Name} ({hw.HardwareType}) Update() threw " +
                $"({ex.GetType().Name}: {ex.Message}; total errors={count})");
        }
    }

    public void Dispose()
    {
        _computer.Close();
    }
}
