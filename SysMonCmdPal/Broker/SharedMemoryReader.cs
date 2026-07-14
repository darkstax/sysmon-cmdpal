// SysMonCmdPal/Broker/SharedMemoryReader.cs
// Plugin side: reads sensor data from Broker's MemoryMappedFile (v2 layout)
// v2: reads full sensor array in addition to CPU/GPU
// Runs on a background thread, updates BrokerPushReceiver singleton
//
// Uses managed MemoryMappedFile API with a persistent read-only view.
//
// This does NOT affect antivirus detection — the HWiNFO P/Invoke removal was
// about the OpenFileMapping→MapViewOfFile data-exfiltration pattern match in
// HwinfoSharedMemoryReader. This reader targets our own Broker SHM, not HWiNFO.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SysMonCmdPal.Broker;

/// <summary>
/// Reads sensor data from Broker's shared memory and feeds it to BrokerPushReceiver.
/// Uses managed MemoryMappedFile with persistent handles to avoid reconnect churn.
/// </summary>
public sealed class SharedMemoryReader : IDisposable
{
    private const string MapName = @"Global\SysMonBrokerShm";

    private const int MagicValue = 0x5342524B;
    private const int MaxGpus = 4;
    private const int MaxSensors = 250;
    private const int MapSize = 16384;

    // v2 offsets
    private const int OffMagic = 0;
    private const int OffVersion = 4;
    private const int OffCounter = 8;
    private const int OffCpuTemp = 16;
    private const int OffSource = 24;
    private const int OffGpuCount = 56;
    private const int OffGpuBase = 60;
    private const int OffTimestamp = 348;
    private const int OffSensorCount = 360;
    private const int OffSensorBase = 364;

    // GPU entry (72 bytes)
    private const int GpuNameLen = 32;
    private const int GpuTempOff = 32;
    private const int GpuUsageOff = 40;
    private const int GpuMemUsedOff = 48;
    private const int GpuMemTotalOff = 56;
    private const int GpuEntrySize = 72;

    // Sensor entry (64 bytes)
    private const int SensorTagOff = 0;
    private const int SensorNameOff = 4;
    private const int SensorValueOff = 36;
    private const int SensorUnitOff = 44;
    private const int SensorHwOff = 60;
    private const int SensorEntrySize = 64;

    private readonly Thread _readerThread;
    private volatile bool _running;
    private int _lastCounter;
    private bool _disposed;

    // ---- 持久句柄 — 避免每次循环 OpenExisting/Dispose 的开销 ----
    // L1: 已移除未使用的 P/Invoke 声明（OpenFileMapping/MapViewOfFile/UnmapViewOfFile/CloseHandle），
    //     实际使用 managed MemoryMappedFile API。

    private System.IO.MemoryMappedFiles.MemoryMappedFile? _mmf;
    private System.IO.MemoryMappedFiles.MemoryMappedViewAccessor? _accessor;
    private readonly byte[] _buffer = new byte[MapSize];
    private static readonly object s_diagnosticsLock = new();
    private static SharedMemoryReaderDiagnostics s_diagnostics = new();

    public static SharedMemoryReaderDiagnostics Diagnostics
    {
        get { lock (s_diagnosticsLock) return s_diagnostics; }
    }

    public SharedMemoryReader()
    {
        _running = true;
        _readerThread = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name = "ShmReader"
        };
        _readerThread.Start();
    }

    private void ReaderLoop()
    {
        while (_running)
        {
            try
            {
                // 持久句柄 — 只在首次或断开后重新连接
                if (_mmf == null || _accessor == null)
                {
                    _mmf = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(
                        MapName, System.IO.MemoryMappedFiles.MemoryMappedFileRights.Read);
                    _accessor = _mmf.CreateViewAccessor(0, MapSize,
                        System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                }

                // 复用 buffer — 只拷贝实际需要的数据
                _accessor.ReadArray(0, _buffer, 0, MapSize);
                ParseSnapshot(_buffer);
            }
            catch (FileNotFoundException)
            {
                // Broker has not created the global map yet; this is a normal
                // unavailable state, not a user-facing read error.
                _accessor?.Dispose();
                _mmf?.Dispose();
                _accessor = null;
                _mmf = null;
                UpdateDiagnostics(connected: false, error: "");
            }
            catch (Exception ex)
            {
                // 句柄失效 — 重置以便下次重连
                _accessor?.Dispose();
                _mmf?.Dispose();
                _accessor = null;
                _mmf = null;
                UpdateDiagnostics(connected: false, error: ex.Message);
            }

            Thread.Sleep(1000);
        }
    }

    private void ParseSnapshot(byte[] buf)
    {
        int magic = BitConverter.ToInt32(buf, OffMagic);
        if (magic != MagicValue)
        {
            UpdateDiagnostics(connected: true, error: "Invalid shared memory magic");
            return;
        }

        int counter = BitConverter.ToInt32(buf, OffCounter);
        if (counter == _lastCounter)
        {
            // M2: counter 未变 = Broker 没有在写。不发 Ping，让 LastPing 自然过期，
            // IsAlive 才能正确反映 Broker 是否存活。
            UpdateDiagnostics(connected: true, counter: counter);
            return;
        }
        _lastCounter = counter;

        int version = BitConverter.ToInt32(buf, OffVersion);

        // Read CPU
        double cpuTemp = BitConverter.ToDouble(buf, OffCpuTemp);
        string source = ReadString(buf, OffSource, 32);

        // Read GPUs
        int gpuCount = ClampCount(BitConverter.ToInt32(buf, OffGpuCount), MaxGpus);
        var gpus = new List<KeyValuePair<int, BrokerGpuSnapshot>>(gpuCount);
        for (int i = 0; i < gpuCount; i++)
        {
            int gpuOff = OffGpuBase + (i * GpuEntrySize);
            string name = ReadString(buf, gpuOff, GpuNameLen);
            double temp = BitConverter.ToDouble(buf, gpuOff + GpuTempOff);
            double usage = BitConverter.ToDouble(buf, gpuOff + GpuUsageOff);
            double memUsed = BitConverter.ToDouble(buf, gpuOff + GpuMemUsedOff);
            double memTotal = BitConverter.ToDouble(buf, gpuOff + GpuMemTotalOff);

            gpus.Add(new KeyValuePair<int, BrokerGpuSnapshot>(i, new BrokerGpuSnapshot
            {
                Name = name,
                Temperature = IsReasonableTemp(temp) ? temp : -1,
                UsagePercent = IsReasonablePercent(usage) ? usage : -1,
                MemoryUsedMB = IsFiniteNonNegative(memUsed) ? memUsed : 0,
                MemoryTotalMB = IsFiniteNonNegative(memTotal) ? memTotal : 0,
                Timestamp = DateTime.UtcNow,
            }));
        }

        IReadOnlyList<BrokerSensorEntry> sensors = Array.Empty<BrokerSensorEntry>();

        // Read generic sensors (v2)
        if (version >= 2)
        {
            int sensorCount = ClampCount(BitConverter.ToInt32(buf, OffSensorCount), MaxSensors);
            var list = new List<BrokerSensorEntry>(sensorCount);

            for (int i = 0; i < sensorCount; i++)
            {
                int sOff = OffSensorBase + (i * SensorEntrySize);
                int tag = BitConverter.ToInt32(buf, sOff + SensorTagOff);
                string name = ReadString(buf, sOff + SensorNameOff, 32);
                double value = BitConverter.ToDouble(buf, sOff + SensorValueOff);
                string unit = ReadString(buf, sOff + SensorUnitOff, 16);
                int hwTag = BitConverter.ToInt32(buf, sOff + SensorHwOff);

                if (double.IsNaN(value) || double.IsInfinity(value))
                    continue;

                list.Add(new BrokerSensorEntry
                {
                    Tag = tag,
                    Name = name,
                    Value = value,
                    Unit = unit,
                    HardwareTag = hwTag,
                });
            }

            sensors = list;
        }

        UpdateDiagnostics(
            connected: true,
            counter: counter,
            version: version,
            sensorCount: sensors.Count,
            error: "");
        BrokerPushReceiver.Instance.PushSnapshot(cpuTemp, source, gpus, sensors);
    }

    private static void UpdateDiagnostics(
        bool connected,
        int? counter = null,
        int? version = null,
        int? sensorCount = null,
        string? error = null)
    {
        lock (s_diagnosticsLock)
        {
            s_diagnostics = s_diagnostics with
            {
                IsConnected = connected,
                LastReadUtc = DateTime.UtcNow,
                LastCounter = counter ?? s_diagnostics.LastCounter,
                LastVersion = version ?? s_diagnostics.LastVersion,
                LastSensorCount = sensorCount ?? s_diagnostics.LastSensorCount,
                LastError = error ?? s_diagnostics.LastError,
            };
        }
    }

    private static int ClampCount(int raw, int max)
    {
        if (raw <= 0) return 0;
        return raw > max ? max : raw;
    }

    private static bool IsFiniteNonNegative(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;

    private static bool IsReasonablePercent(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0 && value <= 100;

    private static bool IsReasonableTemp(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value) && value > 0 && value < 150;

    private static string ReadString(byte[] buf, int offset, int maxBytes)
    {
        int len = 0;
        while (len < maxBytes && buf[offset + len] != 0)
            len++;
        return len > 0 ? Encoding.UTF8.GetString(buf, offset, len) : "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        try { _readerThread.Join(3000); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ShmReader] Join timeout: {ex.Message}"); }
        _accessor?.Dispose();
        _mmf?.Dispose();
    }
}

public sealed record SharedMemoryReaderDiagnostics
{
    public bool IsConnected { get; init; }
    public DateTime LastReadUtc { get; init; } = DateTime.MinValue;
    public int LastCounter { get; init; }
    public int LastVersion { get; init; }
    public int LastSensorCount { get; init; }
    public string LastError { get; init; } = "";
}
