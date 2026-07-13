// Copyright (c) 2026 SysMonCmdPal
// 电池实时查询服务 — 通过 WMI root\wmi\BatteryStatus 查询
// 非管理员可调用，不需要 CreateFile/DeviceIoControl
// 覆盖笔记本电池和 USB HID UPS（Windows 把两者都注册为 battery device）
//
// 关键发现：充电上限场景下 WMI Charging=True/DischargeRate=0 不可靠，
// 但 RemainingCapacity 趋势是真相——容量在掉就是电池在输出功率。

using System.Diagnostics;
using System.Globalization;
using System.Management;

namespace SysMonCmdPal;

/// <summary>
/// 电池实时状态（来自 WMI root\wmi\BatteryStatus）
/// </summary>
internal sealed class BatteryStatusInfo
{
    public int ChargeRateMw;       // mW，充电速率（充电上限场景下可能报正值但实际在掉电）
    public int DischargeRateMw;    // mW，放电速率（0=不在放电，充电上限场景下也可能是 0 不可靠）
    public int VoltageMv;          // mV
    public int RemainingCapacityMwh; // mWh，剩余容量（精确值，趋势检测用）
    public bool Charging;
    public bool Discharging;
    public bool PowerOnline;       // AC 在线
    public bool Critical;
    public bool IsValid;

    // 趋势检测：RemainingCapacity 是否在下降（=电池在输出功率）
    // 这是唯一能可靠识别"双重供电"的信号
    public bool IsDraining;
}

/// <summary>
/// 通过 WMI root\wmi\BatteryStatus 查询电池实时状态。
/// 单例，缓存上次 RemainingCapacity 用于趋势检测。
/// </summary>
internal sealed class BatteryQueryService
{
    public static BatteryQueryService Instance { get; } = new();

    private int _lastCapacity = -1;
    private DateTime _lastCapacityTime = DateTime.MinValue;
    private DateTime _drainingSince = DateTime.MinValue;  // 迟滞：一旦判定 draining 保持 10 秒

    // P4: 缓存 WMI 查询结果 3 秒，避免每秒都做 WMI 调用
    private BatteryStatusInfo? _cached;
    private DateTime _cacheTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);
    private readonly object _lock = new();

    private BatteryQueryService() { }

    /// <summary>
    /// 查询电池实时状态。无电池返回 IsValid=false。
    /// </summary>
    public BatteryStatusInfo GetStatus()
    {
        lock (_lock)
        {
            // P4: 3 秒缓存 — 电池状态变化缓慢，不需要每秒 WMI 查询
            if (_cached != null && (DateTime.UtcNow - _cacheTime) < CacheTtl)
                return _cached;

            var info = QueryStatusCore();
            _cached = info;
            _cacheTime = DateTime.UtcNow;
            return info;
        }
    }

    private BatteryStatusInfo QueryStatusCore()
    {
        var info = new BatteryStatusInfo();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\wmi", "SELECT * FROM BatteryStatus WHERE Active = TRUE");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                try
                {
                    info.ChargeRateMw = GetInt(obj, "ChargeRate");
                    info.DischargeRateMw = GetInt(obj, "DischargeRate");
                    info.VoltageMv = GetInt(obj, "Voltage");
                    info.RemainingCapacityMwh = GetInt(obj, "RemainingCapacity");
                    info.Charging = GetBool(obj, "Charging");
                    info.Discharging = GetBool(obj, "Discharging");
                    info.PowerOnline = GetBool(obj, "PowerOnline");
                    info.Critical = GetBool(obj, "Critical");
                    info.IsValid = info.RemainingCapacityMwh >= 0;

                    if (info.RemainingCapacityMwh >= 0)
                    {
                        // 趋势检测：RemainingCapacity 下降 = 电池在输出功率
                        // 5 秒窗口内 mWh 下降 → draining
                        bool capacityDropped = _lastCapacity >= 0 &&
                            info.RemainingCapacityMwh < _lastCapacity &&
                            (DateTime.UtcNow - _lastCapacityTime).TotalSeconds <= 5;

                        // 迟滞：一旦判定 draining，保持 10 秒不退回
                        // 防止 WMI 更新间隔导致 IsDraining 在 true/false 之间闪烁
                        if (capacityDropped)
                            _drainingSince = DateTime.UtcNow;

                        info.IsDraining = _drainingSince != DateTime.MinValue &&
                            (DateTime.UtcNow - _drainingSince).TotalSeconds <= 10;

                        // 如果容量明确在涨（充电），清除 draining 状态
                        if (_lastCapacity >= 0 && info.RemainingCapacityMwh > _lastCapacity + 50)
                            _drainingSince = DateTime.MinValue;

                        _lastCapacity = info.RemainingCapacityMwh;
                        _lastCapacityTime = DateTime.UtcNow;
                    }
                    break; // 只取第一个电池
                }
                finally { obj.Dispose(); } // M5: 释放 WMI COM 对象
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BatteryQuery] WMI query: {ex.Message}");
        }
        return info;
    }

    private static int GetInt(ManagementBaseObject obj, string name, int fallback = -1)
    {
        try
        {
            var value = obj[name];
            return value is null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch { return fallback; }
    }

    private static bool GetBool(ManagementBaseObject obj, string name)
    {
        try
        {
            var value = obj[name];
            return value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch { return false; }
    }
}
