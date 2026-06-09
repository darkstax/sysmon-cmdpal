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
/// 依赖 PawnIO 驱动（一次安装，免管理员）。
/// </summary>
public sealed class LhmSensorService
{
    public static LhmSensorService Instance { get; } = new();

    private Computer? _computer;
    private bool _available;
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
        // 加载配置（即使 LHM 失败也保留，不丢失用户配置）
        try { Config = LoadConfig(); }
        catch (Exception ex)
        {
            Config = new();
            LastError = $"配置加载失败: {ex.Message}";
        }

        // 初始化 LHM（可能因 PawnIO 未安装/驱动异常而失败）
        InitLhm();
        if (_available)
        {
            Refresh();
            LastError = null;
        }
    }

    private void InitLhm()
    {
        try
        {
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
            _computer.Open();
            _available = true;
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] LHM init failed: {ex.Message}");
            _available = false;
            _computer = null;
            _consecutiveFailures = MaxFailuresBeforeUnavailable; // 初始化失败直接计满
            LastError = $"LHM 初始化失败（PawnIO 驱动可能未安装）: {ex.Message}";
        }
    }

    /// <summary>尝试重新初始化 LHM（用户修复 PawnIO 后调用）</summary>
    public bool TryReconnect()
    {
        Shutdown();
        _consecutiveFailures = 0;
        InitLhm();
        if (_available)
        {
            try { Refresh(); } catch { _available = false; }
        }
        return _available;
    }

    public bool IsAvailable => _available;

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
                        if (!s.Value.HasValue) continue;

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

                        // Also collect sub-hardware sensors
                        CollectSubHardware(hw, hwName, allReadings);
                    }
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
                if (!s.Value.HasValue) continue;
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
        catch { }
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
        catch { }
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
