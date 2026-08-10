// Copyright (c) 2026 SysMonCmdPal

using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SharedMemoryRestartTests
{
    [Fact]
    public void OldV2BackwardCounter_DetectsRestartAndAcceptsNewPayload()
    {
        using var harness = new SharedMemoryReaderHarness();
        long firstTimestamp = DateTime.UtcNow.AddSeconds(-1).Ticks;
        long restartedTimestamp = DateTime.UtcNow.Ticks;
        harness.ProcessV2(
            BrokerTestData.V2Buffer(
                20,
                cpuTemperature: 60,
                timestampTicks: firstTimestamp,
                extensionMagic: 0,
                instanceId: 0,
                monotonicPublishMs: 0),
            20,
            firstTimestamp);

        harness.ProcessV2(
            BrokerTestData.V2Buffer(
                2,
                cpuTemperature: 72,
                timestampTicks: restartedTimestamp,
                extensionMagic: 0,
                instanceId: 0,
                monotonicPublishMs: 0),
            2,
            restartedTimestamp);

        Assert.True(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(72, harness.Receiver.Snapshot.CpuTemperature);
        Assert.Equal(2, SharedMemoryReader.Diagnostics.LastCounter);
        Assert.Equal(1, SharedMemoryReader.Diagnostics.RestartCount);
    }

    [Fact]
    public void NewInstanceId_DetectsRestartEvenWhenCounterIsReused()
    {
        using var harness = new SharedMemoryReaderHarness();
        long firstTimestamp = DateTime.UtcNow.AddSeconds(-1).Ticks;
        long restartedTimestamp = DateTime.UtcNow.Ticks;
        harness.PublishFirstV2(
            BrokerTestData.V2Buffer(
                20,
                cpuTemperature: 60,
                timestampTicks: firstTimestamp,
                instanceId: 11),
            20,
            firstTimestamp);

        harness.ProcessV2(
            BrokerTestData.V2Buffer(
                20,
                cpuTemperature: 74,
                timestampTicks: restartedTimestamp,
                instanceId: 22),
            20,
            restartedTimestamp);

        Assert.False(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(1, SharedMemoryReader.Diagnostics.RestartCount);
        Assert.Equal(20, SharedMemoryReader.Diagnostics.LastCounter);
        Assert.Equal((ulong)22, SharedMemoryReader.Diagnostics.LastInstanceId);
        Assert.Contains("waiting for the next committed update", SharedMemoryReader.Diagnostics.LastError);

        long committedTimestamp = DateTime.UtcNow.AddTicks(1).Ticks;
        harness.ProcessV2(
            BrokerTestData.V2Buffer(
                21,
                cpuTemperature: 74,
                timestampTicks: committedTimestamp,
                instanceId: 22),
            21,
            committedTimestamp);

        Assert.True(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(74, harness.Receiver.Snapshot.CpuTemperature);
        Assert.Equal(21, SharedMemoryReader.Diagnostics.LastCounter);
    }

    [Fact]
    public void SameCounterWithNewTimestamp_WaitsForNextCounterBeforePublishingRestart()
    {
        using var harness = new SharedMemoryReaderHarness();
        long firstTimestamp = DateTime.UtcNow.AddSeconds(-2).Ticks;
        long commitTimestamp = DateTime.UtcNow.AddSeconds(-1).Ticks;
        long restartTimestamp = DateTime.UtcNow.Ticks;

        // 首连双快照：counter 4（基线）→ 5（提交帧）发布 60。
        harness.ProcessV2(
            BrokerTestData.V2Buffer(
                4,
                cpuTemperature: 60,
                timestampTicks: firstTimestamp,
                extensionMagic: 0,
                instanceId: 0,
                monotonicPublishMs: 0),
            4,
            firstTimestamp);
        harness.ProcessV2(
            BrokerTestData.V2Buffer(
                5,
                cpuTemperature: 60,
                timestampTicks: commitTimestamp,
                extensionMagic: 0,
                instanceId: 0,
                monotonicPublishMs: 0),
            5,
            commitTimestamp);

        // 同 counter 新 timestamp → 重启信号，等待下一次 counter 前进。
        harness.ProcessV2(
            BrokerTestData.V2Buffer(
                5,
                cpuTemperature: 99,
                timestampTicks: restartTimestamp,
                extensionMagic: 0,
                instanceId: 0,
                monotonicPublishMs: 0),
            5,
            restartTimestamp);

        Assert.False(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(60, harness.Receiver.Snapshot.CpuTemperature);
        Assert.Equal(1, SharedMemoryReader.Diagnostics.RestartCount);
        Assert.Contains("waiting for the next committed update", SharedMemoryReader.Diagnostics.LastError);

        long committedTimestamp = DateTime.UtcNow.AddTicks(1).Ticks;
        harness.ProcessV2(
            BrokerTestData.V2Buffer(
                6,
                cpuTemperature: 75,
                timestampTicks: committedTimestamp,
                extensionMagic: 0,
                instanceId: 0,
                monotonicPublishMs: 0),
            6,
            committedTimestamp);

        Assert.True(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(75, harness.Receiver.Snapshot.CpuTemperature);
        Assert.Equal(6, SharedMemoryReader.Diagnostics.LastCounter);
        Assert.Equal(1, SharedMemoryReader.Diagnostics.RestartCount);
    }
}
