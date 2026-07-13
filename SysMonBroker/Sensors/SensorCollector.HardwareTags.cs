using LibreHardwareMonitor.Hardware;

namespace SysMonBroker.Sensors;

public sealed partial class SensorCollector
{
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

    private static int PackHardwareTag(int typeTag, int typeInstance) =>
        typeTag | (Math.Max(0, typeInstance) << 8);

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
}
