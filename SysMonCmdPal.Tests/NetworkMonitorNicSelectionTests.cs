// Copyright (c) 2026 SysMonCmdPal
// 三层判定链 + 手动覆盖优先级 + 接口集合变化重置测试。
//
// fixture 数据全部取自宿主机实测（2025-10-05 注册表绑定表 + NetworkInterface 枚举），
// 不触碰真实注册表/网络栈 —— 判定链是纯函数 SelectInterfaceIds / IsPhysicalInterfaceCandidate。

using System.Net.NetworkInformation;
using Xunit;

namespace SysMonCmdPal.Tests;

public class NetworkMonitorNicSelectionTests
{
    private const long Gbe = 1_000_000_000;

    // ================================================================
    // 宿主机实测 fixture：接口 Id / 描述 / 绑定表 PnpInstanceID
    // ================================================================

    private static readonly string HwEthernetGuid = "{A844F74B-BAB2-459B-9EC8-922D56E146EC}";
    private static readonly string HwWlanGuid = "{5B4FBC47-6C51-48AC-8D79-E6E057816F16}";
    private static readonly string HyperVDefaultGuid = "{13C2129E-B2AE-4ECD-AA70-3D6B918BB548}";
    private static readonly string HyperVWslGuid = "{FB5D5E33-E942-4089-854F-6ADFE58FFD1E}";
    private static readonly string KasperskyMirrorGuid = "{4622D370-C075-11F1-8017-30050549416A}";
    private static readonly string VSwitchGuid = "{D5539751-CA69-4B63-B048-19994AF0B850}";

    private static NicCandidate HwEthernet() => new(
        HwEthernetGuid, OperationalStatus.Up, NetworkInterfaceType.Ethernet,
        "Realtek PCIe GbE Family Controller", "以太网", Gbe,
        NicClassification.PhysicalHardware);

    private static NicCandidate HwWlanDown() => new(
        HwWlanGuid, OperationalStatus.Down, NetworkInterfaceType.Wireless80211,
        "Intel(R) Wi-Fi 6E AX210 160MHz", "WLAN", -1,
        NicClassification.PhysicalHardware);

    private static NicCandidate HyperVDefault() => new(
        HyperVDefaultGuid, OperationalStatus.Up, NetworkInterfaceType.Ethernet,
        "Hyper-V Virtual Ethernet Adapter", "vEthernet (Default Switch)", 10 * Gbe,
        NicClassification.Unknown);   // 绑定表内 Connection 子键无 PnpInstanceID ⇒ Unknown

    private static NicCandidate HyperVWsl() => new(
        HyperVWslGuid, OperationalStatus.Up, NetworkInterfaceType.Ethernet,
        "Hyper-V Virtual Ethernet Adapter #2", "vEthernet (WSL (Hyper-V firewall))", 10 * Gbe,
        NicClassification.Unknown);

    private static NicCandidate KasperskyMirror() => new(
        KasperskyMirrorGuid, OperationalStatus.Up, NetworkInterfaceType.Ethernet,
        "Realtek PCIe GbE Family Controller-Kaspersky Lab NDIS 6 Filter-0000",
        "以太网-Kaspersky Lab NDIS 6 Filter-0000", Gbe,
        NicClassification.Unknown);   // 镜像接口不在绑定表内 ⇒ Unknown

    private static NicCandidate HyperVVSwitch() => new(
        VSwitchGuid, OperationalStatus.Up, NetworkInterfaceType.Ethernet,
        "Hyper-V Virtual Switch Extension Adapter", "vSwitch (Default Switch)", 10 * Gbe,
        NicClassification.Virtual);   // ROOT\VMS_VSMP\0000

    private static IReadOnlySet<string> None() => new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> Select(params string[] ids) =>
        new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);

    // ================================================================
    // ① 硬件白名单层：PhysicalHardware 命中 ⇒ 保留，且跳过关键词黑名单
    // ================================================================

    [Fact]
    public void Whitelist_PhysicalHardwareIsKept()
    {
        var selected = NetworkMonitor.SelectInterfaceIds([HwEthernet(), HwWlanDown()], None());

        Assert.Equal([HwEthernetGuid], selected);
    }

    [Fact]
    public void Whitelist_PhysicalHardwareSkipsKeywordBlacklist()
    {
        // 名字/描述里含黑名单关键词的真硬件网卡不得被误杀
        // （黑名单有 "Virtual"，个别板载网卡描述确实可能命中）。
        var oddHardware = new NicCandidate(
            "{ODD}", OperationalStatus.Up, NetworkInterfaceType.Ethernet,
            "Some Virtual Named Real Adapter", "Ethernet", Gbe,
            NicClassification.PhysicalHardware);

        Assert.True(NetworkMonitor.IsPhysicalInterfaceCandidate(
            OperationalStatus.Up, NetworkInterfaceType.Ethernet,
            "Some Virtual Named Real Adapter", "Ethernet", Gbe,
            NicClassification.PhysicalHardware));
    }

    [Fact]
    public void Whitelist_VirtualIsAlwaysExcluded()
    {
        Assert.False(NetworkMonitor.IsPhysicalInterfaceCandidate(
            OperationalStatus.Up, NetworkInterfaceType.Ethernet,
            "Hyper-V Virtual Switch Extension Adapter", "vSwitch (Default Switch)", 10 * Gbe,
            NicClassification.Virtual));

        var selected = NetworkMonitor.SelectInterfaceIds(
            [HwEthernet(), HyperVVSwitch()], None());

        Assert.Equal([HwEthernetGuid], selected);
    }

    // ================================================================
    // ② 白名单命中时的过滤器镜像兜底（防流量翻倍回归）
    // ================================================================

    [Theory]
    [InlineData("Realtek PCIe GbE Family Controller-Kaspersky Lab NDIS 6 Filter-0000", "以太网-Kaspersky Lab NDIS 6 Filter-0000")]
    [InlineData("Realtek PCIe GbE Family Controller-WFP Native MAC Layer LightWeight Filter-0000", "以太网-WFP Native MAC Layer LightWeight Filter-0000")]
    [InlineData("Intel(R) Wi-Fi 6E AX210 160MHz-Native WiFi Filter Driver-0000", "WLAN-Native WiFi Filter Driver-0000")]
    [InlineData("Realtek PCIe GbE Family Controller-QoS Packet Scheduler-0000", "以太网-QoS Packet Scheduler-0000")]
    public void Whitelist_FilterMirrorTokensStillExcludedEvenIfWhitelisted(
        string description,
        string name)
    {
        // 宿主机实测镜像接口不在绑定表内（走 Unknown + 黑名单），
        // 但万一未来 Windows 把父网卡 PnpInstanceID 也写进镜像接口子键，
        // 白名单就会把它判成 PhysicalHardware 从而跳过黑名单 ⇒ 流量翻倍复现。
        // 这些 token 绝不出现在真硬件描述里，故即使白名单命中也必须排除。
        Assert.False(NetworkMonitor.IsPhysicalInterfaceCandidate(
            OperationalStatus.Up, NetworkInterfaceType.Ethernet,
            description, name, Gbe,
            NicClassification.PhysicalHardware));
    }

    [Fact]
    public void Whitelist_RealHardwareDescriptionsDoNotHitMirrorTokens()
    {
        // 反向守卫：真硬件描述不得被镜像兜底误杀
        foreach (var (desc, name) in new[]
        {
            ("Realtek PCIe GbE Family Controller", "以太网"),
            ("Intel(R) Wi-Fi 6E AX210 160MHz", "WLAN"),
            ("Intel(R) Ethernet Connection I219-V", "Ethernet 2"),
            ("Realtek USB GbE Family Controller", "Ethernet 3"),
        })
        {
            Assert.True(NetworkMonitor.IsPhysicalInterfaceCandidate(
                OperationalStatus.Up, NetworkInterfaceType.Ethernet, desc, name, Gbe,
                NicClassification.PhysicalHardware), desc);
        }
    }

    // ================================================================
    // ③ Unknown 降级：行为必须与现状（无 classification）逐字节一致
    // ================================================================

    [Theory]
    [InlineData("hyper-v virtual ethernet adapter", "Ethernet", false)]
    [InlineData("intel ethernet controller", "ethernet 2-qos packet scheduler", false)]
    [InlineData("openvpn tap-windows adapter", "Ethernet", false)]
    [InlineData("Realtek PCIe GbE Family Controller-Kaspersky Lab NDIS 6 Filter-0000", "以太网-Kaspersky Lab NDIS 6 Filter-0000", false)]
    [InlineData("intel wi-fi 7", "bluetooth network connection", false)]
    [InlineData("realtek usb gbe family controller", "Ethernet", true)]
    [InlineData("intel wi-fi 7 be200", "Wi-Fi", true)]
    public void Unknown_FallsBackToKeywordBlacklist(
        string description,
        string name,
        bool expected)
    {
        var unknown = NetworkMonitor.IsPhysicalInterfaceCandidate(
            OperationalStatus.Up, NetworkInterfaceType.Ethernet, description, name, Gbe,
            NicClassification.Unknown);

        var legacy = NetworkMonitor.IsPhysicalInterfaceCandidate(
            OperationalStatus.Up, NetworkInterfaceType.Ethernet, description, name, Gbe);

        Assert.Equal(expected, unknown);
        // 降级路径必须与旧签名（无 classification）结果完全一致
        Assert.Equal(legacy, unknown);
    }

    [Fact]
    public void Unknown_NullClassificationIsSameAsUnknown()
    {
        Assert.Equal(
            NetworkMonitor.IsPhysicalInterfaceCandidate(
                OperationalStatus.Up, NetworkInterfaceType.Ethernet, "Hyper-V Virtual Adapter", "Ethernet", Gbe,
                NicClassification.Unknown),
            NetworkMonitor.IsPhysicalInterfaceCandidate(
                OperationalStatus.Up, NetworkInterfaceType.Ethernet, "Hyper-V Virtual Adapter", "Ethernet", Gbe,
                null));
    }

    [Fact]
    public void HostFixture_KasperskyMirrorAndHyperVAreExcludedByUnknownPath()
    {
        // 宿主机实测：卡巴斯基镜像 + Hyper-V vEthernet 都不在绑定表/无 PnpInstanceID ⇒ Unknown。
        // 正是靠降级黑名单兜住它们 —— 这是"流量翻倍"修复不回归的关键。
        var selected = NetworkMonitor.SelectInterfaceIds(
            [HwEthernet(), HyperVDefault(), HyperVWsl(), KasperskyMirror(), HyperVVSwitch()],
            None());

        Assert.Equal([HwEthernetGuid], selected);
    }

    // ================================================================
    // 硬门槛：三层共用
    // ================================================================

    [Theory]
    [InlineData(OperationalStatus.Down, NetworkInterfaceType.Ethernet, Gbe, false)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Loopback, Gbe, false)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Tunnel, Gbe, false)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Ethernet, 0, false)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Ethernet, -1, false)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Ethernet, Gbe, true)]
    [InlineData(OperationalStatus.Up, NetworkInterfaceType.Wireless80211, Gbe, true)]
    public void HardGate_AppliesToAllLayers(
        OperationalStatus status,
        NetworkInterfaceType type,
        long speed,
        bool expected)
    {
        Assert.Equal(expected, NetworkMonitor.PassesHardGate(status, type, speed));

        // 即使白名单命中，硬门槛也必须生效（Down 的网卡没有流量可采）
        Assert.Equal(expected, NetworkMonitor.IsPhysicalInterfaceCandidate(
            status, type, "Realtek PCIe GbE Family Controller", "以太网", speed,
            NicClassification.PhysicalHardware));
    }

    [Fact]
    public void HardGate_ManualSelectionCannotBypass()
    {
        // 用户手选了但网卡是 Down ⇒ 仍不采集（避免把"无流量"误报成"已选中"）
        var selected = NetworkMonitor.SelectInterfaceIds(
            [HwWlanDown()], Select(HwWlanGuid));

        Assert.Empty(selected);
    }

    // ================================================================
    // ④ 手动覆盖优先级
    // ================================================================

    [Fact]
    public void ManualSelection_OverridesAutomaticDetection()
    {
        // 自动判定会选物理网卡；用户手选 Hyper-V（自动判定排除它）⇒ 以用户为准
        var selected = NetworkMonitor.SelectInterfaceIds(
            [HwEthernet(), HyperVDefault()], Select(HyperVDefaultGuid));

        Assert.Equal([HyperVDefaultGuid], selected);
    }

    [Fact]
    public void ManualSelection_CanPickNicThatKeywordBlacklistWouldReject()
    {
        // 用户之所以手选，往往正是因为自动判定错了。
        // 手选必须能压过关键词黑名单，否则这个功能毫无意义。
        var virtualLookingReal = new NicCandidate(
            "{MANUAL}", OperationalStatus.Up, NetworkInterfaceType.Ethernet,
            "Some Virtual Looking Adapter", "Ethernet", Gbe,
            NicClassification.Unknown);

        var automatic = NetworkMonitor.SelectInterfaceIds([virtualLookingReal], None());
        Assert.Empty(automatic);   // 自动判定排除（描述含 "Virtual"）

        var manual = NetworkMonitor.SelectInterfaceIds([virtualLookingReal], Select("{MANUAL}"));
        Assert.Equal(["{MANUAL}"], manual);
    }

    [Fact]
    public void ManualSelection_OnlyKeepsSelectedInterfaces()
    {
        var selected = NetworkMonitor.SelectInterfaceIds(
            [HwEthernet(), HwWlanDown(), HyperVDefault()],
            Select(HwEthernetGuid));

        Assert.Equal([HwEthernetGuid], selected);
    }

    [Fact]
    public void ManualSelection_IsCaseInsensitive()
    {
        var selected = NetworkMonitor.SelectInterfaceIds(
            [HwEthernet()], Select(HwEthernetGuid.ToLowerInvariant()));

        Assert.Equal([HwEthernetGuid], selected);
    }

    // ================================================================
    // ⑤ 接口集合变化时重置（选中网卡全部消失 ⇒ 回退自动判定）
    // ================================================================

    [Fact]
    public void InterfaceSetChanged_FallsBackToAutomaticWhenSelectedNicGone()
    {
        // 选中网卡被拔出 ⇒ 不得返回空集合（速度恒为 0 = 用户看到"网络坏了"）
        var selected = NetworkMonitor.SelectInterfaceIds(
            [HwEthernet(), HyperVDefault()],
            Select("{GONE}"));

        Assert.Equal([HwEthernetGuid], selected);
    }

    [Fact]
    public void InterfaceSetChanged_KeepsManualWhileSelectedNicPresent()
    {
        var selected = NetworkMonitor.SelectInterfaceIds(
            [HwEthernet(), HyperVDefault()],
            Select(HyperVDefaultGuid, "{GONE}"));

        Assert.Equal([HyperVDefaultGuid], selected);
    }

    [Fact]
    public void InterfaceSetChanged_EmptyInterfaceListYieldsEmptyResult()
    {
        // 网络栈返回 0 个接口 ⇒ 空集合（ReadSpeedInternal 会据此重置平滑与基线）
        Assert.Empty(NetworkMonitor.SelectInterfaceIds([], None()));
        Assert.Empty(NetworkMonitor.SelectInterfaceIds([], Select(HwEthernetGuid)));
    }

    [Fact]
    public void InterfaceSetChanged_AllNicsDownYieldsEmptyAndTriggersReset()
    {
        var selected = NetworkMonitor.SelectInterfaceIds([HwWlanDown()], None());

        Assert.Empty(selected);
    }

    // ================================================================
    // ⑥ 注册表绑定表读取接缝（fixture 注入）
    // ================================================================

    private sealed class FixtureBindingProvider(Dictionary<string, string?> table) : INicBindingProvider
    {
        public string? GetPnpInstanceId(string interfaceGuid)
            => table.TryGetValue(interfaceGuid, out var v) ? v : null;
    }

    [Fact]
    public void BindingProvider_MapsGuidToClassification()
    {
        var provider = new FixtureBindingProvider(new Dictionary<string, string?>
        {
            [HwEthernetGuid] = @"PCI\VEN_10EC&DEV_8168\01000000684CE00000",
            [HyperVWslGuid] = null,
        });

        Assert.Equal(
            NicClassification.PhysicalHardware,
            NetworkInterfaceClassifier.ClassifyPnpInstanceId(provider.GetPnpInstanceId(HwEthernetGuid)));
        Assert.Equal(
            NicClassification.Unknown,
            NetworkInterfaceClassifier.ClassifyPnpInstanceId(provider.GetPnpInstanceId(HyperVWslGuid)));
        Assert.Equal(
            NicClassification.Unknown,
            NetworkInterfaceClassifier.ClassifyPnpInstanceId(provider.GetPnpInstanceId("{NOT-IN-TABLE}")));
    }

    [Fact]
    public void BindingProvider_ThrowingProviderDegradesToUnknown()
    {
        // 接缝抛异常时必须降级 Unknown（最坏情况 = 现状，不会更差）
        var provider = new ThrowingBindingProvider();
        string? pnp = null;
        try { pnp = provider.GetPnpInstanceId(HwEthernetGuid); }
        catch { pnp = null; }

        Assert.Equal(NicClassification.Unknown,
            NetworkInterfaceClassifier.ClassifyPnpInstanceId(pnp));
    }

    private sealed class ThrowingBindingProvider : INicBindingProvider
    {
        public string? GetPnpInstanceId(string interfaceGuid)
            => throw new UnauthorizedAccessException("simulated registry denial");
    }

    // ================================================================
    // ⑦ 手动选择缓存失效（settings 变更 ⇒ 立即生效）
    // ================================================================

    [Fact]
    public void ManualSelectionOverride_IsReadThroughCacheAndInvalidated()
    {
        string? value = null;
        NetworkMonitor.SetManualSelectionOverride(() => value);
        try
        {
            Assert.Empty(NetworkMonitor.GetManualSelection());

            // 缓存窗口内改值不生效（1s TTL）
            value = HwEthernetGuid;
            Assert.Empty(NetworkMonitor.GetManualSelection());

            // 清缓存后立即生效
            NetworkMonitor.SetManualSelectionOverride(() => value);
            Assert.Equal([HwEthernetGuid], NetworkMonitor.GetManualSelection());
        }
        finally
        {
            NetworkMonitor.ResetBindingProvider();
        }
    }

    [Fact]
    public void ResetBindingProvider_ClearsManualSelectionCache()
    {
        NetworkMonitor.SetManualSelectionOverride(() => HwEthernetGuid);
        try
        {
            Assert.Equal([HwEthernetGuid], NetworkMonitor.GetManualSelection());

            NetworkMonitor.ResetBindingProvider();
            Assert.Empty(NetworkMonitor.GetManualSelection());   // override 已清除
        }
        finally
        {
            NetworkMonitor.ResetBindingProvider();
        }
    }
}
