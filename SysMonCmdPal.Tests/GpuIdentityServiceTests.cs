// Copyright (c) 2026 SysMonCmdPal
// T2-3（P1-3）：GPU 身份判定单一来源。
// 本文件取代原 GpuClassifierTests（28 个展开用例）——分类逻辑原样迁入
// GpuIdentityService.ClassifyName 并**降级为辅助判据**；新增前缀判据与真实机器用例。
using System;
using System.Collections.Generic;
using Xunit;

namespace SysMonCmdPal.Tests;

public sealed class GpuIdentityServiceTests
{
    // ================= 迁入的原名称 marker 判据（辅助层，28 例语义保持） =================

    [Theory]
    [InlineData("Intel(R) UHD Graphics 770", 2048)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", 4096)]
    [InlineData("Intel(R) Arc(TM) Graphics", 8192)]
    [InlineData("AMD Radeon(TM) Graphics", 4096)]
    [InlineData("AMD Radeon 780M Graphics", 4096)]
    [InlineData("AMD Radeon 8060S Graphics", 8192)]
    [InlineData("AMD Radeon RX Vega 10 Graphics", 2048)]
    [InlineData("Qualcomm Adreno X1-85 GPU", 16384)]
    public void ClassifyName_KnownIntegratedGpu_ReturnsIntegrated(string name, double memoryTotalMb)
    {
        var gpu = new GpuInfo { Name = name, MemoryTotalMB = memoryTotalMb };

        Assert.Equal(GpuKind.Integrated, GpuIdentityService.ClassifyName(gpu.Name, gpu.MemoryTotalMB));
        Assert.Equal(SysMonIcons.GpuIntegrated, Service().ResolveIcon(gpu));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090")]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("AMD Radeon Pro W6800")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    [InlineData("Intel Arc B580 Graphics")]
    [InlineData("Intel(R) Arc(TM) Pro A60 Graphics")]
    [InlineData("AMD Radeon RX 7600M XT")]
    public void ClassifyName_KnownDiscreteGpu_ReturnsDiscrete(string name)
    {
        var gpu = new GpuInfo { Name = name };

        Assert.Equal(GpuKind.Discrete, GpuIdentityService.ClassifyName(gpu.Name, gpu.MemoryTotalMB));
        Assert.Equal(SysMonIcons.GpuDiscrete, Service().ResolveIcon(gpu));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Mystery GPU")]
    public void ClassifyName_UnknownWithoutMemory_ReturnsUnknown(string? name)
    {
        var gpu = new GpuInfo { Name = name! };

        Assert.Equal(GpuKind.Unknown, GpuIdentityService.ClassifyName(gpu.Name, gpu.MemoryTotalMB));
        Assert.Equal(SysMonIcons.Gpu, Service().ResolveIcon(gpu));
    }

    [Fact]
    public void ClassifyName_UnknownNameWithMemory_UsesDiscreteFallback()
    {
        var gpu = new GpuInfo { Name = "Vendor Accelerator", MemoryTotalMB = 8192 };

        Assert.Equal(GpuKind.Discrete, GpuIdentityService.ClassifyName(gpu.Name, gpu.MemoryTotalMB));
    }

    [Theory]
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("Remote Display Adapter")]
    [InlineData("Contoso Virtual Display Adapter")]
    [InlineData("VMware SVGA 3D")]
    [InlineData("VirtualBox Graphics Adapter")]
    [InlineData("Parallels Display Adapter")]
    [InlineData("Citrix Indirect Display Adapter")]
    [InlineData("VirtIO GPU")]
    [InlineData("QXL Display Adapter")]
    public void ClassifyName_NonPhysicalDisplayAdapter_IgnoresMemoryFallback(string name)
    {
        var gpu = new GpuInfo { Name = name, MemoryTotalMB = 8192 };

        Assert.Equal(GpuKind.Unknown, GpuIdentityService.ClassifyName(gpu.Name, gpu.MemoryTotalMB));
        Assert.Equal(SysMonIcons.Gpu, Service().ResolveIcon(gpu));
    }

    // ================= 新增：前缀判据（唯一硬判据，真实机器数据） =================

    [Theory]
    [InlineData(HwinfoTestData.PhysicalIdAmd, true)]
    [InlineData(HwinfoTestData.PhysicalIdNvidia, true)]
    [InlineData(@"PCI\VEN_8086&DEV_A780\3&11583659&0&10", true)]
    [InlineData(@"PCIENUM\VEN_10DE&DEV_28E0\1", true)]
    [InlineData(HwinfoTestData.VirtualIdMuMu, false)]
    [InlineData(HwinfoTestData.VirtualIdGameViewer, false)]
    [InlineData(HwinfoTestData.VirtualIdZako, false)]
    [InlineData(@"ROOT\SYSTEM\0000", false)]
    [InlineData(@"ACPI\VEN_AMI&DEV_0001", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPhysicalInstanceId_PrefixCriterion(string? pnp, bool expected) =>
        Assert.Equal(expected, GpuIdentityService.IsPhysicalInstanceId(pnp));

    [Fact]
    public void VirtualDevices_WithPrefixCriterion_AreNotPhysical_ThoughWmiNamesMatchNothing()
    {
        // 本机真实三张虚拟卡：名称 marker 一条都不命中（"Zako Display Adapter" 尤其），
        // 只有前缀判据能识别 ⇒ 旧纯名称过滤必漏（绿灯假阳性先例）。
        Assert.False(GpuIdentityService.ClassifyName(HwinfoTestData.VirtualNameZako, 0) == GpuKind.Discrete);

        var svc = new GpuIdentityService(HwinfoTestData.FakeGpuIdentityProvider.LocalMachineLike());
        var identities = svc.GetIdentities();

        Assert.Equal(2, identities.Count);                       // 虚拟卡不入身份表
        Assert.DoesNotContain(identities, i => i.Name == HwinfoTestData.VirtualNameZako);
        Assert.Contains(identities, i => i.Name == HwinfoTestData.IgpuOsName);
        Assert.Contains(identities, i => i.Name == HwinfoTestData.DgpuOsName);
    }

    [Theory]
    [InlineData(HwinfoTestData.VirtualNameZako)]
    [InlineData("MuMu Virtual Display Adapter")]
    [InlineData("GameViewer Virtual Display Adapter")]
    public void KnownVirtualName_IsSuppressedEvenWithMemoryReading(string name)
    {
        // 验收 2：虚拟卡不翻 Discrete、不抢主卡 —— 即便携带显存读数。
        var svc = new GpuIdentityService(HwinfoTestData.FakeGpuIdentityProvider.LocalMachineLike());
        var gpu = new GpuInfo { Name = name, MemoryTotalMB = 8192 };

        Assert.Equal(GpuKind.Unknown, svc.Classify(gpu));
        Assert.False(svc.LooksDiscrete(gpu));
        Assert.Equal(SysMonIcons.Gpu, svc.ResolveIcon(gpu));
    }

    // ================= 新增：身份表构建 / 判据优先级 =================

    [Fact]
    public void IdentityTable_UsesRealPnpIdsAndVendorTokens()
    {
        var svc = new GpuIdentityService(HwinfoTestData.FakeGpuIdentityProvider.LocalMachineLike());
        var ids = svc.GetIdentities();

        Assert.Collection(ids,
            i =>
            {
                Assert.Equal(HwinfoTestData.PhysicalIdAmd, i.PnpDeviceId);
                Assert.Equal(HwinfoTestData.IgpuOsName, i.Name);
                Assert.Equal("1002", i.VendorToken);
            },
            i =>
            {
                Assert.Equal(HwinfoTestData.PhysicalIdNvidia, i.PnpDeviceId);
                Assert.Equal(HwinfoTestData.DgpuOsName, i.Name);
                Assert.Equal("10DE", i.VendorToken);
            });
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_28E0\1", "10DE")]
    [InlineData(@"PCI\VEN_1002&dev_1681\1", "1002")]
    [InlineData(@"ROOT\DISPLAY\0000", "")]
    [InlineData(@"PCI\VEN_ZZZZ&DEV_1\1", "")]
    [InlineData("garbage", "")]
    public void ParseVendorToken(string pnp, string expected) =>
        Assert.Equal(expected, GpuIdentityService.ParseVendorToken(pnp));

    [Theory]
    [InlineData(HwinfoTestData.DgpuUnitName, "10DE")]
    [InlineData(HwinfoTestData.IgpuUnitName, "1002")]
    [InlineData("Intel(R) UHD Graphics 770", "8086")]
    [InlineData("Qualcomm Adreno X1-85 GPU", "17CB")]
    [InlineData("Some Unnamed Sensor Group", null)]
    public void UnitNameVendorToken(string unit, string? expected) =>
        Assert.Equal(expected, GpuIdentityService.UnitNameVendorToken(unit));

    [Theory]
    [InlineData(HwinfoTestData.IgpuUnitName, "Integrated")]
    [InlineData(HwinfoTestData.DgpuUnitName, "Discrete")]
    [InlineData("dGPU [#0]: NVIDIA GeForce RTX 4060 Laptop", "Discrete")]
    [InlineData("AMD Radeon 680M", "None")]
    [InlineData("", "None")]
    [InlineData(null, "None")]
    // 期望值用字符串：GpuRoleHint 是 internal 类型，public 测试方法的参数须可访问
    public void UnitNameRole(string? unit, string expected) =>
        Assert.Equal(expected, GpuIdentityService.UnitNameRole(unit).ToString());

    [Fact]
    public void HwinfoRole_TakesPriorityOverNameMarker()
    {
        // 角色（COM-free 的 HWiNFO 分组名前缀）优先于名称 marker；
        // DXGI 独立显存只作辅助信号，不得作唯一依据。
        var provider = new HwinfoTestData.FakeGpuIdentityProvider(
            new GpuDisplayDevice(HwinfoTestData.PhysicalIdNvidia, "Mystery Accelerator"));
        var svc = new GpuIdentityService(provider);
        var id = Assert.Single(svc.GetIdentities());

        id.HwinfoRole = GpuRoleHint.Integrated;
        id.DedicatedVideoMemoryMB = 8192;      // 与角色矛盾的辅助信号
        Assert.Equal(GpuKind.Integrated, svc.Classify(new GpuInfo { Name = id.Name, MemoryTotalMB = 8192 }));

        id.HwinfoRole = GpuRoleHint.None;
        Assert.Equal(GpuKind.Discrete, svc.Classify(new GpuInfo { Name = id.Name, MemoryTotalMB = 0 }));
        Assert.Equal("DxgiDedicatedAux", id.LastKindSource);
    }

    [Fact]
    public void ProviderThrowing_YieldsEmptyIdentityTable_AndFallsBackToNameCriterion()
    {
        var provider = new HwinfoTestData.FakeGpuIdentityProvider() { ThrowOnEnumerate = true };
        var svc = new GpuIdentityService(provider);

        Assert.Empty(svc.GetIdentities());
        // 身份表不可用 ⇒ 退回名称辅助判据（仍不猜虚拟/物理）
        Assert.Equal(GpuKind.Discrete, svc.Classify(new GpuInfo { Name = "NVIDIA GeForce RTX 4090" }));
    }

    [Fact]
    public void IdentityTable_IsCachedWithinTtl_AndRefreshedOnDemand()
    {
        var provider = new HwinfoTestData.FakeGpuIdentityProvider(
            new GpuDisplayDevice(HwinfoTestData.PhysicalIdAmd, HwinfoTestData.IgpuOsName));
        var svc = new GpuIdentityService(provider);

        Assert.Single(svc.GetIdentities());
        Assert.Single(svc.GetIdentities());
        Assert.Equal(1, provider.EnumerateCalls);      // TTL 内只枚举一次

        Assert.Single(svc.GetIdentities(forceRefresh: true));
        Assert.Equal(2, provider.EnumerateCalls);
    }

    [Fact]
    public void FindByName_MatchesCaseInsensitiveAndTrims()
    {
        var svc = new GpuIdentityService(HwinfoTestData.FakeGpuIdentityProvider.LocalMachineLike());

        var hit = svc.FindByName("  nvidia geforce rtx 4060 laptop gpu ");
        Assert.NotNull(hit);
        Assert.Equal("10DE", hit!.VendorToken);
        Assert.Null(svc.FindByName("Zako Display Adapter"));
    }

    // ================= 新增：显存辅助信号为「可选注入」，零 COM =================

    [Fact]
    public void DedicatedMemorySource_Absent_LeavesNameCriterionInCharge()
    {
        // 无辅助供给（等价于 trim 宿主下 DXGI 不可得）⇒ 未知名不翻独显，判定退回 COM-free 依据
        var svc = new GpuIdentityService(new HwinfoTestData.FakeGpuIdentityProvider(
            new GpuDisplayDevice(HwinfoTestData.PhysicalIdNvidia, "Mystery Accelerator")));

        Assert.Null(Assert.Single(svc.GetIdentities()).DedicatedVideoMemoryMB);
        Assert.Equal(GpuKind.Unknown, svc.Classify(new GpuInfo { Name = "Mystery Accelerator" }));
    }

    [Fact]
    public void DedicatedMemorySource_PresentAtFirstRefresh_AppliesAsAuxiliaryOnly()
    {
        // 辅助信号在身份表**首次刷新**时采样（TTL 缓存，与生产一致）⇒ 构造期就注入。
        var svc = new GpuIdentityService(new HwinfoTestData.FakeGpuIdentityProvider(
            new GpuDisplayDevice(HwinfoTestData.PhysicalIdNvidia, "Mystery Accelerator")))
        {
            DedicatedMemorySource = () => new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Mystery Accelerator"] = 8192,
            },
        };

        // 未知名 + dedicated>0 ⇒ 独显（辅助信号生效），且记录落点标明它是辅助来源
        Assert.Equal(GpuKind.Discrete, svc.Classify(new GpuInfo { Name = "Mystery Accelerator" }));
        Assert.Equal("DxgiDedicatedAux", Assert.Single(svc.GetIdentities()).LastKindSource);
    }

    [Fact]
    public void DedicatedMemorySource_NeverOverridesNameOrRoleEvidence()
    {
        // 名称/角色证据强于显存辅助信号（契约：DedicatedVideoMemory 不得作唯一/最高依据）
        var svc = new GpuIdentityService(new HwinfoTestData.FakeGpuIdentityProvider(
            new GpuDisplayDevice(HwinfoTestData.PhysicalIdAmd, HwinfoTestData.IgpuOsName)))
        {
            DedicatedMemorySource = () => new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [HwinfoTestData.IgpuOsName] = 16384,     // 与实际相反的显存读数
            },
        };

        Assert.Equal(GpuKind.Integrated, svc.Classify(new GpuInfo { Name = HwinfoTestData.IgpuOsName }));
        Assert.Equal("NameMarker", Assert.Single(svc.GetIdentities()).LastKindSource);
    }

    [Fact]
    public void DedicatedMemorySource_Throwing_DoesNotBreakIdentityChain()
    {
        var provider = HwinfoTestData.FakeGpuIdentityProvider.LocalMachineLike();
        var svc = new GpuIdentityService(provider)
        {
            DedicatedMemorySource = () => throw new InvalidOperationException("COM unavailable"),
        };

        var ids = svc.GetIdentities();
        Assert.Equal(2, ids.Count);                                  // 身份链不受辅助失败影响
        Assert.All(ids, i => Assert.Null(i.DedicatedVideoMemoryMB));
        Assert.Equal(GpuKind.Integrated, svc.Classify(new GpuInfo { Name = HwinfoTestData.IgpuOsName }));
    }

    [Fact]
    public void DedicatedMemory_UnknownDeviceName_IsNotBound()
    {
        var svc = new GpuIdentityService(new HwinfoTestData.FakeGpuIdentityProvider(
            new GpuDisplayDevice(HwinfoTestData.PhysicalIdAmd, HwinfoTestData.IgpuOsName)))
        {
            DedicatedMemorySource = () => new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Some Other Adapter"] = 16384,
            },
        };

        Assert.Null(Assert.Single(svc.GetIdentities()).DedicatedVideoMemoryMB);
    }

    private static GpuIdentityService Service() =>
        new(new HwinfoTestData.FakeGpuIdentityProvider());
}
