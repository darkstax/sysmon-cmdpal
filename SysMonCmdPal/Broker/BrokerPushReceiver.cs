// SysMonCmdPal/Broker/BrokerPushReceiver.cs
// ISysMonBrokerPush 的实现，存储 Broker 推送的数据
// 线程安全，带时间戳新鲜度检查

using System;
using System.Collections.Concurrent;
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

/// <summary>Broker 推送的完整传感器快照</summary>
public sealed class BrokerSensorSnapshot
{
    public double CpuTemperature { get; init; } = -1;
    public string CpuSource { get; init; } = "";
    public ConcurrentDictionary<int, BrokerGpuSnapshot> Gpus { get; init; } = new();
    public DateTime LastPush { get; init; } = DateTime.UtcNow;
    public DateTime LastPing { get; init; } = DateTime.UtcNow;

    /// <summary>数据是否新鲜（10 秒内有更新）</summary>
    public bool IsFresh => (DateTime.UtcNow - LastPush).TotalSeconds < 10;

    /// <summary>Broker 是否存活（5 秒内有心跳）</summary>
    public bool IsAlive => (DateTime.UtcNow - LastPing).TotalSeconds < 5;
}

/// <summary>
/// ISysMonBrokerPush 的 COM 可见实现。
/// Broker 通过 COM 调用此对象推送传感器数据。
/// </summary>
[ComVisible(true)]
[Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567892")]
public sealed class BrokerPushReceiver : ISysMonBrokerPush
{
    /// <summary>单例 — 供 CpuSensorReader/GpuSensorReader 读取</summary>
    public static BrokerPushReceiver Instance { get; } = new();

    private volatile BrokerSensorSnapshot _snapshot = new();

    /// <summary>当前快照（只读）</summary>
    public BrokerSensorSnapshot Snapshot => _snapshot;

    /// <summary>Broker 当前是否可用（有心跳且数据新鲜）</summary>
    public bool IsBrokerAvailable => _snapshot.IsAlive && _snapshot.IsFresh;

    // ===== ISysMonBrokerPush 实现 =====

    public void PushCpuTemp(double celsius, string source)
    {
        var old = _snapshot;
        _snapshot = new BrokerSensorSnapshot
        {
            CpuTemperature = celsius,
            CpuSource = source,
            Gpus = old.Gpus,
            LastPush = DateTime.UtcNow,
            LastPing = old.LastPing,
        };
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

        var old = _snapshot;
        var newGpus = new ConcurrentDictionary<int, BrokerGpuSnapshot>(old.Gpus);
        newGpus[gpuIndex] = gpu;

        _snapshot = new BrokerSensorSnapshot
        {
            CpuTemperature = old.CpuTemperature,
            CpuSource = old.CpuSource,
            Gpus = newGpus,
            LastPush = DateTime.UtcNow,
            LastPing = old.LastPing,
        };
    }

    public void Ping()
    {
        var old = _snapshot;
        _snapshot = new BrokerSensorSnapshot
        {
            CpuTemperature = old.CpuTemperature,
            CpuSource = old.CpuSource,
            Gpus = old.Gpus,
            LastPush = old.LastPush,
            LastPing = DateTime.UtcNow,
        };
    }
}
