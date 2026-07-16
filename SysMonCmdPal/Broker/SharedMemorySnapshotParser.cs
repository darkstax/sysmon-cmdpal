// SysMonCmdPal/Broker/SharedMemorySnapshotParser.cs

using System;
using System.Collections.Generic;
using System.Text;

namespace SysMonCmdPal.Broker;

/// <summary>Parses a stable shared-memory image into the plugin snapshot model.</summary>
internal static class SharedMemorySnapshotParser
{
    public static bool TryParse(
        StableSnapshot stableSnapshot,
        out ParsedSnapshot snapshot,
        out string error)
    {
        SnapshotLayout layout = stableSnapshot.Layout;
        byte[] buffer = stableSnapshot.Data;
        int rawGpuCount = BitConverter.ToInt32(buffer, layout.GpuCountOffset);
        if (rawGpuCount < 0 || rawGpuCount > ShmLayout.MaxGpus)
        {
            snapshot = default;
            error = $"Invalid GPU count: {rawGpuCount}";
            return false;
        }

        var nowUtc = DateTime.UtcNow;
        var gpus = new List<KeyValuePair<int, BrokerGpuSnapshot>>(rawGpuCount);
        for (int i = 0; i < rawGpuCount; i++)
        {
            int gpuOffset = layout.GpuBaseOffset + (i * ShmLayout.GpuEntrySize);
            string name = ReadString(buffer, gpuOffset, ShmLayout.GpuNameLen);
            double temperature = BitConverter.ToDouble(buffer, gpuOffset + ShmLayout.GpuTempOff);
            double usage = BitConverter.ToDouble(buffer, gpuOffset + ShmLayout.GpuUsageOff);
            double memoryUsed = BitConverter.ToDouble(buffer, gpuOffset + ShmLayout.GpuMemUsedOff);
            double memoryTotal = BitConverter.ToDouble(buffer, gpuOffset + ShmLayout.GpuMemTotalOff);

            gpus.Add(new KeyValuePair<int, BrokerGpuSnapshot>(i, new BrokerGpuSnapshot
            {
                Name = name,
                Temperature = IsReasonableTemp(temperature) ? temperature : -1,
                UsagePercent = IsReasonablePercent(usage) ? usage : -1,
                MemoryUsedMB = IsFiniteNonNegative(memoryUsed) ? memoryUsed : 0,
                MemoryTotalMB = IsFiniteNonNegative(memoryTotal) ? memoryTotal : 0,
                Timestamp = nowUtc,
            }));
        }

        IReadOnlyList<BrokerSensorEntry> sensors = Array.Empty<BrokerSensorEntry>();
        if (layout.Version >= 2)
        {
            int rawSensorCount = BitConverter.ToInt32(buffer, layout.SensorCountOffset);
            int capacitySensorCount = Math.Min(
                ShmLayout.MaxSensors,
                (layout.ReadLength - layout.SensorBaseOffset) / ShmLayout.SensorEntrySize);

            if (rawSensorCount < 0 || rawSensorCount > capacitySensorCount)
            {
                snapshot = default;
                error = $"Invalid sensor count: {rawSensorCount} (capacity {capacitySensorCount})";
                return false;
            }

            var list = new List<BrokerSensorEntry>(rawSensorCount);
            for (int i = 0; i < rawSensorCount; i++)
            {
                int sensorOffset = layout.SensorBaseOffset + (i * ShmLayout.SensorEntrySize);
                double value = BitConverter.ToDouble(
                    buffer,
                    sensorOffset + ShmLayout.SensorValueOff);
                if (double.IsNaN(value) || double.IsInfinity(value))
                    continue;

                list.Add(new BrokerSensorEntry
                {
                    Tag = BitConverter.ToInt32(buffer, sensorOffset + ShmLayout.SensorTagOff),
                    Name = ReadString(buffer, sensorOffset + ShmLayout.SensorNameOff, 32),
                    Value = value,
                    Unit = ReadString(buffer, sensorOffset + ShmLayout.SensorUnitOff, 16),
                    HardwareTag = BitConverter.ToInt32(
                        buffer,
                        sensorOffset + ShmLayout.SensorHardwareOff),
                });
            }

            sensors = list;
        }

        double cpuTemperature = BitConverter.ToDouble(buffer, layout.CpuTempOffset);
        snapshot = new ParsedSnapshot(
            CpuTemperature: IsReasonableTemp(cpuTemperature) ? cpuTemperature : -1,
            CpuSource: ReadString(buffer, layout.SourceOffset, 32),
            Gpus: gpus,
            Sensors: sensors);
        error = "";
        return true;
    }

    private static bool IsFiniteNonNegative(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

    private static bool IsReasonablePercent(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= 100;

    private static bool IsReasonableTemp(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value > 0 && value < 150;

    private static string ReadString(byte[] buffer, int offset, int maxBytes)
    {
        int length = 0;
        while (length < maxBytes && buffer[offset + length] != 0)
            length++;

        return length > 0 ? Encoding.UTF8.GetString(buffer, offset, length) : "";
    }
}

internal readonly record struct ParsedSnapshot(
    double CpuTemperature,
    string CpuSource,
    IReadOnlyList<KeyValuePair<int, BrokerGpuSnapshot>> Gpus,
    IReadOnlyList<BrokerSensorEntry> Sensors);
