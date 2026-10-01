// SysMonCmdPal/Broker/SnapshotDeserializer.cs
//
// ProcessStableSnapshot 的**解析 + 诊断上报适配层**（P0 盲区 3/3 的第二半）。
//
// 为什么单独成文件（与 SnapshotStabilityTracker 的分工）：
//   Tracker 负责「下一步该做什么」（6 分支纯决策）；本类负责「把结果翻译成诊断上报与数据发布」。
//   原 226 行里真正重复的不是解析（解析早已有深模块 SharedMemorySnapshotParser），
//   而是 4 处 `UpdateDiagnostics(...14 个命名参数)` 的样板——其中 mapName/counter/version/
//   brokerTimestampUtc/usesCommitSequence/commitSequence/instanceId/monotonicPublishMs
//   这 8 个参数在每处都逐字重复。
//   本类收敛为 DiagnosticUpdate 值对象 + 5 个具名工厂，调用点从「14 行参数包」压到 2 行。
//
// ⚠ 行为保持的硬约束：
//   1. 诊断字段的 **null 语义**必须逐位保留（UpdateDiagnostics 里 `?? s_diagnostics.X`
//      表示「不更新、沿用上次」）：分支 4/5 不传 sensorCount/commitUtc，
//      写成显式值就会改写诊断语义。
//   2. 全部文案逐字保持（多个用例断言精确子串，见各方法注释）。
//   3. 本类不持有状态、不读 reader 字段、不碰时钟（时间由调用方传入）。

using System;

namespace SysMonCmdPal.Broker;

/// <summary>
/// 一次诊断上报的完整参数包。<c>null</c> 字段表示「不更新、沿用 s_diagnostics 上次的值」
/// （与 <c>UpdateDiagnostics</c> 里 <c>?? s_diagnostics.X</c> 的语义一一对应）。
/// </summary>
internal readonly record struct DiagnosticUpdate(
    bool Connected,
    string MapName,
    int Counter,
    int Version,
    DateTime BrokerTimestampUtc,
    bool UsesCommitSequence,
    int CommitSequence,
    ulong InstanceId,
    long MonotonicPublishMs,
    int RestartDelta,
    int UnstableReadDelta,
    string Error,
    bool? ProtocolValid = null,
    bool? Stalled = null,
    int? SensorCount = null,
    DateTime? CommitUtc = null);

/// <summary>
/// outcome → diagnostics/push 的适配层。无状态；只构造值对象，
/// 真正的上报（<c>UpdateDiagnostics</c>）与推送由 reader 执行。
/// </summary>
internal static class SnapshotDeserializer
{
    /// <summary>
    /// 解析稳定快照。委托给已有的深模块 <see cref="SharedMemorySnapshotParser"/>；
    /// 本方法作为「反序列化」职责在本层的命名入口，使 reader 不直接依赖解析器。
    /// </summary>
    public static bool TryDeserialize(
        in StableSnapshot snapshot, out ParsedSnapshot parsed, out string error) =>
        SharedMemorySnapshotParser.TryParse(snapshot, out parsed, out error);

    /// <summary>发布数据面（副作用由 reader 侧统一执行，本方法只是命名入口）。</summary>
    public static void Publish(in ParsedSnapshot parsed) =>
        BrokerPushReceiver.Instance.PushSnapshot(
            parsed.CpuTemperature, parsed.CpuSource, parsed.Gpus, parsed.Sensors);

    // ==================== 五个诊断上报工厂 ====================
    // 各工厂都从 Compact(...) 的公共 8 参数起步，再叠加本分支特有字段；
    // 公共部分只出现一次，避免又造出 4 份重复参数包。

    /// <summary>等待 counter 前进 / 首连 / 重启等待（connected 由确认链决定）。</summary>
    /// <remarks>
    /// 文案两条：停滞 <c>Broker commit counter has not advanced for {N} seconds</c>；
    /// 否则 <c>Broker is initialized; waiting for the first data commit</c>（B1 用例断言精确串）。
    /// 调用方通常会给出 <paramref name="overrideError"/>（首连/重启等待两条固定文案）。
    /// </remarks>
    public static DiagnosticUpdate WaitingForCommit(
        in StableSnapshot s, string mapName, DateTime brokerTimestampUtc,
        int restartDelta, int unstableReadDelta, TimeSpan stallTimeout,
        bool stalled, bool connected, string? overrideError = null) =>
        Compact(s, mapName, brokerTimestampUtc, restartDelta, unstableReadDelta, connected: connected) with
        {
            ProtocolValid = true,
            Stalled = stalled,
            Error = overrideError ?? (stalled
                ? $"Broker commit counter has not advanced for {stallTimeout.TotalSeconds:F0} seconds"
                : "Broker is initialized; waiting for the first data commit"),
        };

    /// <summary>
    /// extension 的发布时刻过期 ⇒ 即时降级（connected:true / protocolValid:true / stalled:true 固定，
    /// 且**刻意旁路确认窗**）。
    /// </summary>
    public static DiagnosticUpdate PublishTimeExpired(
        in StableSnapshot s, string mapName, DateTime brokerTimestampUtc,
        int restartDelta, int unstableReadDelta, TimeSpan stallTimeout) =>
        Compact(s, mapName, brokerTimestampUtc, restartDelta, unstableReadDelta, connected: true) with
        {
            ProtocolValid = true,
            Stalled = true,
            Error = $"Broker has not published for {stallTimeout.TotalSeconds:F0} seconds",
        };

    /// <summary>
    /// counter 未前进。<c>connected = HasExtension || !stallConfirmed</c>；
    /// 未停滞且从未成功发布过时给出首连文案。
    /// </summary>
    /// <remarks>本分支**刻意不携带 restartDelta**（原实现如此，勿「顺手补全」）。</remarks>
    public static DiagnosticUpdate CounterUnchanged(
        in StableSnapshot s, string mapName, DateTime brokerTimestampUtc,
        int unstableReadDelta, TimeSpan stallTimeout,
        bool stalled, bool stallConfirmed, bool hasPublishedSnapshot) =>
        Compact(s, mapName, brokerTimestampUtc, restartDelta: 0, unstableReadDelta,
                connected: s.HasExtension || !stallConfirmed) with
        {
            ProtocolValid = true,
            Stalled = stalled,
            Error = stalled
                ? $"Broker commit counter has not advanced for {stallTimeout.TotalSeconds:F0} seconds"
                : hasPublishedSnapshot
                    ? ""
                    : "Broker is initialized; waiting for the first data commit",
        };

    /// <summary>解析失败：protocolValid:false，error 为解析器原文（如 <c>Invalid sensor count: …</c>）。</summary>
    public static DiagnosticUpdate ParseFailed(
        in StableSnapshot s, string mapName, DateTime brokerTimestampUtc,
        int restartDelta, int unstableReadDelta, string parseError) =>
        Compact(s, mapName, brokerTimestampUtc, restartDelta, unstableReadDelta, connected: true) with
        {
            ProtocolValid = false,
            Stalled = false,
            Error = parseError,
        };

    /// <summary>成功发布：带 sensorCount 与 commitUtc，error 清空。</summary>
    public static DiagnosticUpdate Published(
        in StableSnapshot s, string mapName, DateTime brokerTimestampUtc,
        int restartDelta, int unstableReadDelta, int sensorCount, DateTime commitUtc) =>
        Compact(s, mapName, brokerTimestampUtc, restartDelta, unstableReadDelta, connected: true) with
        {
            ProtocolValid = true,
            Stalled = false,
            SensorCount = sensorCount,
            CommitUtc = commitUtc,
            Error = "",
        };

    /// <summary>
    /// 公共参数收敛点：8 个在各处上报里逐字重复的字段只在这里出现一次。
    /// <c>ProtocolValid</c>/<c>Stalled</c>/<c>SensorCount</c>/<c>CommitUtc</c> 保持 null（= 不更新），
    /// 由各工厂按需覆盖。
    /// </summary>
    private static DiagnosticUpdate Compact(
        in StableSnapshot s, string mapName, DateTime brokerTimestampUtc,
        int restartDelta, int unstableReadDelta, bool connected) => new(
            Connected: connected,
            MapName: mapName,
            Counter: s.Counter,
            Version: s.Layout.Version,
            BrokerTimestampUtc: brokerTimestampUtc,
            UsesCommitSequence: s.HasExtension,
            CommitSequence: s.CommitSequence,
            InstanceId: s.InstanceId,
            MonotonicPublishMs: s.MonotonicPublishMs,
            RestartDelta: restartDelta,
            UnstableReadDelta: unstableReadDelta,
            Error: "");
}
