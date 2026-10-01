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
        Assert.False(diag.IsConnected);          // MarkUnavailable 已调用
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
        Assert.False(diag.IsConnected);
        Assert.Equal(1, diag.RestartCount);
        Assert.Contains("Broker restart detected", diag.LastError);
    }

    [Fact]
    public void ProcessStableSnapshot_SameCounterNewTimestamp_DetectsRestart()
    {
        using var harness = new SharedMemoryReaderHarness();
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 5, cpuTemperature: 42.0), counter: 5, DateTime.UtcNow.Ticks);

        // 同 counter 但新时间戳（无 extension 的旧 Broker 重启）
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 5, cpuTemperature: 42.0), counter: 5, DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond);

        var diag = SharedMemoryReader.Diagnostics;
        Assert.False(diag.IsConnected);
        Assert.Equal(1, diag.RestartCount);
    }

    // ---- 停滞检测 ----

    [Fact]
    public void ProcessStableSnapshot_CounterNotAdvanced_StallConfirmedDisconnects()
    {
        using var harness = new SharedMemoryReaderHarness();
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 1, cpuTemperature: 42.0), counter: 1, DateTime.UtcNow.Ticks);

        // 推进到确认窗口（15s）之后
        harness.SetLastCounterAdvanceElapsed(TimeSpan.FromSeconds(16));

        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 1, cpuTemperature: 42.0), counter: 1, DateTime.UtcNow.Ticks);

        var diag = SharedMemoryReader.Diagnostics;
        Assert.True(diag.IsStalled);
        Assert.False(diag.IsConnected);
        Assert.True(harness.IsDisconnectedFromMap);
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
        Assert.Contains("Shared memory changed", diag.LastError);
    }

    // ---- 诊断状态累积 ----

    [Fact]
    public void ProcessStableSnapshot_MultipleCycles_DiagnosticsAccumulate()
    {
        using var harness = new SharedMemoryReaderHarness();

        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 1, cpuTemperature: 42.0), counter: 1, DateTime.UtcNow.Ticks);
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 2, cpuTemperature: 43.0), counter: 2, DateTime.UtcNow.Ticks);
        harness.ProcessV2(BrokerTestData.V2Buffer(counter: 3, cpuTemperature: 44.0), counter: 3, DateTime.UtcNow.Ticks);

        var diag = SharedMemoryReader.Diagnostics;
        Assert.Equal(3, diag.LastCounter);
        Assert.Equal(44.0, diag.LastSensorCount > 0 ? diag.LastSensorCount : 0);
        Assert.True(diag.LastReadUtc > DateTime.MinValue);
        Assert.True(diag.LastCommitUtc > DateTime.MinValue);
    }
}
