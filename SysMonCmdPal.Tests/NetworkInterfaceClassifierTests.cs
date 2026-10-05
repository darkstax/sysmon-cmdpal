// Copyright (c) 2026 SysMonCmdPal
// 网卡识别 + 手动选择测试 — 硬件白名单前缀、三态判定链、手动覆盖优先级、接口集合变化重置。
//
// 全部纯函数 / fixture 注入，不触碰真实注册表与网络栈。

using System.Net.NetworkInformation;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Xunit;

namespace SysMonCmdPal.Tests;

public class NetworkInterfaceClassifierTests
{
    // ================================================================
    // ClassifyPnpInstanceId — 硬件总线白名单前缀表
    // 数据来源：宿主机实测注册表绑定表（HKLM\SYSTEM\...\Network\{4D36E972...}）
    // ================================================================

    [Theory]
    // ---- 白名单：真实硬件总线 ⇒ PhysicalHardware ----
    [InlineData(@"PCI\VEN_10EC&DEV_8168&SUBSYS_208F1043&REV_15\01000000684CE00000", NicClassification.PhysicalHardware)]
    [InlineData(@"PCI\VEN_8086&DEV_2725&SUBSYS_00248086&REV_1A\4&383213db&0&0012", NicClassification.PhysicalHardware)]
    [InlineData(@"PCIENUM\VEN_8086&DEV_2725\0&0000", NicClassification.PhysicalHardware)]
    [InlineData(@"USB\VID_2717&PID_FF88&MI_00\7&32654382&0&0000", NicClassification.PhysicalHardware)]
    // 大小写不敏感（注册表值实测为大写，但不得依赖）
    [InlineData(@"pci\ven_10ec&dev_8168\0", NicClassification.PhysicalHardware)]
    [InlineData(@"  PCI\VEN_10EC\0  ", NicClassification.PhysicalHardware)]
    // ---- 非白名单前缀 ⇒ Virtual（宿主机实测的全部虚拟接口）----
    [InlineData(@"SWD\Wintun\{0DCCC63E-5622-3880-1E09-7CC9C46AD7B4}", NicClassification.Virtual)] // clash/sing-box TUN
    [InlineData(@"SWD\MSRRAS\MS_SSTPMINIPORT", NicClassification.Virtual)]                        // VPN 迷你端口
    [InlineData(@"SWD\MSRRAS\MS_NDISWANIPV6", NicClassification.Virtual)]
    [InlineData(@"BTH\MS_BTHPAN\7&7146bd8&0&2", NicClassification.Virtual)]                       // 蓝牙 PAN
    [InlineData(@"ROOT\KDNIC\0000", NicClassification.Virtual)]                                   // 内核调试器
    [InlineData(@"ROOT\VMS_VSMP\0000", NicClassification.Virtual)]                                // Hyper-V vSwitch
    [InlineData(@"{5d624f94-8850-40c3-a3fa-a4fd2080baf3}\vwifimp_wfd\5&283223bf&0&11", NicClassification.Virtual)] // Wi-Fi Direct
    [InlineData(@"ACPI\SOMETHING\0", NicClassification.Virtual)]
    [InlineData(@"VMBUS\{GUID}", NicClassification.Virtual)]
    // ---- 空 / null ⇒ Unknown（保守：交降级黑名单，绝不判 Virtual）----
    [InlineData(null, NicClassification.Unknown)]
    [InlineData("", NicClassification.Unknown)]
    [InlineData("   ", NicClassification.Unknown)]
    public void ClassifyPnpInstanceId_UsesHardwareBusWhitelist(
        string? pnpInstanceId,
        NicClassification expected)
    {
        Assert.Equal(expected, NetworkInterfaceClassifier.ClassifyPnpInstanceId(pnpInstanceId));
    }

    [Fact]
    public void ClassifyPnpInstanceId_UnknownIsNotVirtual()
    {
        // 回归守卫：Unknown 与 Virtual 必须是不同语义。
        // 把读失败误判成 Virtual 会过滤掉真网卡 ⇒ 速度恒为 0。
        Assert.NotEqual(
            NetworkInterfaceClassifier.ClassifyPnpInstanceId(null),
            NetworkInterfaceClassifier.ClassifyPnpInstanceId(@"SWD\Wintun\x"));
    }

    // ================================================================
    // ParseSelectedGuids — settings.json 选择值解析
    // ================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("Auto")]
    public void ParseSelectedGuids_AutoOrEmpty_YieldsEmptySet(string? raw)
    {
        Assert.Empty(NetworkInterfaceClassifier.ParseSelectedGuids(raw));
    }

    [Fact]
    public void ParseSelectedGuids_ParsesSingleAndMultipleGuids()
    {
        const string g1 = "{A844F74B-BAB2-459B-9EC8-922D56E146EC}";
        const string g2 = "{5B4FBC47-6C51-48AC-8D79-E6E057816F16}";

        Assert.Equal([g1], NetworkInterfaceClassifier.ParseSelectedGuids(g1));
        Assert.Equal([g1, g2], NetworkInterfaceClassifier.ParseSelectedGuids($"{g1};{g2}"));
        // 前后空白与空段容忍
        Assert.Equal([g1, g2], NetworkInterfaceClassifier.ParseSelectedGuids($" {g1} ;; {g2} ;"));
    }

    [Fact]
    public void ParseSelectedGuids_IgnoresMalformedTokens()
    {
        // 非 GUID 的垃圾值不得进入选择集合（否则永远匹配不上任何接口）
        var parsed = NetworkInterfaceClassifier.ParseSelectedGuids(
            "not-a-guid;{A844F74B-BAB2-459B-9EC8-922D56E146EC};12345");

        Assert.Single(parsed);
        Assert.Contains("{A844F74B-BAB2-459B-9EC8-922D56E146EC}", parsed);
    }

    // ================================================================
    // BuildNicChoices — 设置页选项构造
    // ================================================================

    private static string KindLabel(NicClassification kind) => kind switch
    {
        NicClassification.PhysicalHardware => "硬件",
        NicClassification.Virtual => "虚拟",
        _ => "未识别",
    };

    // 必须用真实 GUID 形态：ParseSelectedGuids 只接受合法 GUID
    // （NetworkInterface.Id 形如 {A844F74B-...}），占位符会被拒绝。
    private const string GuidHw1 = "{A844F74B-BAB2-459B-9EC8-922D56E146EC}";
    private const string GuidHw2 = "{5B4FBC47-6C51-48AC-8D79-E6E057816F16}";
    private const string GuidUnk = "{13C2129E-B2AE-4ECD-AA70-3D6B918BB548}";
    private const string GuidVirt = "{0DCCC63E-5622-3880-1E09-7CC9C46AD7B4}";
    private const string GuidGone = "{11111111-2222-3333-4444-555555555555}";

    private static List<NicChoiceSource> SampleChoices() =>
    [
        new(GuidVirt, "tun0", "sing-tun Tunnel", NicClassification.Virtual),
        new(GuidHw1, "以太网", "Realtek PCIe GbE Family Controller", NicClassification.PhysicalHardware),
        new(GuidUnk, "神秘网卡", "Some Unknown Adapter", NicClassification.Unknown),
        new(GuidHw2, "WLAN", "Intel(R) Wi-Fi 6E AX210 160MHz", NicClassification.PhysicalHardware),
    ];

    [Fact]
    public void BuildNicChoices_FirstChoiceIsAlwaysAuto()
    {
        var choices = NetworkInterfaceClassifier.BuildNicChoices(SampleChoices(), KindLabel, "自动（推荐）");

        Assert.Equal(NetworkInterfaceClassifier.AutoSelectionValue, choices[0].Value);
        Assert.Equal("自动（推荐）", choices[0].Title);
        // 全部可生效接口都在（用户需要看到虚拟接口才能纠正误判）
        Assert.Equal(5, choices.Count);
    }

    [Fact]
    public void BuildNicChoices_OrdersHardwareFirstThenUnknownThenVirtual()
    {
        var choices = NetworkInterfaceClassifier.BuildNicChoices(SampleChoices(), KindLabel, "自动（推荐）");
        var kinds = choices.Skip(1).Select(c => c.Value).ToList();

        Assert.Equal([GuidHw1, GuidHw2, GuidUnk, GuidVirt], kinds);
    }

    [Fact]
    public void BuildNicChoices_LabelsKindAndTruncatesLongDescription()
    {
        var choices = NetworkInterfaceClassifier.BuildNicChoices(SampleChoices(), KindLabel, "自动（推荐）");

        Assert.Equal("以太网 — 硬件 — Realtek PCIe GbE Family Controller", choices[1].Title);
        Assert.Equal("tun0 — 虚拟 — sing-tun Tunnel", choices[4].Title);

        // 长描述截断（避免 AdaptiveCard 选项标题过长）
        var longDesc = new string('x', 100);
        var truncated = NetworkInterfaceClassifier.BuildNicChoices(
            [new NicChoiceSource("{L}", "N", longDesc, NicClassification.Virtual)],
            KindLabel,
            "自动");
        Assert.Contains("…", truncated[1].Title);
        Assert.True(truncated[1].Title.Length < longDesc.Length + 20);
    }

    [Fact]
    public void BuildNicChoices_ValueIsInterfaceGuid()
    {
        var choices = NetworkInterfaceClassifier.BuildNicChoices(SampleChoices(), KindLabel, "自动（推荐）");

        // Choice.Value 必须是 NetworkInterface.Id，才能与手动选择集合匹配
        Assert.Contains(choices, c => c.Value == GuidHw1);
    }

    // ================================================================
    // F2 回归（t3 审查）：只列可生效接口
    // ================================================================

    [Fact]
    public void F2_BuildNicChoices_ExcludesNonEffectiveInterfaces()
    {
        // 回归（t3 F2）：宿主机实测 65 个接口里仅 20 个可生效，其余 45 个
        // （NotPresent 的 WAN Miniport、Down 的 filter 镜像等）选中是静默 no-op。
        // 修复后这些不得出现在选项里。
        List<NicChoiceSource> mixed =
        [
            new(GuidHw1, "以太网", "Realtek PCIe GbE Family Controller", NicClassification.PhysicalHardware, IsEffective: true),
            new(GuidGone, "本地连接* 3", "WAN Miniport (SSTP)", NicClassification.Virtual, IsEffective: false),
            new(GuidVirt, "tun0", "sing-tun Tunnel", NicClassification.Virtual, IsEffective: false),
            new(GuidHw2, "WLAN", "Intel(R) Wi-Fi 6E AX210 160MHz", NicClassification.PhysicalHardware, IsEffective: false),
        ];

        var choices = NetworkInterfaceClassifier.BuildNicChoices(mixed, KindLabel, "自动（推荐）");

        // auto + 唯一可生效项
        Assert.Equal(2, choices.Count);
        Assert.Contains(choices, c => c.Value == GuidHw1);
        Assert.DoesNotContain(choices, c => c.Value == GuidGone);
        Assert.DoesNotContain(choices, c => c.Value == GuidVirt);
        Assert.DoesNotContain(choices, c => c.Value == GuidHw2);
    }

    [Fact]
    public void F2_BuildNicChoices_StillStartsWithAutoWhenNothingEffective()
    {
        // 极端：没有任何可生效接口 ⇒ 只剩 auto（用户至少能看到"自动"）
        List<NicChoiceSource> noneEffective =
        [
            new(GuidVirt, "tun0", "sing-tun Tunnel", NicClassification.Virtual, IsEffective: false),
        ];

        var choices = NetworkInterfaceClassifier.BuildNicChoices(noneEffective, KindLabel, "自动（推荐）");

        Assert.Single(choices);
        Assert.Equal(NetworkInterfaceClassifier.AutoSelectionValue, choices[0].Value);
    }

    [Fact]
    public void F2_DefaultIsEffective_BackwardCompatible()
    {
        // IsEffective 默认 true：既有构造点（未显式传参）行为不变
        var src = new NicChoiceSource(GuidHw1, "以太网", "Realtek", NicClassification.PhysicalHardware);
        Assert.True(src.IsEffective);
    }

    [Fact]
    public void F2_NonEffectiveSelectedNic_FallsBackToAuto()
    {
        // 已选网卡变得不可生效（如临时 Down）⇒ 选项里没有它 ⇒ 收敛回 auto。
        // 这是刻意的：Down 的网卡采集不到流量，保留只会让用户看到 0 B/s。
        List<NicChoiceSource> onlyOther =
        [
            new(GuidHw1, "以太网", "Realtek PCIe GbE Family Controller", NicClassification.PhysicalHardware, IsEffective: true),
        ];

        var choices = NetworkInterfaceClassifier.BuildNicChoices(onlyOther, KindLabel, "自动");

        Assert.Equal("auto", NetworkInterfaceClassifier.ResolveNicSelection(GuidHw2, choices, "auto"));
    }

    // ================================================================
    // ResolveNicSelection — 接口集合变化时的收敛
    // ================================================================

    [Fact]
    public void ResolveNicSelection_KeepsValueWhenSelectedNicStillPresent()
    {
        var choices = NetworkInterfaceClassifier.BuildNicChoices(SampleChoices(), KindLabel, "自动");

        Assert.Equal(GuidHw1, NetworkInterfaceClassifier.ResolveNicSelection(GuidHw1, choices, "auto"));
    }

    [Fact]
    public void ResolveNicSelection_FallsBackToAutoWhenAllSelectedNicsGone()
    {
        var choices = NetworkInterfaceClassifier.BuildNicChoices(SampleChoices(), KindLabel, "自动");

        // 选中的网卡被拔出/卸载驱动 ⇒ 回退 auto（否则速度恒为 0）
        Assert.Equal("auto", NetworkInterfaceClassifier.ResolveNicSelection(GuidGone, choices, "auto"));
    }

    [Fact]
    public void ResolveNicSelection_KeepsValueWhenAtLeastOneOfMultipleStillPresent()
    {
        var choices = NetworkInterfaceClassifier.BuildNicChoices(SampleChoices(), KindLabel, "自动");

        Assert.Equal(
            $"{GuidGone};{GuidHw2}",
            NetworkInterfaceClassifier.ResolveNicSelection($"{GuidGone};{GuidHw2}", choices, "auto"));
    }

    [Fact]
    public void ResolveNicSelection_AutoStaysAuto()
    {
        var choices = NetworkInterfaceClassifier.BuildNicChoices(SampleChoices(), KindLabel, "自动");

        Assert.Equal("auto", NetworkInterfaceClassifier.ResolveNicSelection("auto", choices, "auto"));
        Assert.Equal("auto", NetworkInterfaceClassifier.ResolveNicSelection(null, choices, "auto"));
    }

    [Fact]
    public void ResolveNicSelection_EmptyChoiceListFallsBackToAuto()
    {
        // 极端：枚举不到任何接口（网络栈异常）⇒ 回退 auto 而非保留悬空 GUID
        Assert.Equal("auto", NetworkInterfaceClassifier.ResolveNicSelection(GuidHw1, [], "auto"));
    }
}
