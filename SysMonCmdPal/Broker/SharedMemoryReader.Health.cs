// SysMonCmdPal/Broker/SharedMemoryReader.Health.cs

using System;
using System.Diagnostics;

namespace SysMonCmdPal.Broker;

public sealed partial class SharedMemoryReader
{
    private static readonly TimeSpan StallTimeout = BrokerSensorSnapshot.AvailabilityTimeout;

    // stall 去抖：Broker 单周期最长可达 8s（硬件超时），StallTimeout 仅 5s，
    // 连续 StallDebounceThreshold 次检测到 stall 才 MarkUnavailable/Disconnect。
    private const int StallDebounceThreshold = 2;

    private int? _lastCounter;
    private long _lastBrokerTimestampTicks;
    private ulong _lastInstanceId;
    private long _lastCounterAdvanceTimestamp;
    private bool _hasObservedSnapshot;
    private bool _hasPublishedSnapshot;
    private string _lastObservedMapName = "";
    private bool _awaitingCounterAdvance;
    private int _restartBaselineCounter;

    // 仅由 reader 线程访问：连续 stall 观察次数（恢复后清零）。
    private int _consecutiveStalls;
    // 上次计 restartDelta 时的 instanceId（无 extension 时为 0），用于按实例去重。
    private ulong? _lastRestartCountedInstanceId;

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

        // 首连一律要求 counter 前进验证（双快照）后再发布：
        // 旧版（无 extension）Broker 初始化窗口内 counter≠0 的历史假设不可靠，
        // 直接发布可能泄漏初始化中的空数据；带 extension 的提交帧也需要
        // 等后续 counter 前进确认该帧已稳定提交。
        if (!_hasObservedSnapshot)
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
                // 按 instanceId 去重：仅当 instanceId 与上次计数时不同才 +1，
                // 避免一次重启被计两次（如 extensionRemoved 后再 instanceChanged）。
                if (_lastRestartCountedInstanceId != snapshot.InstanceId)
                {
                    restartDelta = 1;
                    _lastRestartCountedInstanceId = snapshot.InstanceId;
                }

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
                bool stallConfirmed = RecordStallObservation(stalled);
                ReportWaitingForCommit(
                    snapshot,
                    brokerTimestampUtc,
                    retryCount,
                    restartDelta: 0,
                    stalled
                        ? $"Broker commit counter has not advanced for {StallTimeout.TotalSeconds:F0} seconds"
                        : "Broker is initialized; waiting for the first data commit",
                    stalled: stalled,
                    connected: !stallConfirmed);
                if (stallConfirmed)
                    Disconnect();
                return;
            }

            _awaitingCounterAdvance = false;
            skipPreviousCounterComparison = true;
        }

        if (snapshot.HasExtension && IsBrokerPublishStalled(snapshot.MonotonicPublishMs))
        {
            // 去抖：连续 StallDebounceThreshold 次 publish 超时才 MarkUnavailable，
            // 容忍 Broker 单周期可达 8s（StallTimeout 仅 5s）的硬件超时。
            bool stallConfirmed = RecordStallObservation(true);
            if (_lastCounter != counter || _lastInstanceId != snapshot.InstanceId)
                ObserveBaseline(snapshot);

            if (stallConfirmed)
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
            bool stallConfirmed = RecordStallObservation(stalled);
            string mapName = _connectedMapName;
            if (stallConfirmed && !snapshot.HasExtension)
            {
                BrokerPushReceiver.Instance.MarkUnavailable();
                Disconnect();
            }

            UpdateDiagnostics(
                connected: snapshot.HasExtension || !stallConfirmed,
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
        // 成功发布即恢复，stall 去抖计数清零。
        _consecutiveStalls = 0;

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

    /// <summary>
    /// 记录一次 stall 观察：连续 StallDebounceThreshold 次才确认（返回 true）；
    /// 观察到非 stall（恢复）时计数清零。仅由 reader 线程调用。
    /// </summary>
    private bool RecordStallObservation(bool stalled)
    {
        if (!stalled)
        {
            _consecutiveStalls = 0;
            return false;
        }

        _consecutiveStalls++;
        return _consecutiveStalls >= StallDebounceThreshold;
    }

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
