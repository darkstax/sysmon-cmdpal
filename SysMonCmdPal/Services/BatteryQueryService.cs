// Copyright (c) 2026 SysMonCmdPal
// 电池实时查询服务 — 通过 WMI root\wmi\BatteryStatus 查询
// 非管理员可调用，不需要 CreateFile/DeviceIoControl
// 覆盖笔记本电池和 USB HID UPS（Windows 把两者都注册为 battery device）
//
// 关键发现：充电上限场景下 WMI Charging=True/DischargeRate=0 不可靠，
// 但 RemainingCapacity 趋势是真相——容量在掉就是电池在输出功率。

using System.Diagnostics;
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

    private BatteryQueryService() { }

    /// <summary>
    /// 查询电池实时状态。无电池返回 IsValid=false。
    /// </summary>
    public BatteryStatusInfo GetStatus()
    {
        var info = new BatteryStatusInfo();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\wmi", "SELECT * FROM BatteryStatus WHERE Active = TRUE");
            using var results = searcher.Get();
            foreach (var obj in results)
            {
                info.ChargeRateMw = Convert.ToInt32(obj["ChargeRate"]);
                info.DischargeRateMw = Convert.ToInt32(obj["DischargeRate"]);
                info.VoltageMv = Convert.ToInt32(obj["Voltage"]);
                info.RemainingCapacityMwh = Convert.ToInt32(obj["RemainingCapacity"]);
                info.Charging = Convert.ToBoolean(obj["Charging"]);
                info.Discharging = Convert.ToBoolean(obj["Discharging"]);
                info.PowerOnline = Convert.ToBoolean(obj["PowerOnline"]);
                info.Critical = Convert.ToBoolean(obj["Critical"]);
                info.IsValid = true;

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
                break; // 只取第一个电池
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BatteryQuery] WMI query: {ex.Message}");
        }
        return info;
    }
}
