// SysMonCmdPal/Broker/SnapshotStabilityTracker.cs
//
// ProcessStableSnapshot 的**纯决策核**（从 226 行线性状态机提取，P0 盲区 3/3）。
//
// 为什么单独成文件：
//   原 ProcessStableSnapshot 把「6 个互斥分支决策」与「诊断上报/断连/推送等副作用」
//   缠在一条 226 行的控制流里，任何一条分支想在测试里单独喂参都必须起 SharedMemoryReaderHarness
//   （反射 + 静态单例 + MemoryMappedFile）。本类把这 6 个分支的**判定条件全集**收拢为
//   一个纯函数 Decide(观测, 状态视图) —— 无副作用、不碰 reader 字段、不碰 BrokerPushReceiver，
//   因此可以直接喂参数测试（含「重启计数去重」「同 counter 新时间戳」这类极难复现的角落）。
//
// 三条时间判据的语义约束见 SharedMemoryReader.Health.cs 头部 T2-4 注释块（不得与此处漂移）：
//   A) IsStalled        —— 5s 单次 raw 观察（写进 Diagnostics.IsStalled）；
//   B) IsStallConfirmed —— 15s 时间基确认链，决定是否 MarkUnavailable/Disconnect；
//   C) IsUsable         —— 5s 新鲜度网（在 BrokerSensorSnapshot 上，与本类无关）。
// 阈值由构造函数注入（reader 把 static 常量传进来），本类不复制常量，避免双份真相。

using System;

namespace SysMonCmdPal.Broker;

/// <summary>一次稳定读的观测（reader 算好传入；Tracker 不读 reader 字段）。</summary>
/// <param name="MapChanged">
/// <c>_lastObservedMapName != _connectedMapName</c>（Ordinal）。这两个字段都在 reader 上，
/// 故由 reader 比较后传入，而不是把字符串搬进本结构。
/// </param>
internal readonly record struct StabilityObservation(
    int Counter,
    long BrokerTimestampTicks,
    bool HasExtension,
    ulong InstanceId,
    long MonotonicPublishMs,
    bool MapChanged);

/// <summary>reader 稳态字段的只读视图（每次决策前由 reader 现构，决策后由 reader 写回）。</summary>
internal readonly record struct StabilityState(
    int? LastCounter,
    long LastBrokerTimestampTicks,
    ulong LastInstanceId,
    long LastCounterAdvanceTimestamp,
    bool HasObservedSnapshot,
    bool AwaitingCounterAdvance,
    int RestartBaselineCounter,
    ulong? LastRestartCountedInstanceId);

/// <summary>决策结论：对应原 226 行里的 6 个「report + return」分支出口。</summary>
internal enum StabilityKind
{
    /// <summary>首连：观察 baseline + 等待 counter 前进（原 :68-80）。</summary>
    FirstConnectBaseline,

    /// <summary>检出重启且需等下一次提交（原 :116-131）。</summary>
    RestartAwaitingAdvance,

    /// <summary>仍在等 counter 前进且 counter 未变（原 :137-156）。</summary>
    WaitingForCounterAdvance,

    /// <summary>extension 的发布时刻已过期 ⇒ 即时降级，**刻意旁路确认窗**（原 :162-189）。</summary>
    PublishTimeExpired,

    /// <summary>counter 未前进（原 :191-221）。</summary>
    CounterUnchanged,

    /// <summary>可以解析并发布（原 :223 起）。</summary>
    ReadyToPublish,
}

/// <summary>
/// 决策结果：<see cref="Kind"/> 之外的字段**都是 reader 必须应用的副作用指令**
/// （本结构自身不执行任何副作用）。
/// </summary>
internal readonly record struct StabilityDecision(
    StabilityKind Kind,
    bool RestartDetected = false,
    int RestartDelta = 0,
    bool UpdateRestartCountedInstanceId = false,
    bool ClearAwaitingCounterAdvance = false,
    bool ObserveBaselineFirst = false,
    bool Stalled = false,
    bool StallConfirmed = false);

/// <summary>
/// 稳态决策核。<see cref="Decide"/> 无副作用、不读时钟以外的外部状态，可脱离单例直接测试。
/// </summary>
internal sealed class SnapshotStabilityTracker
{
    private readonly TimeSpan _stallTimeout;
    private readonly TimeSpan _confirmStallWindow;

    public SnapshotStabilityTracker(TimeSpan stallTimeout, TimeSpan confirmStallWindow)
    {
        _stallTimeout = stallTimeout;
        _confirmStallWindow = confirmStallWindow;
    }

    public TimeSpan StallTimeout => _stallTimeout;

    /// <summary>
    /// 六分支决策（原 ProcessStableSnapshot 的判定条件全集）。
    /// </summary>
    /// <remarks>
    /// ⚠ 两处「顺序即语义」的地方，拆分了也必须保持：
    /// 1. **分支 3→4→5→6 的 fallthrough**：等待 counter 前进的分支在 counter 前进后
    ///    **清除标志并继续向下**，不是 return（原 :158-160）。门禁 =
    ///    SharedMemoryCounterTests.ExtendedInitializationFrame_DoesNotPublishBeforeCounterAdvances。
    /// 2. **重启前导副作用影响后续判定**：重启块无条件把 <c>_lastCounterAdvanceTimestamp</c> 置 0
    ///    （原 :114，哨兵语义见 Health.cs 注释），因此「检出重启但继续向下」时，
    ///    分支 4/5 观察到的 stalled/stallConfirmed **必然是 false**。本方法用
    ///    <c>restartTimestampZeroed</c> 重放该副作用，使纯决策与有副作用的原实现逐位一致。
    /// </remarks>
    public StabilityDecision Decide(in StabilityObservation o, in StabilityState s)
    {
        // ---- 分支 1：首连（此时 _hasObservedSnapshot == false）----
        if (!s.HasObservedSnapshot)
            return new StabilityDecision(StabilityKind.FirstConnectBaseline);

        long advanceTimestamp = s.LastCounterAdvanceTimestamp;
        bool restartDetected = false;
        int restartDelta = 0;
        bool updateCounted = false;
        bool skipPreviousCounterComparison = false;

        // ---- 分支 2：重启检测（5 个判据，一个都不能少）----
        bool counterMovedBackwards = s.LastCounter.HasValue &&
            CounterMovedBackwards(s.LastCounter.Value, o.Counter);
        bool instanceChanged = o.HasExtension && o.InstanceId != s.LastInstanceId;
        bool extensionRemoved = s.LastInstanceId != 0 && !o.HasExtension;
        bool sameCounterWithNewTimestamp = !o.HasExtension &&
            s.LastCounter == o.Counter &&
            s.LastBrokerTimestampTicks != 0 &&
            o.BrokerTimestampTicks != 0 &&
            o.BrokerTimestampTicks != s.LastBrokerTimestampTicks;

        if (o.MapChanged || instanceChanged || extensionRemoved ||
            counterMovedBackwards || sameCounterWithNewTimestamp)
        {
            restartDetected = true;
            // 按 instanceId 去重：仅当 instanceId 与上次计数时不同才 +1，
            // 避免一次重启被计两次（如 extensionRemoved 后再 instanceChanged）。
            if (s.LastRestartCountedInstanceId != o.InstanceId)
            {
                restartDelta = 1;
                updateCounted = true;
            }

            if (o.HasExtension || extensionRemoved || o.Counter == 0 || sameCounterWithNewTimestamp)
            {
                return new StabilityDecision(
                    StabilityKind.RestartAwaitingAdvance,
                    RestartDetected: true,
                    RestartDelta: restartDelta,
                    UpdateRestartCountedInstanceId: updateCounted);
            }

            // reader 侧已把 _lastCounterAdvanceTimestamp 置 0 ⇒ 后续判定的 time base 归零
            advanceTimestamp = 0;
            skipPreviousCounterComparison = true;
        }

        // ---- 分支 3：等待 counter 前进 ----
        bool clearAwaiting = false;
        if (s.AwaitingCounterAdvance)
        {
            if (o.Counter == s.RestartBaselineCounter)
            {
                return new StabilityDecision(
                    StabilityKind.WaitingForCounterAdvance,
                    RestartDetected: restartDetected,
                    RestartDelta: restartDelta,
                    UpdateRestartCountedInstanceId: updateCounted,
                    Stalled: IsStalled(advanceTimestamp),
                    StallConfirmed: IsStallConfirmed(advanceTimestamp));
            }

            // counter 已前进：清除等待标志后**继续向下**（fallthrough，不是 return）
            clearAwaiting = true;
            skipPreviousCounterComparison = true;
        }

        // ---- 分支 4：extension 的发布时刻过期 ⇒ 即时降级（刻意旁路确认窗，勿改成去抖）----
        if (o.HasExtension && IsBrokerPublishStalled(o.MonotonicPublishMs))
        {
            return new StabilityDecision(
                StabilityKind.PublishTimeExpired,
                RestartDetected: restartDetected,
                RestartDelta: restartDelta,
                UpdateRestartCountedInstanceId: updateCounted,
                ClearAwaitingCounterAdvance: clearAwaiting,
                ObserveBaselineFirst: s.LastCounter != o.Counter || s.LastInstanceId != o.InstanceId);
        }

        // ---- 分支 5：counter 未前进 ----
        if (!skipPreviousCounterComparison && s.LastCounter == o.Counter)
        {
            return new StabilityDecision(
                StabilityKind.CounterUnchanged,
                RestartDetected: restartDetected,
                RestartDelta: restartDelta,
                UpdateRestartCountedInstanceId: updateCounted,
                ClearAwaitingCounterAdvance: clearAwaiting,
                Stalled: IsStalled(advanceTimestamp),
                StallConfirmed: IsStallConfirmed(advanceTimestamp));
        }

        // ---- 分支 6：解析 + 发布（解析成败由 reader 用 buffer 判定）----
        return new StabilityDecision(
            StabilityKind.ReadyToPublish,
            RestartDetected: restartDetected,
            RestartDelta: restartDelta,
            UpdateRestartCountedInstanceId: updateCounted,
            ClearAwaitingCounterAdvance: clearAwaiting);
    }

    /// <summary>
    /// 判据 A：5s 单次 raw 观察（只写进 Diagnostics.IsStalled，不决定动作）。
    /// <paramref name="lastCounterAdvanceTimestamp"/> &lt;= 0 是「刚重启/刚重连」哨兵，一律 false。
    /// </summary>
    public bool IsStalled(long lastCounterAdvanceTimestamp) =>
        lastCounterAdvanceTimestamp > 0 &&
        System.Diagnostics.Stopwatch.GetElapsedTime(lastCounterAdvanceTimestamp) >= _stallTimeout;

    /// <summary>
    /// 判据 B：15s 时间基确认链（决定是否 MarkUnavailable/Disconnect）。
    /// <paramref name="lastCounterAdvanceTimestamp"/> &lt;= 0 时一律不确认（哨兵语义，不得放宽）。
    /// </summary>
    public bool IsStallConfirmed(long lastCounterAdvanceTimestamp) =>
        lastCounterAdvanceTimestamp > 0 &&
        System.Diagnostics.Stopwatch.GetElapsedTime(lastCounterAdvanceTimestamp) >= _confirmStallWindow;

    /// <summary>
    /// 判据 A 在 extension 面：Broker 自己写下的 publish 时刻已超过 StallTimeout 未刷新。
    /// 这是**既成事实**（新鲜度已过期），不属于「counter 暂未推进」的瞬时抖动 ⇒ 由调用方直接确认、不进时间窗。
    /// </summary>
    public bool IsBrokerPublishStalled(long monotonicPublishMs)
    {
        long elapsedMilliseconds = unchecked(Environment.TickCount64 - monotonicPublishMs);
        return elapsedMilliseconds < 0 || elapsedMilliseconds >= _stallTimeout.TotalMilliseconds;
    }

    /// <summary>counter 是否回退（uint 环绕距离 &gt; int.MaxValue ⇒ 判定为向后）。</summary>
    public static bool CounterMovedBackwards(int previous, int current)
    {
        uint forwardDistance = unchecked((uint)(current - previous));
        return forwardDistance > int.MaxValue;
    }
}
