// Copyright (c) 2026 SysMonCmdPal
// 系统信息采集服务 — 使用 Win32 API (P/Invoke) 获取基础指标。
// 温度和 GPU 数据委托给 CpuSensorReader / GpuSensorReader。
// 网络采集委托给 NetworkMonitor，磁盘采集委托给 DiskMonitor。
// 精简版: Broker 共享内存推送 + ThermalZone 作为回退

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

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

/// <summary>
/// 系统信息采集器（单例）。每秒调用 Refresh() 获取最新指标。
/// 所有页面和 Dock Band 共用同一实例。
/// CPU/GPU 温度依赖 CpuSensorReader / GpuSensorReader（独立工作，无需外部配置）。
/// 网络采集委托给 NetworkMonitor，磁盘采集委托给 DiskMonitor。
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
        public byte SystemStatusFlag;     // 0=省电关闭, 1=省电开启
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    // ---- Sub-monitors ----
    private readonly NetworkMonitor _network = new();
    private readonly DiskMonitor _disk = new();
    private readonly CpuFrequencyReader _cpuFreq = new();

    // ---- CPU counter (cached, reused across Refresh calls) ----
    private PerformanceCounter? _cpuCounter;
    private readonly object _cpuCounterLock = new();

    // ---- Sparkline charts (pushed every Refresh, read by detail pages) ----
    public SparklineChart CpuChart { get; } = new(maxPoints: 34, metric: ChartMetric.Cpu);
    public SparklineChart MemChart { get; } = new(maxPoints: 34, metric: ChartMetric.Memory);
    public SparklineChart GpuChart { get; } = new(maxPoints: 34, metric: ChartMetric.Gpu);
    public SparklineChart GpuMemChart { get; } = new(maxPoints: 34, metric: ChartMetric.GpuMemory);
    public SparklineChart NetDownChart { get; } = new(maxPoints: 34, metric: ChartMetric.Network);
    public SparklineChart NetUpChart { get; } = new(maxPoints: 34, metric: ChartMetric.NetworkUp);

    // ---- Cached hardware names (read once) ----
    public static string CpuName { get; } = ReadCpuName();

    private static string ReadCpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string ?? "";
        }
        catch { return ""; }
    }

    // ---- WiFi SSID (cached, refreshed every 15s) ----
    private static string? _cachedSsid;
    private static DateTime _ssidLastQuery = DateTime.MinValue;
    private static readonly object _ssidLock = new();

    public string GetWifiSsid()
    {
        lock (_ssidLock)
        {
            if (DateTime.UtcNow - _ssidLastQuery < TimeSpan.FromSeconds(15) && _cachedSsid != null)
                return _cachedSsid;
            _ssidLastQuery = DateTime.UtcNow;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p == null) { _cachedSsid = ""; return ""; }
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                // Parse "    SSID : MyNetwork"
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(":"))
                    {
                        var val = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
                        if (!string.IsNullOrEmpty(val) && !val.Equals("BSSID", StringComparison.OrdinalIgnoreCase))
                        {
                            _cachedSsid = val;
                            return val;
                        }
                    }
                }
                _cachedSsid = "";
            }
            catch { _cachedSsid = ""; }
            return _cachedSsid ?? "";
        }
    }

    private SystemSnapshot _current;
    private readonly object _currentLock = new();

    public SystemSnapshot Current
    {
        get { lock (_currentLock) return _current; }
        private set { lock (_currentLock) _current = value; }
    }

    private SystemInfoService()
    {
        // 首次播种网络计数器（不计算速度，只记录基线）
        try { _network.Seed(); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] Network baseline init: {ex.Message}"); }

        try { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] CPU counter init failed: {ex.Message}"); _cpuCounter = null; }

        // 初始化快照（LHM 在首次访问时惰性初始化）
        try { Refresh(); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] Initial Refresh(): {ex.Message}"); }
    }

    /// <summary>
    /// 刷新所有指标。建议每秒调用一次。
    /// 线程安全：如果另一个线程正在刷新，本次调用直接跳过（返回旧快照）。
    /// </summary>
    public void Refresh()
    {
        // P6: 防止多线程并发 Refresh（DockBand coordinator + 任何直接调用者）
        if (System.Threading.Interlocked.Exchange(ref _isRefreshing, 1) != 0)
            return;

        try
        {
        var now = DateTime.UtcNow;
        var (netDown, netUp) = _network.ReadSpeed(now);

        var snapshot = new SystemSnapshot
        {
            CpuUsage = ReadCpuUsage(),
            NetDown = netDown,
            NetUp = netUp,
            Disks = _disk.Read(),
            CpuTemperature = -1,
            Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 },
            Gpus = [],
            Backend = SensorBackend.None,
        };

        // 物理磁盘查询（WMI，稍重）— 复用已读的逻辑分区数据
        try { snapshot.PhysicalDisks = _disk.ReadPhysicalDisks(snapshot.Disks); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] ReadPhysicalDisks: {ex.Message}"); snapshot.PhysicalDisks = []; }

        ReadMemory(ref snapshot);
        ReadBattery(ref snapshot);

        // 传感器采集: Broker 共享内存推送 → ThermalZone → None
        TryReadSensors(ref snapshot);

        // CPU 频率 (任务管理器算法: 基础频率 × 性能百分比 / 100)
        try { snapshot.CpuFrequency = _cpuFreq.ReadFrequency(); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] CpuFreq: {ex.Message}"); }

        // Push to sparkline charts for real-time trend visualization
        CpuChart.Push((float)snapshot.CpuUsage);
        MemChart.Push((float)snapshot.MemoryUsed);
        if (snapshot.Gpu.UsagePercent >= 0)
            GpuChart.Push((float)snapshot.Gpu.UsagePercent);
        if (snapshot.Gpu.MemoryTotalMB > 0)
            GpuMemChart.Push((float)(snapshot.Gpu.MemoryUsedMB * 100.0 / snapshot.Gpu.MemoryTotalMB));
        NetDownChart.PushRaw((float)(snapshot.NetDown / 1_000_000.0));
        NetUpChart.PushRaw((float)(snapshot.NetUp / 1_000_000.0));

        Current = snapshot;
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }

    private int _isRefreshing;

    // ===== CPU =====
    private double ReadCpuUsage()
    {
        try
        {
            // H3: 懒重建 — 如果计数器为 null（之前异常销毁），尝试重新创建
            PerformanceCounter? counter;
            lock (_cpuCounterLock)
            {
                if (_cpuCounter is null)
                {
                    try { _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); }
                    catch (Exception ex) { Debug.WriteLine($"[SysMon] CPU counter re-init failed: {ex.Message}"); return 0; }
                }
                counter = _cpuCounter;
            }

            return Math.Round(counter.NextValue(), 1);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] ReadCpuUsage: {ex.Message}");
            // H3: 销毁损坏的计数器，下次调用时会重建
            lock (_cpuCounterLock)
            {
                _cpuCounter?.Dispose();
                _cpuCounter = null;
            }
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
            // GlobalMemoryStatusEx failure is extremely rare; if it happens, leave fields at 0
            // rather than reporting misleading GC heap limit as "total memory".
            Debug.WriteLine($"[SysMon] ReadMemory (GlobalMemoryStatusEx): {ex.Message} — memory unavailable");
        }
    }

    // ===== Battery =====
    private static void ReadBattery(ref SystemSnapshot snapshot)
    {
        try
        {
            if (GetSystemPowerStatus(out var pwr))
            {
                snapshot.BatterySaverOn = pwr.SystemStatusFlag == 1;
                snapshot.BatteryLifeSeconds = pwr.BatteryLifeTime;

                int flag = pwr.BatteryFlag;
                if (flag <= 9 && flag != 128) // has battery
                {
                    snapshot.BatteryPercent = pwr.BatteryLifePercent > 100 ? -1 : pwr.BatteryLifePercent;

                    bool chargingBit = (flag & 8) != 0;
                    bool acOnline = pwr.ACLineStatus == 1;

                    // WMI BatteryStatus.RemainingCapacity 趋势检测（mWh 精度，3 秒窗口）
                    // 这是唯一能可靠识别"双重供电"的信号——充电上限场景下
                    // GetSystemPowerStatus 和 WMI 都报 charging，但容量在掉
                    var wmi = BatteryQueryService.Instance.GetStatus();
                    bool draining = wmi is { IsValid: true, IsDraining: true };

                    // 充电位置位 + 电池容量在掉 → 双重供电
                    // 充电位置位 + 不掉 → 充电中
                    // 无充电位 + AC + 不掉 → 满电
                    // 无充电位 + 掉 或 无AC → 放电中
                    if (chargingBit && draining)
                        snapshot.BatteryStatus = "dual";
                    else if (chargingBit)
                        snapshot.BatteryStatus = "charging";
                    else if (acOnline && !draining)
                        snapshot.BatteryStatus = "full";
                    else if (draining || !acOnline)
                        snapshot.BatteryStatus = "discharging";
                    else
                        snapshot.BatteryStatus = "unknown";
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
    // CpuSensorReader.Read() 和 GpuSensorReader.ReadAll() 独立工作
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
                string s when s.StartsWith("Broker") => SensorBackend.Broker,
                string s when s.Contains("HWiNFO") => SensorBackend.HWiNFO,
                "ThermalZone" => SensorBackend.ThermalZone,
                _ => SensorBackend.None,
            };

            // HWiNFO 12h 重置检测
            var hwinfo = HwinfoSharedMemoryReader.Instance;
            snapshot.HwinfoNearReset = hwinfo.IsNearResetWindow;
            snapshot.HwinfoTimeRemaining = hwinfo.TimeUntilReset;

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
                // 主 GPU：优先独显（有独立显存），其次有负载的，最后温度最高的
                var primary = gpuResults
                    .OrderByDescending(g => g.MemoryTotalMB > 0 ? 1 : 0)  // 有独立显存 = 独显优先
                    .ThenByDescending(g => g.UsagePercent > 0 ? 1 : 0)
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
                ? (cpuResult.Source == "无" ? Loc.Get("Backend.BothUnavailable") : Loc.Format("Backend.DataSource", cpuResult.Source))
                : $"CPU: {cpuResult.Source}, GPU: {gpuSource}" +
                  (snapshot.Gpus.Length > 1 ? Loc.Format("Backend.GpuCount", snapshot.Gpus.Length) : "");

            // HWiNFO 12h 警告追加到后端描述
            if (snapshot.HwinfoNearReset && snapshot.Backend == SensorBackend.HWiNFO)
            {
                var remaining = snapshot.HwinfoTimeRemaining;
                snapshot.BackendNote += remaining.TotalMinutes > 0
                    ? Loc.Format("Backend.HwinfoWarningSoon", (int)remaining.TotalMinutes)
                    : Loc.Get("Backend.HwinfoExpired");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] TryReadSensors 异常: {ex.GetType().Name}: {ex.Message}");
            snapshot.CpuTemperature = -1;
            snapshot.Backend = SensorBackend.None;
            snapshot.BackendNote = Loc.Format("Backend.Exception", ex.Message);
            snapshot.Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 };
            snapshot.Gpus = [];
        }
    }
}
