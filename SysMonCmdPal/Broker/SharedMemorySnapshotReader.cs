// SysMonCmdPal/Broker/SharedMemorySnapshotReader.cs

using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace SysMonCmdPal.Broker;

/// <summary>Copies and validates one committed Broker shared-memory snapshot.</summary>
internal sealed class SharedMemorySnapshotReader
{
    private const int SnapshotReadAttempts = 4;
    private const int SnapshotVerificationSpinWait = 128;

    private readonly byte[] _buffer = new byte[ShmLayout.MapSize];
    private readonly byte[] _verificationBuffer = new byte[ShmLayout.MapSize];

    public StableReadStatus TryRead(
        MemoryMappedViewAccessor accessor,
        int viewSize,
        out StableSnapshot snapshot,
        out int rawVersion,
        out int retryCount,
        out string error)
    {
        snapshot = default;
        rawVersion = 0;
        retryCount = 0;
        error = "";

        if (viewSize < ShmLayout.V1MinimumSize)
        {
            error = $"Shared memory is too small: {viewSize} bytes";
            return StableReadStatus.ViewTooSmall;
        }

        if (viewSize > ShmLayout.LegacyMapSize && viewSize < ShmLayout.MapSize)
        {
            error = $"Shared memory has unsupported size: {viewSize} bytes";
            return StableReadStatus.ViewTooSmall;
        }

        for (int attempt = 0; attempt < SnapshotReadAttempts; attempt++)
        {
            int magicBefore = accessor.ReadInt32(ShmLayout.OffMagic);
            rawVersion = accessor.ReadInt32(ShmLayout.OffVersion);

            if (magicBefore != ShmLayout.MagicValue)
            {
                error = $"Invalid shared memory magic: 0x{magicBefore:X8}";
                return StableReadStatus.InvalidMagic;
            }

            if (!TryResolveLayout(rawVersion, viewSize, out SnapshotLayout layout, out error))
                return StableReadStatus.UnsupportedVersion;

            int counterBefore = accessor.ReadInt32(layout.CounterOffset);
            int commitSequenceBefore = layout.Version >= 2
                ? accessor.ReadInt32(ShmLayout.OffCommitSequence)
                : 0;
            int extensionMagicBefore = layout.Version >= 2
                ? accessor.ReadInt32(ShmLayout.OffExtensionMagic)
                : 0;

            if ((commitSequenceBefore & 1) != 0)
            {
                retryCount++;
                Thread.Yield();
                continue;
            }

            if (extensionMagicBefore != 0 &&
                extensionMagicBefore != ShmLayout.ExtensionMagicValue)
            {
                error = $"Unsupported shared memory extension: 0x{extensionMagicBefore:X8}";
                return StableReadStatus.InvalidExtension;
            }

            Thread.MemoryBarrier();
            int firstRead = accessor.ReadArray(0, _buffer, 0, layout.ReadLength);
            Thread.MemoryBarrier();
            int counterAfterFirstRead = accessor.ReadInt32(layout.CounterOffset);
            int commitSequenceAfterFirstRead = layout.Version >= 2
                ? accessor.ReadInt32(ShmLayout.OffCommitSequence)
                : 0;

            if (firstRead != layout.ReadLength ||
                counterBefore != counterAfterFirstRead ||
                commitSequenceBefore != commitSequenceAfterFirstRead ||
                (commitSequenceAfterFirstRead & 1) != 0)
            {
                retryCount++;
                Thread.Yield();
                continue;
            }

            Thread.SpinWait(SnapshotVerificationSpinWait);
            Thread.MemoryBarrier();
            int secondRead = accessor.ReadArray(0, _verificationBuffer, 0, layout.ReadLength);
            Thread.MemoryBarrier();
            int counterAfterSecondRead = accessor.ReadInt32(layout.CounterOffset);
            int commitSequenceAfterSecondRead = layout.Version >= 2
                ? accessor.ReadInt32(ShmLayout.OffCommitSequence)
                : 0;
            int magicAfter = accessor.ReadInt32(ShmLayout.OffMagic);
            int headerAfter = accessor.ReadInt32(ShmLayout.OffVersion);
            int extensionMagicAfter = layout.Version >= 2
                ? accessor.ReadInt32(ShmLayout.OffExtensionMagic)
                : 0;

            bool headerStable = magicAfter == magicBefore &&
                (layout.Version == ShmLayout.MinimumSupportedVersion || headerAfter == rawVersion);
            bool buffersMatch = _buffer.AsSpan(0, layout.ReadLength)
                .SequenceEqual(_verificationBuffer.AsSpan(0, layout.ReadLength));
            bool copiedHeaderMatches =
                BitConverter.ToInt32(_buffer, ShmLayout.OffMagic) == magicBefore &&
                BitConverter.ToInt32(_buffer, layout.CounterOffset) == counterBefore &&
                (layout.Version < 2 ||
                    (BitConverter.ToInt32(_buffer, ShmLayout.OffVersion) == rawVersion &&
                     BitConverter.ToInt32(_buffer, ShmLayout.OffCommitSequence) == commitSequenceBefore &&
                     BitConverter.ToInt32(_buffer, ShmLayout.OffExtensionMagic) == extensionMagicBefore));

            if (secondRead != layout.ReadLength ||
                counterBefore != counterAfterSecondRead ||
                commitSequenceBefore != commitSequenceAfterSecondRead ||
                (commitSequenceAfterSecondRead & 1) != 0 ||
                extensionMagicBefore != extensionMagicAfter ||
                !headerStable ||
                !buffersMatch ||
                !copiedHeaderMatches)
            {
                retryCount++;
                Thread.Yield();
                continue;
            }

            long brokerTimestampTicks = BitConverter.ToInt64(_buffer, layout.TimestampOffset);
            bool hasExtension = extensionMagicBefore == ShmLayout.ExtensionMagicValue;
            ulong instanceId = hasExtension
                ? BitConverter.ToUInt64(_buffer, ShmLayout.OffInstanceId)
                : 0;
            long monotonicPublishMs = hasExtension
                ? BitConverter.ToInt64(_buffer, ShmLayout.OffMonotonicPublishMs)
                : 0;

            if (hasExtension && (instanceId == 0 || monotonicPublishMs <= 0))
            {
                error = "Invalid shared memory extension payload";
                return StableReadStatus.InvalidExtension;
            }

            snapshot = new StableSnapshot(
                layout,
                _buffer,
                counterBefore,
                brokerTimestampTicks,
                hasExtension,
                commitSequenceBefore,
                instanceId,
                monotonicPublishMs);
            return StableReadStatus.Success;
        }

        error = $"Shared memory changed during {SnapshotReadAttempts} snapshot attempts";
        return StableReadStatus.Unstable;
    }

    private static bool TryResolveLayout(
        int rawVersion,
        int viewSize,
        out SnapshotLayout layout,
        out string error)
    {
        if (viewSize <= ShmLayout.LegacyMapSize)
        {
            layout = new SnapshotLayout(
                Version: ShmLayout.MinimumSupportedVersion,
                CounterOffset: ShmLayout.V1OffCounter,
                CpuTempOffset: ShmLayout.V1OffCpuTemp,
                SourceOffset: ShmLayout.V1OffSource,
                GpuCountOffset: ShmLayout.V1OffGpuCount,
                GpuBaseOffset: ShmLayout.V1OffGpuBase,
                TimestampOffset: ShmLayout.V1OffTimestamp,
                SensorCountOffset: -1,
                SensorBaseOffset: -1,
                ReadLength: Math.Min(viewSize, ShmLayout.LegacyMapSize));
            error = "";
            return true;
        }

        if (rawVersion != ShmLayout.Version)
        {
            layout = default;
            error = $"Unsupported shared memory version: {rawVersion} " +
                $"(supported {ShmLayout.MinimumSupportedVersion}-{ShmLayout.Version})";
            return false;
        }

        layout = new SnapshotLayout(
            Version: rawVersion,
            CounterOffset: ShmLayout.OffCounter,
            CpuTempOffset: ShmLayout.OffCpuTemp,
            SourceOffset: ShmLayout.OffSource,
            GpuCountOffset: ShmLayout.OffGpuCount,
            GpuBaseOffset: ShmLayout.OffGpuBase,
            TimestampOffset: ShmLayout.OffTimestamp,
            SensorCountOffset: ShmLayout.OffSensorCount,
            SensorBaseOffset: ShmLayout.OffSensorBase,
            ReadLength: ShmLayout.MapSize);
        error = "";
        return true;
    }
}

internal enum StableReadStatus
{
    Success,
    InvalidMagic,
    UnsupportedVersion,
    InvalidExtension,
    ViewTooSmall,
    Unstable,
}

internal readonly record struct SnapshotLayout(
    int Version,
    int CounterOffset,
    int CpuTempOffset,
    int SourceOffset,
    int GpuCountOffset,
    int GpuBaseOffset,
    int TimestampOffset,
    int SensorCountOffset,
    int SensorBaseOffset,
    int ReadLength);

internal readonly record struct StableSnapshot(
    SnapshotLayout Layout,
    byte[] Data,
    int Counter,
    long BrokerTimestampTicks,
    bool HasExtension,
    int CommitSequence,
    ulong InstanceId,
    long MonotonicPublishMs);
