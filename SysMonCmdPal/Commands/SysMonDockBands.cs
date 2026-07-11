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
    private static int _isRefreshing; // 0=idle, 1=refreshing (Interlocked flag, 防并发)

    public static void Subscribe(Action refresh)
    {
        lock (_lock)
        {
            _subscribers.Add(refresh);
            _refCount++;
            if (_timer == null)
            {
                _timer = new System.Timers.Timer(1000) { AutoReset = true };
                _timer.Elapsed += OnTick;
                _timer.Start();
                // Defer first refresh to timer tick — don't block COM server startup
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
        // 防并发：如果上一次 tick 仍在执行，跳过本次
        if (Interlocked.Exchange(ref _isRefreshing, 1) != 0)
            return;

        try
        {
            SystemInfoService.Instance.Refresh();
            Action[] snapshot;
            lock (_lock) { snapshot = _subscribers.ToArray(); }
            foreach (var action in snapshot)
            {
                try { action(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SysMon] DockBand refresh subscriber: {ex.Message}"); }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRefreshing, 0);
        }
    }
}

// ============================================================================
// Format helpers
// ============================================================================

internal static class DockFormat
{
    /// <summary>格式化速度（字节/秒）。>=1MB/s 显示 MB/s，>=1KB/s 显示 KB/s。</summary>
    public static string Speed(double bytesPerSec) => bytesPerSec switch
    {
        >= 1_000_000 => $"{bytesPerSec / 1_000_000:F1} MB/s",
        >= 1_000 => $"{bytesPerSec / 1_000:F0} KB/s",
        >= 1 => $"{bytesPerSec:F0} B/s",
        _ => "0 B/s",
    };

    /// <summary>紧凑速度（Dock 栏用），单位缩写成单个字母以节省宽度。输入：字节/秒。</summary>
    public static string CompactSpeed(double bytesPerSec) => bytesPerSec switch
    {
        >= 1_000_000 => $"{bytesPerSec / 1_000_000:F1}M/s",
        >= 1_000 => $"{bytesPerSec / 1_000:F0}K/s",
        >= 1 => $"{bytesPerSec:F0}B/s",
        _ => "0",
    };

    /// <summary>CPU/GPU 温度。>=0 显示数值，-1 表示不可用。</summary>
    public static string Temp(double c) => c >= 0 ? $"{c:F0}°C" : "N/A";

    /// <summary>百分比。>=0 显示数值，负值表示不可用。</summary>
    public static string Percent(double p) => p >= 0 ? $"{p:F0}%" : "N/A";

    /// <summary>电池状态文本。</summary>
    public static string BatteryStatusText(string status) => status switch
    {
        "charging" => Loc.Get("BatteryStatus.Charging"),
        "discharging" => Loc.Get("BatteryStatus.Discharging"),
        "dual" => Loc.Get("BatteryStatus.Dual"),
        "full" => Loc.Get("BatteryStatus.Full"),
        "no battery" => Loc.Get("BatteryStatus.NoBattery"),
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
            Title = Loc.Get("Dock.Cpu"),
            Subtitle = Loc.Get("Common.Loading"),
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
            ? Loc.Format("Dock.CpuTitle", $"{info.CpuUsage:F0}", temp)
            : Loc.Format("Dock.CpuTitleNoTemp", $"{info.CpuUsage:F0}");
        _cpuItem.Subtitle = info.CpuUsage switch
        {
            >= 90 => Loc.Get("Dock.CpuHighLoad"),
            >= 70 => Loc.Get("Dock.CpuElevated"),
            >= 40 => Loc.Get("Dock.CpuMedium"),
            _ => Loc.Get("Dock.CpuIdle"),
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
            Title = Loc.Get("Dock.Memory"),
            Subtitle = Loc.Get("Common.Loading"),
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
        _memItem.Title = Loc.Format("Dock.MemoryTitle", $"{info.MemoryUsed:F0}");
        _memItem.Subtitle = Loc.Format("Dock.MemorySubtitle", $"{used:F1}", $"{total:F1}");
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
            Title = Loc.Get("Dock.Disk"),
            Subtitle = Loc.Get("Common.Loading"),
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
            _diskItem.Title = Loc.Get("Dock.DiskNoDrive");
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
            Title = Loc.Get("Dock.Download"),
            Subtitle = Loc.Get("Common.Loading"),
            Icon = new IconInfo(""), // Segoe Fluent: Download
        };
        Items = [_netItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        _netItem.Title = $"↓ {DockFormat.Speed(info.NetDown)}";
        _netItem.Subtitle = Loc.Get("Dock.Download");
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
            Title = Loc.Get("Dock.Upload"),
            Subtitle = Loc.Get("Common.Loading"),
            Icon = new IconInfo(""), // Segoe Fluent: Upload
        };
        Items = [_netItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        _netItem.Title = $"↑ {DockFormat.Speed(info.NetUp)}";
        _netItem.Subtitle = Loc.Get("Dock.Upload");
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
            Title = Loc.Get("Dock.Battery"),
            Subtitle = Loc.Get("Common.Loading"),
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
            _batItem.Title = Loc.Get("Dock.BatteryNoBattery");
            _batItem.Subtitle = Loc.Get("Dock.BatteryDesktop");
            return;
        }
        _batItem.Title = Loc.Format("Dock.BatteryTitle", $"{info.BatteryPercent:F0}");
        _batItem.Subtitle = DockFormat.BatteryStatusText(info.BatteryStatus);
    }
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
            Title = Loc.Get("Dock.Gpu"),
            Subtitle = Loc.Get("Common.Loading"),
            Icon = new IconInfo(""), // GPU — SensorShelf
        };
        Items = [_gpuItem];
        DockBandRefreshCoordinator.Subscribe(OnRefresh);
    }

    private void OnRefresh()
    {
        var info = SystemInfoService.Instance.Current;
        var gpus = info.Gpus;
        if (gpus == null || gpus.Length == 0)
        {
            _gpuItem.Title = Loc.Get("Dock.GpuUnavailable");
            _gpuItem.Subtitle = Loc.Get("Dock.GpuSubtitle");
            return;
        }

        // 选择主 GPU（优先独显=有独立显存，其次有负载的）
        var primary = gpus.OrderByDescending(g => g.MemoryTotalMB > 0 ? 1 : 0)
                          .ThenByDescending(g => g.UsagePercent > 0 ? 1 : 0)
                          .ThenByDescending(g => g.Temperature)
                          .First();
        var temp = DockFormat.Temp(primary.Temperature);
        _gpuItem.Title = temp.Length > 0
            ? Loc.Format("Dock.GpuTitle", $"{primary.UsagePercent:F0}", temp)
            : Loc.Format("Dock.GpuTitleNoTemp", DockFormat.Percent(primary.UsagePercent));
        _gpuItem.Subtitle = gpus.Length > 1
            ? $"{primary.Name} (+{gpus.Length - 1})"
            : primary.Name;
    }
}
