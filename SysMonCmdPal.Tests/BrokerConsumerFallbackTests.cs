// Copyright (c) 2026 SysMonCmdPal

using SysMonCmdPal.Broker;
using Xunit;

namespace SysMonCmdPal.Tests;

public class BrokerConsumerFallbackTests
{
    [Fact]
    public void CpuReader_ExpiredBrokerPayloadFallsThroughInsteadOfReturningBroker()
    {
        using var brokerScope = new BrokerRuntimeTestScope();
        using var fallbackScope = DisableCpuFallbacks();
        brokerScope.SetSnapshot(BrokerTestData.Snapshot(
            isAvailable: true,
            isExpired: true,
            cpuTemperature: 93,
            cpuSource: "MustNotEscape"));

        CpuTempResult result = CpuSensorReader.Read();

        Assert.Equal(CpuTempResult.None, result);
    }

    [Fact]
    public void GpuReader_ExpiredBrokerPayloadFallsThroughInsteadOfReturningBroker()
    {
        using var brokerScope = new BrokerRuntimeTestScope();
        using var fallbackScope = DisableGpuFallbacks();
        brokerScope.SetSnapshot(BrokerTestData.Snapshot(
            isAvailable: true,
            isExpired: true,
            gpus:
            [
                new KeyValuePair<int, BrokerGpuSnapshot>(0, new BrokerGpuSnapshot
                {
                    Name = "Must Not Escape",
                    Temperature = 99,
                }),
            ]));

        List<GpuResult> results = GpuSensorReader.ReadAll();

        Assert.Empty(results);
    }

    [Fact]
    public void PowerReader_ExpiredBrokerPayloadFallsThroughInsteadOfReturningBroker()
    {
        using var brokerScope = new BrokerRuntimeTestScope();
        using var fallbackScope = DisableHwinfo();
        brokerScope.SetSnapshot(BrokerTestData.Snapshot(
            isAvailable: true,
            isExpired: true,
            sensors:
            [
                new BrokerSensorEntry
                {
                    Tag = ShmLayout.TagCpuPower,
                    Name = "CPU Package",
                    Value = 120,
                    Unit = "W",
                },
            ]));

        SystemPowerResult result = SystemPowerReader.Read();

        Assert.Equal(SystemPowerResult.None, result);
    }

    [Fact]
    public void AvailableBrokerPayloadRemainsFirstChoiceForAllConsumers()
    {
        using var brokerScope = new BrokerRuntimeTestScope();
        brokerScope.SetSnapshot(BrokerTestData.Snapshot(
            isAvailable: true,
            cpuTemperature: 64,
            cpuSource: "Broker_Primary",
            gpus:
            [
                new KeyValuePair<int, BrokerGpuSnapshot>(0, new BrokerGpuSnapshot
                {
                    Name = "Primary GPU",
                    Temperature = 73,
                    UsagePercent = 81,
                }),
            ],
            sensors:
            [
                new BrokerSensorEntry
                {
                    Tag = ShmLayout.TagCpuPower,
                    Name = "CPU Package",
                    Value = 48,
                    Unit = "W",
                },
            ]));

        CpuTempResult cpu = CpuSensorReader.Read();
        GpuResult gpu = Assert.Single(GpuSensorReader.ReadAll());
        SystemPowerResult power = SystemPowerReader.Read();

        Assert.Equal(new CpuTempResult(64, "Broker_Primary"), cpu);
        Assert.Equal("Primary GPU", gpu.Name);
        Assert.Equal("Broker", gpu.Source);
        Assert.Equal(new SystemPowerResult(48, "Broker (CPU 48.0)"), power);
    }

    private static PrivateFieldScope DisableCpuFallbacks()
    {
        PrivateFieldScope scope = DisableHwinfo();
        scope.Set(ThermalZoneReader.Instance, "_initAttempted", true);
        scope.Set(ThermalZoneReader.Instance, "_available", false);
        return scope;
    }

    private static PrivateFieldScope DisableGpuFallbacks()
    {
        PrivateFieldScope scope = DisableHwinfo();
        scope.Set(D3dkmtGpuReader.Instance, "_initAttempted", true);
        scope.Set(D3dkmtGpuReader.Instance, "_adapters", null);
        scope.Set(PdhGpuReader.Instance, "_initAttempted", true);
        scope.Set(PdhGpuReader.Instance, "_available", false);
        scope.Set(PdhGpuReader.Instance, "_category", null);
        return scope;
    }

    private static PrivateFieldScope DisableHwinfo()
    {
        var scope = new PrivateFieldScope();
        scope.Set(HwinfoSharedMemoryReader.Instance, "_available", false);
        scope.Set(HwinfoSharedMemoryReader.Instance, "_lastRetryTime", DateTime.UtcNow);
        return scope;
    }
}
