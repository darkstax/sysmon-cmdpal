using System;

namespace SysMonCmdPal;

/// <summary>传感器后端状态 — 表示当前使用哪个数据源</summary>
public enum SensorBackend
{
    /// <summary>Broker 共享内存推送（最精准）</summary>
    Broker,
    /// <summary>HWiNFO 共享内存（用户态，每 ~12h 需重启 HWiNFO）</summary>
    HWiNFO,
    /// <summary>Windows ACPI 热区 (PerformanceCounter)</summary>
    ThermalZone,
    /// <summary>无可用传感器后端</summary>
    None,
}

public struct SystemSnapshot
{
    public double CpuUsage;
    public long MemoryTotalBytes;
    public long MemoryUsedBytes;
    public double MemoryUsed;
    public double NetDown;
    public double NetUp;
    public double BatteryPercent;
    public string BatteryStatus;
    public int BatteryLifeSeconds;       // 秒，剩余可用时间，-1=未知（接电源时）
    public bool BatterySaverOn;          // 省电模式开关
    public DiskInfo[] Disks;
    public PhysicalDiskInfo[] PhysicalDisks;
    public double CpuTemperature;       // °C, -1 if unavailable
    public GpuInfo Gpu;                 // 主 GPU（向后兼容）
    public GpuInfo[] Gpus;              // 所有检测到的 GPU
    public SensorBackend Backend;       // 当前传感器数据源
    public string BackendNote;          // 后端状态描述（null=正常）
    public bool HwinfoNearReset;        // HWiNFO 接近 12h 重置窗口
    public TimeSpan HwinfoTimeRemaining; // HWiNFO 距重置剩余时间
    public double CpuFrequency;         // 全核心平均频率 MHz, -1=不可用
}

public struct DiskInfo
{
    public string Name;
    public string VolumeLabel;
    public long TotalBytes;
    public long FreeBytes;
    public double UsedPercent;
    public double ReadBytesPerSec;      // -1 if unavailable
    public double WriteBytesPerSec;     // -1 if unavailable
}

public struct PhysicalDiskInfo
{
    public string Model;                // e.g. "Samsung SSD 980 PRO 1TB"
    public string SerialNumber;
    public long TotalBytes;             // 物理磁盘总大小
    public string InterfaceType;        // SATA/NVMe/USB
    public DiskInfo[] Partitions;       // 该物理磁盘上的所有分区
    public double ReadBytesPerSec;      // 汇总 IO 读
    public double WriteBytesPerSec;     // 汇总 IO 写
}

public struct GpuInfo
{
    public string Name;                 // e.g. "AMD Radeon RX 680M"
    public double UsagePercent;         // -1 if unavailable
    public double Temperature;          // °C, -1 if unavailable
    public double MemoryUsedMB;
    public double MemoryTotalMB;
}
