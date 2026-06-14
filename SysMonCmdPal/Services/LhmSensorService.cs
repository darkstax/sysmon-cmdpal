// Copyright (c) 2026 SysMonCmdPal
// 全量 LHM 传感器采集器 — 枚举所有硬件传感器的读数，按类别组织。
// 替代 SystemInfoService 中零散的 LHM 调用，提供完整传感器目录。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace SysMonCmdPal;

/// <summary>
/// LHM 全量传感器服务（单例）。读取所有硬件传感器的当前值，
/// 按类别分组，供页面和 Dock Band 使用。
/// 依赖 HWiNFO 共享内存或内置传感器库（免驱动）。
/// </summary>
public sealed class LhmSensorService
{
    public static LhmSensorService Instance { get; } = new();

    private Computer? _computer;
    private bool _available;
    private bool _initAttempted;  // 是否已尝试初始化（惰性，避免构造时卡死）
    private int _consecutiveFailures;
    private const int MaxFailuresBeforeUnavailable = 3;
    private readonly object _lock = new();

    /// <summary>最后一次错误信息（null 表示健康）</summary>
    public string? LastError { get; private set; }

    /// <summary>按类别分组的最新传感器读数</summary>
    public Dictionary<SensorCategory, List<SensorReading>> Catalog { get; private set; } = [];

    /// <summary>所有传感器平铺列表</summary>
    public List<SensorReading> AllReadings { get; private set; } = [];

    /// <summary>用户配置</summary>
    public SensorConfig Config { get; private set; } = new();

    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysMonCmdPal", "sensors.json");

    // ---- 分类映射 ----
    private static SensorCategory Categorize(string sensorName, string label, HardwareType hwType)
    {
        bool isTemp = label.Contains("Temperature", StringComparison.OrdinalIgnoreCase);
        bool isLoad = label.Contains("Load", StringComparison.OrdinalIgnoreCase);
        bool isClock = label.Contains("Clock", StringComparison.OrdinalIgnoreCase);
        bool isPower = label.Contains("Power", StringComparison.OrdinalIgnoreCase);
        bool isVolt = label.Contains("Voltage", StringComparison.OrdinalIgnoreCase);
        bool isFan = label.Contains("Fan", StringComparison.OrdinalIgnoreCase) ||
                     label.Contains("Control", StringComparison.OrdinalIgnoreCase);
        bool isSmall = label.Contains("SmallData", StringComparison.OrdinalIgnoreCase);

        return hwType switch
        {
            HardwareType.Cpu when isTemp => SensorCategory.CpuTemp,
            HardwareType.Cpu when isLoad => SensorCategory.CpuLoad,
            HardwareType.Cpu when isClock => SensorCategory.CpuClock,
            HardwareType.Cpu when isPower => SensorCategory.CpuPower,
            HardwareType.Cpu when isVolt => SensorCategory.CpuVoltage,

            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when isTemp => SensorCategory.GpuTemp,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when isLoad => SensorCategory.GpuLoad,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when isClock => SensorCategory.GpuClock,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when isPower => SensorCategory.GpuPower,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when isFan => SensorCategory.GpuFan,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when isSmall => SensorCategory.GpuMemory,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel when isVolt => SensorCategory.GpuVoltage,

            HardwareType.Motherboard when isTemp => SensorCategory.MbTemp,
            HardwareType.Motherboard when isFan => SensorCategory.MbFan,
            HardwareType.Motherboard when isVolt => SensorCategory.MbVoltage,

            HardwareType.Storage when isTemp => SensorCategory.StorageTemp,
            HardwareType.Storage when isLoad => SensorCategory.StorageLoad,

            _ => SensorCategory.CpuTemp, // fallback (won't be used in practice)
        };
    }

    private static string ToUnit(string label)
    {
        if (label.Contains("Temperature", StringComparison.OrdinalIgnoreCase)) return "°C";
        if (label.Contains("Load", StringComparison.OrdinalIgnoreCase)) return "%";
        if (label.Contains("Clock", StringComparison.OrdinalIgnoreCase)) return "MHz";
        if (label.Contains("Power", StringComparison.OrdinalIgnoreCase)) return "W";
        if (label.Contains("Voltage", StringComparison.OrdinalIgnoreCase)) return "V";
        if (label.Contains("Fan", StringComparison.OrdinalIgnoreCase)) return "RPM";
        if (label.Contains("Control", StringComparison.OrdinalIgnoreCase)) return "%";
        if (label.Contains("Data", StringComparison.OrdinalIgnoreCase)) return "MB";
        return "";
    }

    private LhmSensorService()
    {
        try { Config = LoadConfig(); }
        catch (Exception ex)
        {
            Config = new();
            LastError = $"配置加载失败: {ex.Message}";
        }
    }

    /// <summary>上次尝试初始化的时间，用于冷却重试</summary>
    private DateTime _lastInitAttempt = DateTime.MinValue;
    private static readonly TimeSpan InitRetryCooldown = TimeSpan.FromSeconds(30);

    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                if (!_initAttempted)
                {
                    _initAttempted = true;
                    _lastInitAttempt = DateTime.UtcNow;
                    InitLhm();
                    if (_available) Refresh(); // 填充 Catalog/AllReadings
                }
                else if (!_available && DateTime.UtcNow - _lastInitAttempt > InitRetryCooldown)
                {
                    // 冷却重试：HWiNFO 用户可能稍后启动
                    _lastInitAttempt = DateTime.UtcNow;
                    _consecutiveFailures = 0;
                    InitLhm();
                    if (_available)
                    {
                        try { Refresh(); }
                        catch (Exception ex) { _available = false; Debug.WriteLine($"[SysMon] LHM retry refresh failed: {ex.Message}"); }
                    }
                }
                return _available;
            }
        }
    }

    private void InitLhm()
    {
        var logPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SysMonCmdPal", "lhm_init.log");
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
            System.IO.File.AppendAllText(logPath, $"{DateTime.Now:O} [INIT] Starting LibreHardwareMonitor...\n");

            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsMemoryEnabled = false,
                IsNetworkEnabled = false,
                IsStorageEnabled = true,
            };
            System.IO.File.AppendAllText(logPath, $"{DateTime.Now:O} [INIT] Computer created, calling Open()...\n");

            _computer.Open();

            System.IO.File.AppendAllText(logPath, $"{DateTime.Now:O} [INIT] Open() OK. Hardware count: {_computer.Hardware.Count}\n");
            foreach (var hw in _computer.Hardware)
                System.IO.File.AppendAllText(logPath, $"  {hw.HardwareType}: {hw.Name} (sensors: {hw.Sensors.Count()})\n");

            _available = true;
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            System.IO.File.AppendAllText(logPath, $"{DateTime.Now:O} [FAIL] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}\n");
            Debug.WriteLine($"[SysMon] LHM init failed: {ex.Message}");
            _available = false;
            _computer = null;
            _consecutiveFailures = MaxFailuresBeforeUnavailable;
            LastError = $"LHM 初始化失败: {ex.Message}";
        }
    }

    /// <summary>尝试重新初始化 LHM（用户修复 PawnIO 后调用）</summary>
    public bool TryReconnect()
    {
        lock (_lock)
        {
            Shutdown();
            _consecutiveFailures = 0;
            InitLhm();
            if (_available)
            {
                try { Refresh(); }
                catch (Exception ex) { _available = false; Debug.WriteLine($"[SysMon] LHM TryReconnect refresh failed: {ex.Message}"); }
            }
            return _available;
        }
    }


    /// <summary>刷新所有传感器读数</summary>
    public void Refresh()
    {
        if (!_available || _computer is null) return;

        lock (_lock)
        {
            try
            {
                _computer.Accept(new SensorVisitor());

                var allReadings = new List<SensorReading>();

                foreach (var hw in _computer.Hardware)
                {
                    var hwName = hw.Name;

                    foreach (var s in hw.Sensors)
                    {
                        if (!s.Value.HasValue || double.IsNaN((double)s.Value) || double.IsInfinity((double)s.Value)) continue;

                        var label = s.SensorType.ToString();
                        var unit = ToUnit(label);
                        var category = Categorize(s.Name, label, hw.HardwareType);

                        // Skip unknown/uncategorized
                        if (category == SensorCategory.CpuTemp && hw.HardwareType != HardwareType.Cpu) continue;

                        var key = $"{hw.HardwareType}|{hwName}|{s.Name}|{label}";

                        // Apply user config
                        var cfgEntry = Config.Sensors.FirstOrDefault(c => c.Key == key);
                        var displayName = cfgEntry?.DisplayName ?? s.Name;

                        allReadings.Add(new SensorReading
                        {
                            HardwareName = hwName,
                            SensorName = s.Name,
                            Label = label,
                            Value = Math.Round((double)s.Value, 2),
                            Unit = unit,
                            Category = category,
                            UniqueKey = key,
                            DisplayName = displayName,
                            WarningThreshold = cfgEntry?.WarningThreshold ?? 0,
                            CriticalThreshold = cfgEntry?.CriticalThreshold ?? 0,
                        });
                    }

                    // Collect sub-hardware sensors ONCE per hardware (was inside the sensor loop, causing N× duplicates)
                    CollectSubHardware(hw, hwName, allReadings);
                }

                AllReadings = allReadings;
                Catalog = allReadings
                    .GroupBy(r => r.Category)
                    .ToDictionary(g => g.Key, g => g.ToList());

                _consecutiveFailures = 0;
                LastError = null;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                LastError = $"LHM 读取失败 ({_consecutiveFailures}/{MaxFailuresBeforeUnavailable}): {ex.Message}";

                if (_consecutiveFailures >= MaxFailuresBeforeUnavailable)
                {
                    _available = false;
                    LastError = $"LHM 已断开（连续 {_consecutiveFailures} 次失败）: {ex.Message}";
                }
            }
        }
    }

    private void CollectSubHardware(IHardware hw, string hwName, List<SensorReading> list)
    {
        foreach (var sub in hw.SubHardware)
        {
            foreach (var s in sub.Sensors)
            {
                if (!s.Value.HasValue || double.IsNaN((double)s.Value) || double.IsInfinity((double)s.Value)) continue;
                var label = s.SensorType.ToString();
                var key = $"{hw.HardwareType}|{hwName}|{s.Name}|{label}";

                list.Add(new SensorReading
                {
                    HardwareName = hwName,
                    SensorName = s.Name,
                    Label = label,
                    Value = Math.Round((double)s.Value, 2),
                    Unit = ToUnit(label),
                    Category = Categorize(s.Name, label, hw.HardwareType),
                    UniqueKey = key,
                    DisplayName = s.Name,
                });
            }

            // 递归: AMD CPU 可能有 CCD#0 → Core#0 等多层子硬件
            CollectSubHardware(sub, hwName, list);
        }
    }

    // ---- Config management ----

    private static SensorConfig LoadConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<SensorConfig>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] Sensor config load failed: {ex.Message}");
        }
        return new();
    }

    public void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(Config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] Sensor config save failed: {ex.Message}");
        }
    }

    public void AddSensorToConfig(SensorReading reading)
    {
        var key = reading.UniqueKey ?? "";
        if (Config.Sensors.Any(e => e.Key == key)) return;

        Config.Sensors.Add(new SensorConfigEntry
        {
            Key = key,
            DisplayName = reading.SensorName ?? key,
            Order = Config.Sensors.Count,
            InDock = true,
        });
        SaveConfig();
    }

    public void RemoveSensorFromConfig(string key)
    {
        Config.Sensors.RemoveAll(e => e.Key == key);
        SaveConfig();
    }

    public bool IsInConfig(string key) => Config.Sensors.Any(e => e.Key == key);

    public SensorConfigEntry? GetConfigEntry(string key) =>
        Config.Sensors.FirstOrDefault(e => e.Key == key);

    // ---- Visitor ----
    private sealed class SensorVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
                sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    public void Shutdown()
    {
        _computer?.Close();
        _available = false;
    }
}
