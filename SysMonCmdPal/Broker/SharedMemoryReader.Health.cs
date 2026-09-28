// SysMonCmdPal/Broker/SharedMemoryReader.Health.cs

using System;
using System.Diagnostics;

namespace SysMonCmdPal.Broker;

public sealed partial class SharedMemoryReader
{
    // ---- T2-4：四个阈值的真实关系（本注释纠正原误推导）----
    //  * StallTimeout ≡ BrokerSensorSnapshot.AvailabilityTimeout = 5s。它**不是独立常量**，
    //    同一个数同时承担「快照新鲜度过期」与「单次 raw stall 观察」两件事；
    //    写断言时不得把这两条机制混成一条证据（见本文件下方三处显式分离的注释）。
    //  * PollIntervalMilliseconds = 1s（SharedMemoryReader.cs）——这是**观察频率**，不是等待时长。
    //    原路线图把 5s 当观察间隔、据此推出「连续 3 次 ≈ ≥15s 容忍窗口」是错的：
    //    计数式去抖的实际确认耗时 ≈ StallTimeout + (threshold − 1) × 1s，
    //    即阈值 2→3 只把确认从 6s 推到 7s —— 仅多 1s，达不到「8s 档不误判」。
    //  * Broker 声明的合法静默上界 ≈ 15s（SysMonBroker/Program.cs:30-34 推导：
    //    单硬件更新超时 8s + 慢主板更新 ≈5s + 2s 周期），计数式的 7s 落在该上界之内 ⇒ 高负载误杀。
    //  * ConfirmStallWindow = 15s：本任务改用的**时间基**确认窗口，与上述合法上界对齐，
    //    且仍低于 Broker 自身 CycleTimeout=20s（再晚就与 Broker 看门狗重启撞车，失去意义）。
    //
    // 【本修复的实际收益边界（诚实声明，勿夸大）】不改变用户可见的降级时机——那由 5s
    // AvailabilityTimeout 新鲜度网支配（ConfirmStallWindow>5s 治不到它）；消除的是**确认链的
    // 提前动作**：传统无 extension 路径上因瞬时慢周期而提前 MarkUnavailable + Disconnect
    // （及其附带的「重连退避 + 首帧 baseline 不可用」窗口）。现代路径本就不 Disconnect
    // （确认链仅在 !HasExtension 时断开），该语义保持不变、不扩大。
    private static readonly TimeSpan StallTimeout = BrokerSensorSnapshot.AvailabilityTimeout;

    // 确认链阈值：自上次 counter 前进起持续静默 ≥ 此窗口才认定「真停滞」。必须有界——
    // 窗口耗尽即确认，不得无限容忍慢周期。取代原计数式 StallDebounceThreshold（零测试覆盖，
    // 且如上所述只多 1s，无法覆盖合法静默上界）。
    private static readonly TimeSpan ConfirmStallWindow = TimeSpan.FromSeconds(15);

    private int? _lastCounter;
    private long _lastBrokerTimestampTicks;
    private ulong _lastInstanceId;
    private long _lastCounterAdvanceTimestamp;
    private bool _hasObservedSnapshot;
    private bool _hasPublishedSnapshot;
    private string _lastObservedMapName = "";
    private bool _awaitingCounterAdvance;
    private int _restartBaselineCounter;

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
                bool stallConfirmed = IsStallConfirmed();
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
            // 【即时降级 —— 刻意旁路 ConfirmStallWindow，勿改成去抖】
            // 判据依据是 Broker 自己写下的 publish 时刻已超过 StallTimeout 未刷新：这是**既成事实**
            // （新鲜度已过期），不属于「counter 暂未推进、下一次可能推进」的瞬时抖动，容忍它没有意义。
            // 因此本分支直接确认，不进时间窗；ExtendedSnapshot_WithExpiredPublishTimeIsImmediately...
            // 用例即为此语义的门禁（名字与期望值都不得弱化）。
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
            bool stallConfirmed = IsStallConfirmed();
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
        // 成功发布即恢复：上方 _lastCounterAdvanceTimestamp 的刷新已把确认窗口重新起算，
        // 时间基确认链无需再单独清零计数（原计数式实现才需要这一步）。

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
    /// 确认链（时间基，与 raw 观察面 <see cref="IsStalled"/> 分离）：自上次 counter 前进起
    /// 持续静默 ≥ <see cref="ConfirmStallWindow"/> 才认定「真停滞」。
    ///
    /// 三条判据不得混淆：
    ///   1) <see cref="IsStalled"/>：5s 的**单次 raw 观察**（写进 Diagnostics.IsStalled）；
    ///   2) 本方法：**确认链**（15s 窗口），决定是否 MarkUnavailable/Disconnect；
    ///   3) BrokerSensorSnapshot.IsUsable：**新鲜度网**（同为 5s，但语义独立），
    ///      仅按 LastAvailableTimestamp 判定，与确认链无关。
    /// 断言时一条只代表一条机制，否则会把新鲜度自然过期「蹭看」成确认链效果。
    ///
    /// _lastCounterAdvanceTimestamp == 0 是「刚重启/刚重连，窗口从下一次前进重新计」的哨兵，
    /// 此时一律不确认（与原实现 IsStalled() 的短路语义一致，重启检测/reset 行为不变）。
    /// 窗口**有界**：耗尽即返回 true，不存在无限容忍。
    /// </summary>
    private bool IsStallConfirmed()
    {
        if (_lastCounterAdvanceTimestamp <= 0)
            return false;

        // ConfirmStallWindow(15s) > StallTimeout(5s)，故窗口耗尽必然已满足 raw stall 观察；
        // 确认链是观察面的**超集**，不会在观察尚未成立时提前确认。
        return Stopwatch.GetElapsedTime(_lastCounterAdvanceTimestamp) >= ConfirmStallWindow;
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
