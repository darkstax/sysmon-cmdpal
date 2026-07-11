// SysMonBroker/Sensors/SensorCollector.cs
// LHM thin-shell: collects ALL sensors from LibreHardwareMonitor
// No custom ring-0 code — LHM handles PawnIO internally.

using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using SysMonBroker.IPC;

namespace SysMonBroker.Sensors;

/// <summary>GPU reading snapshot from LHM</summary>
public sealed record GpuReading(string Name, double TempCelsius, double UsagePercent,
    double MemUsedMB, double MemTotalMB);

/// <summary>
/// Thin wrapper around LibreHardwareMonitorLib.
/// Opens hardware on construction; polls sensors via Update().
/// Thread-safety: NOT safe — all calls must be from a single thread.
/// </summary>
public sealed class SensorCollector : IDisposable
{
    private readonly Computer _computer;
    private readonly string _cpuSource;

    public bool PawnIoInstalled { get; }

    public SensorCollector()
    {
        PawnIoInstalled = CheckPawnIoInstalled();
        _cpuSource = DetermineCpuSource();

        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
        };
        _computer.Open();
    }

    // ---- PawnIO detection ----

    private static bool CheckPawnIoInstalled()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
        if (TryReadVersion(Registry.LocalMachine, key)) return true;
        using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return TryReadVersion(hklm64, key);
    }

    private static bool TryReadVersion(RegistryKey root, string subkeyPath)
    {
        using var sub = root.OpenSubKey(subkeyPath);
        return sub?.GetValue("DisplayVersion") is string s
            && Version.TryParse(s, out var v)
            && v >= new Version(2, 0, 0);
    }

    private string DetermineCpuSource()
    {
        if (!PawnIoInstalled) return "Broker_LHM";
        var cpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        if (cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            return "Broker_SMU";
        if (cpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return "Broker_MSR";
        return "Broker_LHM";
    }

    // ---- Hardware type helpers ----

    private static string HardwareTypeName(HardwareType type) => type switch
    {
        HardwareType.Cpu => "Cpu",
        HardwareType.GpuNvidia => "GpuNvidia",
        HardwareType.GpuAmd => "GpuAmd",
        HardwareType.GpuIntel => "GpuIntel",
        HardwareType.Motherboard => "Motherboard",
        HardwareType.Storage => "Storage",
        _ => type.ToString(),
    };

    private static int HardwareTypeTag(HardwareType type) => type switch
    {
        HardwareType.Cpu => 0,
        HardwareType.GpuNvidia => 1,
        HardwareType.GpuAmd => 2,
        HardwareType.GpuIntel => 3,
        HardwareType.Motherboard => 4,
        HardwareType.Storage => 5,
        _ => -1,
    };

    private static int CategorizeSensor(HardwareType hwType, SensorType sType, string name)
    {
        return hwType switch
        {
            HardwareType.Cpu => sType switch
            {
                SensorType.Temperature => 0,  // CpuTemp
                SensorType.Load => 1,         // CpuLoad
                SensorType.Clock => 2,        // CpuClock
                SensorType.Power => 3,        // CpuPower
                SensorType.Voltage => 4,      // CpuVoltage
                _ => -1,
            },
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => sType switch
            {
                SensorType.Temperature => 5,  // GpuTemp
                SensorType.Load => 6,         // GpuLoad
                SensorType.Clock => 7,        // GpuClock
                SensorType.Power => 8,        // GpuPower
                SensorType.SmallData when name.Contains("Memory") => 9, // GpuMemory
                SensorType.Fan or SensorType.Control => 10, // GpuFan
                SensorType.Voltage => 11,     // GpuVoltage
                _ => -1,
            },
            HardwareType.Motherboard => sType switch
            {
                SensorType.Temperature => 12, // MbTemp
                SensorType.Fan or SensorType.Control => 13, // MbFan
                SensorType.Voltage => 14,     // MbVoltage
                _ => -1,
            },
            HardwareType.Storage => sType switch
            {
                SensorType.Temperature => 15, // StorageTemp
                SensorType.Load => 16,        // StorageLoad
                _ => -1,
            },
            _ => -1,
        };
    }

    // ---- Public API ----

    /// <summary>
    /// Single-pass read: updates all hardware once and returns CPU temp, GPUs, and all sensors.
    /// Replaces the triple-Update pattern of calling ReadCpuTemp + ReadGpus + ReadAllSensors separately.
    /// </summary>
    public (double CpuTemp, string CpuSource, List<GpuReading> Gpus, List<SensorEntry> Sensors) ReadAll()
    {
        double cpuTemp = -1;
        var gpus = new List<GpuReading>();
        var sensors = new List<SensorEntry>();

        foreach (var hw in _computer.Hardware)
        {
            var ht = hw.HardwareType;
            bool isCpu = ht == HardwareType.Cpu;
            bool isGpu = ht == HardwareType.GpuNvidia || ht == HardwareType.GpuAmd || ht == HardwareType.GpuIntel;

            try { hw.Update(); }
            catch { continue; }

            int hwTag = HardwareTypeTag(ht);
            if (hwTag < 0) continue;

            // Collect all categorized sensors from this hardware (and sub-hardware)
            CollectSensorsFromHardware(hw, sensors);

            if (isCpu)
            {
                cpuTemp = ExtractCpuTemp(hw);
            }

            if (isGpu)
            {
                var gpu = ExtractGpuReading(hw);
                if (gpu != null) gpus.Add(gpu);
            }
        }

        return (cpuTemp, cpuTemp > 0 ? _cpuSource : "None", gpus, sensors);
    }

    private double ExtractCpuTemp(IHardware cpu)
    {
        foreach (var pattern in s_cpuTempPatterns)
        {
            var val = cpu.Sensors
                .FirstOrDefault(s => s.SensorType == SensorType.Temperature
                    && s.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))?.Value;
            if (val is > 0 and < 150) return val.Value;
        }
        return -1;
    }

    private static GpuReading? ExtractGpuReading(IHardware gpu)
    {
        double temp = FindSensorValue(gpu, SensorType.Temperature, "Core")
            ?? FindSensorValue(gpu, SensorType.Temperature, "SoC")
            ?? FindSensorValue(gpu, SensorType.Temperature, "Hot Spot")
            ?? -1;

        double load = FindSensorValue(gpu, SensorType.Load, "GPU Core")
            ?? FindSensorValue(gpu, SensorType.Load, "Core")
            ?? -1;

        double memUsed = FindSensorValue(gpu, SensorType.SmallData, "Memory Used") ?? 0;
        double memTotal = FindSensorValue(gpu, SensorType.SmallData, "Memory Total") ?? 0;

        return new GpuReading(gpu.Name, temp, load, memUsed, memTotal);
    }

    private static readonly string[] s_cpuTempPatterns = ["Package", "Tctl", "Core Max", "Core"];

    private void CollectSensorsFromHardware(IHardware hw, List<SensorEntry> results)
    {
        int hwTag = HardwareTypeTag(hw.HardwareType);

        foreach (var sensor in hw.Sensors)
        {
            var entry = TryCreateEntry(hw.HardwareType, sensor, hwTag);
            if (entry != null) results.Add(entry);
        }

        foreach (var sub in hw.SubHardware)
        {
            try { sub.Update(); }
            catch { continue; }
            CollectSensorsFromHardware(sub, results);
        }
    }

    private static SensorEntry? TryCreateEntry(HardwareType hwType, ISensor sensor, int hwTag)
    {
        if (!sensor.Value.HasValue) return null;

        int tag = CategorizeSensor(hwType, sensor.SensorType, sensor.Name ?? "");
        if (tag < 0) return null;

        float val = sensor.Value.Value;
        if (float.IsNaN(val) || float.IsInfinity(val)) return null;

        string name = sensor.Name ?? "";
        if (name.Length > 30) name = name[..30];

        string unit = sensor.SensorType switch
        {
            SensorType.Temperature => "°C",
            SensorType.Load => "%",
            SensorType.Clock => "MHz",
            SensorType.Power => "W",
            SensorType.Voltage => "V",
            SensorType.SmallData => "MB",
            SensorType.Fan => "RPM",
            SensorType.Control => "%",
            SensorType.Throughput => "B/s",
            _ => "",
        };

        return new SensorEntry(tag, name, val, unit, hwTag);
    }

    /// <summary>Read CPU temperature and source tag</summary>
    public (double Temp, string Source) ReadCpuTemp()
    {
        var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu == null) return (-1, "None");

        cpu.Update();
        double temp = ExtractCpuTemp(cpu);
        return (temp, temp > 0 ? _cpuSource : "None");
    }

    private static float? FindSensorValue(IHardware hw, SensorType type, string nameContains) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type
            && s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))?.Value;

    public void Dispose()
    {
        _computer.Close();
    }
}
