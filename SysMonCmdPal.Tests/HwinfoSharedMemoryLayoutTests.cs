// Copyright (c) 2026 SysMonCmdPal
// HwinfoSharedMemoryLayout.TryParse 独立单元测试
//
// 目标：把此前仅经 GpuHwinfoLayoutFailFastTests 集成路径间接覆盖的
// TryParse 分支（签名/entry 尺寸/units 越界/空数据/正常解析）拆成
// 可独立定位的单元测试，为后续拆分 ValidateHeader/ValidateEntrySize 做准备。

using Xunit;

namespace SysMonCmdPal.Tests;

public sealed class HwinfoSharedMemoryLayoutTests
{
    // ---- 正常路径 ----

    [Fact]
    public void TryParse_ValidSingleUnitSingleEntry_ReturnsSnapshotWithBothFaces()
    {
        var units = new[] { HwinfoTestData.Unit("GPU [#0]: NVIDIA GeForce RTX 4060", id: 0) };
        var entries = new[] { HwinfoTestData.Temp(0, 65.0) };

        var snapshot = HwinfoTestData.Snapshot(units, entries);

        Assert.True(snapshot.UnitsAvailable);
        Assert.Single(snapshot.Units);
        Assert.Equal("GPU [#0]: NVIDIA GeForce RTX 4060", snapshot.Units[0].Name);
        Assert.Single(snapshot.Entries);
        Assert.Equal("GPU Temperature", snapshot.Entries[0].Label);
        Assert.Equal(65.0, snapshot.Entries[0].Value);
    }

    [Fact]
    public void TryParse_ValidDualGpu_PreservesUnitOrderAndEntryAttribution()
    {
        var units = new[]
        {
            HwinfoTestData.Unit(HwinfoTestData.IgpuUnitName, id: 10),
            HwinfoTestData.Unit(HwinfoTestData.DgpuUnitName, id: 11),
        };
        var entries = new[]
        {
            HwinfoTestData.Temp(0, 55.0, "iGPU Temperature"),
            HwinfoTestData.Temp(1, 72.0, "dGPU Temperature"),
        };

        var snapshot = HwinfoTestData.Snapshot(units, entries);

        Assert.True(snapshot.UnitsAvailable);
        Assert.Equal(2, snapshot.Units.Count);
        Assert.Equal(HwinfoTestData.IgpuUnitName, snapshot.Units[0].Name);
        Assert.Equal(HwinfoTestData.DgpuUnitName, snapshot.Units[1].Name);
        Assert.Equal(0, snapshot.Entries[0].SensorIndex);
        Assert.Equal(1, snapshot.Entries[1].SensorIndex);
    }

    // ---- header 级 fail-fast ----

    [Fact]
    public void TryParse_EmptyData_FailsWithSectionTooSmall()
    {
        byte[] empty = [];

        bool ok = HwinfoSharedMemoryLayout.TryParse(empty, out var snapshot, out string failure);

        Assert.False(ok);
        Assert.Null(snapshot);
        Assert.Contains("section too small", failure);
    }

    [Fact]
    public void TryParse_DataShorterThanHeader_FailsWithSectionTooSmall()
    {
        // 只给 40 字节（HdrEntryCount 下标 10 需要 44 字节）
        byte[] truncated = new byte[40];

        bool ok = HwinfoSharedMemoryLayout.TryParse(truncated, out _, out string failure);

        Assert.False(ok);
        Assert.Contains("section too small", failure);
    }

    [Fact]
    public void TryParse_WrongSignature_FailsWithSignatureMismatch()
    {
        var data = HwinfoTestData.BuildSection(
            [HwinfoTestData.Unit("GPU")],
            [HwinfoTestData.Temp(0, 50.0)]);
        // 破坏签名
        data[0] = 0xDE; data[1] = 0xAD; data[2] = 0xBE; data[3] = 0xEF;

        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out string failure);

        Assert.False(ok);
        Assert.Null(snapshot);
        Assert.Contains("signature", failure);
        Assert.Contains("unexpected", failure);
    }

    // ---- entries 面 fail-fast ----

    [Fact]
    public void TryParse_EntrySizeTooSmall_FailsWithValueFieldCrossCheck()
    {
        var units = new[] { HwinfoTestData.Unit("GPU") };
        var entries = new[] { HwinfoTestData.Temp(0, 50.0) };
        // entrySize = 160 是旧下限，但容不下 EntryValue(284)+8
        byte[] data = HwinfoTestData.BuildSection(units, entries, entrySize: 160);

        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out _, out string failure);

        Assert.False(ok);
        Assert.Contains("entrySize", failure);
        Assert.Contains("value 字段偏移交叉校验失败", failure);
    }

    [Fact]
    public void TryParse_EntrySizeExactlyAtValueBoundary_Succeeds()
    {
        var units = new[] { HwinfoTestData.Unit("GPU") };
        var entries = new[] { HwinfoTestData.Temp(0, 50.0) };
        // 恰好容下 value 字段（284 + 8 = 292）
        byte[] data = HwinfoTestData.BuildSection(units, entries, entrySize: 292);

        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out _);

        Assert.True(ok);
        Assert.NotNull(snapshot);
        Assert.Single(snapshot.Entries);
    }

    [Fact]
    public void TryParse_EntryCountZero_FailsWithOutOfRange()
    {
        var units = new[] { HwinfoTestData.Unit("GPU") };
        var entries = System.Array.Empty<HwinfoEntrySpec>();
        byte[] data = HwinfoTestData.BuildSection(units, entries);

        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out _, out string failure);

        Assert.False(ok);
        Assert.Contains("entry layout out of range", failure);
    }

    [Fact]
    public void TryParse_EntriesExceedSection_FailsWithArrayOverrun()
    {
        var units = new[] { HwinfoTestData.Unit("GPU") };
        var entries = new[] { HwinfoTestData.Temp(0, 50.0), HwinfoTestData.Temp(0, 60.0) };
        byte[] data = HwinfoTestData.BuildSection(units, entries);
        // 截断 buffer，让 entries 数组越界
        var truncated = data[..(data.Length - 100)];

        bool ok = HwinfoSharedMemoryLayout.TryParse(truncated, out _, out string failure);

        Assert.False(ok);
        Assert.Contains("entries array exceeds section", failure);
    }

    // ---- units 面 fail-fast（entries 面仍可用） ----

    [Fact]
    public void TryParse_UnitsOverrunEntriesStart_DisablesUnitsFaceButKeepsEntries()
    {
        var units = new[] { HwinfoTestData.Unit("GPU") };
        var entries = new[] { HwinfoTestData.Temp(0, 50.0) };
        // 把 unitSize 撑大到越界
        byte[] data = HwinfoTestData.BuildSection(units, entries, unitSize: 1000);

        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out _);

        Assert.True(ok);                    // entries 面正常
        Assert.NotNull(snapshot);
        Assert.False(snapshot.UnitsAvailable);
        Assert.Contains("units array overruns entries start", snapshot.UnitsUnavailableReason);
        Assert.Single(snapshot.Entries);    // entries 不受影响
    }

    [Fact]
    public void TryParse_UnitSizeTooSmallForName_DisablesUnitsFace()
    {
        var units = new[] { HwinfoTestData.Unit("GPU") };
        var entries = new[] { HwinfoTestData.Temp(0, 50.0) };
        // unitSize = 100 < UnitName(8) + HwinfoStrLen(128) = 136
        byte[] data = HwinfoTestData.BuildSection(units, entries, unitSize: 100);

        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out _);

        Assert.True(ok);
        Assert.False(snapshot!.UnitsAvailable);
        Assert.Contains("unitSize", snapshot.UnitsUnavailableReason);
        Assert.Contains("name 字段交叉校验失败", snapshot.UnitsUnavailableReason);
    }

    [Fact]
    public void TryParse_UnitsOffsetOverlapsHeader_DisablesUnitsFace()
    {
        var units = new[] { HwinfoTestData.Unit("GPU") };
        var entries = new[] { HwinfoTestData.Temp(0, 50.0) };
        byte[] data = HwinfoTestData.BuildSection(units, entries);
        // 把 unitsOffset 改到 header 区域内（< 44）
        System.BitConverter.TryWriteBytes(data.AsSpan(20), 20);  // unitsOffset = 20

        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out _);

        Assert.True(ok);
        Assert.False(snapshot!.UnitsAvailable);
        Assert.Contains("overlaps header", snapshot.UnitsUnavailableReason);
    }

    // ---- 版本号透传 ----

    [Fact]
    public void TryParse_NonDefaultVersion_PreservesVersionFields()
    {
        var units = new[] { HwinfoTestData.Unit("GPU") };
        var entries = new[] { HwinfoTestData.Temp(0, 50.0) };
        byte[] data = HwinfoTestData.BuildSection(units, entries, version1: 3, version2: 7);

        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out _);

        Assert.True(ok);
        Assert.Equal(3, snapshot!.Version1);
        Assert.Equal(7, snapshot.Version2);
    }
}
