// Copyright (c) 2026 SysMonCmdPal

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal.Tests;

internal static class BrokerTestData
{
    public static BrokerSensorSnapshot Snapshot(
        bool isAvailable,
        bool isExpired = false,
        bool hasRecentData = true,
        double cpuTemperature = 61.5,
        string cpuSource = "Broker_Test",
        IEnumerable<KeyValuePair<int, BrokerGpuSnapshot>>? gpus = null,
        IReadOnlyList<BrokerSensorEntry>? sensors = null)
    {
        long recentTimestamp = TimestampAgo(TimeSpan.FromSeconds(1));
        long expiredTimestamp = TimestampAgo(TimeSpan.FromMinutes(1));
        DateTime now = DateTime.UtcNow;

        return new BrokerSensorSnapshot
        {
            CpuTemperature = cpuTemperature,
            CpuSource = cpuSource,
            Gpus = new ConcurrentDictionary<int, BrokerGpuSnapshot>(gpus ?? []),
            AllSensors = sensors ?? [],
            LastPush = now - (hasRecentData ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1)),
            LastPing = now - (isAvailable && !isExpired
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromMinutes(1)),
            LastDataTimestamp = hasRecentData ? recentTimestamp : expiredTimestamp,
            LastAvailableTimestamp = isAvailable
                ? isExpired ? expiredTimestamp : recentTimestamp
                : 0,
        };
    }

    public static byte[] V2Buffer(
        int counter,
        int version = ShmLayout.Version,
        int magic = ShmLayout.MagicValue,
        double cpuTemperature = 61.5,
        string source = "Broker_Test",
        IReadOnlyList<TestGpu>? gpus = null,
        IReadOnlyList<TestSensor>? sensors = null,
        int? rawGpuCount = null,
        int? rawSensorCount = null,
        long? timestampTicks = null,
        int? commitSequence = null,
        int extensionMagic = ShmLayout.ExtensionMagicValue,
        ulong instanceId = 1,
        long? monotonicPublishMs = null)
    {
        gpus ??= [];
        sensors ??= [];

        byte[] buffer = new byte[ShmLayout.MapSize];
        WriteInt32(buffer, ShmLayout.OffMagic, magic);
        WriteInt32(buffer, ShmLayout.OffVersion, version);
        WriteInt32(buffer, ShmLayout.OffCounter, counter);
        WriteInt32(
            buffer,
            ShmLayout.OffCommitSequence,
            commitSequence ?? (extensionMagic == 0 ? 0 : 2));
        WriteDouble(buffer, ShmLayout.OffCpuTemp, cpuTemperature);
        WriteString(buffer, ShmLayout.OffSource, 32, source);
        WriteInt32(buffer, ShmLayout.OffGpuCount, rawGpuCount ?? gpus.Count);
        WriteInt64(
            buffer,
            ShmLayout.OffTimestamp,
            timestampTicks ?? DateTime.UtcNow.Ticks);
        WriteInt32(buffer, ShmLayout.OffSensorCount, rawSensorCount ?? sensors.Count);
        WriteInt32(buffer, ShmLayout.OffExtensionMagic, extensionMagic);
        WriteUInt64(buffer, ShmLayout.OffInstanceId, instanceId);
        WriteInt64(
            buffer,
            ShmLayout.OffMonotonicPublishMs,
            monotonicPublishMs ?? Math.Max(1, Environment.TickCount64));

        WriteGpus(buffer, ShmLayout.OffGpuBase, gpus);
        WriteSensors(buffer, sensors);
        return buffer;
    }

    public static byte[] V1Buffer(
        int counter,
        double cpuTemperature = 58.5,
        string source = "LegacyBroker",
        IReadOnlyList<TestGpu>? gpus = null,
        int? rawGpuCount = null,
        long? timestampTicks = null)
    {
        gpus ??= [];
        byte[] buffer = new byte[ShmLayout.LegacyMapSize];
        WriteInt32(buffer, ShmLayout.V1OffMagic, ShmLayout.MagicValue);
        WriteInt32(buffer, ShmLayout.V1OffCounter, counter);
        WriteDouble(buffer, ShmLayout.V1OffCpuTemp, cpuTemperature);
        WriteString(buffer, ShmLayout.V1OffSource, 32, source);
        WriteInt32(buffer, ShmLayout.V1OffGpuCount, rawGpuCount ?? gpus.Count);
        WriteInt64(
            buffer,
            ShmLayout.V1OffTimestamp,
            timestampTicks ?? DateTime.UtcNow.Ticks);
        WriteGpus(buffer, ShmLayout.V1OffGpuBase, gpus);
        return buffer;
    }

    private static long TimestampAgo(TimeSpan elapsed) =>
        Stopwatch.GetTimestamp() - (long)(Stopwatch.Frequency * elapsed.TotalSeconds);

    private static void WriteGpus(
        byte[] buffer,
        int baseOffset,
        IReadOnlyList<TestGpu> gpus)
    {
        for (int i = 0; i < Math.Min(gpus.Count, ShmLayout.MaxGpus); i++)
        {
            int offset = baseOffset + (i * ShmLayout.GpuEntrySize);
            TestGpu gpu = gpus[i];
            WriteString(buffer, offset, ShmLayout.GpuNameLen, gpu.Name);
            WriteDouble(buffer, offset + ShmLayout.GpuTempOff, gpu.Temperature);
            WriteDouble(buffer, offset + ShmLayout.GpuUsageOff, gpu.UsagePercent);
            WriteDouble(buffer, offset + ShmLayout.GpuMemUsedOff, gpu.MemoryUsedMB);
            WriteDouble(buffer, offset + ShmLayout.GpuMemTotalOff, gpu.MemoryTotalMB);
        }
    }

    private static void WriteSensors(byte[] buffer, IReadOnlyList<TestSensor> sensors)
    {
        for (int i = 0; i < Math.Min(sensors.Count, ShmLayout.MaxSensors); i++)
        {
            int offset = ShmLayout.OffSensorBase + (i * ShmLayout.SensorEntrySize);
            TestSensor sensor = sensors[i];
            WriteInt32(buffer, offset + ShmLayout.SensorTagOff, sensor.Tag);
            WriteString(buffer, offset + ShmLayout.SensorNameOff, 32, sensor.Name);
            WriteDouble(buffer, offset + ShmLayout.SensorValueOff, sensor.Value);
            WriteString(buffer, offset + ShmLayout.SensorUnitOff, 16, sensor.Unit);
            WriteInt32(buffer, offset + ShmLayout.SensorHardwareOff, sensor.HardwareTag);
        }
    }

    private static void WriteInt32(byte[] buffer, int offset, int value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, sizeof(int)), value);

    private static void WriteInt64(byte[] buffer, int offset, long value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, sizeof(long)), value);

    private static void WriteUInt64(byte[] buffer, int offset, ulong value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, sizeof(ulong)), value);

    private static void WriteDouble(byte[] buffer, int offset, double value) =>
        BitConverter.TryWriteBytes(buffer.AsSpan(offset, sizeof(double)), value);

    private static void WriteString(byte[] buffer, int offset, int length, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        int count = Math.Min(bytes.Length, length - 1);
        bytes.AsSpan(0, count).CopyTo(buffer.AsSpan(offset, count));
    }
}

internal readonly record struct TestGpu(
    string Name,
    double Temperature,
    double UsagePercent,
    double MemoryUsedMB,
    double MemoryTotalMB);

internal readonly record struct TestSensor(
    int Tag,
    string Name,
    double Value,
    string Unit,
    int HardwareTag);
