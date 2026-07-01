// Copyright (c) 2026 SysMonCmdPal
// SystemInfoService 测试 — 网络接口过滤 + 结构体 + SensorBackend 枚举

using System.Net.NetworkInformation;
using Xunit;

namespace SysMonCmdPal.Tests;

public class SystemInfoServiceTests
{
    // ================================================================
    // GetPhysicalInterfacesStatic — 网络接口过滤
    // ================================================================

    [Fact]
    public void GetPhysicalInterfaces_ExcludesVirtual_AllResultsArePhysical()
    {
        var interfaces = NetworkMonitor.GetPhysicalInterfaces();

        foreach (var ni in interfaces)
        {
            Assert.True(
                ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                $"接口 {ni.Name} ({ni.Description}) 类型是 {ni.NetworkInterfaceType}，" +
                "应为 Ethernet 或 Wireless80211");

            var desc = ni.Description ?? "";
            Assert.DoesNotContain("Hyper-V", desc);
            Assert.DoesNotContain("vEthernet", desc);
            Assert.DoesNotContain("WSL", desc);
            Assert.DoesNotContain("Virtual", desc);
            Assert.DoesNotContain("Loopback", desc);
            Assert.DoesNotContain("Teredo", desc);
            Assert.DoesNotContain("ISATAP", desc);
            Assert.DoesNotContain("Bluetooth", desc);
            Assert.DoesNotContain("Wintun", desc);
            Assert.DoesNotContain("Tunnel", desc);

            var name = ni.Name ?? "";
            Assert.DoesNotContain("Bluetooth", name);
            Assert.DoesNotContain("-WFP", name);
            Assert.DoesNotContain("-Native WiFi Filter", name);
            Assert.DoesNotContain("-QoS Packet Scheduler", name);

            Assert.True(ni.Speed > 0, $"接口 {ni.Name} 的 Speed 应为正数");
            Assert.Equal(OperationalStatus.Up, ni.OperationalStatus);
        }
    }

    [Fact]
    public void GetPhysicalInterfaces_ReturnsNoDuplicates()
    {
        var interfaces = NetworkMonitor.GetPhysicalInterfaces();
        var ids = interfaces.Select(ni => ni.Id).ToList();

        Assert.Equal(ids.Distinct().Count(), ids.Count);
    }

    // ================================================================
    // DockFormat.Speed — 格式化边界值
    // ================================================================

    [Fact]
    public void DockFormat_Speed_EdgeCases()
    {
        // 负值
        Assert.Equal("0 B/s", DockFormat.Speed(-1));
        Assert.Equal("0 B/s", DockFormat.Speed(-100));

        // 零
        Assert.Equal("0 B/s", DockFormat.Speed(0));

        // 小于 1（舍入到 0 B/s）
        Assert.Equal("0 B/s", DockFormat.Speed(0.4));

        // B/s 范围 [1, 999]
        Assert.Equal("1 B/s", DockFormat.Speed(1));
        Assert.Equal("999 B/s", DockFormat.Speed(999));

        // KB/s 范围 [1000, 999999]
        Assert.Equal("1 KB/s", DockFormat.Speed(1000));
        Assert.Equal("2 KB/s", DockFormat.Speed(1500));    // 1500/1000 = 1.5 → F0 = 2
        Assert.Equal("1000 KB/s", DockFormat.Speed(999_999));

        // MB/s 范围
        Assert.Equal("1.0 MB/s", DockFormat.Speed(1_000_000));
        Assert.Equal("1.5 MB/s", DockFormat.Speed(1_500_000));
        Assert.Equal("10.0 MB/s", DockFormat.Speed(10_000_000));
    }

    // ================================================================
    // SystemSnapshot — 默认值
    // ================================================================

    [Fact]
    public void SystemSnapshot_Default_AllZeros()
    {
        var s = new SystemSnapshot();
        Assert.Equal(0, s.CpuUsage);
        Assert.Equal(0, s.MemoryTotalBytes);
        Assert.Equal(0, s.MemoryUsedBytes);
        Assert.Equal(0, s.NetDown);
        Assert.Equal(0, s.NetUp);
        Assert.Equal(0, s.BatteryPercent);
        // struct default: reference types are null
        Assert.Null(s.BatteryStatus);
        Assert.Null(s.Disks);
        Assert.Null(s.Gpus);
        Assert.Null(s.BackendNote);
        Assert.Equal(0, s.CpuTemperature);
        // enum default ctor = 0 = Broker (first value)
        Assert.Equal(SensorBackend.Broker, s.Backend);
    }

    // ================================================================
    // DiskInfo — 默认值
    // ================================================================

    [Fact]
    public void DiskInfo_Default_Values()
    {
        var d = new DiskInfo();
        Assert.Null(d.Name);
        Assert.Null(d.VolumeLabel);
        Assert.Equal(0, d.TotalBytes);
        Assert.Equal(0, d.FreeBytes);
        Assert.Equal(0, d.UsedPercent);
        // struct default ctor zeroes all fields
        Assert.Equal(0, d.ReadBytesPerSec);
        Assert.Equal(0, d.WriteBytesPerSec);
    }

    // ================================================================
    // GpuInfo — 默认值
    // ================================================================

    [Fact]
    public void GpuInfo_Default_AllZero()
    {
        var g = new GpuInfo();
        Assert.Null(g.Name);
        Assert.Equal(0, g.UsagePercent);   // struct default ctor zeroes
        Assert.Equal(0, g.Temperature);
        Assert.Equal(0, g.MemoryUsedMB);
        Assert.Equal(0, g.MemoryTotalMB);
    }

    // ================================================================
    // SensorBackend — 枚举值
    // ================================================================

    [Fact]
    public void SensorBackend_HasExpectedMembers()
    {
        var values = Enum.GetValues<SensorBackend>();
        Assert.Contains(SensorBackend.Broker, values);
        Assert.Contains(SensorBackend.HWiNFO, values);
        Assert.Contains(SensorBackend.ThermalZone, values);
        Assert.Contains(SensorBackend.None, values);
    }
}
