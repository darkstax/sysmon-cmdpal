// Copyright (c) 2026 SysMonCmdPal
// 系统信息采集服务 — 使用 Win32 API (P/Invoke) 获取基础指标。
// 温度和 GPU 数据委托给 CpuSensorReader / GpuSensorReader。
// 网络采集委托给 NetworkMonitor，磁盘采集委托给 DiskMonitor。
// 精简版: Broker 共享内存推送 + ThermalZone 作为回退

using System;
using System.Diagnostics;

namespace SysMonCmdPal;

/// <summary>
/// 系统信息采集器（单例）。每秒调用 Refresh() 获取最新指标。
/// 所有页面和 Dock Band 共用同一实例。
/// CPU/GPU 温度依赖 CpuSensorReader / GpuSensorReader（独立工作，无需外部配置）。
/// 网络采集委托给 NetworkMonitor，磁盘采集委托给 DiskMonitor。
/// </summary>
public partial class SystemInfoService
{
    public static SystemInfoService Instance { get; } = new();

    // ---- Sub-monitors ----
    private readonly NetworkMonitor _network = new();
    private readonly DiskMonitor _disk = new();
    private readonly CpuFrequencyReader _cpuFreq = new();
    private readonly CpuUsageReader _cpuUsage = new();

    // ---- Sparkline charts (pushed every Refresh, read by detail pages) ----
    public SparklineChart CpuChart { get; } = new(maxPoints: 34, metric: ChartMetric.Cpu);
    public SparklineChart MemChart { get; } = new(maxPoints: 34, metric: ChartMetric.Memory);
    public SparklineChart GpuChart { get; } = new(maxPoints: 34, metric: ChartMetric.Gpu);
    public SparklineChart GpuMemChart { get; } = new(maxPoints: 34, metric: ChartMetric.GpuMemory);
    public SparklineChart NetDownChart { get; } = new(maxPoints: 34, metric: ChartMetric.Network);
    public SparklineChart NetUpChart { get; } = new(maxPoints: 34, metric: ChartMetric.NetworkUp);

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
                CpuUsage = _cpuUsage.Read(),
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

            SystemMemoryReader.Read(ref snapshot);
            SystemBatteryReader.Read(ref snapshot);

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

    internal static bool HasSystemBattery(int batteryFlag)
    {
        return SystemBatteryReader.HasSystemBattery(batteryFlag);
    }
}
