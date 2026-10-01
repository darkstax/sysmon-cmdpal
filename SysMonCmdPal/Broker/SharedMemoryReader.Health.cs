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

    // 稳态决策核（纯决策，状态留在上方字段）。
    // ⚠ 必须**惰性创建**，不能用字段初始化器：测试 harness 经
    // RuntimeHelpers.GetUninitializedObject 构造 reader（绕过构造函数与字段初始化器），
    // 字段初始化器在那条路径下不会执行 ⇒ 直接读会拿到 null 并抛 NullReferenceException。
    private SnapshotStabilityTracker? _stabilityTracker;

    private SnapshotStabilityTracker _stability =>
        _stabilityTracker ??= new SnapshotStabilityTracker(StallTimeout, ConfirmStallWindow);

    private static readonly object s_diagnosticsLock = new();
    private static SharedMemoryReaderDiagnostics s_diagnostics = new();

    public static SharedMemoryReaderDiagnostics Diagnostics
    {
        get { lock (s_diagnosticsLock) return s_diagnostics; }
    }

    /// <summary>
    /// 稳定快照的处理入口：**编排 + 副作用 dispatch**。
    /// </summary>
    /// <remarks>
    /// 判定条件全集已提取到 <see cref="SnapshotStabilityTracker.Decide"/>（纯决策，无副作用）；
    /// 本方法只负责：构造观测/状态视图 → 拿决策 → 按下发的指令执行副作用 → 上报诊断。
    /// ⚠ 状态字段（含 <c>_lastCounterAdvanceTimestamp</c>）**必须留驻在本类**：
    /// 测试 harness 用反射按名绑定它们（见 SharedMemoryReaderHarness），移家即六个兄弟套件连锁爆炸。
    /// </remarks>
    private void ProcessStableSnapshot(StableSnapshot snapshot, int retryCount)
    {
        DateTime brokerTimestampUtc = ToUtcTimestamp(snapshot.BrokerTimestampTicks);

        // 观测：mapChanged 在 reader 侧比较（两个字段都在本类，字符串不搬进纯决策核）
        var observation = new StabilityObservation(
            Counter: snapshot.Counter,
            BrokerTimestampTicks: snapshot.BrokerTimestampTicks,
            HasExtension: snapshot.HasExtension,
            InstanceId: snapshot.InstanceId,
            MonotonicPublishMs: snapshot.MonotonicPublishMs,
            MapChanged: !string.Equals(_lastObservedMapName, _connectedMapName, StringComparison.Ordinal));

        StabilityDecision decision = _stability.Decide(in observation, BuildStabilityState());

        // 决策要求的前置副作用（顺序即语义，见 ApplyRestartAccounting 注释）
        ApplyRestartAccounting(in decision, snapshot);
        if (decision.ClearAwaitingCounterAdvance)
            _awaitingCounterAdvance = false;

        Dispatch(in decision, snapshot, retryCount, brokerTimestampUtc);
    }

    /// <summary>按决策结果执行副作用（6 个分支各一个小方法，分支体 3-10 行）。</summary>
    private void Dispatch(
        in StabilityDecision d, in StableSnapshot snapshot, int retryCount, DateTime brokerTimestampUtc)
    {
        switch (d.Kind)
        {
            case StabilityKind.FirstConnectBaseline:
                HandleBaselineWait(in snapshot, retryCount, brokerTimestampUtc, restartDelta: 0,
                    "Broker is initialized; waiting for the first data commit");
                return;
            case StabilityKind.RestartAwaitingAdvance:
                HandleBaselineWait(in snapshot, retryCount, brokerTimestampUtc, d.RestartDelta,
                    "Broker restart detected; waiting for the next committed update");
                return;
            case StabilityKind.WaitingForCounterAdvance:
                HandleWaitingForCounterAdvance(in d, in snapshot, retryCount, brokerTimestampUtc);
                return;
            case StabilityKind.PublishTimeExpired:
                HandlePublishTimeExpired(in d, in snapshot, retryCount, brokerTimestampUtc);
                return;
            case StabilityKind.CounterUnchanged:
                HandleCounterUnchanged(in d, in snapshot, retryCount, brokerTimestampUtc);
                return;
            case StabilityKind.ReadyToPublish:
                HandleReadyToPublish(in snapshot, retryCount, brokerTimestampUtc, d.RestartDelta);
                return;
        }
    }

    /// <summary>
    /// 分支 1/2：观察 baseline + 重新起算等待窗口 + 上报等待提交。
    /// </summary>
    /// <remarks>
    /// 首连一律要求 counter 前进验证（双快照）后再发布：旧版（无 extension）Broker 初始化窗口内
    /// counter≠0 的历史假设不可靠，直接发布可能泄漏初始化中的空数据；
    /// 带 extension 的提交帧也需要等后续 counter 前进确认该帧已稳定提交。
    /// </remarks>
    private void HandleBaselineWait(
        in StableSnapshot snapshot, int retryCount, DateTime brokerTimestampUtc,
        int restartDelta, string error)
    {
        ObserveBaseline(snapshot);
        _awaitingCounterAdvance = !snapshot.HasExtension;
        _restartBaselineCounter = snapshot.Counter;
        ReportWaitingForCommit(snapshot, brokerTimestampUtc, retryCount, restartDelta, error);
    }

    /// <summary>分支 3：仍在等 counter 前进且 counter 未变；确认窗耗尽则断连。</summary>
    private void HandleWaitingForCounterAdvance(
        in StabilityDecision d, in StableSnapshot snapshot, int retryCount, DateTime brokerTimestampUtc)
    {
        ReportWaitingForCommit(
            snapshot, brokerTimestampUtc, retryCount, restartDelta: 0,
            d.Stalled
                ? $"Broker commit counter has not advanced for {StallTimeout.TotalSeconds:F0} seconds"
                : "Broker is initialized; waiting for the first data commit",
            stalled: d.Stalled,
            connected: !d.StallConfirmed);
        if (d.StallConfirmed)
            Disconnect();
    }

    /// <summary>
    /// 分支 4：extension 的发布时刻过期 ⇒ 即时降级。
    /// </summary>
    /// <remarks>
    /// 【即时降级 —— 刻意旁路 ConfirmStallWindow，勿改成去抖】
    /// 判据依据是 Broker 自己写下的 publish 时刻已超过 StallTimeout 未刷新：这是**既成事实**
    /// （新鲜度已过期），不属于「counter 暂未推进、下一次可能推进」的瞬时抖动，容忍它没有意义。
    /// 因此本分支直接确认，不进时间窗；ExtendedSnapshot_WithExpiredPublishTimeIsImmediately...
    /// 用例即为此语义的门禁（名字与期望值都不得弱化）。
    /// </remarks>
    private void HandlePublishTimeExpired(
        in StabilityDecision d, in StableSnapshot snapshot, int retryCount, DateTime brokerTimestampUtc)
    {
        if (d.ObserveBaselineFirst)
            ObserveBaseline(snapshot);

        BrokerPushReceiver.Instance.MarkUnavailable();
        Report(SnapshotDeserializer.PublishTimeExpired(
            snapshot, _connectedMapName, brokerTimestampUtc, d.RestartDelta, retryCount, StallTimeout));
    }

    /// <summary>分支 5：counter 未前进（传统路径确认窗耗尽才断连）。</summary>
    private void HandleCounterUnchanged(
        in StabilityDecision d, in StableSnapshot snapshot, int retryCount, DateTime brokerTimestampUtc)
    {
        // 断连判据用的是**断连前**的 map 名（原实现先取局部量再 Disconnect）
        string mapName = _connectedMapName;
        if (d.StallConfirmed && !snapshot.HasExtension)
        {
            BrokerPushReceiver.Instance.MarkUnavailable();
            Disconnect();
        }

        Report(SnapshotDeserializer.CounterUnchanged(
            snapshot, mapName, brokerTimestampUtc, retryCount, StallTimeout,
            d.Stalled, d.StallConfirmed, _hasPublishedSnapshot));
    }

    private void HandleReadyToPublish(
        in StableSnapshot snapshot, int retryCount, DateTime brokerTimestampUtc, int restartDelta)
    {
        if (!SnapshotDeserializer.TryDeserialize(snapshot, out ParsedSnapshot parsed, out string parseError))
        {
            BrokerPushReceiver.Instance.MarkUnavailable();
            Report(SnapshotDeserializer.ParseFailed(
                snapshot, _connectedMapName, brokerTimestampUtc, restartDelta, retryCount, parseError));
            return;
        }

        var nowUtc = DateTime.UtcNow;
        SnapshotDeserializer.Publish(in parsed);
        AdvancePublishedState(snapshot);

        Report(SnapshotDeserializer.Published(
            snapshot, _connectedMapName, brokerTimestampUtc, restartDelta, retryCount,
            parsed.Sensors.Count, nowUtc));
    }

    /// <summary>成功发布后的 6 个字段推进（顺序与集合不得变）。</summary>
    private void AdvancePublishedState(in StableSnapshot snapshot)
    {
        _lastCounter = snapshot.Counter;
        _lastBrokerTimestampTicks = snapshot.BrokerTimestampTicks;
        _lastInstanceId = snapshot.InstanceId;
        _lastCounterAdvanceTimestamp = Stopwatch.GetTimestamp();
        _hasObservedSnapshot = true;
        _hasPublishedSnapshot = true;
        _lastObservedMapName = _connectedMapName;
        // 成功发布即恢复：上方 _lastCounterAdvanceTimestamp 的刷新已把确认窗口重新起算，
        // 时间基确认链无需再单独清零计数（原计数式实现才需要这一步）。
    }

    /// <summary>把适配层产出的参数包交给 Diagnostics（唯一的上报出口）。</summary>
    private static void Report(in DiagnosticUpdate u) => UpdateDiagnostics(
        connected: u.Connected,
        protocolValid: u.ProtocolValid,
        stalled: u.Stalled,
        mapName: u.MapName,
        counter: u.Counter,
        version: u.Version,
        sensorCount: u.SensorCount,
        commitUtc: u.CommitUtc,
        brokerTimestampUtc: u.BrokerTimestampUtc,
        usesCommitSequence: u.UsesCommitSequence,
        commitSequence: u.CommitSequence,
        instanceId: u.InstanceId,
        monotonicPublishMs: u.MonotonicPublishMs,
        restartDelta: u.RestartDelta,
        unstableReadDelta: u.UnstableReadDelta,
        error: u.Error);

    /// <summary>把决策要求的重启计数副作用写回 reader 字段（纯决策核不持有状态）。</summary>
    private void ApplyRestartAccounting(in StabilityDecision decision, in StableSnapshot snapshot)
    {
        if (!decision.RestartDetected)
            return;

        if (decision.UpdateRestartCountedInstanceId)
            _lastRestartCountedInstanceId = snapshot.InstanceId;

        BrokerPushReceiver.Instance.MarkUnavailable();
        // 哨兵 0 =「窗口从下一次 counter 前进重新起算」，语义见 IsStallConfirmed 注释
        _lastCounterAdvanceTimestamp = 0;
    }

    /// <summary>构造决策核所需的状态视图（reader 字段 → 只读结构）。</summary>
    private StabilityState BuildStabilityState() => new(
        LastCounter: _lastCounter,
        LastBrokerTimestampTicks: _lastBrokerTimestampTicks,
        LastInstanceId: _lastInstanceId,
        LastCounterAdvanceTimestamp: _lastCounterAdvanceTimestamp,
        HasObservedSnapshot: _hasObservedSnapshot,
        AwaitingCounterAdvance: _awaitingCounterAdvance,
        RestartBaselineCounter: _restartBaselineCounter,
        LastRestartCountedInstanceId: _lastRestartCountedInstanceId);


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

    /// <summary>
    /// 等待提交的统一出口（保留原签名与默认值，供本类各分支复用）。
    /// 实现已收敛到 <see cref="SnapshotDeserializer.WaitingForCommit"/>。
    /// </summary>
    /// <remarks><c>MarkUnavailable</c> 与上报顺序保持原样（先摘可用性、后写诊断）。</remarks>
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
        Report(SnapshotDeserializer.WaitingForCommit(
            snapshot, _connectedMapName, brokerTimestampUtc, restartDelta, retryCount,
            StallTimeout, stalled, connected, overrideError: error));
    }

    private bool IsStalled() => _stability.IsStalled(_lastCounterAdvanceTimestamp);

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
    /// 实现已移至 <see cref="SnapshotStabilityTracker.IsStallConfirmed"/>，本方法保持原签名转发
    /// （SharedMemoryReader.cs:ReadOnce 仍在调用，且 harness 依赖 <c>_lastCounterAdvanceTimestamp</c> 留驻）。
    /// </summary>
    private bool IsStallConfirmed() => _stability.IsStallConfirmed(_lastCounterAdvanceTimestamp);

    /// <summary>判据 A 的 extension 面：见 <see cref="SnapshotStabilityTracker.IsBrokerPublishStalled"/>。</summary>
    private bool IsBrokerPublishStalled(long monotonicPublishMs) =>
        _stability.IsBrokerPublishStalled(monotonicPublishMs);

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
