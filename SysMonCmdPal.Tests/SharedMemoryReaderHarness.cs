// Copyright (c) 2026 SysMonCmdPal

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.CompilerServices;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal.Tests;

internal sealed class SharedMemoryReaderHarness : IDisposable
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type ReaderType = typeof(SharedMemoryReader);
    private static readonly Type SnapshotReaderType = typeof(SharedMemorySnapshotReader);
    private static readonly MethodInfo ResolveLayoutMethod = SnapshotReaderType.GetMethod(
        "TryResolveLayout",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(SnapshotReaderType.FullName, "TryResolveLayout");
    private static readonly MethodInfo ProcessSnapshotMethod = ReaderType.GetMethod(
        "ProcessStableSnapshot",
        InstanceFields)
        ?? throw new MissingMethodException(ReaderType.FullName, "ProcessStableSnapshot");
    private static readonly FieldInfo DiagnosticsField = ReaderType.GetField(
        "s_diagnostics",
        BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(ReaderType.FullName, "s_diagnostics");

    private readonly object _reader = RuntimeHelpers.GetUninitializedObject(ReaderType);
    private readonly SharedMemorySnapshotReader _snapshotReader = new();
    private readonly BrokerRuntimeTestScope _brokerScope = new();
    private readonly SharedMemoryReaderDiagnostics _originalDiagnostics;

    public SharedMemoryReaderHarness()
    {
        _originalDiagnostics = SharedMemoryReader.Diagnostics;
        DiagnosticsField.SetValue(null, new SharedMemoryReaderDiagnostics());
        SetField("_connectedMapName", ShmLayout.MapName);
        SetField("_lastObservedMapName", "");
    }

    public BrokerPushReceiver Receiver => _brokerScope.Receiver;

    public void SetReceiverSnapshot(BrokerSensorSnapshot snapshot) =>
        _brokerScope.SetSnapshot(snapshot);

    public void ExpireReceiverAvailability()
    {
        BrokerSensorSnapshot source = Receiver.Snapshot;
        long expired = Stopwatch.GetTimestamp() - (Stopwatch.Frequency * 60);
        _brokerScope.SetSnapshot(new BrokerSensorSnapshot
        {
            CpuTemperature = source.CpuTemperature,
            CpuSource = source.CpuSource,
            Gpus = source.Gpus,
            AllSensors = source.AllSensors,
            LastPush = source.LastPush,
            LastPing = source.LastPing,
            LastDataTimestamp = source.LastDataTimestamp,
            LastAvailableTimestamp = expired,
        });
    }

    public ResolvedSnapshotLayout ResolveLayout(int rawVersion, int viewSize)
    {
        object?[] arguments = [rawVersion, viewSize, null, null];
        bool success = (bool)(ResolveLayoutMethod.Invoke(null, arguments) ?? false);
        object? layout = arguments[2];
        string error = arguments[3] as string ?? "";
        return new ResolvedSnapshotLayout(
            success,
            layout == null ? 0 : GetProperty<int>(layout, "Version"),
            layout == null ? 0 : GetProperty<int>(layout, "CounterOffset"),
            layout == null ? 0 : GetProperty<int>(layout, "SensorCountOffset"),
            layout == null ? 0 : GetProperty<int>(layout, "ReadLength"),
            error);
    }

    public StableReadResult ReadStable(byte[] buffer, int viewSize)
    {
        using MemoryMappedFile mmf = MemoryMappedFile.CreateNew(null, viewSize);
        using MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(
            0,
            viewSize,
            MemoryMappedFileAccess.ReadWrite);
        accessor.WriteArray(0, buffer, 0, Math.Min(buffer.Length, viewSize));

        StableReadStatus status = _snapshotReader.TryRead(
            accessor,
            viewSize,
            out _,
            out int rawVersion,
            out int retryCount,
            out string error);
        return new StableReadResult(status.ToString(), rawVersion, retryCount, error);
    }

    public void ProcessV2(byte[] buffer, int counter, long timestampTicks, int retryCount = 0) =>
        Process(buffer, counter, timestampTicks, ShmLayout.Version, ShmLayout.MapSize, retryCount);

    public void PublishFirstV2(byte[] buffer, int counter, long timestampTicks, int retryCount = 0)
    {
        byte[] baseline = (byte[])buffer.Clone();
        int baselineCounter = counter == 1 ? 0 : unchecked(counter - 1);
        long baselineTimestamp = timestampTicks > DateTime.MinValue.Ticks
            ? timestampTicks - 1
            : timestampTicks;
        BitConverter.TryWriteBytes(
            baseline.AsSpan(ShmLayout.OffCounter, sizeof(int)),
            baselineCounter);
        BitConverter.TryWriteBytes(
            baseline.AsSpan(ShmLayout.OffTimestamp, sizeof(long)),
            baselineTimestamp);

        ProcessV2(baseline, baselineCounter, baselineTimestamp, retryCount);
        ProcessV2(buffer, counter, timestampTicks, retryCount);
    }

    public void ProcessV1(byte[] buffer, int counter, long timestampTicks, int retryCount = 0) =>
        Process(
            buffer,
            counter,
            timestampTicks,
            rawVersion: counter,
            ShmLayout.LegacyMapSize,
            retryCount);

    public void SetLastCounterAdvanceElapsed(TimeSpan elapsed)
    {
        long timestamp = Stopwatch.GetTimestamp() -
            (long)(Stopwatch.Frequency * elapsed.TotalSeconds);
        SetField("_lastCounterAdvanceTimestamp", timestamp);
    }

    public void Dispose()
    {
        DiagnosticsField.SetValue(null, _originalDiagnostics);
        _brokerScope.Dispose();
    }

    private void Process(
        byte[] buffer,
        int counter,
        long timestampTicks,
        int rawVersion,
        int viewSize,
        int retryCount)
    {
        SnapshotLayout layout = ResolveLayoutObject(rawVersion, viewSize);
        byte[] snapshotData = new byte[ShmLayout.MapSize];
        buffer.AsSpan(0, Math.Min(buffer.Length, snapshotData.Length)).CopyTo(snapshotData);

        bool hasExtension = buffer.Length >= ShmLayout.MapSize &&
            BitConverter.ToInt32(buffer, ShmLayout.OffExtensionMagic) == ShmLayout.ExtensionMagicValue;
        int commitSequence = buffer.Length >= ShmLayout.OffCommitSequence + sizeof(int)
            ? BitConverter.ToInt32(buffer, ShmLayout.OffCommitSequence)
            : 0;
        ulong instanceId = hasExtension
            ? BitConverter.ToUInt64(buffer, ShmLayout.OffInstanceId)
            : 0;
        long monotonicPublishMs = hasExtension
            ? BitConverter.ToInt64(buffer, ShmLayout.OffMonotonicPublishMs)
            : 0;

        var stableSnapshot = new StableSnapshot(
            layout,
            snapshotData,
            counter,
            timestampTicks,
            hasExtension,
            commitSequence,
            instanceId,
            monotonicPublishMs);
        ProcessSnapshotMethod.Invoke(_reader, [stableSnapshot, retryCount]);
    }

    private static SnapshotLayout ResolveLayoutObject(int rawVersion, int viewSize)
    {
        object?[] arguments = [rawVersion, viewSize, null, null];
        bool success = (bool)(ResolveLayoutMethod.Invoke(null, arguments) ?? false);
        if (!success || arguments[2] == null)
            throw new InvalidOperationException(arguments[3] as string ?? "Layout resolution failed");
        return (SnapshotLayout)(arguments[2]
            ?? throw new InvalidOperationException("Layout resolution returned no layout"));
    }

    private void SetField(string name, object? value) =>
        RequiredField(name).SetValue(_reader, value);

    private static FieldInfo RequiredField(string name) =>
        ReaderType.GetField(name, InstanceFields)
        ?? throw new MissingFieldException(ReaderType.FullName, name);

    private static T GetProperty<T>(object instance, string name) =>
        (T)(instance.GetType().GetProperty(name)?.GetValue(instance)
            ?? throw new MissingMemberException(instance.GetType().FullName, name));
}

internal readonly record struct ResolvedSnapshotLayout(
    bool Success,
    int Version,
    int CounterOffset,
    int SensorCountOffset,
    int ReadLength,
    string Error);

internal readonly record struct StableReadResult(
    string Status,
    int RawVersion,
    int RetryCount,
    string Error);
