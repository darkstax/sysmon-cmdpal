// SysMonCmdPal/Broker/BrokerPushReceiver.cs
// ISysMonBrokerPush 的实现，存储 Broker 推送的数据
// v2: 增加全量传感器快照

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    public double CpuTemperature { get; init; } = -1;
    public string CpuSource { get; init; } = "";
    public ConcurrentDictionary<int, BrokerGpuSnapshot> Gpus { get; init; } = new();
    public IReadOnlyList<BrokerSensorEntry> AllSensors { get; init; } = [];
    public DateTime LastPush { get; init; } = DateTime.UtcNow;
    public DateTime LastPing { get; init; } = DateTime.UtcNow;

    /// <summary>数据是否新鲜（10 秒内有更新）</summary>
    public bool IsFresh => (DateTime.UtcNow - LastPush).TotalSeconds < 10;

    /// <summary>Broker 是否存活（5 秒内有心跳）</summary>
    public bool IsAlive => (DateTime.UtcNow - LastPing).TotalSeconds < 5;
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

    public bool IsBrokerAvailable => Snapshot.IsAlive && Snapshot.IsFresh;

    // ===== ISysMonBrokerPush 实现 =====

    public void PushCpuTemp(double celsius, string source)
    {
        lock (_lock)
        {
            var old = _snapshot;
            _snapshot = new BrokerSensorSnapshot
            {
                CpuTemperature = celsius,
                CpuSource = source,
                Gpus = old.Gpus,
                AllSensors = old.AllSensors,
                LastPush = DateTime.UtcNow,
                LastPing = old.LastPing,
            };
        }
    }

    public void PushGpuData(int gpuIndex, string name, double tempCelsius,
        double usagePercent, double memUsedMB, double memTotalMB)
    {
        var gpu = new BrokerGpuSnapshot
        {
            Name = name,
            Temperature = tempCelsius,
            UsagePercent = usagePercent,
            MemoryUsedMB = memUsedMB,
            MemoryTotalMB = memTotalMB,
            Timestamp = DateTime.UtcNow,
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
                LastPush = DateTime.UtcNow,
                LastPing = old.LastPing,
            };
        }
    }

    /// <summary>推送全量传感器数据（v2 新增）</summary>
    public void PushAllSensors(IReadOnlyList<BrokerSensorEntry> sensors)
    {
        lock (_lock)
        {
            var old = _snapshot;
            _snapshot = new BrokerSensorSnapshot
            {
                CpuTemperature = old.CpuTemperature,
                CpuSource = old.CpuSource,
                Gpus = old.Gpus,
                AllSensors = sensors,
                LastPush = DateTime.UtcNow,
                LastPing = old.LastPing,
            };
        }
    }

    public void Ping()
    {
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
                LastPing = DateTime.UtcNow,
            };
        }
    }
}
