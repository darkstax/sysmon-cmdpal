// Copyright (c) 2026 SysMonCmdPal

using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SharedMemoryParsingTests
{
    [Fact]
    public void V2Snapshot_PublishesCpuGpuAndFiniteSensors()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = BrokerTestData.V2Buffer(
            counter: 7,
            cpuTemperature: 67.25,
            source: "Broker_LHM",
            gpus:
            [
                new TestGpu("Discrete GPU", 70, 85, 6144, 12288),
                new TestGpu("Integrated GPU", 54, 32, 0, 0),
            ],
            sensors:
            [
                new TestSensor(ShmLayout.TagCpuPower, "CPU Package", 52.5, "W", ShmLayout.HwCpu),
                new TestSensor(ShmLayout.TagGpuPower, "GPU Core", double.NaN, "W", ShmLayout.HwGpuNvidia),
            ],
            timestampTicks: timestamp);

        harness.PublishFirstV2(buffer, counter: 7, timestamp);

        BrokerSensorSnapshot snapshot = harness.Receiver.Snapshot;
        Assert.True(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(67.25, snapshot.CpuTemperature);
        Assert.Equal("Broker_LHM", snapshot.CpuSource);
        Assert.Equal(2, snapshot.Gpus.Count);
        Assert.Equal("Discrete GPU", snapshot.Gpus[0].Name);
        Assert.Equal(6144, snapshot.Gpus[0].MemoryUsedMB);
        BrokerSensorEntry sensor = Assert.Single(snapshot.AllSensors);
        Assert.Equal("CPU Package", sensor.Name);
        Assert.Equal(52.5, sensor.Value);
        Assert.True(SharedMemoryReader.Diagnostics.IsProtocolValid);
        Assert.True(SharedMemoryReader.Diagnostics.UsesCommitSequence);
        Assert.Equal(2, SharedMemoryReader.Diagnostics.LastCommitSequence);
        Assert.Equal((ulong)1, SharedMemoryReader.Diagnostics.LastInstanceId);
        Assert.Equal(7, SharedMemoryReader.Diagnostics.LastCounter);
        Assert.Equal(1, SharedMemoryReader.Diagnostics.LastSensorCount);
    }

    [Fact]
    public void V1Snapshot_RemainsReadableWithoutGenericSensors()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = BrokerTestData.V1Buffer(
            counter: 4,
            cpuTemperature: 59,
            source: "LegacyBroker",
            gpus: [new TestGpu("Legacy GPU", 66, 41, 2048, 8192)],
            timestampTicks: timestamp);

        harness.ProcessV1(buffer, counter: 4, timestamp);

        BrokerSensorSnapshot snapshot = harness.Receiver.Snapshot;
        Assert.True(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(59, snapshot.CpuTemperature);
        Assert.Equal("LegacyBroker", snapshot.CpuSource);
        Assert.Equal("Legacy GPU", Assert.Single(snapshot.Gpus).Value.Name);
        Assert.Empty(snapshot.AllSensors);
        Assert.Equal(ShmLayout.MinimumSupportedVersion, SharedMemoryReader.Diagnostics.LastVersion);
    }

    [Fact]
    public void OldV2Snapshot_UsesLegacyCounterAndTimestampFallback()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = BrokerTestData.V2Buffer(
            counter: 3,
            cpuTemperature: 62,
            timestampTicks: timestamp,
            extensionMagic: 0,
            instanceId: 0,
            monotonicPublishMs: 0);

        harness.ProcessV2(buffer, 3, timestamp);

        Assert.True(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(62, harness.Receiver.Snapshot.CpuTemperature);
        Assert.False(SharedMemoryReader.Diagnostics.UsesCommitSequence);
        Assert.Equal((ulong)0, SharedMemoryReader.Diagnostics.LastInstanceId);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(5, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 251)]
    public void InvalidCollectionCounts_RejectEntireSnapshot(int gpuCount, int sensorCount)
    {
        using var harness = new SharedMemoryReaderHarness();
        harness.SetReceiverSnapshot(BrokerTestData.Snapshot(isAvailable: true, cpuTemperature: 55));
        long timestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = BrokerTestData.V2Buffer(
            counter: 1,
            rawGpuCount: gpuCount,
            rawSensorCount: sensorCount,
            timestampTicks: timestamp);

        harness.PublishFirstV2(buffer, 1, timestamp);

        Assert.False(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(55, harness.Receiver.Snapshot.CpuTemperature);
        Assert.False(SharedMemoryReader.Diagnostics.IsProtocolValid);
        Assert.Contains("Invalid", SharedMemoryReader.Diagnostics.LastError);
    }

    [Fact]
    public void InvalidMeasurements_AreNormalizedWithoutRejectingSnapshot()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = BrokerTestData.V2Buffer(
            counter: 3,
            cpuTemperature: double.PositiveInfinity,
            gpus:
            [
                new TestGpu("Invalid readings", 151, 101, -1, double.NaN),
            ],
            timestampTicks: timestamp);

        harness.PublishFirstV2(buffer, 3, timestamp);

        BrokerSensorSnapshot snapshot = harness.Receiver.Snapshot;
        Assert.True(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(-1, snapshot.CpuTemperature);
        BrokerGpuSnapshot gpu = Assert.Single(snapshot.Gpus).Value;
        Assert.Equal(-1, gpu.Temperature);
        Assert.Equal(-1, gpu.UsagePercent);
        Assert.Equal(0, gpu.MemoryUsedMB);
        Assert.Equal(0, gpu.MemoryTotalMB);
    }
}
