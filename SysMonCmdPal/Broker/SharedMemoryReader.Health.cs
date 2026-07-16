// SysMonCmdPal/Broker/SharedMemoryReader.Health.cs

using System;
using System.Diagnostics;

namespace SysMonCmdPal.Broker;

public sealed partial class SharedMemoryReader
{
    private static readonly TimeSpan StallTimeout = BrokerSensorSnapshot.AvailabilityTimeout;

    private int? _lastCounter;
    private long _lastBrokerTimestampTicks;
    private ulong _lastInstanceId;
    private long _lastCounterAdvanceTimestamp;
    private bool _hasObservedSnapshot;
    private bool _hasPublishedSnapshot;
    private string _lastObservedMapName = "";
    private bool _awaitingCounterAdvance;
    private int _restartBaselineCounter;

    private static readonly object s_diagnosticsLock = new();
    private static SharedMemoryReaderDiagnostics s_diagnostics = new();

    public static SharedMemoryReaderDiagnostics Diagnostics
    {
        get { lock (s_diagnosticsLock) return s_diagnostics; }
    }

    private void ProcessStableSnapshot(StableSnapshot snapshot, int retryCount)
    {
        int counter = snapshot.Counter;
        long brokerTimestampTicks = snapshot.BrokerTimestampTicks;
        DateTime brokerTimestampUtc = ToUtcTimestamp(brokerTimestampTicks);
        int restartDelta = 0;
        bool skipPreviousCounterComparison = false;

        // A structurally committed SMX1 initialization is not sensor data.
        // Require a later counter advance from the same instance before publish.
        if (!_hasObservedSnapshot && (snapshot.HasExtension || counter == 0))
        {
            ObserveBaseline(snapshot);
            _awaitingCounterAdvance = !snapshot.HasExtension;
            _restartBaselineCounter = counter;
            ReportWaitingForCommit(
                snapshot,
                brokerTimestampUtc,
                retryCount,
                restartDelta: 0,
                "Broker is initialized; waiting for the first data commit");
            return;
        }

        if (_hasObservedSnapshot)
        {
            bool mapChanged = !string.Equals(
                _lastObservedMapName,
                _connectedMapName,
                StringComparison.Ordinal);
            bool counterMovedBackwards = _lastCounter.HasValue &&
                CounterMovedBackwards(_lastCounter.Value, counter);
            bool instanceChanged = snapshot.HasExtension &&
                snapshot.InstanceId != _lastInstanceId;
            bool extensionRemoved = _lastInstanceId != 0 && !snapshot.HasExtension;
            bool sameCounterWithNewTimestamp = !snapshot.HasExtension &&
                _lastCounter == counter &&
                _lastBrokerTimestampTicks != 0 &&
                brokerTimestampTicks != 0 &&
                brokerTimestampTicks != _lastBrokerTimestampTicks;

            if (mapChanged ||
                instanceChanged ||
                extensionRemoved ||
                counterMovedBackwards ||
                sameCounterWithNewTimestamp)
            {
                restartDelta = 1;
                BrokerPushReceiver.Instance.MarkUnavailable();
                _lastCounterAdvanceTimestamp = 0;

                if (snapshot.HasExtension ||
                    extensionRemoved ||
                    counter == 0 ||
                    sameCounterWithNewTimestamp)
                {
                    ObserveBaseline(snapshot);
                    _awaitingCounterAdvance = !snapshot.HasExtension;
                    _restartBaselineCounter = counter;
                    ReportWaitingForCommit(
                        snapshot,
                        brokerTimestampUtc,
                        retryCount,
                        restartDelta,
                        "Broker restart detected; waiting for the next committed update");
                    return;
                }

                skipPreviousCounterComparison = true;
            }
        }

        if (_awaitingCounterAdvance)
        {
            if (counter == _restartBaselineCounter)
            {
                bool stalled = IsStalled();
                ReportWaitingForCommit(
                    snapshot,
                    brokerTimestampUtc,
                    retryCount,
                    restartDelta: 0,
                    stalled
                        ? $"Broker commit counter has not advanced for {StallTimeout.TotalSeconds:F0} seconds"
                        : "Broker is initialized; waiting for the first data commit",
                    stalled: stalled,
                    connected: !stalled);
                if (stalled)
                    Disconnect();
                return;
            }

            _awaitingCounterAdvance = false;
            skipPreviousCounterComparison = true;
        }

        if (snapshot.HasExtension && IsBrokerPublishStalled(snapshot.MonotonicPublishMs))
        {
            if (_lastCounter != counter || _lastInstanceId != snapshot.InstanceId)
                ObserveBaseline(snapshot);

            BrokerPushReceiver.Instance.MarkUnavailable();
            UpdateDiagnostics(
                connected: true,
                protocolValid: true,
                stalled: true,
                mapName: _connectedMapName,
                counter: counter,
                version: snapshot.Layout.Version,
                brokerTimestampUtc: brokerTimestampUtc,
                usesCommitSequence: true,
                commitSequence: snapshot.CommitSequence,
                instanceId: snapshot.InstanceId,
                monotonicPublishMs: snapshot.MonotonicPublishMs,
                restartDelta: restartDelta,
                unstableReadDelta: retryCount,
                error: $"Broker has not published for {StallTimeout.TotalSeconds:F0} seconds");
            return;
        }

        if (!skipPreviousCounterComparison && _lastCounter == counter)
        {
            bool stalled = IsStalled();
            string mapName = _connectedMapName;
            if (stalled && !snapshot.HasExtension)
            {
                BrokerPushReceiver.Instance.MarkUnavailable();
                Disconnect();
            }

            UpdateDiagnostics(
                connected: snapshot.HasExtension || !stalled,
                protocolValid: true,
                stalled: stalled,
                mapName: mapName,
                counter: counter,
                version: snapshot.Layout.Version,
                brokerTimestampUtc: brokerTimestampUtc,
                usesCommitSequence: snapshot.HasExtension,
                commitSequence: snapshot.CommitSequence,
                instanceId: snapshot.InstanceId,
                monotonicPublishMs: snapshot.MonotonicPublishMs,
                unstableReadDelta: retryCount,
                error: stalled
                    ? $"Broker commit counter has not advanced for {StallTimeout.TotalSeconds:F0} seconds"
                    : _hasPublishedSnapshot
                        ? ""
                        : "Broker is initialized; waiting for the first data commit");
            return;
        }

        if (!SharedMemorySnapshotParser.TryParse(
            snapshot,
            out ParsedSnapshot parsed,
            out string parseError))
        {
            BrokerPushReceiver.Instance.MarkUnavailable();
            UpdateDiagnostics(
                connected: true,
                protocolValid: false,
                stalled: false,
                mapName: _connectedMapName,
                counter: counter,
                version: snapshot.Layout.Version,
                brokerTimestampUtc: brokerTimestampUtc,
                usesCommitSequence: snapshot.HasExtension,
                commitSequence: snapshot.CommitSequence,
                instanceId: snapshot.InstanceId,
                monotonicPublishMs: snapshot.MonotonicPublishMs,
                restartDelta: restartDelta,
                unstableReadDelta: retryCount,
                error: parseError);
            return;
        }

        var nowUtc = DateTime.UtcNow;
        BrokerPushReceiver.Instance.PushSnapshot(
            parsed.CpuTemperature,
            parsed.CpuSource,
            parsed.Gpus,
            parsed.Sensors);

        _lastCounter = counter;
        _lastBrokerTimestampTicks = brokerTimestampTicks;
        _lastInstanceId = snapshot.InstanceId;
        _lastCounterAdvanceTimestamp = Stopwatch.GetTimestamp();
        _hasObservedSnapshot = true;
        _hasPublishedSnapshot = true;
        _lastObservedMapName = _connectedMapName;

        UpdateDiagnostics(
            connected: true,
            protocolValid: true,
            stalled: false,
            mapName: _connectedMapName,
            counter: counter,
            version: snapshot.Layout.Version,
            sensorCount: parsed.Sensors.Count,
            commitUtc: nowUtc,
            brokerTimestampUtc: brokerTimestampUtc,
            usesCommitSequence: snapshot.HasExtension,
            commitSequence: snapshot.CommitSequence,
            instanceId: snapshot.InstanceId,
            monotonicPublishMs: snapshot.MonotonicPublishMs,
            restartDelta: restartDelta,
            unstableReadDelta: retryCount,
            error: "");
    }

    private void ObserveBaseline(StableSnapshot snapshot)
    {
        _lastCounter = snapshot.Counter;
        _lastBrokerTimestampTicks = snapshot.BrokerTimestampTicks;
        _lastInstanceId = snapshot.InstanceId;
        _lastCounterAdvanceTimestamp = Stopwatch.GetTimestamp();
        _hasObservedSnapshot = true;
        _hasPublishedSnapshot = false;
        _lastObservedMapName = _connectedMapName;
    }

    private void ReportWaitingForCommit(
        StableSnapshot snapshot,
        DateTime brokerTimestampUtc,
        int retryCount,
        int restartDelta,
        string error,
        bool stalled = false,
        bool connected = true)
    {
        BrokerPushReceiver.Instance.MarkUnavailable();
        UpdateDiagnostics(
            connected: connected,
            protocolValid: true,
            stalled: stalled,
            mapName: _connectedMapName,
            counter: snapshot.Counter,
            version: snapshot.Layout.Version,
            brokerTimestampUtc: brokerTimestampUtc,
            usesCommitSequence: snapshot.HasExtension,
            commitSequence: snapshot.CommitSequence,
            instanceId: snapshot.InstanceId,
            monotonicPublishMs: snapshot.MonotonicPublishMs,
            restartDelta: restartDelta,
            unstableReadDelta: retryCount,
            error: error);
    }

    private bool IsStalled() => _lastCounterAdvanceTimestamp > 0 &&
        Stopwatch.GetElapsedTime(_lastCounterAdvanceTimestamp) >= StallTimeout;

    private static bool IsBrokerPublishStalled(long monotonicPublishMs)
    {
        long elapsedMilliseconds = unchecked(Environment.TickCount64 - monotonicPublishMs);
        return elapsedMilliseconds < 0 || elapsedMilliseconds >= StallTimeout.TotalMilliseconds;
    }

    private static bool CounterMovedBackwards(int previous, int current)
    {
        uint forwardDistance = unchecked((uint)(current - previous));
        return forwardDistance > int.MaxValue;
    }

    private static DateTime ToUtcTimestamp(long ticks)
    {
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return DateTime.MinValue;

        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static void UpdateDiagnostics(
        bool connected,
        bool? protocolValid = null,
        bool? stalled = null,
        string? mapName = null,
        int? counter = null,
        int? version = null,
        int? sensorCount = null,
        DateTime? commitUtc = null,
        DateTime? brokerTimestampUtc = null,
        bool? usesCommitSequence = null,
        int? commitSequence = null,
        ulong? instanceId = null,
        long? monotonicPublishMs = null,
        int restartDelta = 0,
        int unstableReadDelta = 0,
        int connectionDelta = 0,
        string? error = null)
    {
        lock (s_diagnosticsLock)
        {
            s_diagnostics = s_diagnostics with
            {
                IsConnected = connected,
                IsProtocolValid = protocolValid ?? s_diagnostics.IsProtocolValid,
                IsStalled = stalled ?? s_diagnostics.IsStalled,
                ActiveMapName = mapName ?? s_diagnostics.ActiveMapName,
                LastReadUtc = DateTime.UtcNow,
                LastCommitUtc = commitUtc ?? s_diagnostics.LastCommitUtc,
                LastBrokerTimestampUtc = brokerTimestampUtc ?? s_diagnostics.LastBrokerTimestampUtc,
                UsesCommitSequence = usesCommitSequence ?? s_diagnostics.UsesCommitSequence,
                LastCommitSequence = commitSequence ?? s_diagnostics.LastCommitSequence,
                LastInstanceId = instanceId ?? s_diagnostics.LastInstanceId,
                LastMonotonicPublishMs = monotonicPublishMs ?? s_diagnostics.LastMonotonicPublishMs,
                LastCounter = counter ?? s_diagnostics.LastCounter,
                LastVersion = version ?? s_diagnostics.LastVersion,
                LastSensorCount = sensorCount ?? s_diagnostics.LastSensorCount,
                RestartCount = s_diagnostics.RestartCount + restartDelta,
                UnstableReadCount = s_diagnostics.UnstableReadCount + unstableReadDelta,
                ConnectionCount = s_diagnostics.ConnectionCount + connectionDelta,
                LastError = error ?? s_diagnostics.LastError,
            };
        }
    }
}

public sealed record SharedMemoryReaderDiagnostics
{
    public bool IsConnected { get; init; }
    public bool IsProtocolValid { get; init; }
    public bool IsStalled { get; init; }
    public string ActiveMapName { get; init; } = "";
    public DateTime LastReadUtc { get; init; } = DateTime.MinValue;
    public DateTime LastCommitUtc { get; init; } = DateTime.MinValue;
    public DateTime LastBrokerTimestampUtc { get; init; } = DateTime.MinValue;
    public bool UsesCommitSequence { get; init; }
    public int LastCommitSequence { get; init; }
    public ulong LastInstanceId { get; init; }
    public long LastMonotonicPublishMs { get; init; }
    public int LastCounter { get; init; }
    public int LastVersion { get; init; }
    public int LastSensorCount { get; init; }
    public int RestartCount { get; init; }
    public int UnstableReadCount { get; init; }
    public int ConnectionCount { get; init; }
    public string LastError { get; init; } = "";
}
