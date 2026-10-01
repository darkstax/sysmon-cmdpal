// Copyright (c) 2026 SysMonCmdPal
// SharedMemoryReader.ProcessStableSnapshot 直接单元测试
//
// 目标：把 226 行的 ProcessStableSnapshot 从「仅经 ReadOnce 集成路径间接覆盖」
// 提升为可独立定位的单元测试，覆盖稳态检测、baseline 观察、停滞判定、提交等待、
// 快照解析等核心分支，为后续拆分 SnapshotStabilityTracker / SnapshotDeserializer 做准备。

using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public sealed class SharedMemoryReaderHealthTests
{
    // ---- 首连路径：baseline 观察 + 等待提交 ----

    [Fact]
    public void ProcessStableSnapshot_FirstSnapshot_ObservesBaselineAndWaitsForCommit()
    {
        using var harness = new SharedMemoryReaderHarness();
        byte[] buffer = BrokerTestData.V2Buffer(counter: 1, cpuTemperature: 42.0);

        harness.ProcessV2(buffer, counter: 1, DateTime.UtcNow.Ticks);

        // 首帧后：已观察 baseline，未发布，等待 counter 前进
        var diag = SharedMemoryReader.Diagnostics;
        // 等待提交期间 reader **仍然连着 map**：ReportWaitingForCommit 的 connected 参数默认 true，
        // 它只把可用性面摘掉。不可用的是 BrokerPushReceiver **数据面**，不是 Diagnostics.IsConnected——
        // 两者混为一谈是本用例原断言错误（正典对照见 SharedMemoryStallConfirmWindowTests）。
        Assert.True(diag.IsConnected);
        Assert.False(harness.Receiver.IsBrokerAvailable);   // MarkUnavailable 已生效（数据面）
        Assert.True(diag.IsProtocolValid);
        Assert.False(diag.IsStalled);
        Assert.Equal("Broker is initialized; waiting for the first data commit", diag.LastError);
        Assert.Equal(1, diag.LastCounter);
    }

    [Fact]
    public void ProcessStableSnapshot_SecondSnapshotWithAdvancedCounter_Publishes()
    {
        using var harness = new SharedMemoryReaderHarness();
        byte[] baseline = BrokerTestData.V2Buffer(counter: 1, cpuTemperature: 42.0);
        byte[] advanced = BrokerTestData.V2Buffer(counter: 2, cpuTemperature: 43.0);

        harness.ProcessV2(baseline, counter: 1, DateTime.UtcNow.Ticks);
        harness.ProcessV2(advanced, counter: 2, DateTime.UtcNow.Ticks);

        var diag = SharedMemoryReader.Diagnostics;
        Assert.True(diag.IsConnected);
        Assert.True(diag.IsProtocolValid);
        Assert.False(diag.IsStalled);
        Assert.Equal("", diag.LastError);
        Assert.Equal(2, diag.LastCounter);
    }

    // ---- 重启检测 ----

    [Fact]
    public void ProcessStableSnapshot_CounterMovedBackwards_DetectsRestartAndWaits()
    {
        using var harness = new SharedMemoryReaderHarness();
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 5, cpuTemperature: 42.0), counter: 5, DateTime.UtcNow.Ticks);
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 6, cpuTemperature: 43.0), counter: 6, DateTime.UtcNow.Ticks);

        // counter 回退 → 重启
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 1, cpuTemperature: 40.0), counter: 1, DateTime.UtcNow.Ticks);

        var diag = SharedMemoryReader.Diagnostics;
        // 重启后进入「等待下一次提交」，reader 仍连着 map（同 B1：IsConnected 不等于数据面可用）
        Assert.True(diag.IsConnected);
        Assert.False(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(1, diag.RestartCount);
        Assert.Contains("Broker restart detected", diag.LastError);
    }

    [Fact]
    public void ProcessStableSnapshot_SameCounterNewTimestamp_DetectsRestart()
    {
        using var harness = new SharedMemoryReaderHarness();
        // 「同 counter 但新时间戳 ⇒ 重启」的判据 sameCounterNewTimestamp 仅对**无 extension 的传统路径**成立
        // （Health.cs 的 !snapshot.HasExtension 前置），带 extension 时必须走 instanceId 判据。
        // 故此处显式构造传统 map（extensionMagic:0）——V2Buffer 默认带 extension，是原夹具的错路。
        harness.ProcessV2(
            BrokerTestData.V2Buffer(counter: 5, cpuTemperature: 42.0, extensionMagic: 0), counter: 5, DateTime.UtcNow.Ticks);

        // 同 counter 但新时间戳（无 extension 的旧 Broker 重启）
        harness.ProcessV2(
            BrokerTestData.V2Buffer(counter: 5, cpuTemperature: 42.0, extensionMagic: 0),
            counter: 5,
            DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond);

        var diag = SharedMemoryReader.Diagnostics;
        Assert.True(diag.IsConnected);
        Assert.False(harness.Receiver.IsBrokerAvailable);
        Assert.Equal(1, diag.RestartCount);
    }

    // ---- 停滞检测 ----

    [Fact]
    public void ProcessStableSnapshot_CounterNotAdvanced_StallConfirmedDisconnects()
    {
        using var harness = new SharedMemoryReaderHarness();
        // 断连（Disconnect）**仅对无 extension 的传统路径生效**（Health.cs 的 !snapshot.HasExtension 前置）；
        // 带 extension 的现代路径按设计绝不 Disconnect（可用性由新鲜度网支配）。
        // 原夹具用默认带 extension 的 V2Buffer 断言 Disconnect，属走错路径——正典蓝本见
        // SharedMemoryStallConfirmWindowTests 用例②（LegacyMap_SilentBeyondConfirmWindow_ConfirmsStallAndDisconnects）。
        byte[] buffer = BrokerTestData.V2Buffer(counter: 1, cpuTemperature: 42.0, extensionMagic: 0);
        long ts = DateTime.UtcNow.Ticks;   // 两次必须同一时间戳，否则首帧即触发 sameCounterNewTimestamp 重启分支
        harness.ProcessV2(buffer, counter: 1, ts);

        // 推进到确认窗口（15s）之后
        harness.SetLastCounterAdvanceElapsed(TimeSpan.FromSeconds(16));

        harness.ProcessV2(buffer, counter: 1, ts);

        var diag = SharedMemoryReader.Diagnostics;
        Assert.True(diag.IsStalled);
        Assert.False(diag.IsConnected);
        Assert.True(harness.IsDisconnectedFromMap);
        Assert.False(harness.Receiver.IsBrokerAvailable);
    }

    [Fact]
    public void ProcessStableSnapshot_CounterNotAdvanced_WithinStallWindow_KeepsConnected()
    {
        using var harness = new SharedMemoryReaderHarness();
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 1, cpuTemperature: 42.0), counter: 1, DateTime.UtcNow.Ticks);

        // 在确认窗口内（< 15s）
        harness.SetLastCounterAdvanceElapsed(TimeSpan.FromSeconds(10));

        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 1, cpuTemperature: 42.0), counter: 1, DateTime.UtcNow.Ticks);

        var diag = SharedMemoryReader.Diagnostics;
        Assert.True(diag.IsStalled);           // 5s raw stall 已触发
        Assert.True(diag.IsConnected);         // 但确认链未断连
        Assert.False(harness.IsDisconnectedFromMap);
    }

    // ---- Extension 路径 ----

    [Fact]
    public void ProcessStableSnapshot_ExtensionSnapshot_ImmediatePublishOnStall()
    {
        using var harness = new SharedMemoryReaderHarness();
        // 构造带 extension 的 buffer（commitSequence 偶数 = 已提交）
        byte[] buffer = BrokerTestData.V2Buffer(
            counter: 1, cpuTemperature: 42.0, commitSequence: 2, instanceId: 123, monotonicPublishMs: 1000);

        harness.ProcessV2(buffer, counter: 1, DateTime.UtcNow.Ticks);

        var diag = SharedMemoryReader.Diagnostics;
        Assert.True(diag.UsesCommitSequence);
        Assert.Equal(123UL, diag.LastInstanceId);
    }

    [Fact]
    public void ProcessStableSnapshot_ExtensionInstanceChanged_DetectsRestart()
    {
        using var harness = new SharedMemoryReaderHarness();
        byte[] first = BrokerTestData.V2Buffer(
            counter: 1, cpuTemperature: 42.0, commitSequence: 2, instanceId: 123, monotonicPublishMs: 1000);
        harness.ProcessV2(first, counter: 1, DateTime.UtcNow.Ticks);

        byte[] second = BrokerTestData.V2Buffer(
            counter: 2, cpuTemperature: 43.0, commitSequence: 4, instanceId: 456, monotonicPublishMs: 2000);
        harness.ProcessV2(second, counter: 2, DateTime.UtcNow.Ticks);

        var diag = SharedMemoryReader.Diagnostics;
        Assert.Equal(1, diag.RestartCount);
        Assert.Equal(456UL, diag.LastInstanceId);
    }

    // ---- 解析失败路径 ----

    [Fact]
    public void ProcessStableSnapshot_InvalidSnapshotData_ProtocolInvalid()
    {
        using var harness = new SharedMemoryReaderHarness();
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 1, cpuTemperature: 42.0), counter: 1, DateTime.UtcNow.Ticks);
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 2, cpuTemperature: 43.0), counter: 2, DateTime.UtcNow.Ticks);

        // 构造损坏的 buffer（sensor count 越界）
        byte[] corrupted = BrokerTestData.V2Buffer(counter: 3, cpuTemperature: 44.0);
        BitConverter.TryWriteBytes(corrupted.AsSpan(ShmLayout.OffSensorCount, 4), 99999);

        harness.ProcessV2(corrupted, counter: 3, DateTime.UtcNow.Ticks);

        var diag = SharedMemoryReader.Diagnostics;
        Assert.False(diag.IsProtocolValid);
        // 文案来自解析路径（SharedMemorySnapshotParser：sensor count 越界），
        // 而 "Shared memory changed" 是**读取端不稳定双读**的报错（SharedMemorySnapshotReader），
        // 根本不是本用例构造的解析失败——原期望串张冠李戴。
        Assert.Contains("Invalid sensor count", diag.LastError);
    }

    // ---- 诊断状态累积 ----

    [Fact]
    public void ProcessStableSnapshot_MultipleCycles_DiagnosticsAccumulate()
    {
        using var harness = new SharedMemoryReaderHarness();

        // 原断言把 CPU 温度 44.0 当成传感器计数比（`diag.LastSensorCount > 0 ? ... : 0`），
        // 而 V2Buffer 不传 sensors ⇒ 计数恒 0，该等式无从成立（44.0 是温度，不是计数）。
        // 修复 = 喂入 1 个真实测试传感器，让 LastSensorCount 有确定语义，断言保留累积意图。
        TestSensor[] sensors = [new TestSensor(ShmLayout.TagCpuPower, "CPU Package", 52.5, "W", ShmLayout.HwCpu)];

        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 1, cpuTemperature: 42.0, sensors: sensors), counter: 1, DateTime.UtcNow.Ticks);
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 2, cpuTemperature: 43.0, sensors: sensors), counter: 2, DateTime.UtcNow.Ticks);
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 3, cpuTemperature: 44.0, sensors: sensors), counter: 3, DateTime.UtcNow.Ticks);

        var diag = SharedMemoryReader.Diagnostics;
        Assert.Equal(3, diag.LastCounter);
        Assert.Equal(1, diag.LastSensorCount);
        Assert.True(diag.LastReadUtc > DateTime.MinValue);
        Assert.True(diag.LastCommitUtc > DateTime.MinValue);
    }
}
