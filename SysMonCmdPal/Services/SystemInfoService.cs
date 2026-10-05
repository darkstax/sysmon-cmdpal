// Copyright (c) 2026 SysMonCmdPal
// 系统信息采集服务 — 使用 Win32 API (P/Invoke) 获取基础指标。
// 温度和 GPU 数据委托给 CpuSensorReader / GpuSensorReader。
// 网络采集委托给 NetworkMonitor，磁盘采集委托给 DiskMonitor。
// 精简版回退链（分型，见 §3 约束 1）: GPU=Broker SHM → HWiNFO → D3DKMT → PDH（ThermalZone 不得进入）；CPU温度=Broker SHM → HWiNFO → ThermalZone。
//
// T1-1 架构加固: 采集项以 ISystemInfoSource 注册表组织（见 docs/TECHNICAL_ROADMAP.md §5 Phase1 T1-1）。
// 本类不再逐个硬编码调用 monitor，Refresh() 只按注册顺序遍历源；默认注册表在构造时建立，
// 顺序与重构前 Refresh() 的语句顺序完全一致（网络 → CPU 使用率 → 磁盘 → 内存 → 电池 → 传感器 → CPU 频率）。
// public static Instance 兼容入口保留；internal 构造仅供测试注入 mock 源。

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SysMonCmdPal;

/// <summary>
/// 系统信息采集器（单例）。每秒调用 Refresh() 获取最新指标。
/// 所有页面和 Dock Band 共用同一实例。
/// CPU/GPU 温度依赖 CpuSensorReader / GpuSensorReader（独立工作，无需外部配置）。
/// 网络采集委托给 NetworkMonitor，磁盘采集委托给 DiskMonitor。
/// 采集项通过 <see cref="ISystemInfoSource"/> 注册表驱动，可整体替换以便单元测试注入。
/// </summary>
public partial class SystemInfoService
{
    public static SystemInfoService Instance { get; } = new();

    // ---- Sparkline charts (pushed every Refresh, read by detail pages) ----
    public SparklineChart CpuChart { get; } = new(maxPoints: 34, metric: ChartMetric.Cpu);
    public SparklineChart MemChart { get; } = new(maxPoints: 34, metric: ChartMetric.Memory);
    public SparklineChart GpuChart { get; } = new(maxPoints: 34, metric: ChartMetric.Gpu);
    public SparklineChart GpuMemChart { get; } = new(maxPoints: 34, metric: ChartMetric.GpuMemory);
    public SparklineChart NetDownChart { get; } = new(maxPoints: 34, metric: ChartMetric.Network);
    public SparklineChart NetUpChart { get; } = new(maxPoints: 34, metric: ChartMetric.NetworkUp);

    // ---- 采集源注册表（生产唯一数据源；注册顺序 = 重构前 Refresh() 的语句顺序）----
    private readonly ISystemInfoSource[] _sources;

    /// <summary>
    /// 生产注册表里的网络采集源（null = 测试注入构造，无真实 monitor）。
    /// 供设置变更时失效网卡选择缓存用。
    /// </summary>
    internal NetworkMonitor? NetworkMonitorSource { get; }

    /// <summary>
    /// 默认注册表 — 生产构造函数与顺序守护测试共用这一份数据（R(t1) F2：消灭影子注册表）。
    /// 只创建源对象，不触发任何硬件读取。时序与注册序严格解耦（F2 硬约束）：
    ///   ① 实例化顺序 = 重构前字段初始化器声明顺序（network→disk→cpuFreq→cpuUsage），
    ///      PerformanceCounter 创建时序敏感，不得为去重而改动 new 的顺序；
    ///   ② 注册（采集执行）顺序 = 重构前 Refresh() 语句顺序
    ///      （网络 → CPU 使用率 → 磁盘 → 内存 → 电池 → 传感器 → CPU 频率）。
    /// </summary>
    internal static ISystemInfoSource[] BuildDefaultSources(out NetworkMonitor network)
    {
        // ① 实例化时序（与原 ctor 一致）
        network = new NetworkMonitor();
        var disk = new DiskMonitor();
        var cpuFreq = new CpuFrequencyReader();
        var cpuUsage = new CpuUsageReader();

        // ② 注册顺序（Refresh 遍历执行顺序）
        return
        [
            network,
            cpuUsage,
            disk,
            new MemoryInfoSource(),
            new BatteryInfoSource(),
            new SensorInfoSource(),
            cpuFreq,
        ];
    }

    private SystemInfoService()
    {
        // 生产注册表 = BuildDefaultSources（守护测试验证的正是这份数据）。
        _sources = BuildDefaultSources(out var network);
        NetworkMonitorSource = network;

        // 首次播种网络计数器（不计算速度，只记录基线）
        try { network.Seed(); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] Network baseline init: {ex.Message}"); }

        // 初始化快照（LHM 在首次访问时惰性初始化）
        try { Refresh(); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] Initial Refresh(): {ex.Message}"); }
    }

    /// <summary>测试注入入口：使用给定的采集源构造服务，不创建任何真实 monitor，也不做首次 Refresh。</summary>
    internal SystemInfoService(IEnumerable<ISystemInfoSource> sources)
    {
        _sources = sources is ISystemInfoSource[] arr ? arr : new List<ISystemInfoSource>(sources).ToArray();
        NetworkMonitorSource = _sources.OfType<NetworkMonitor>().FirstOrDefault();
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
            // 哨兵默认值：与重构前 Refresh() 的发布路径语义一致（R(t1) F1 构造期不变式）。
            // Disks/PhysicalDisks/Gpus 恒非 null —— 重构前发布路径 never 产生 null 集合，
            // 下面的逐源异常隔离不得放开这一点；消费端（DiskDockBand/DiskDetailPage/
            // SysMonMainPage 回退分支）对这三个集合裸解引用、无 null 防护。
            // 其他未被写入的字段保持这里的取值。
            var snapshot = new SystemSnapshot
            {
                CpuTemperature = -1,
                Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 },
                Gpus = [],
                Disks = [],
                PhysicalDisks = [],
                Backend = SensorBackend.None,
            };

            // 按注册顺序遍历采集源。真实源各自带与重构前一致的异常吞噬语义
            // （内存/电池/磁盘/网络/传感器/频率都不会向外抛出）；这里的兜底 try/catch
            // 只保证「某个源抛异常不影响后续源」，正常路径行为不变。
            foreach (var source in _sources)
            {
                try
                {
                    source.ReadInto(ref snapshot);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SysMon] Source {source?.GetType().Name}: {ex.Message}");
                }
            }

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

    private SystemSnapshot _current;
    private readonly object _currentLock = new();

    public SystemSnapshot Current
    {
        get { lock (_currentLock) return _current; }
        private set { lock (_currentLock) _current = value; }
    }

    internal static bool HasSystemBattery(int batteryFlag)
    {
        return SystemBatteryReader.HasSystemBattery(batteryFlag);
    }
}
