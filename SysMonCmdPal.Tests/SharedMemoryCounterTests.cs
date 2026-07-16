// Copyright (c) 2026 SysMonCmdPal

using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SharedMemoryCounterTests
{
    [Fact]
    public void UnchangedCounter_DoesNotRepublishPayloadOrRefreshCommitTime()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        harness.PublishFirstV2(
            BrokerTestData.V2Buffer(5, cpuTemperature: 60, timestampTicks: timestamp),
            5,
            timestamp);
        BrokerSensorSnapshot first = harness.Receiver.Snapshot;
        DateTime firstCommit = SharedMemoryReader.Diagnostics.LastCommitUtc;

        harness.ProcessV2(
            BrokerTestData.V2Buffer(5, cpuTemperature: 99, timestampTicks: timestamp),
            5,
            timestamp);

        Assert.Same(first, harness.Receiver.Snapshot);
        Assert.Equal(60, harness.Receiver.Snapshot.CpuTemperature);
        Assert.Equal(firstCommit, SharedMemoryReader.Diagnostics.LastCommitUtc);
        Assert.False(SharedMemoryReader.Diagnostics.IsStalled);
    }

    [Fact]
    public void UnchangedCounterPastTimeout_MarksBrokerStalledAndUnavailable()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        harness.PublishFirstV2(BrokerTestData.V2Buffer(8, timestampTicks: timestamp), 8, timestamp);
        harness.SetLastCounterAdvanceElapsed(TimeSpan.FromMinutes(1));
        harness.ExpireReceiverAvailability();

        harness.ProcessV2(BrokerTestData.V2Buffer(8, timestampTicks: timestamp), 8, timestamp);

        Assert.False(harness.Receiver.IsBrokerAvailable);
        Assert.True(SharedMemoryReader.Diagnostics.IsStalled);
        Assert.True(SharedMemoryReader.Diagnostics.IsConnected);
        Assert.Contains("has not advanced", SharedMemoryReader.Diagnostics.LastError);
    }

    [Fact]
    public void InitialZeroCounter_WaitsForFirstDataCommit()
    {
        using var harness = new SharedMemoryReaderHarness();
        long initializedTimestamp = DateTime.UtcNow.AddSeconds(-1).Ticks;
        harness.ProcessV2(
            BrokerTestData.V2Buffer(
                0,
                cpuTemperature: 99,
                timestampTicks: initializedTimestamp,
                extensionMagic: 0,
                instanceId: 0,
                monotonicPublishMs: 0),
            0,
            initializedTimestamp);

        Assert.False(harness.Receiver.IsBrokerAvailable);
        Assert.Contains("waiting for the first data commit", SharedMemoryReader.Diagnostics.LastError);

        long firstCommitTimestamp = DateTime.UtcNow.Ticks;
        harness.ProcessV2(
            BrokerTestData.V2Buffer(
                1,
                cpuTemperature: 63,
                timestampTicks: firstCommitTimestamp,
                extensionMagic: 0,
                instanceId: 0,
                monotonicPublishMs: 0),
            1,
            firstCommitTimestamp);

        Assert.True(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(63, harness.Receiver.Snapshot.CpuTemperature);
    }

    [Fact]
    public void ExtendedInitializationFrame_DoesNotPublishBeforeCounterAdvances()
    {
        using var harness = new SharedMemoryReaderHarness();
        long initializedTimestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = BrokerTestData.V2Buffer(
            counter: 0,
            cpuTemperature: -1,
            source: "None",
            timestampTicks: initializedTimestamp,
            instanceId: 77);

        harness.ProcessV2(buffer, 0, initializedTimestamp);

        Assert.False(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(0, SharedMemoryReader.Diagnostics.LastCounter);
        Assert.True(SharedMemoryReader.Diagnostics.UsesCommitSequence);
        Assert.Equal((ulong)77, SharedMemoryReader.Diagnostics.LastInstanceId);
        Assert.Contains("waiting for the first data commit", SharedMemoryReader.Diagnostics.LastError);
    }

    [Fact]
    public void SignedCounterWrap_IsForwardProgressNotRestart()
    {
        using var harness = new SharedMemoryReaderHarness();
        long firstTimestamp = DateTime.UtcNow.AddSeconds(-1).Ticks;
        long secondTimestamp = DateTime.UtcNow.Ticks;
        harness.PublishFirstV2(
            BrokerTestData.V2Buffer(int.MaxValue, timestampTicks: firstTimestamp),
            int.MaxValue,
            firstTimestamp);

        harness.ProcessV2(
            BrokerTestData.V2Buffer(int.MinValue, cpuTemperature: 69, timestampTicks: secondTimestamp),
            int.MinValue,
            secondTimestamp);

        Assert.True(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(69, harness.Receiver.Snapshot.CpuTemperature);
        Assert.Equal(0, SharedMemoryReader.Diagnostics.RestartCount);
    }

    [Fact]
    public void ExtendedSnapshot_WithExpiredPublishTimeIsImmediatelyUnavailable()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = BrokerTestData.V2Buffer(
            counter: 4,
            timestampTicks: timestamp,
            monotonicPublishMs: Math.Max(1, Environment.TickCount64 - 60_000));

        harness.PublishFirstV2(buffer, 4, timestamp);

        Assert.False(harness.Receiver.IsBrokerAvailable);
        Assert.True(SharedMemoryReader.Diagnostics.IsStalled);
        Assert.Contains("has not published", SharedMemoryReader.Diagnostics.LastError);
    }
}
