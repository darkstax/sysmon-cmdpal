// Copyright (c) 2026 SysMonCmdPal
// Individual Dock Bands — each monitoring category gets its own pinnable dock band.
// Architecture: shared refresh coordinator + one WrappedDockItem per category.
// CmdPal treats each ICommandItem from GetDockBands() as an independent atomic band.
//
// Icons: same Segoe Fluent Icons glyphs from SensorShelf (verified working).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

// ============================================================================
// Shared Refresh Coordinator
// ============================================================================

/// <summary>
/// Singleton timer shared by all dock bands. Calls SystemInfoService.Refresh()
/// once per tick, then notifies all subscribers to read the cached snapshot.
/// </summary>
internal static class DockBandRefreshCoordinator
{
    private static readonly List<Action> _subscribers = [];
    private static readonly object _lock = new();
    private static System.Timers.Timer? _timer;
    private static int _refCount;

    public static void Subscribe(Action refresh)
    {
        lock (_lock)
        {
            _subscribers.Add(refresh);
            _refCount++;
            if (_timer == null)
            {
                SystemInfoService.Instance.Refresh();
                _timer = new System.Timers.Timer(1000) { AutoReset = true };
                _timer.Elapsed += OnTick;
                _timer.Start();
            }
        }
    }

    public static void Unsubscribe(Action refresh)
    {
        lock (_lock)
        {
            _subscribers.Remove(refresh);
            _refCount--;
            if (_refCount <= 0 && _timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
        }
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
            _subscribers.Clear();
            _refCount = 0;
        }
    }

    private static void OnTick(object? sender, ElapsedEventArgs e)
    {
        SystemInfoService.Instance.Refresh();
        Action[] snapshot;
        lock (_lock) { snapshot = _subscribers.ToArray(); }
        foreach (var action in snapshot)
        {
            try { action(); } catch { }
        }
    }
}

// ============================================================================
// Format helpers
// ============================================================================

internal static class DockFormat
{
    public static string Speed(double bps) => bps switch
    {
        >= 1_000_000 => $"{bps / 1_000_000:F1} MB/s",
        >= 1_000 => $"{bps / 1_000:F0} KB/s",
        >= 1 => $"{bps:F0} B/s",
        _ => "0 B/s",
    };

    /// <summary>紧凑速度（Dock 栏用），单位缩写成单个字母以节省宽度。</summary>
    public static string CompactSpeed(double bps) => bps switch
    {
        >= 1_000_000 => $"{bps / 1_000_000:F1}M/s",
        >= 1_000 => $"{bps / 1_000:F0}K/s",
        >= 1 => $"{bps:F0}B/s",
        _ => "0",
    };

    /// <summary>CPU/GPU 温度。>=0 显示数值，-1 表示不可用。</summary>
    public static string Temp(double c) => c >= 0 ? $"{c:F0}°C" : "N/A";

    /// <summary>百分比。>=0 显示数值，负值表示不可用。</summary>
    public static string Percent(double p) => p >= 0 ? $"{p:F0}%" : "N/A";

    /// <summary>电池状态文本（中文）。</summary>
    public static string BatteryStatusText(string status) => status switch
    {
        "charging" => "充电中",
        "discharging" => "放电中",
        "full" => "已充满",
        "no battery" => "无电池",
        _ => status,
    };

    /// <summary>Markdown 格式化：温度或斜体 N/A。</summary>
    public static string TempMd(double c) => c >= 0 ? $"**{c:F0}°C**" : "*N/A*";

    /// <summary>Markdown 格式化：百分比或斜体 N/A。</summary>
    public static string PercentMd(double p) => p >= 0 ? $"**{p:F1}%**" : "*N/A*";
}

// ============================================================================
// Individual Dock Band Classes
// ============================================================================

/// <summary>Dock band: CPU usage + temperature. Links to CpuDetailPage.</summary>
internal sealed partial class CpuDockBand : WrappedDockItem
{
    private readonly ListItem _cpuItem;

    public CpuDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.cpu", "CPU")
    {
        _cpuItem = new ListItem(new CpuDetailPage())
        {
            Title = "CPU",
            Subtitle = "加载中…",
            Icon = new IconInfo(""), // CPU — SensorShelf
        };
        Items = [_cpuItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        var temp = DockFormat.Temp(info.CpuTemperature);
        _cpuItem.Title = temp.Length > 0
            ? $"CPU {info.CpuUsage:F0}%  {temp}"
            : $"CPU {info.CpuUsage:F0}%";
        _cpuItem.Subtitle = info.CpuUsage switch
        {
            >= 90 => "⚠ 高负载",
            >= 70 => "负载偏高",
            >= 40 => "中等",
            _ => "空闲",
        };
    }
}

/// <summary>Dock band: Memory usage in % and GB. Links to MemoryDetailPage.</summary>
internal sealed partial class MemoryDockBand : WrappedDockItem
{
    private readonly ListItem _memItem;

    public MemoryDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.memory", "内存")
    {
        _memItem = new ListItem(new MemoryDetailPage())
        {
            Title = "内存",
            Subtitle = "加载中…",
            Icon = new IconInfo(""), // RAM — SensorShelf
        };
        Items = [_memItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        var used = info.MemoryUsedBytes / (1024.0 * 1024 * 1024);
        var total = info.MemoryTotalBytes / (1024.0 * 1024 * 1024);
        _memItem.Title = $"内存 {info.MemoryUsed:F0}%";
        _memItem.Subtitle = $"已用 {used:F1} / {total:F1} GB";
    }
}

/// <summary>Dock band: Disk IO speed (primary) + usage. Links to DiskDetailPage.</summary>
internal sealed partial class DiskDockBand : WrappedDockItem
{
    private readonly ListItem _diskItem;

    public DiskDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.disk", "磁盘")
    {
        _diskItem = new ListItem(new DiskDetailPage())
        {
            Title = "磁盘",
            Subtitle = "加载中…",
            Icon = new IconInfo(""), // Storage — SensorShelf
        };
        Items = [_diskItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        if (info.Disks.Length == 0)
        {
            _diskItem.Title = "磁盘 — 无驱动器";
            _diskItem.Subtitle = "";
            return;
        }

        double totalRead = 0, totalWrite = 0;
        foreach (var d in info.Disks)
        {
            if (d.ReadBytesPerSec > 0) totalRead += d.ReadBytesPerSec;
            if (d.WriteBytesPerSec > 0) totalWrite += d.WriteBytesPerSec;
        }

        // 标题只显示 IO 速度（去掉"磁盘"省空间），副标题显示分区
        _diskItem.Title = $"↓{DockFormat.CompactSpeed(totalRead)} ↑{DockFormat.CompactSpeed(totalWrite)}";
        _diskItem.Subtitle = string.Join("  ", info.Disks.Select(d => $"{d.Name} {d.UsedPercent:F0}%"));
    }
}

/// <summary>Dock band: Network download speed. Links to NetworkDetailPage.</summary>
internal sealed partial class NetworkDownDockBand : WrappedDockItem
{
    private readonly ListItem _netItem;

    public NetworkDownDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.network.down", "下载")
    {
        _netItem = new ListItem(new NetworkDetailPage())
        {
            Title = "下载",
            Subtitle = "加载中…",
            Icon = new IconInfo(""), // Segoe Fluent: Download
        };
        Items = [_netItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        _netItem.Title = $"↓ {DockFormat.Speed(info.NetDown)}";
        _netItem.Subtitle = "下载";
    }
}

/// <summary>Dock band: Network upload speed. Links to NetworkDetailPage.</summary>
internal sealed partial class NetworkUpDockBand : WrappedDockItem
{
    private readonly ListItem _netItem;

    public NetworkUpDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.network.up", "上传")
    {
        _netItem = new ListItem(new NetworkDetailPage())
        {
            Title = "上传",
            Subtitle = "加载中…",
            Icon = new IconInfo(""), // Segoe Fluent: Upload
        };
        Items = [_netItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        _netItem.Title = $"↑ {DockFormat.Speed(info.NetUp)}";
        _netItem.Subtitle = "上传";
    }
}

/// <summary>Dock band: Battery percentage and status. Links to BatteryDetailPage.</summary>
internal sealed partial class BatteryDockBand : WrappedDockItem
{
    private readonly ListItem _batItem;

    public BatteryDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.battery", "电池")
    {
        _batItem = new ListItem(new BatteryDetailPage())
        {
            Title = "电池",
            Subtitle = "加载中…",
            Icon = new IconInfo(""), // Battery — SensorShelf
        };
        Items = [_batItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        if (info.BatteryPercent < 0)
        {
            _batItem.Title = "电池 — 无电池";
            _batItem.Subtitle = "台式机 / 交流电";
            return;
        }
        _batItem.Title = $"电池 {info.BatteryPercent:F0}%";
        _batItem.Subtitle = DockFormat.BatteryStatusText(info.BatteryStatus);
    }
}

/// <summary>Dock band: 单个传感器（动态、用户可选）。显示传感器名称 + 数值。</summary>
internal sealed partial class SensorDockBand : WrappedDockItem
{
    private readonly ListItem _item;
    private readonly string _sensorKey;

    public SensorDockBand(string sensorKey, string displayName)
        : base(Array.Empty<IListItem>(), $"sysmon.dock.sensor.{sensorKey.GetHashCode():X8}", displayName)
    {
        _sensorKey = sensorKey;
        _item = new ListItem(new NoOpCommand())
        {
            Title = displayName,
            Subtitle = "加载中…",
            Icon = new IconInfo(""),
        };
        Items = [_item];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    internal void OnRefresh()
    {
        var reading = LhmSensorService.Instance.AllReadings
            .FirstOrDefault(r => r.UniqueKey == _sensorKey);

        if (reading.SensorName == null)
        {
            _item.Title = "传感器不可用";
            _item.Subtitle = _sensorKey;
            return;
        }

        // 根据类别选图标（复用缓存实例，避免每秒分配）
        _item.Icon = SensorCategoryMeta.GetIcon(reading.Category);

        // 值 + 单位
        _item.Title = $"{reading.DisplayName}  {reading.FormatValue()}";

        // 阈值着色标记
        string status = "";
        if (reading.CriticalThreshold > 0 && reading.Value >= reading.CriticalThreshold)
            status = "🔴 ";
        else if (reading.WarningThreshold > 0 && reading.Value >= reading.WarningThreshold)
            status = "🟡 ";

        _item.Subtitle = $"{status}{reading.HardwareName}";
    }

    public string SensorKey => _sensorKey;
}

/// <summary>Dock band: GPU usage, temperature. Links to GpuDetailPage.</summary>
internal sealed partial class GpuDockBand : WrappedDockItem
{
    private readonly ListItem _gpuItem;

    public GpuDockBand()
        : base(Array.Empty<IListItem>(), "sysmon.dock.gpu", "GPU")
    {
        _gpuItem = new ListItem(new GpuDetailPage())
        {
            Title = "GPU",
            Subtitle = "加载中…",
            Icon = new IconInfo(""), // GPU — SensorShelf
        };
        Items = [_gpuItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        if (string.IsNullOrEmpty(info.Gpu.Name))
        {
            _gpuItem.Title = "GPU — 不可用";
            _gpuItem.Subtitle = "LHM 未加载";
            return;
        }

        var gpu = info.Gpu;
        var temp = DockFormat.Temp(gpu.Temperature);
        _gpuItem.Title = temp.Length > 0
            ? $"GPU {gpu.UsagePercent:F0}%  {temp}"
            : $"GPU {DockFormat.Percent(gpu.UsagePercent)}";
        _gpuItem.Subtitle = gpu.Name;
    }
}
