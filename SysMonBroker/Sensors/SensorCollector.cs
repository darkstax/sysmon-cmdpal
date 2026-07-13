// SysMonBroker/Sensors/SensorCollector.cs
// LHM thin-shell: collects ALL sensors from LibreHardwareMonitor
// No custom ring-0 code — LHM handles PawnIO internally.

using LibreHardwareMonitor.Hardware;
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
public sealed partial class SensorCollector : IDisposable
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
        var instanceByType = new Dictionary<int, int>();

        foreach (var hw in _computer.Hardware)
        {
            var ht = hw.HardwareType;
            bool isCpu = ht == HardwareType.Cpu;
            bool isGpu = ht == HardwareType.GpuNvidia || ht == HardwareType.GpuAmd || ht == HardwareType.GpuIntel;

            try { hw.Update(); }
            catch { continue; }

            int hwTag = HardwareTypeTag(ht);
            if (hwTag < 0) continue;
            instanceByType.TryGetValue(hwTag, out int typeInstance);
            instanceByType[hwTag] = typeInstance + 1;
            int packedHwTag = PackHardwareTag(hwTag, typeInstance);

            // Collect all categorized sensors from this hardware (and sub-hardware)
            CollectSensorsFromHardware(hw, sensors, packedHwTag);

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

    /// <summary>Read CPU temperature and source tag</summary>
    public (double Temp, string Source) ReadCpuTemp()
    {
        var cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        if (cpu == null) return (-1, "None");

        cpu.Update();
        double temp = ExtractCpuTemp(cpu);
        return (temp, temp > 0 ? _cpuSource : "None");
    }

    public void Dispose()
    {
        _computer.Close();
    }
}
