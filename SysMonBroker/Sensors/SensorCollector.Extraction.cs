using LibreHardwareMonitor.Hardware;

namespace SysMonBroker.Sensors;

public sealed partial class SensorCollector
{
    private static readonly string[] s_cpuTempPatterns = ["Package", "Tctl", "Core Max", "Core"];

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
        double temp = NormalizeGpuValue(FindSensorValue(gpu, SensorType.Temperature, "Core")
            ?? FindSensorValue(gpu, SensorType.Temperature, "SoC")
            ?? FindSensorValue(gpu, SensorType.Temperature, "Hot Spot")
            ?? -1);

        double load = NormalizeGpuValue(FindSensorValue(gpu, SensorType.Load, "GPU Core")
            ?? FindSensorValue(gpu, SensorType.Load, "Core")
            ?? -1);

        double memUsed = NormalizeGpuValue(FindSensorValue(gpu, SensorType.SmallData, "Memory Used") ?? 0);
        double memTotal = NormalizeGpuValue(FindSensorValue(gpu, SensorType.SmallData, "Memory Total") ?? 0);

        return new GpuReading(gpu.Name, temp, load, memUsed, memTotal);
    }

    /// <summary>R10: never write NaN/Infinity/negative GPU readings to SHM — normalize to -1 (same as sensor entries).</summary>
    private static double NormalizeGpuValue(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? -1 : value;

    private static float? FindSensorValue(IHardware hw, SensorType type, string nameContains) =>
        hw.Sensors.FirstOrDefault(s => s.SensorType == type
            && s.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))?.Value;
}
