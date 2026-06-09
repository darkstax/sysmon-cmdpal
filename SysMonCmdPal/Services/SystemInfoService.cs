// Copyright (c) 2026 SysMonCmdPal
// 系统信息采集服务 — 使用 Win32 API (P/Invoke) 获取基础指标。
// 温度和 GPU 数据委托给 LhmSensorService（PawnIO 驱动，免管理员）。
// 回退链: LHM (PawnIO) → AMD ADL (PMLOG) → HWiNFO 共享内存 → 不可用

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace SysMonCmdPal;

/// <summary>传感器后端状态 — 表示当前使用哪个数据源</summary>
public enum SensorBackend
{
    /// <summary>LibreHardwareMonitor (PawnIO 驱动)，功能完整</summary>
    Lhm,
    /// <summary>AMD ADL PMLOG (atiadlxx.dll)，仅 CPU 温度</summary>
    AmdAdl,
    /// <summary>HWiNFO 共享内存 (Global\HWiNFO_SENS_SM2)，需 HWiNFO 后台运行</summary>
    HwInfo,
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
    public DiskInfo[] Disks;
    public double CpuTemperature;       // °C, -1 if unavailable
    public GpuInfo Gpu;                 // default(None) if no GPU detected
    public SensorBackend Backend;       // 当前传感器数据源
    public string BackendNote;          // 后端状态描述（null=正常）
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

public struct GpuInfo
{
    public string Name;                 // e.g. "AMD Radeon RX 680M"
    public double UsagePercent;         // -1 if unavailable
    public double Temperature;          // °C, -1 if unavailable
    public double MemoryUsedMB;
    public double MemoryTotalMB;
}

/// <summary>
/// 系统信息采集器（单例）。每秒调用 Refresh() 获取最新指标。
/// 所有页面和 Dock Band 共用同一实例。
/// CPU/GPU 温度依赖 LibreHardwareMonitor（需管理员权限）。
/// </summary>
public class SystemInfoService
{
    public static SystemInfoService Instance { get; } = new();

    // ---- P/Invoke for memory ----
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---- P/Invoke for battery ----
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    // ---- CPU counter (cached, reused across Refresh calls) ----
    private PerformanceCounter? _cpuCounter;

    // ---- Disk IO counters (lazy-created per drive) ----
    private readonly Dictionary<string, (PerformanceCounter? Read, PerformanceCounter? Write)> _diskIOCounters = new();

    // ---- Network tracking ----
    private long _prevNetDown;
    private long _prevNetUp;
    private DateTime _prevNetTime;

    public SystemSnapshot Current { get; private set; }

    private SystemInfoService()
    {
        _prevNetTime = DateTime.UtcNow;

        try { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] CPU counter init failed: {ex.Message}"); _cpuCounter = null; }

        try { Refresh(); } catch { }
    }

    /// <summary>
    /// 刷新所有指标。建议每秒调用一次。
    /// </summary>
    public void Refresh()
    {
        var now = DateTime.UtcNow;

        var snapshot = new SystemSnapshot
        {
            CpuUsage = ReadCpuUsage(),
            NetDown = ReadNetDown(now),
            NetUp = ReadNetUp(now),
            Disks = ReadDisks(),
            CpuTemperature = -1,
            Gpu = default,
            Backend = SensorBackend.None,
        };

        ReadMemory(ref snapshot);
        ReadBattery(ref snapshot);

        // 传感器回退链: LHM (PawnIO) → AMD ADL → HWiNFO → None
        TryReadSensors(ref snapshot);

        Current = snapshot;
    }

    // ===== CPU =====
    private double ReadCpuUsage()
    {
        try
        {
            if (_cpuCounter is null) return 0;
            return Math.Round(_cpuCounter.NextValue(), 1);
        }
        catch
        {
            return 0;
        }
    }

    // ===== Memory =====
    private static void ReadMemory(ref SystemSnapshot snapshot)
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                snapshot.MemoryTotalBytes = (long)memStatus.ullTotalPhys;
                snapshot.MemoryUsedBytes = (long)(memStatus.ullTotalPhys - memStatus.ullAvailPhys);
                snapshot.MemoryUsed = memStatus.dwMemoryLoad; // 0-100
            }
        }
        catch
        {
            // fallback via GC
            snapshot.MemoryTotalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
    }

    // ===== Disks =====
    private DiskInfo[] ReadDisks()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d =>
                {
                    string label = "";
                    try { label = d.VolumeLabel; } catch { }

                    var di = new DiskInfo
                    {
                        Name = d.Name,
                        VolumeLabel = label,
                        TotalBytes = d.TotalSize,
                        FreeBytes = d.AvailableFreeSpace,
                        UsedPercent = Math.Round((double)(d.TotalSize - d.AvailableFreeSpace) / d.TotalSize * 100, 1),
                        ReadBytesPerSec = -1,
                        WriteBytesPerSec = -1,
                    };

                    // Attach IO speed via PerformanceCounter (lazy-create, reused)
                    ReadDiskIO(d.Name.TrimEnd('\\'), ref di);

                    return di;
                })
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private void ReadDiskIO(string driveLetter, ref DiskInfo di)
    {
        if (!_diskIOCounters.TryGetValue(driveLetter, out var counters))
        {
            counters = (CreateIOCounter(driveLetter, "Read"), CreateIOCounter(driveLetter, "Write"));
            _diskIOCounters[driveLetter] = counters;
        }

        try { di.ReadBytesPerSec = counters.Read?.NextValue() ?? -1; } catch { }
        try { di.WriteBytesPerSec = counters.Write?.NextValue() ?? -1; } catch { }
    }

    private static PerformanceCounter? CreateIOCounter(string drive, string rw)
    {
        try
        {
            return new PerformanceCounter("LogicalDisk", $"Disk {rw} Bytes/sec", drive);
        }
        catch
        {
            return null;
        }
    }

    // ===== Network =====
    private double ReadNetDown(DateTime now)
    {
        try
        {
            long total = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up)
                    total += ni.GetIPStatistics().BytesReceived;
            }
            double speed = CalcSpeed(ref _prevNetDown, total, ref _prevNetTime, now);
            return speed;
        }
        catch { return 0; }
    }

    private double ReadNetUp(DateTime now)
    {
        try
        {
            long total = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Up)
                    total += ni.GetIPStatistics().BytesSent;
            }
            double speed = CalcSpeed(ref _prevNetUp, total, ref _prevNetTime, now);
            return speed;
        }
        catch { return 0; }
    }

    private static double CalcSpeed(ref long prevBytes, long currentBytes, ref DateTime prevTime, DateTime now)
    {
        double elapsed = (now - prevTime).TotalSeconds;
        if (elapsed <= 0) return 0;
        double speed = (currentBytes - prevBytes) / elapsed;
        prevBytes = currentBytes;
        prevTime = now;
        return speed < 0 ? 0 : speed; // handle counter reset
    }

    // ===== Battery =====
    private static void ReadBattery(ref SystemSnapshot snapshot)
    {
        try
        {
            if (GetSystemPowerStatus(out var pwr))
            {
                int flag = pwr.BatteryFlag;
                if (flag <= 9 && flag != 128) // has battery
                {
                    snapshot.BatteryPercent = pwr.BatteryLifePercent > 100 ? -1 : pwr.BatteryLifePercent;
                    snapshot.BatteryStatus = flag switch
                    {
                        9 => "charging",
                        _ when pwr.ACLineStatus == 1 => "full",
                        < 9 => "discharging",
                        _ => "unknown",
                    };
                }
                else
                {
                    snapshot.BatteryPercent = -1;
                    snapshot.BatteryStatus = "no battery";
                }
            }
        }
        catch
        {
            snapshot.BatteryPercent = -1;
            snapshot.BatteryStatus = "unknown";
        }
    }

    // ============================================================
    // 传感器回退链: LHM (PawnIO) → AMD ADL → HWiNFO → None
    // ============================================================

    /// <summary>
    /// 按优先级尝试各传感器后端，设置 CPU/GPU 温度和后端状态。
    /// Tier 1: LHM (PawnIO) — 全功能
    /// Tier 2: AMD ADL (atiadlxx.dll) — 仅 CPU 温度，用户态
    /// Tier 3: HWiNFO 共享内存 — CPU + GPU 温度，需 HWiNFO 运行
    /// Tier 4: 不可用
    /// </summary>
    private static void TryReadSensors(ref SystemSnapshot snapshot)
    {
        // Tier 1: LHM (PawnIO) — 最优先
        if (LhmSensorService.Instance.IsAvailable)
        {
            ReadFromLhm(ref snapshot);
            snapshot.Backend = SensorBackend.Lhm;
            snapshot.BackendNote = LhmSensorService.Instance.LastError ?? ""; // null when healthy
            return;
        }

        // LHM 不可用，记录原因
        string lhmError = LhmSensorService.Instance.LastError ?? "PawnIO 驱动未安装";

        // Tier 2: AMD ADL (atiadlxx.dll, 用户态, 免 admin)
        int adlTemp = AmdTempReader.Instance.ReadCpuTemp();
        if (adlTemp > 0)
        {
            snapshot.CpuTemperature = adlTemp;
            snapshot.Backend = SensorBackend.AmdAdl;
            snapshot.BackendNote = $"ADL 回退 (LHM 不可用: {lhmError})";
            // GPU 无法从 ADL 获取
            snapshot.Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 };
            return;
        }

        // Tier 3: HWiNFO 共享内存
        int hwCpuTemp = AmdTempReader.Instance.ReadCpuTempViaHwInfoOnly();
        if (hwCpuTemp > 0)
        {
            snapshot.CpuTemperature = hwCpuTemp;
            snapshot.Backend = SensorBackend.HwInfo;
            snapshot.BackendNote = $"HWiNFO 回退 (LHM: {lhmError}, ADL: 不可用)";
            // GPU 也可能从 HWiNFO 获取
            int hwGpuTemp = AmdTempReader.Instance.ReadGpuTempViaHwInfo();
            snapshot.Gpu = new GpuInfo
            {
                Name = hwGpuTemp > 0 ? "HWiNFO GPU" : "",
                UsagePercent = -1,
                Temperature = hwGpuTemp > 0 ? hwGpuTemp : -1,
            };
            return;
        }

        // Tier 4: 全部不可用
        snapshot.CpuTemperature = -1;
        snapshot.Backend = SensorBackend.None;
        snapshot.BackendNote = $"无可用传感器后端 — LHM: {lhmError}, ADL: 不可用, HWiNFO: 未运行";
        snapshot.Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 };
    }

    // ---- Tier 1: LHM 完整读取 ----

    private static void ReadFromLhm(ref SystemSnapshot snapshot)
    {
        var svc = LhmSensorService.Instance;

        // CPU 温度：取 CPU Package 或第一个 CPU 温度
        if (svc.Catalog.TryGetValue(SensorCategory.CpuTemp, out var cpuTemps) && cpuTemps.Count > 0)
        {
            var pkg = cpuTemps.FirstOrDefault(r =>
                r.SensorName?.Contains("Package", StringComparison.OrdinalIgnoreCase) == true ||
                r.SensorName?.Contains("Tctl", StringComparison.OrdinalIgnoreCase) == true ||
                r.SensorName?.Contains("Tdie", StringComparison.OrdinalIgnoreCase) == true);
            if (pkg.SensorName != null && pkg.Value > 0)
                snapshot.CpuTemperature = pkg.Value;
            else
                snapshot.CpuTemperature = cpuTemps.FirstOrDefault(r => r.Value > 0).Value > 0
                    ? cpuTemps.First(r => r.Value > 0).Value : -1;
        }

        // GPU 完整信息
        var gpuName = svc.AllReadings
            .Where(r => r.Category is SensorCategory.GpuTemp or SensorCategory.GpuLoad)
            .Select(r => r.HardwareName)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(gpuName))
        {
            snapshot.Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 };
            return;
        }

        var gpu = new GpuInfo { Name = gpuName, UsagePercent = -1, Temperature = -1 };

        if (svc.Catalog.TryGetValue(SensorCategory.GpuTemp, out var gpuTemps) && gpuTemps.Count > 0)
        {
            var core = gpuTemps.FirstOrDefault(r => r.SensorName?.Contains("Core", StringComparison.OrdinalIgnoreCase) == true);
            gpu.Temperature = core.SensorName != null && core.Value > 0 ? core.Value
                : gpuTemps.FirstOrDefault(r => r.Value > 0).Value > 0 ? gpuTemps.First(r => r.Value > 0).Value : -1;
        }

        if (svc.Catalog.TryGetValue(SensorCategory.GpuLoad, out var gpuLoads) && gpuLoads.Count > 0)
        {
            var core = gpuLoads.FirstOrDefault(r => r.SensorName?.Contains("Core", StringComparison.OrdinalIgnoreCase) == true);
            gpu.UsagePercent = core.SensorName != null && core.Value >= 0 ? core.Value
                : gpuLoads.FirstOrDefault(r => r.Value >= 0).SensorName != null ? gpuLoads.First(r => r.Value >= 0).Value : -1;
        }

        if (svc.Catalog.TryGetValue(SensorCategory.GpuMemory, out var gpuMem) && gpuMem.Count > 0)
        {
            var used = gpuMem.FirstOrDefault(r => r.SensorName?.Contains("Used", StringComparison.OrdinalIgnoreCase) == true);
            var total = gpuMem.FirstOrDefault(r => r.SensorName?.Contains("Total", StringComparison.OrdinalIgnoreCase) == true);
            if (used.SensorName != null) gpu.MemoryUsedMB = used.Value;
            if (total.SensorName != null) gpu.MemoryTotalMB = total.Value;
        }

        snapshot.Gpu = gpu;
    }
}
