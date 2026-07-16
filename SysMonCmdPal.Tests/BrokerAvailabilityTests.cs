// Copyright (c) 2026 SysMonCmdPal

using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public class BrokerAvailabilityTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void AvailabilityAliases_UseOneMonotonicHealthDecision(
        bool hasAvailabilityTimestamp,
        bool isExpired,
        bool expected)
    {
        var receiver = new BrokerPushReceiver();
        BrokerRuntimeTestScope.SetSnapshot(
            receiver,
            BrokerTestData.Snapshot(hasAvailabilityTimestamp, isExpired));

        BrokerSensorSnapshot snapshot = receiver.Snapshot;
        Assert.Equal(expected, snapshot.IsUsable);
        Assert.Equal(expected, snapshot.IsFresh);
        Assert.Equal(expected, snapshot.IsAlive);
        Assert.Equal(expected, receiver.IsBrokerAvailable);
        Assert.Equal(expected, receiver.IsUsable);
    }

    [Fact]
    public void PushSnapshot_AtomicallyPublishesAvailablePayload()
    {
        var receiver = new BrokerPushReceiver();
        var gpu = new BrokerGpuSnapshot
        {
            Name = "Test GPU",
            Temperature = 72,
            UsagePercent = 88,
            MemoryUsedMB = 4096,
            MemoryTotalMB = 8192,
        };
        BrokerSensorEntry sensor = new()
        {
            Tag = ShmLayout.TagCpuPower,
            Name = "CPU Package",
            Value = 45,
            Unit = "W",
        };

        receiver.PushSnapshot(
            63.5,
            "Broker_Test",
            [new KeyValuePair<int, BrokerGpuSnapshot>(2, gpu)],
            [sensor]);

        BrokerSensorSnapshot snapshot = receiver.Snapshot;
        Assert.True(receiver.IsBrokerAvailable);
        Assert.Equal(63.5, snapshot.CpuTemperature);
        Assert.Equal("Broker_Test", snapshot.CpuSource);
        Assert.Same(gpu, snapshot.Gpus[2]);
        Assert.Same(sensor, Assert.Single(snapshot.AllSensors));
        Assert.Equal(snapshot.LastPush, snapshot.LastPing);
        Assert.Equal(snapshot.LastDataTimestamp, snapshot.LastAvailableTimestamp);
    }

    [Fact]
    public void MarkUnavailable_InvalidatesHealthWithoutDiscardingLastPayload()
    {
        var receiver = new BrokerPushReceiver();
        receiver.PushSnapshot(65, "Broker_Test", [], []);
        BrokerSensorSnapshot available = receiver.Snapshot;

        receiver.MarkUnavailable();

        BrokerSensorSnapshot unavailable = receiver.Snapshot;
        Assert.False(receiver.IsBrokerAvailable);
        Assert.Equal(available.CpuTemperature, unavailable.CpuTemperature);
        Assert.Equal(available.CpuSource, unavailable.CpuSource);
        Assert.Equal(available.LastPush, unavailable.LastPush);
        Assert.Equal(available.LastDataTimestamp, unavailable.LastDataTimestamp);
        Assert.Equal(0, unavailable.LastAvailableTimestamp);
    }

    [Fact]
    public void TryGetAvailableSnapshot_ReturnsStatusAndPayloadFromOneSnapshot()
    {
        var receiver = new BrokerPushReceiver();
        receiver.PushSnapshot(66, "Atomic", [], []);

        Assert.True(receiver.TryGetAvailableSnapshot(out BrokerSensorSnapshot available));
        Assert.Equal(66, available.CpuTemperature);
        Assert.Equal("Atomic", available.CpuSource);

        receiver.MarkUnavailable();

        Assert.False(receiver.TryGetAvailableSnapshot(out BrokerSensorSnapshot unavailable));
        Assert.Equal(66, unavailable.CpuTemperature);
        Assert.Equal("Atomic", unavailable.CpuSource);
    }

    [Fact]
    public void Ping_DoesNotRestoreAvailabilityForExpiredPayload()
    {
        var receiver = new BrokerPushReceiver();
        BrokerSensorSnapshot expired = BrokerTestData.Snapshot(
            isAvailable: false,
            hasRecentData: false);
        BrokerRuntimeTestScope.SetSnapshot(receiver, expired);

        receiver.Ping();

        BrokerSensorSnapshot snapshot = receiver.Snapshot;
        Assert.False(receiver.IsBrokerAvailable);
        Assert.Equal(expired.LastPush, snapshot.LastPush);
        Assert.Equal(expired.LastDataTimestamp, snapshot.LastDataTimestamp);
        Assert.Equal(0, snapshot.LastAvailableTimestamp);
    }

    [Fact]
    public void Ping_CanRestoreAvailabilityOnlyWhilePayloadIsStillRecent()
    {
        var receiver = new BrokerPushReceiver();
        BrokerSensorSnapshot recent = BrokerTestData.Snapshot(
            isAvailable: false,
            hasRecentData: true);
        BrokerRuntimeTestScope.SetSnapshot(receiver, recent);

        receiver.Ping();

        Assert.True(receiver.IsBrokerAvailable);
        Assert.Equal(recent.LastPush, receiver.Snapshot.LastPush);
        Assert.True(receiver.Snapshot.LastAvailableTimestamp > 0);
    }

    [Fact]
    public void IncrementalPayloadPush_DoesNotCreateAvailabilityWithoutHeartbeat()
    {
        var receiver = new BrokerPushReceiver();

        receiver.PushCpuTemp(68, "LegacyPush");

        Assert.False(receiver.IsBrokerAvailable);
        Assert.True(receiver.Snapshot.LastDataTimestamp > 0);
        Assert.Equal(0, receiver.Snapshot.LastAvailableTimestamp);
    }
}
