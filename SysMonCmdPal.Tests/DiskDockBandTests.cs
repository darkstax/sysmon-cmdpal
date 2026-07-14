// Copyright (c) 2026 SysMonCmdPal

using Xunit;

namespace SysMonCmdPal.Tests;

public class DiskDockBandTests
{
    [Fact]
    public void FormatDiskSubtitle_PhysicalDisk_ShowsPartitionAndAggregateUsageWithoutProtocol()
    {
        var snapshot = new SystemSnapshot
        {
            PhysicalDisks =
            [
                new PhysicalDiskInfo
                {
                    InterfaceType = "NVMe",
                    Partitions =
                    [
                        Partition("C:", totalBytes: 100, freeBytes: 40),
                    ],
                },
            ],
        };

        string subtitle = DiskDockBand.FormatDiskSubtitle(snapshot);

        Assert.Equal("C: 60%", subtitle);
        Assert.DoesNotContain("NVMe", subtitle);
    }

    [Fact]
    public void FormatDiskSubtitle_MultiplePartitions_GroupsNamesAndUsesWeightedUsage()
    {
        var snapshot = new SystemSnapshot
        {
            PhysicalDisks =
            [
                new PhysicalDiskInfo
                {
                    InterfaceType = "SATA",
                    Partitions =
                    [
                        Partition("C:", totalBytes: 100, freeBytes: 0),
                        Partition("D:", totalBytes: 300, freeBytes: 300),
                    ],
                },
                new PhysicalDiskInfo
                {
                    InterfaceType = "USB",
                    Partitions =
                    [
                        Partition("E:", totalBytes: 200, freeBytes: 150),
                    ],
                },
            ],
        };

        Assert.Equal("C: + D: 25%  E: 25%", DiskDockBand.FormatDiskSubtitle(snapshot));
    }

    [Fact]
    public void FormatDiskSubtitle_WithoutPhysicalDisks_UsesLogicalDiskFallback()
    {
        var snapshot = new SystemSnapshot
        {
            PhysicalDisks = [],
            Disks =
            [
                Partition("C:", usedPercent: 61.6),
                Partition("D:", usedPercent: 24.4),
            ],
        };

        Assert.Equal("C: 62%  D: 24%", DiskDockBand.FormatDiskSubtitle(snapshot));
    }

    [Fact]
    public void FormatDiskSubtitle_PhysicalDiskWithoutPartitions_UsesLogicalDiskFallback()
    {
        var snapshot = new SystemSnapshot
        {
            PhysicalDisks = [new PhysicalDiskInfo { InterfaceType = "NVMe", Partitions = [] }],
            Disks = [Partition("C:", usedPercent: 42.4)],
        };

        Assert.Equal("C: 42%", DiskDockBand.FormatDiskSubtitle(snapshot));
    }

    private static DiskInfo Partition(
        string name,
        long totalBytes = 0,
        long freeBytes = 0,
        double usedPercent = 0) =>
        new()
        {
            Name = name,
            TotalBytes = totalBytes,
            FreeBytes = freeBytes,
            UsedPercent = usedPercent,
        };
}
