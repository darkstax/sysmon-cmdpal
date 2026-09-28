// Copyright (c) 2026 SysMonCmdPal
// T2-2（P1-2）：COM-free gdi32 GPU adapter 枚举 + 显存查询的**可判定内核**。
// 全部为纯函数 / 结构体布局断言（零硬件、零 COM、零 WMI）：
//   这些用例证明"选型与归属规则"正确，**不**据此声称"COM-free 通路在生产 trim 宿主已验证"——
//   活体可达性由任务报告的隔离探针另行标注（见 t5 output）。
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Xunit;

namespace SysMonCmdPal.Tests;

public sealed class GpuDxgkrnlAdaptersTests
{
    private static readonly ulong MB = 1024UL * 1024UL;

    // ============ 本地显存段选择 ============

    [Fact]
    public void SelectLocalVramMemory_PicksNonApertureMaxCommitLimit_UsedFromResident()
    {
        // 本机活体探针真实段（RTX4060 LUID 0x15568）：
        //   s0 [aperture=1] CommitLimit=31995MB（系统内存孔径）
        //   s1 [aperture=0] CommitLimit=7956  Committed=2929 Resident=3243（真实本地显存）
        //   s2 [aperture=1] CommitLimit=huge
        var segments = new List<GpuMemorySegment>
        {
            new(Aperture: 1, CommitLimit: 31995UL * MB, BytesCommitted: 21UL * MB, BytesResident: 21UL * MB),
            new(Aperture: 0, CommitLimit: 7956UL * MB, BytesCommitted: 2929UL * MB, BytesResident: 3243UL * MB),
            new(Aperture: 1, CommitLimit: ulong.MaxValue / 4, BytesCommitted: 774UL * MB, BytesResident: 774UL * MB),
        };

        var mem = GpuDxgkrnlAdapters.SelectLocalVramMemory(segments);

        Assert.NotNull(mem);
        Assert.Equal(7956UL * MB, mem.Value.TotalBytes);
        Assert.Equal(3243UL * MB, mem.Value.UsedBytes);   // Resident 与 t4 活体 HWiNFO dGPU MemUsed=3243.027MB 一致
    }

    [Fact]
    public void SelectLocalVramMemory_ApertureOnly_ReturnsNull_NotSystemMemory()
    {
        // 只有 aperture 段 ⇒ 绝不拿系统内存冒充显存（保守）
        var segments = new List<GpuMemorySegment>
        {
            new(Aperture: 1, CommitLimit: 31995UL * MB, BytesCommitted: 10UL * MB, BytesResident: 10UL * MB),
            new(Aperture: 1, CommitLimit: 8000UL * MB, BytesCommitted: 1UL * MB, BytesResident: 1UL * MB),
        };

        Assert.Null(GpuDxgkrnlAdapters.SelectLocalVramMemory(segments));
    }

    [Fact]
    public void SelectLocalVramMemory_EmptyOrZeroCommit_ReturnsNull()
    {
        Assert.Null(GpuDxgkrnlAdapters.SelectLocalVramMemory(new List<GpuMemorySegment>()));
        Assert.Null(GpuDxgkrnlAdapters.SelectLocalVramMemory(
            [new GpuMemorySegment(Aperture: 0, CommitLimit: 0, BytesCommitted: 0, BytesResident: 0)]));
    }

    // ============ 归属 + 显存落定（集显陷阱 / 未归属排除） ============

    [Fact]
    public void Attribute_Integrated_ReportsZeroVram_EvenWithLocalSegment()
    {
        // 本机活体证据：APU 集显也有非 aperture 本地段，但仅 239/244MB（WDDM 预留分段），
        // 不是真实 VRAM ⇒ 如实上报会显示误导读数。集显显存必须为 0（沿用 t4 语义）。
        var segments = new List<GpuMemorySegment>
        {
            new(Aperture: 0, CommitLimit: 244UL * MB, BytesCommitted: 233UL * MB, BytesResident: 236UL * MB),
        };

        var a = GpuDxgkrnlAdapters.Attribute(HwinfoTestData.IgpuOsName, GpuKind.Integrated, 0x1341F, 0, segments);

        Assert.NotNull(a);
        Assert.Equal(0, a!.MemoryTotalMB, 3);
        Assert.Equal(0, a.MemoryUsedMB, 3);
        Assert.Equal(GpuKind.Integrated, a.Kind);
    }

    [Fact]
    public void Attribute_Discrete_TakesSelectedLocalSegment()
    {
        var segments = new List<GpuMemorySegment>
        {
            new(Aperture: 1, CommitLimit: 31995UL * MB, BytesCommitted: 0, BytesResident: 0),
            new(Aperture: 0, CommitLimit: 7956UL * MB, BytesCommitted: 2929UL * MB, BytesResident: 3243UL * MB),
        };

        var a = GpuDxgkrnlAdapters.Attribute(HwinfoTestData.DgpuOsName, GpuKind.Discrete, 0x15568, 0, segments);

        Assert.NotNull(a);
        // MemoryTotalMB/MemoryUsedMB 已是 MB（record 里 Bytes/1024/1024）
        Assert.Equal(7956.0, a!.MemoryTotalMB, 0);
        Assert.Equal(3243.0, a.MemoryUsedMB, 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Attribute_Unattributable_ProducesNoAdapter(string? name)
    {
        // 不可归属（虚拟/副本）⇒ 绝不命名、绝不填显存（契约「风险警示」+ 验收 1）
        Assert.Null(GpuDxgkrnlAdapters.Attribute(name, GpuKind.Unknown, 1, 0,
            [new GpuMemorySegment(Aperture: 0, CommitLimit: 8192UL * MB, BytesCommitted: 1, BytesResident: 1)]));
    }

    // ============ LUID→身份：复用 t4 SetupDi 表（真实机器串） ============

    [Fact]
    public void ResolveIdentity_RealDxgkAdapterStrings_JoinSetupDiDeviceDesc()
    {
        // 本机活体实测：D3DKMT ADAPTERREGISTRYINFO.AdapterString 与 SetupDi SPDRP_DEVICEDESC 逐字相等
        var svc = new GpuIdentityService(HwinfoTestData.FakeGpuIdentityProvider.LocalMachineLike());

        var amd = GpuDxgkrnlAdapters.ResolveIdentity("AMD Radeon(TM) Graphics", svc);
        var nv = GpuDxgkrnlAdapters.ResolveIdentity("NVIDIA GeForce RTX 4060 Laptop GPU", svc);

        Assert.NotNull(amd);
        Assert.NotNull(nv);
        Assert.Equal(HwinfoTestData.PhysicalIdAmd, amd!.PnpDeviceId);
        Assert.Equal(HwinfoTestData.PhysicalIdNvidia, nv!.PnpDeviceId);
    }

    [Theory]
    [InlineData("Zako Display Adapter")]           // 本机真实虚拟卡，名称不命中任何 marker
    [InlineData("MuMu Virtual Display Adapter")]
    [InlineData("GameViewer Virtual Display Adapter")]
    [InlineData("Microsoft Basic Display Adapter")]
    [InlineData("Some Render Node Copy")]
    public void ResolveIdentity_VirtualOrUnknownName_IsNotAttributable(string adapterString)
    {
        // 虚拟卡被前缀判据挡在身份表外 ⇒ D3DKMT 侧同样不可归属，不会凭显存翻独显/抢主卡
        var svc = new GpuIdentityService(HwinfoTestData.FakeGpuIdentityProvider.LocalMachineLike());

        Assert.Null(GpuDxgkrnlAdapters.ResolveIdentity(adapterString, svc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ResolveIdentity_BlankAdapterString_IsNotAttributable(string? adapterString)
    {
        var svc = new GpuIdentityService(HwinfoTestData.FakeGpuIdentityProvider.LocalMachineLike());
        Assert.Null(GpuDxgkrnlAdapters.ResolveIdentity(adapterString, svc));
    }

    // ============ 结构体布局：确定性断言（修掉原 D3kmt NodeId@824 缺陷） ============

    [Fact]
    public void QueryStatisticsLayout_MatchesSdkCAssert_Size808_QueryElementAt800()
    {
        // SDK d3dkmthk.h: C_ASSERT(sizeof(D3DKMT_QUERYSTATISTICS) == 0x328 == 808)
        // 原 D3dkmtGpuReader 用 byte[800] 的 QueryResult + 紧随的 NodeId ⇒ NodeId 落在 24+800=824（越界），
        // 驱动读到的 NodeOrdinal 恒为 0 ⇒ 每条 node 查询实际都打到 node0（利用率被单引擎钉死）。
        // 本布局（union 776 + 内联 QueryElement）把 SegmentId/NodeOrdinal 放回偏移 800。
        int total = Marshal.SizeOf<GpuDxgkrnlAdapters.D3DKMT_QUERYSTATISTICS>();
        int qeOffset = Marshal.OffsetOf<GpuDxgkrnlAdapters.D3DKMT_QUERYSTATISTICS>("QueryElement").ToInt32();

        Assert.Equal(808, total);
        Assert.Equal(800, qeOffset);
    }

    [Fact]
    public void AdapterInfoLayout_MatchesSdk_ThirtyBitHandleIsUint()
    {
        // D3DKMT_HANDLE = UINT（4 字节），不是 IntPtr。原实现按 IntPtr 建 ⇒ 全字段错位（本机探针实测过）。
        int total = Marshal.SizeOf<GpuDxgkrnlAdapters.D3DKMT_ADAPTERINFO>();
        int hAdapter = Marshal.OffsetOf<GpuDxgkrnlAdapters.D3DKMT_ADAPTERINFO>("hAdapter").ToInt32();
        int luid = Marshal.OffsetOf<GpuDxgkrnlAdapters.D3DKMT_ADAPTERINFO>("AdapterLuid").ToInt32();
        int sources = Marshal.OffsetOf<GpuDxgkrnlAdapters.D3DKMT_ADAPTERINFO>("NumOfSources").ToInt32();

        Assert.Equal(20, total);          // 4 + 8 + 4 + 4
        Assert.Equal(0, hAdapter);
        Assert.Equal(4, luid);
        Assert.Equal(12, sources);
    }

    [Fact]
    public void SegmentFieldOffsets_MatchSdkSegmentInformation()
    {
        // Win8+ 64 位分支：CommitLimit@0 BytesCommitted@8 BytesResident@16，Aperture 在 Memory 结构之后 @40
        Assert.Equal(0, GpuDxgkrnlAdapters.SegOffCommitLimit);
        Assert.Equal(8, GpuDxgkrnlAdapters.SegOffBytesCommitted);
        Assert.Equal(16, GpuDxgkrnlAdapters.SegOffBytesResident);
        Assert.Equal(40, GpuDxgkrnlAdapters.SegOffAperture);
    }
}
