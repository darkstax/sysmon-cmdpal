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

    [Theory]
    [InlineData(NetworkInterfaceType.Ethernet, "hyper-v virtual ethernet adapter", "Ethernet", false)]
    [InlineData(NetworkInterfaceType.Wireless80211, "intel wi-fi 7", "bluetooth network connection", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "intel ethernet controller", "ethernet 2-qos packet scheduler", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "openvpn tap-windows adapter", "Ethernet", false)]
    [InlineData(NetworkInterfaceType.Ethernet, "realtek usb gbe family controller", "Ethernet", true)]
    [InlineData(NetworkInterfaceType.Wireless80211, "intel wi-fi 7 be200", "Wi-Fi", true)]
    public void IsPhysicalInterfaceCandidate_FiltersDescriptionsAndNamesCaseInsensitively(
        NetworkInterfaceType type,
        string description,
        string name,
        bool expected)
    {
        var result = NetworkMonitor.IsPhysicalInterfaceCandidate(
            OperationalStatus.Up,
            type,
            description,
            name,
            speed: 1_000_000_000);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(OperationalStatus.Down, NetworkInterfaceType.Ethernet, 1_000_000_000, false)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Loopback, 1_000_000_000, false)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Wireless80211, 0, false)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Wireless80211, 1_000_000_000, true)]
    public void IsPhysicalInterfaceCandidate_RequiresUpPhysicalInterfaceAndPositiveSpeed(
        OperationalStatus status,
        NetworkInterfaceType type,
        long speed,
        bool expected)
    {
        var result = NetworkMonitor.IsPhysicalInterfaceCandidate(
            status,
            type,
            description: "Intel Ethernet Controller",
            name: "Wi-Fi",
            speed);

        Assert.Equal(expected, result);
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

    [Theory]
    [InlineData(8, true)]      // charging
    [InlineData(10, true)]     // low + charging
    [InlineData(12, true)]     // critical + charging
    [InlineData(128, false)]   // no system battery
    [InlineData(255, false)]   // unknown
    public void HasSystemBattery_TreatsBatteryFlagAsBitMask(int flag, bool expected)
    {
        Assert.Equal(expected, SystemInfoService.HasSystemBattery(flag));
    }

    [Fact]
    public void ParseWifiSsid_IgnoresBssidAndReturnsSsid()
    {
        var output = "    BSSID : 11:22:33:44:55:66\n    SSID : MyNetwork\n";

        Assert.Equal("MyNetwork", SystemInfoService.ParseWifiSsid(output));
    }

    [Fact]
    public void ParseWifiSsid_EmptyOutput_ReturnsEmpty()
    {
        Assert.Equal("", SystemInfoService.ParseWifiSsid(""));
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
