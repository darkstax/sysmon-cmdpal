// Copyright (c) 2026 SysMonCmdPal
// 契约「必做：布局 fail-fast + version 打点」：§7 声称的「格式变更自动禁用」
// 过去只实现了范围检查那一半 —— entrySize 从不与硬编码字段偏移交叉验证，
// 任何把记录撑大到 ≥292B 或挪字段的布局变更会通过校验并按老偏移静默读出垃圾。
// 本文件把这些校验变成可门禁的纯 buffer 用例。
using System.Collections.Generic;
using Xunit;

namespace SysMonCmdPal.Tests;

public sealed class GpuHwinfoLayoutFailFastTests
{
    private static List<HwinfoUnitSpec> Units() =>
    [
        HwinfoTestData.Unit(HwinfoTestData.IgpuUnitName),
        HwinfoTestData.Unit(HwinfoTestData.DgpuUnitName),
    ];

    private static List<HwinfoEntrySpec> Entries() =>
    [
        HwinfoTestData.Temp(0, 64.8),
        HwinfoTestData.Load(0, 88, "GPU Utilization"),
        HwinfoTestData.Temp(1, 57.23),
        HwinfoTestData.Load(1, 0, "GPU Core Load"),
    ];

    [Fact]
    public void RealMachineLikeLayout_ParsesWithUnitsFace()
    {
        var (ok, failure, snapshot) = HwinfoTestData.TrySnapshot(Units(), Entries());

        Assert.True(ok, failure);
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.UnitsAvailable, snapshot.UnitsUnavailableReason);
        Assert.Equal(2, snapshot.Units.Count);
        Assert.Equal(HwinfoTestData.IgpuUnitName, snapshot.Units[0].Name);
        Assert.Equal(HwinfoTestData.DgpuUnitName, snapshot.Units[1].Name);
        Assert.Equal(4, snapshot.Entries.Count);
        // value 字段（偏移 284）按 record 定位读回
        Assert.Equal(64.8, snapshot.Entries[0].Value, 3);
        Assert.Equal(57.23, snapshot.Entries[2].Value, 3);
        // sensor_index（偏移 4）= units 归属的唯一通路
        Assert.Equal(0, snapshot.Entries[0].SensorIndex);
        Assert.Equal(1, snapshot.Entries[2].SensorIndex);
    }

    // ============ ① entrySize 必须容得下 value 字段（不等式，不硬编码本机 460） ============

    [Theory]
    [InlineData(292)]   // = EntryValue(284) + sizeof(double)：恰好容得下 ⇒ 接受
    [InlineData(460)]   // 本机实测值 ⇒ 接受
    [InlineData(1024)]  // 旧范围上界 ⇒ 接受
    public void EntrySize_AdequateForValueField_IsAccepted(int entrySize)
    {
        var (ok, failure, snapshot) = HwinfoTestData.TrySnapshot([], Entries(), entrySize: entrySize);
        Assert.True(ok, failure);
        Assert.NotNull(snapshot);
    }

    [Theory]
    [InlineData(160)]   // 旧范围检查会放行（>=160），但 value 偏移根本不在记录内
    [InlineData(200)]
    [InlineData(291)]   // 差 1 字节 ⇒ 必须拒绝（旧实现会静默读垃圾）
    public void EntrySize_TooSmallForValueField_FailsFastInsteadOfSilentGarbage(int entrySize)
    {
        var (ok, failure, snapshot) = HwinfoTestData.TrySnapshot(Units(), Entries(), entrySize: entrySize);

        Assert.False(ok);
        Assert.Null(snapshot);
        Assert.Contains("value", failure);
    }

    [Theory]
    [InlineData(159)]
    [InlineData(1025)]
    public void EntrySize_OutOfLegacyRange_StillRejected(int entrySize)
    {
        var (ok, _, _) = HwinfoTestData.TrySnapshot(Units(), Entries(), entrySize: entrySize);
        Assert.False(ok);
    }

    // ============ ② unitSize 必须容得下名称字段 ============

    [Theory]
    [InlineData(136)]   // = UnitName(8) + 128：恰好容得下 ⇒ units 面可用
    [InlineData(392)]   // 本机实测值
    public void UnitSize_AdequateForNameField_KeepsUnitsFace(int unitSize)
    {
        var (ok, failure, snapshot) = HwinfoTestData.TrySnapshot(Units(), Entries(), unitSize: unitSize);

        Assert.True(ok, failure);
        Assert.True(snapshot!.UnitsAvailable, snapshot.UnitsUnavailableReason);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(135)]
    public void UnitSize_TooSmallForNameField_DisablesOnlyUnitsFace(int unitSize)
    {
        var (ok, failure, snapshot) = HwinfoTestData.TrySnapshot(Units(), Entries(), unitSize: unitSize);

        // 关键设计：units 面禁用，但 entries 面**仍然可用**（消费端退回保守归属）
        Assert.True(ok, failure);
        Assert.NotNull(snapshot);
        Assert.False(snapshot!.UnitsAvailable);
        Assert.Contains("unitSize", snapshot.UnitsUnavailableReason);
        Assert.Empty(snapshot.Units);
        Assert.Equal(4, snapshot.Entries.Count);
    }

    // ============ ③ units 数组不得越过 entries 起点（本机恰等，写成不等式校验） ============

    [Fact]
    public void UnitsArray_TouchingEntriesStart_IsLegalBoundary()
    {
        // 本机实测：48 + 392*17 = 6712 恰等于 entries 起点 ⇒ 相等是合法边界（不是巧合，写成校验）
        var (ok, _, snapshot) = HwinfoTestData.TrySnapshot(Units(), Entries());

        Assert.True(ok);
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.UnitsAvailable, snapshot.UnitsUnavailableReason);
    }

    [Fact]
    public void UnitsArray_OverrunningEntriesStart_DisablesUnitsFace()
    {
        // 谎报 unitCount ⇒ units 数组末尾越过 entries 起点（真正的越界情形）
        byte[] data = HwinfoTestData.BuildSection(Units(), Entries());
        System.BitConverter.TryWriteBytes(data.AsSpan(28, 4), 10);   // unitCount 10 > 实际 2

        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out string failure);

        Assert.True(ok);                       // entries 面仍可信
        Assert.NotNull(snapshot);
        Assert.False(snapshot!.UnitsAvailable); // units 面被禁用
        Assert.Contains("overruns entries start", snapshot.UnitsUnavailableReason);
    }

    // ============ header 拒绝路径 ============

    [Fact]
    public void SignatureMismatch_RejectsWholeSection()
    {
        var data = HwinfoTestData.BuildSection(Units(), Entries());
        data[0] ^= 0xFF;    // 破坏 signature

        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out string failure);

        Assert.False(ok);
        Assert.Null(snapshot);
        Assert.Contains("signature", failure);
    }

    [Fact]
    public void SectionShorterThanHeader_Rejects()
    {
        var data = new byte[32];
        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out string failure);

        Assert.False(ok);
        Assert.Null(snapshot);
        Assert.Contains("too small", failure);
    }

    [Fact]
    public void EntriesArray_ExceedingSectionLength_Rejects()
    {
        // entryCount 谎报过大 ⇒ 数组末尾越过 section 实际长度
        var data = HwinfoTestData.BuildSection(Units(), Entries());
        int entryOffset = HwinfoTestData.UnitsOffset + HwinfoTestData.DefaultUnitSize * 2;
        // 把 count 改成远超 section 的值
        System.BitConverter.TryWriteBytes(data.AsSpan(40, 4), 4000);

        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out string failure);

        Assert.False(ok);
        Assert.Null(snapshot);
        Assert.Contains("exceeds section", failure);
        _ = entryOffset;
    }

    // ============ version 打点（本机 2/1；namazso 帖记录 1/1，7.33 声称布局改过） ============

    [Fact]
    public void HeaderVersions_AreExposedForLoggingNotHardRequirement()
    {
        var snapshot = HwinfoTestData.Snapshot(Units(), Entries(), version1: 2, version2: 1);

        Assert.Equal(2, snapshot.Version1);
        Assert.Equal(1, snapshot.Version2);

        // 版本号不同**不得**造成拒绝（不等式原则：版本只做指纹，不做门禁）
        var other = HwinfoTestData.Snapshot(Units(), Entries(), version1: 1, version2: 1);
        Assert.Equal(1, other.Version1);
        Assert.True(other.UnitsAvailable);
    }

    [Fact]
    public void NonAsciiLabel_IsDecodedWithoutThrowing()
    {
        // 本机 entry 记录含本地化标签副本（中文）；解析层按 UTF-8 取主标签，不得抛
        var entries = new List<HwinfoEntrySpec>
        {
            new() { Type = HwinfoTestData.TypeTemp, SensorIndex = 0, Label = "GPU 温度", Value = 42.0 },
        };
        var snapshot = HwinfoTestData.Snapshot([HwinfoTestData.Unit("温度组")], entries);

        Assert.Equal("GPU 温度", Assert.Single(snapshot.Entries).Label);
    }
}
