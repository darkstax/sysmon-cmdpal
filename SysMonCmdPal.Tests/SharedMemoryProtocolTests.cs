// Copyright (c) 2026 SysMonCmdPal

using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SharedMemoryProtocolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void CurrentSizeMap_RejectsEveryVersionExceptExactV2(int version)
    {
        using var harness = new SharedMemoryReaderHarness();

        ResolvedSnapshotLayout layout = harness.ResolveLayout(version, ShmLayout.MapSize);

        Assert.False(layout.Success);
        Assert.Contains("Unsupported shared memory version", layout.Error);
    }

    [Fact]
    public void CurrentSizeMap_ResolvesExactV2Layout()
    {
        using var harness = new SharedMemoryReaderHarness();

        ResolvedSnapshotLayout layout = harness.ResolveLayout(
            ShmLayout.Version,
            ShmLayout.MapSize);

        Assert.True(layout.Success);
        Assert.Equal(ShmLayout.Version, layout.Version);
        Assert.Equal(ShmLayout.OffCounter, layout.CounterOffset);
        Assert.Equal(ShmLayout.OffSensorCount, layout.SensorCountOffset);
        Assert.Equal(ShmLayout.MapSize, layout.ReadLength);
    }

    [Fact]
    public void LegacySizeMap_UsesV1LayoutRegardlessOfCounterValueInVersionSlot()
    {
        using var harness = new SharedMemoryReaderHarness();

        ResolvedSnapshotLayout layout = harness.ResolveLayout(
            rawVersion: 12345,
            ShmLayout.LegacyMapSize);

        Assert.True(layout.Success);
        Assert.Equal(ShmLayout.MinimumSupportedVersion, layout.Version);
        Assert.Equal(ShmLayout.V1OffCounter, layout.CounterOffset);
        Assert.Equal(-1, layout.SensorCountOffset);
        Assert.Equal(ShmLayout.LegacyMapSize, layout.ReadLength);
    }

    [Fact]
    public void StableRead_RejectsUnsupportedV2VersionBeforePublishing()
    {
        using var harness = new SharedMemoryReaderHarness();
        byte[] buffer = BrokerTestData.V2Buffer(counter: 1, version: ShmLayout.Version + 1);

        StableReadResult result = harness.ReadStable(buffer, ShmLayout.MapSize);

        Assert.Equal("UnsupportedVersion", result.Status);
        Assert.Equal(ShmLayout.Version + 1, result.RawVersion);
        Assert.Equal(0, result.RetryCount);
        Assert.Contains("Unsupported shared memory version", result.Error);
    }

    [Fact]
    public void StableRead_RejectsInvalidMagicAndUndersizedViews()
    {
        using var harness = new SharedMemoryReaderHarness();
        byte[] invalidMagic = BrokerTestData.V2Buffer(counter: 1, magic: 0x12345678);

        StableReadResult invalid = harness.ReadStable(invalidMagic, ShmLayout.MapSize);
        StableReadResult tooSmall = harness.ReadStable(
            new byte[ShmLayout.V1MinimumSize - 1],
            ShmLayout.V1MinimumSize - 1);

        Assert.Equal("InvalidMagic", invalid.Status);
        Assert.Contains("Invalid shared memory magic", invalid.Error);
        Assert.Equal("ViewTooSmall", tooSmall.Status);
        Assert.Contains("too small", tooSmall.Error);
    }

    [Fact]
    public void StableRead_AcceptsByteForByteStableV2Snapshot()
    {
        using var harness = new SharedMemoryReaderHarness();
        byte[] buffer = BrokerTestData.V2Buffer(counter: 9);

        StableReadResult result = harness.ReadStable(buffer, ShmLayout.MapSize);

        Assert.Equal("Success", result.Status);
        Assert.Equal(ShmLayout.Version, result.RawVersion);
        Assert.Equal(0, result.RetryCount);
        Assert.Equal("", result.Error);
    }

    [Fact]
    public void StableRead_AcceptsOldV2SnapshotWithoutExtension()
    {
        using var harness = new SharedMemoryReaderHarness();
        byte[] buffer = BrokerTestData.V2Buffer(
            counter: 9,
            extensionMagic: 0,
            instanceId: 0,
            monotonicPublishMs: 0);

        StableReadResult result = harness.ReadStable(buffer, ShmLayout.MapSize);

        Assert.Equal("Success", result.Status);
        Assert.Equal(ShmLayout.Version, result.RawVersion);
        Assert.Equal(0, result.RetryCount);
    }

    [Fact]
    public void StableRead_AcceptsExtendedInitializationWithoutCounterAdvance()
    {
        using var harness = new SharedMemoryReaderHarness();
        byte[] buffer = BrokerTestData.V2Buffer(
            counter: 0,
            cpuTemperature: -1,
            source: "None",
            instanceId: 77);

        StableReadResult result = harness.ReadStable(buffer, ShmLayout.MapSize);

        Assert.Equal("Success", result.Status);
        Assert.Equal(ShmLayout.Version, result.RawVersion);
        Assert.Equal(0, result.RetryCount);
    }

    [Fact]
    public void StableRead_RejectsOddCommitSequenceAsInFlightSnapshot()
    {
        using var harness = new SharedMemoryReaderHarness();
        byte[] buffer = BrokerTestData.V2Buffer(counter: 9, commitSequence: 3);

        StableReadResult result = harness.ReadStable(buffer, ShmLayout.MapSize);

        Assert.Equal("Unstable", result.Status);
        Assert.Equal(4, result.RetryCount);
        Assert.Contains("changed during", result.Error);
    }

    [Fact]
    public void StableRead_RejectsUnknownOrIncompleteExtension()
    {
        using var harness = new SharedMemoryReaderHarness();
        byte[] unknownExtension = BrokerTestData.V2Buffer(
            counter: 1,
            extensionMagic: 0x12345678);
        byte[] missingIdentity = BrokerTestData.V2Buffer(counter: 1, instanceId: 0);

        StableReadResult unknown = harness.ReadStable(unknownExtension, ShmLayout.MapSize);
        StableReadResult incomplete = harness.ReadStable(missingIdentity, ShmLayout.MapSize);

        Assert.Equal("InvalidExtension", unknown.Status);
        Assert.Contains("Unsupported shared memory extension", unknown.Error);
        Assert.Equal("InvalidExtension", incomplete.Status);
        Assert.Contains("Invalid shared memory extension payload", incomplete.Error);
    }
}
