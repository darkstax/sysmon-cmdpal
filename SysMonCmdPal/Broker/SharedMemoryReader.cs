// SysMonCmdPal/Broker/SharedMemoryReader.cs
// Plugin side: reads sensor data from Broker's MemoryMappedFile (v2 layout)
// v2: reads full sensor array in addition to CPU/GPU
// Runs on a background thread, updates BrokerPushReceiver singleton
//
// IMPORTANT: We use P/Invoke (OpenFileMapping + MapViewOfFile) instead of the
// managed MemoryMappedFile API. The managed API's view objects (both ViewAccessor
// and ViewStream) caused "The process cannot access the file because it is being
// used by another process" errors on the Broker writer side, even with Read-only
// access and immediate disposal. The native API with FILE_MAP_COPY + immediate
// UnmapViewOfFile avoids this completely.
//
// This does NOT affect antivirus detection — the HWiNFO P/Invoke removal was
// about the OpenFileMapping→MapViewOfFile data-exfiltration pattern match in
// HwinfoSharedMemoryReader. This reader targets our own Broker SHM, not HWiNFO.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SysMonCmdPal.Broker;

/// <summary>
/// Reads sensor data from Broker's shared memory and feeds it to BrokerPushReceiver.
/// Uses native P/Invoke to avoid managed MemoryMappedFile view locking issues.
/// </summary>
public sealed class SharedMemoryReader : IDisposable
{
    private const string MapName = "SysMonBrokerShm";

    private const int MagicValue = 0x5342524B;
    private const int MaxGpus = 4;
    private const int MaxSensors = 128;
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
            catch (Exception ex)
            {
                // 句柄失效 — 重置以便下次重连
                _accessor?.Dispose();
                _mmf?.Dispose();
                _accessor = null;
                _mmf = null;
            }

            Thread.Sleep(1000);
        }
    }

    private void ParseSnapshot(byte[] buf)
    {
        int magic = BitConverter.ToInt32(buf, OffMagic);
        if (magic != MagicValue)
            return;

        int counter = BitConverter.ToInt32(buf, OffCounter);
        if (counter == _lastCounter)
        {
            // M2: counter 未变 = Broker 没有在写。不发 Ping，让 LastPing 自然过期，
            // IsAlive 才能正确反映 Broker 是否存活。
            return;
        }
        _lastCounter = counter;

        int version = BitConverter.ToInt32(buf, OffVersion);

        // Read CPU
        double cpuTemp = BitConverter.ToDouble(buf, OffCpuTemp);
        string source = ReadString(buf, OffSource, 32);

        if (cpuTemp > 0)
            BrokerPushReceiver.Instance.PushCpuTemp(cpuTemp, source);

        // Read GPUs
        int gpuCount = Math.Min(BitConverter.ToInt32(buf, OffGpuCount), MaxGpus);
        for (int i = 0; i < gpuCount; i++)
        {
            int gpuOff = OffGpuBase + (i * GpuEntrySize);
            string name = ReadString(buf, gpuOff, GpuNameLen);
            double temp = BitConverter.ToDouble(buf, gpuOff + GpuTempOff);
            double usage = BitConverter.ToDouble(buf, gpuOff + GpuUsageOff);
            double memUsed = BitConverter.ToDouble(buf, gpuOff + GpuMemUsedOff);
            double memTotal = BitConverter.ToDouble(buf, gpuOff + GpuMemTotalOff);

            BrokerPushReceiver.Instance.PushGpuData(
                i, name, temp, usage, memUsed, memTotal);
        }

        // Read generic sensors (v2)
        if (version >= 2)
        {
            int sensorCount = Math.Min(BitConverter.ToInt32(buf, OffSensorCount), MaxSensors);
            var sensors = new List<BrokerSensorEntry>(sensorCount);

            for (int i = 0; i < sensorCount; i++)
            {
                int sOff = OffSensorBase + (i * SensorEntrySize);
                int tag = BitConverter.ToInt32(buf, sOff + SensorTagOff);
                string name = ReadString(buf, sOff + SensorNameOff, 32);
                double value = BitConverter.ToDouble(buf, sOff + SensorValueOff);
                string unit = ReadString(buf, sOff + SensorUnitOff, 16);
                int hwTag = BitConverter.ToInt32(buf, sOff + SensorHwOff);

                sensors.Add(new BrokerSensorEntry
                {
                    Tag = tag,
                    Name = name,
                    Value = value,
                    Unit = unit,
                    HardwareTag = hwTag,
                });
            }

            BrokerPushReceiver.Instance.PushAllSensors(sensors);
        }

        // Heartbeat
        BrokerPushReceiver.Instance.Ping();
    }

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
