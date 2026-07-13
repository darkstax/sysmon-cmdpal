using LibreHardwareMonitor.Hardware;
using SysMonBroker.IPC;

namespace SysMonBroker.Sensors;

public sealed partial class SensorCollector
{
    private void CollectSensorsFromHardware(IHardware hw, List<SensorEntry> results, int packedHwTag)
    {
        foreach (var sensor in hw.Sensors)
        {
            var entry = TryCreateEntry(hw.HardwareType, sensor, packedHwTag);
            if (entry != null) results.Add(entry);
        }

        foreach (var sub in hw.SubHardware)
        {
            try { sub.Update(); }
            catch { continue; }
            CollectSensorsFromHardware(sub, results, packedHwTag);
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
}
