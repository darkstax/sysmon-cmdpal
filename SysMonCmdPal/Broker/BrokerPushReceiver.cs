// SysMonCmdPal/Broker/BrokerPushReceiver.cs
// ISysMonBrokerPush 的实现，存储 Broker 推送的数据
// v2: 增加全量传感器快照

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SysMonCmdPal.Broker;

/// <summary>单个 GPU 的 Broker 推送数据</summary>
public sealed class BrokerGpuSnapshot
{
    public string Name { get; init; } = "";
    public double Temperature { get; init; } = -1;
    public double UsagePercent { get; init; } = -1;
    public double MemoryUsedMB { get; init; }
    public double MemoryTotalMB { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>全量传感器快照中的单个条目</summary>
public sealed class BrokerSensorEntry
{
    public int Tag { get; init; }
    public string Name { get; init; } = "";
    public double Value { get; init; }
    public string Unit { get; init; } = "";
    public int HardwareTag { get; init; }
}

/// <summary>Broker 推送的完整传感器快照</summary>
public sealed class BrokerSensorSnapshot
{
    internal static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(5);

    public double CpuTemperature { get; init; } = -1;
    public string CpuSource { get; init; } = "";
    public ConcurrentDictionary<int, BrokerGpuSnapshot> Gpus { get; init; } = new();
    public IReadOnlyList<BrokerSensorEntry> AllSensors { get; init; } = [];
    public DateTime LastPush { get; init; } = DateTime.MinValue;
    public DateTime LastPing { get; init; } = DateTime.MinValue;
    internal long LastDataTimestamp { get; init; }
    internal long LastAvailableTimestamp { get; init; }

    /// <summary>Broker 快照是否仍可用。仅使用单调时钟，不受系统时间调整影响。</summary>
    public bool IsUsable => LastAvailableTimestamp > 0 &&
        Stopwatch.GetElapsedTime(LastAvailableTimestamp) < AvailabilityTimeout;

    // Compatibility aliases. All callers observe the same runtime health state.
    public bool IsFresh => IsUsable;
    public bool IsAlive => IsUsable;
}

/// <summary>
/// ISysMonBrokerPush 的 COM 可见实现。
/// SharedMemoryReader 通过此单例更新传感器数据。
/// </summary>
[ComVisible(true)]
[Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567892")]
public sealed class BrokerPushReceiver : ISysMonBrokerPush
{
    public static BrokerPushReceiver Instance { get; } = new();

    // Lock protects the read-modify-write cycle on _snapshot.
    // Without it, concurrent Push* calls could lose updates (volatile only
    // guarantees reference visibility, not RMW atomicity).
    private readonly object _lock = new();
    private BrokerSensorSnapshot _snapshot = new();

    public BrokerSensorSnapshot Snapshot
    {
        get { lock (_lock) return _snapshot; }
    }

    public bool IsBrokerAvailable => Snapshot.IsUsable;
    public bool IsUsable => IsBrokerAvailable;

    public bool TryGetAvailableSnapshot(out BrokerSensorSnapshot snapshot)
    {
        lock (_lock)
        {
            snapshot = _snapshot;
            return snapshot.IsUsable;
        }
    }

    public void PushSnapshot(double cpuTemperature, string cpuSource,
        IEnumerable<KeyValuePair<int, BrokerGpuSnapshot>> gpus,
        IReadOnlyList<BrokerSensorEntry> sensors)
    {
        var now = DateTime.UtcNow;
        long timestamp = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            _snapshot = new BrokerSensorSnapshot
            {
                CpuTemperature = cpuTemperature > 0 ? cpuTemperature : -1,
                CpuSource = string.IsNullOrWhiteSpace(cpuSource) ? "None" : cpuSource,
                Gpus = new ConcurrentDictionary<int, BrokerGpuSnapshot>(gpus),
                AllSensors = sensors,
                LastPush = now,
                LastPing = now,
                LastDataTimestamp = timestamp,
                LastAvailableTimestamp = timestamp,
            };
        }
    }

    internal void MarkUnavailable()
    {
        lock (_lock)
        {
            var old = _snapshot;
            if (old.LastAvailableTimestamp == 0)
                return;

            _snapshot = CopySnapshot(old, lastAvailableTimestamp: 0);
        }
    }

    // ===== ISysMonBrokerPush 实现 =====

    public void PushCpuTemp(double celsius, string source)
    {
        var now = DateTime.UtcNow;
        long timestamp = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            var old = _snapshot;
            _snapshot = new BrokerSensorSnapshot
            {
                CpuTemperature = celsius,
                CpuSource = source,
                Gpus = old.Gpus,
                AllSensors = old.AllSensors,
                LastPush = now,
                LastPing = old.LastPing,
                LastDataTimestamp = timestamp,
                LastAvailableTimestamp = old.LastAvailableTimestamp,
            };
        }
    }

    public void PushGpuData(int gpuIndex, string name, double tempCelsius,
        double usagePercent, double memUsedMB, double memTotalMB)
    {
        var now = DateTime.UtcNow;
        long timestamp = Stopwatch.GetTimestamp();
        var gpu = new BrokerGpuSnapshot
        {
            Name = name,
            Temperature = tempCelsius,
            UsagePercent = usagePercent,
            MemoryUsedMB = memUsedMB,
            MemoryTotalMB = memTotalMB,
            Timestamp = now,
        };

        lock (_lock)
        {
            var old = _snapshot;
            var newGpus = new ConcurrentDictionary<int, BrokerGpuSnapshot>(old.Gpus);
            newGpus[gpuIndex] = gpu;

            _snapshot = new BrokerSensorSnapshot
            {
                CpuTemperature = old.CpuTemperature,
                CpuSource = old.CpuSource,
                Gpus = newGpus,
                AllSensors = old.AllSensors,
                LastPush = now,
                LastPing = old.LastPing,
                LastDataTimestamp = timestamp,
                LastAvailableTimestamp = old.LastAvailableTimestamp,
            };
        }
    }

    /// <summary>推送全量传感器数据（v2 新增）</summary>
    public void PushAllSensors(IReadOnlyList<BrokerSensorEntry> sensors)
    {
        var now = DateTime.UtcNow;
        long timestamp = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            var old = _snapshot;
            _snapshot = new BrokerSensorSnapshot
            {
                CpuTemperature = old.CpuTemperature,
                CpuSource = old.CpuSource,
                Gpus = old.Gpus,
                AllSensors = sensors,
                LastPush = now,
                LastPing = old.LastPing,
                LastDataTimestamp = timestamp,
                LastAvailableTimestamp = old.LastAvailableTimestamp,
            };
        }
    }

    public void Ping()
    {
        var now = DateTime.UtcNow;
        long timestamp = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            var old = _snapshot;
            _snapshot = new BrokerSensorSnapshot
            {
                CpuTemperature = old.CpuTemperature,
                CpuSource = old.CpuSource,
                Gpus = old.Gpus,
                AllSensors = old.AllSensors,
                LastPush = old.LastPush,
                LastPing = now,
                LastDataTimestamp = old.LastDataTimestamp,
                LastAvailableTimestamp = IsRecentData(old.LastDataTimestamp)
                    ? timestamp
                    : 0,
            };
        }
    }

    private static bool IsRecentData(long timestamp) =>
        timestamp > 0 && Stopwatch.GetElapsedTime(timestamp) < TimeSpan.FromSeconds(10);

    private static BrokerSensorSnapshot CopySnapshot(
        BrokerSensorSnapshot source,
        long lastAvailableTimestamp) => new()
        {
            CpuTemperature = source.CpuTemperature,
            CpuSource = source.CpuSource,
            Gpus = source.Gpus,
            AllSensors = source.AllSensors,
            LastPush = source.LastPush,
            LastPing = source.LastPing,
            LastDataTimestamp = source.LastDataTimestamp,
            LastAvailableTimestamp = lastAvailableTimestamp,
        };
}
