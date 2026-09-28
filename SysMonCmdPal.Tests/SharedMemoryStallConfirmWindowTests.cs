// SysMonCmdPal.Tests/SharedMemoryStallConfirmWindowTests.cs
// T2-4（P1-4）确认链时间基窗口测试。
//
// 背景（三条判据必须分开，否则测试会「蹭看」）：
//   A) raw 观察 IsStalled      —— StallTimeout = AvailabilityTimeout = 5s（同一个数，非独立常量）；
//   B) 确认链 stallConfirmed    —— ConfirmStallWindow = 15s，决定是否 MarkUnavailable/Disconnect；
//   C) 用户可见可用性 IsBrokerAvailable —— BrokerSensorSnapshot.IsUsable，仅由 5s 新鲜度网支配，
//      与确认链无关。
// 原计数式去抖（连续 N 次观察）已废弃：轮询间隔是 1s 而非 5s，阈值 2→3 只把确认从 6s 推到 7s，
// 覆盖不到 Broker 合法静默上界 ≈15s（SysMonBroker/Program.cs:30-34），高负载下仍会提前断连。
//
// 用例① 必须写在**无 extension 的传统 map 路径**（extensionMagic:0 / instanceId:0）：
// 去抖确认链唯一非冗余的动作是 Disconnect()（仅 !HasExtension 的传统路径才断开）；
// 现代路径上可用性按构造已被 5s 新鲜度网判过期，若在那里断言「仍然 available」会得到
// 一个天然红的用例，进而诱导改弱断言。现代路径只断言「IsConnected 保持 true / 不 Disconnect」。

using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SharedMemoryStallConfirmWindowTests
{
    private const int PublishedCounter = 8;

    // ---- ① 容忍：传统路径 12–15s 静默不得提前断连 ----

    [Fact]
    public void LegacyMap_SilentWithinConfirmWindow_DoesNotDisconnectEarly()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = LegacyBuffer(PublishedCounter, timestamp);
        harness.PublishFirstV2(buffer, PublishedCounter, timestamp);
        Assert.Equal(ShmLayout.MapName, harness.ConnectedMapName); // 前置：确实已连上

        harness.SetLastCounterAdvanceElapsed(TimeSpan.FromSeconds(12));

        // 关键反回归设计：按真实轮询节奏（PollIntervalMilliseconds=1s）喂**多次**连续 stall
        // 观察。12s 静默 ≈ 12 次观察，而原计数式去抖在第 2 次观察即确认（阈值 2）→
        // 会在这里提前 Disconnect，正是本任务要消除的误杀；时间基 15s 窗口必须容忍整段。
        // 只喂一次观察无法区分两种实现（计数式首次同样不确认），故此处喂 5 次。
        for (int poll = 0; poll < 5; poll++)
            harness.ProcessV2(buffer, PublishedCounter, timestamp);

        // 判据 B：确认链未触发 → 不 Disconnect（本修复的核心收益）
        Assert.False(harness.IsDisconnectedFromMap);
        Assert.True(SharedMemoryReader.Diagnostics.IsConnected);
        // 判据 A：raw 观察照常报 stall（阈值仍是 5s，未被改动）
        Assert.True(SharedMemoryReader.Diagnostics.IsStalled);
        // 不得为「过①」把 error 文案也抹掉
        Assert.Contains("has not advanced", SharedMemoryReader.Diagnostics.LastError);
    }

    // ---- ② 真停滞：超过确认窗口必须确认（传统路径 MarkUnavailable + Disconnect） ----

    [Fact]
    public void LegacyMap_SilentBeyondConfirmWindow_ConfirmsStallAndDisconnects()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = LegacyBuffer(PublishedCounter, timestamp);
        harness.PublishFirstV2(buffer, PublishedCounter, timestamp);

        harness.SetLastCounterAdvanceElapsed(TimeSpan.FromSeconds(16));
        harness.ProcessV2(buffer, PublishedCounter, timestamp);

        Assert.True(harness.IsDisconnectedFromMap);
        Assert.False(SharedMemoryReader.Diagnostics.IsConnected);
        Assert.False(harness.Receiver.IsBrokerAvailable);
        Assert.Contains("has not advanced", SharedMemoryReader.Diagnostics.LastError);
    }

    // ---- ③ 有界：容忍窗口耗尽即确认，绝不退化为无限期容忍 ----

    [Fact]
    public void LegacyMap_ToleranceIsBounded_EventuallyConfirmsInSameSession()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = LegacyBuffer(PublishedCounter, timestamp);
        harness.PublishFirstV2(buffer, PublishedCounter, timestamp);

        // 12s + 多次观察：容忍（不提前断连）。计数式实现在第 2 次观察就会断开，
        // 这里正是「有界容忍」与「提前误杀」的分水岭。
        harness.SetLastCounterAdvanceElapsed(TimeSpan.FromSeconds(12));
        for (int poll = 0; poll < 3; poll++)
            harness.ProcessV2(buffer, PublishedCounter, timestamp);
        Assert.False(harness.IsDisconnectedFromMap);

        // 同一会话继续静默到 15.5s：窗口耗尽 → 确认 + 断开（有界，非无限容忍）
        harness.SetLastCounterAdvanceElapsed(TimeSpan.FromSeconds(15.5));
        harness.ProcessV2(buffer, PublishedCounter, timestamp);
        Assert.True(harness.IsDisconnectedFromMap);
        Assert.False(SharedMemoryReader.Diagnostics.IsConnected);
    }

    // ---- ④ 三条判据互不冒充：raw stall / 确认链 / 新鲜度可用性 ----

    [Fact]
    public void ThreeSignalsAreIndependent_RawStallObservationIsNeitherConfirmationNorAvailability()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = LegacyBuffer(PublishedCounter, timestamp);
        harness.PublishFirstV2(buffer, PublishedCounter, timestamp);

        // 只把新鲜度拨过期（判据 C），确认链窗口保持 12s（判据 B 未触发）
        harness.SetLastCounterAdvanceElapsed(TimeSpan.FromSeconds(12));
        harness.ExpireReceiverAvailability();
        harness.ProcessV2(buffer, PublishedCounter, timestamp);

        Assert.True(SharedMemoryReader.Diagnostics.IsStalled,
            "判据 A：5s raw 观察成立（阈值未改）");
        Assert.False(harness.IsDisconnectedFromMap,
            "判据 B：15s 确认窗口未耗尽，确认链不得动作");
        Assert.False(harness.Receiver.IsBrokerAvailable,
            "判据 C：可用性由 5s 新鲜度网支配，与确认链无关");
        // 本用例是「蹭看」的反例守卫：可用性为 false 不得被读成「确认链已生效」。
    }

    // ---- ⑤ 现代路径语义不得扩大：超窗仍不 Disconnect（断开仅对 !HasExtension 生效） ----

    [Fact]
    public void ModernMap_SilentBeyondConfirmWindow_KeepsConnectionSemanticsUnchanged()
    {
        using var harness = new SharedMemoryReaderHarness();
        long timestamp = DateTime.UtcNow.Ticks;
        byte[] buffer = BrokerTestData.V2Buffer(PublishedCounter, timestampTicks: timestamp);
        harness.PublishFirstV2(buffer, PublishedCounter, timestamp);

        harness.SetLastCounterAdvanceElapsed(TimeSpan.FromSeconds(20));
        harness.ProcessV2(buffer, PublishedCounter, timestamp);

        // 确认链在现代路径不产生断连（现状语义，本修复不扩大）
        Assert.False(harness.IsDisconnectedFromMap);
        // 现代路径只可断言 IsConnected 保持 true（可用性由新鲜度网支配，此处不据以证明去抖）
        Assert.True(SharedMemoryReader.Diagnostics.IsConnected);
        Assert.True(SharedMemoryReader.Diagnostics.IsStalled);
    }

    // ---- helpers ----

    /// <summary>无 SMX1 extension 的传统 v2 布局对偶路径（确认链唯一非冗余动作所在）。</summary>
    private static byte[] LegacyBuffer(int counter, long timestampTicks) =>
        BrokerTestData.V2Buffer(
            counter,
            timestampTicks: timestampTicks,
            extensionMagic: 0,
            instanceId: 0,
            monotonicPublishMs: 0);
}
