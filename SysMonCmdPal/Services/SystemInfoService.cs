// Copyright (c) 2026 SysMonCmdPal
// 系统信息采集服务 — 使用 Win32 API (P/Invoke) 获取基础指标。
// 温度和 GPU 数据委托给 CpuSensorReader / GpuSensorReader（全部用户态，无 ring-0）。
// 商店安全版回退链: HWiNFO → ADL → ThermalZone → LHM

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
    /// <summary>HWiNFO 共享内存 (Global\HWiNFO_SENS_SM2) — 最精准</summary>
    HwInfo,
    /// <summary>NVIDIA NVAPI (nvapi64.dll) — 用户态 GPU 数据</summary>
    Nvapi,
    /// <summary>AMD ADL (atiadlxx.dll) — 用户态 CPU/GPU 数据</summary>
    AmdAdl,
    /// <summary>Intel IGCL (igcl.dll) — 用户态 GPU 数据</summary>
    Igcl,
    /// <summary>LibreHardwareMonitor NuGet — 传感器库</summary>
    Lhm,
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
    public DiskInfo[] Disks;
    public double CpuTemperature;       // °C, -1 if unavailable
    public GpuInfo Gpu;                 // 主 GPU（向后兼容）
    public GpuInfo[] Gpus;              // 所有检测到的 GPU
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
    private readonly object _diskIOLock = new();

    // ---- Network tracking ----
    private long _prevNetDown;
    private long _prevNetUp;
    private DateTime _prevNetDownTime;
    private DateTime _prevNetUpTime;

    public SystemSnapshot Current { get; private set; }

    private SystemInfoService()
    {
        // 首次播种网络计数器（不计算速度，只记录基线）
        try
        {
            _prevNetDown = ReadNetBytes(false);
            _prevNetUp = ReadNetBytes(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] Network baseline init: {ex.Message}");
        }
        _prevNetDownTime = DateTime.UtcNow;
        _prevNetUpTime = DateTime.UtcNow;

        try { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] CPU counter init failed: {ex.Message}"); _cpuCounter = null; }

        // 初始化快照（LHM 在首次访问时惰性初始化）
        try { Refresh(); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] Initial Refresh(): {ex.Message}"); }
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
            Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 },
            Gpus = [],
            Backend = SensorBackend.None,
        };

        ReadMemory(ref snapshot);
        ReadBattery(ref snapshot);

        // 传感器回退链: HWiNFO → ADL → ThermalZone → LHM → None
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] ReadCpuUsage: {ex.Message}");
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] ReadMemory (GlobalMemoryStatusEx): {ex.Message}, falling back to GC");
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
                    try { label = d.VolumeLabel; }
                    catch (Exception ex) { Debug.WriteLine($"[SysMon] VolumeLabel ({d.Name}): {ex.Message}"); }

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
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] ReadDisks: {ex.Message}");
            return [];
        }
    }

    private void ReadDiskIO(string driveLetter, ref DiskInfo di)
    {
        (PerformanceCounter? Read, PerformanceCounter? Write) counters;
        lock (_diskIOLock)
        {
            if (!_diskIOCounters.TryGetValue(driveLetter, out counters))
            {
                counters = (CreateIOCounter(driveLetter, "Read"), CreateIOCounter(driveLetter, "Write"));
                _diskIOCounters[driveLetter] = counters;
            }
        }

        try { di.ReadBytesPerSec = counters.Read?.NextValue() ?? -1; }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] Disk read IO ({driveLetter}): {ex.Message}"); }
        try { di.WriteBytesPerSec = counters.Write?.NextValue() ?? -1; }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] Disk write IO ({driveLetter}): {ex.Message}"); }
    }

    private static PerformanceCounter? CreateIOCounter(string drive, string rw)
    {
        try
        {
            return new PerformanceCounter("LogicalDisk", $"Disk {rw} Bytes/sec", drive);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] CreateIOCounter ({drive}/{rw}): {ex.Message}");
            return null;
        }
    }

    // ===== Network =====
    /// <summary>读取所有活跃网络接口的字节计数。upload=true 返回已发送，false 返回已接收。</summary>
    private static long ReadNetBytes(bool upload)
    {
        long total = 0;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus == OperationalStatus.Up)
            {
                var stats = ni.GetIPStatistics();
                total += upload ? stats.BytesSent : stats.BytesReceived;
            }
        }
        return total;
    }

    private double ReadNetDown(DateTime now)
    {
        try
        {
            long current = ReadNetBytes(false);
            double speed = CalcSpeed(ref _prevNetDown, current, ref _prevNetDownTime, now);
            return speed;
        }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] ReadNetDown: {ex.Message}"); return 0; }
    }

    private double ReadNetUp(DateTime now)
    {
        try
        {
            long current = ReadNetBytes(true);
            double speed = CalcSpeed(ref _prevNetUp, current, ref _prevNetUpTime, now);
            return speed;
        }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] ReadNetUp: {ex.Message}"); return 0; }
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] ReadBattery: {ex.Message}");
            snapshot.BatteryPercent = -1;
            snapshot.BatteryStatus = "unknown";
        }
    }

    // ============================================================
    // 传感器采集 — 委托给 CpuSensorReader / GpuSensorReader
    // 全部用户态: HWiNFO → ADL → ThermalZone → LHM
    // ============================================================

    private static void TryReadSensors(ref SystemSnapshot snapshot)
    {
        try
        {
            var cpuResult = CpuSensorReader.Read();
            var gpuResults = GpuSensorReader.ReadAll();

            snapshot.CpuTemperature = cpuResult.Temperature;
            snapshot.Backend = cpuResult.Source switch
            {
                string s when s.StartsWith("HWiNFO") => SensorBackend.HwInfo,
                "ThermalZone" => SensorBackend.ThermalZone,
                string s when s.StartsWith("ADL") => SensorBackend.AmdAdl,
                "LHM" => SensorBackend.Lhm,
                _ => SensorBackend.None,
            };

            // 构建多 GPU 数组
            if (gpuResults.Count > 0)
            {
                snapshot.Gpus = gpuResults.Select(r => new GpuInfo
                {
                    Name = r.Name,
                    UsagePercent = r.UsagePercent,
                    Temperature = r.Temperature,
                    MemoryUsedMB = r.MemoryUsedMB,
                    MemoryTotalMB = r.MemoryTotalMB,
                }).ToArray();
                // 主 GPU：优先有 3D 负载的，否则温度最高的
                var primary = gpuResults
                    .OrderByDescending(g => g.UsagePercent > 0 ? 1 : 0)
                    .ThenByDescending(g => g.Temperature)
                    .First();
                snapshot.Gpu = new GpuInfo
                {
                    Name = primary.Name,
                    UsagePercent = primary.UsagePercent,
                    Temperature = primary.Temperature,
                    MemoryUsedMB = primary.MemoryUsedMB,
                    MemoryTotalMB = primary.MemoryTotalMB,
                };
            }
            else
            {
                snapshot.Gpus = [];
                snapshot.Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 };
            }

            // 后端描述
            string gpuSource = gpuResults.Count > 0 ? gpuResults[0].Source : "无";
            snapshot.BackendNote = cpuResult.Source == gpuSource
                ? (cpuResult.Source == "无" ? "CPU 和 GPU 均无可用数据源" : $"数据源: {cpuResult.Source}")
                : $"CPU: {cpuResult.Source}, GPU: {gpuSource}" +
                  (snapshot.Gpus.Length > 1 ? $" ({snapshot.Gpus.Length} 张卡)" : "");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] TryReadSensors 异常: {ex.GetType().Name}: {ex.Message}");
            snapshot.CpuTemperature = -1;
            snapshot.Backend = SensorBackend.None;
            snapshot.BackendNote = $"传感器采集异常: {ex.Message}";
            snapshot.Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 };
            snapshot.Gpus = [];
        }
    }
}
