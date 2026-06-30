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

    // ---- P/Invoke for shared memory (FILE_MAP_COPY, no write) ----

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenFileMapping(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, nuint dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint FILE_MAP_COPY = 0x0001;

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
            IntPtr hMap = IntPtr.Zero;
            IntPtr pView = IntPtr.Zero;
            try
            {
                // Open the named file mapping (read-only access).
                hMap = OpenFileMapping(FILE_MAP_COPY, false, MapName);
                if (hMap == IntPtr.Zero)
                {
                    // Broker not running — retry later.
                    Thread.Sleep(5000);
                    continue;
                }

                // Map a COPY view. FILE_MAP_COPY creates a private copy-on-write
                // view that NEVER blocks the Broker writer. The OS pages in a
                // snapshot of the current data; Broker can keep writing freely.
                pView = MapViewOfFile(hMap, FILE_MAP_COPY, 0, 0, (nuint)MapSize);
                if (pView == IntPtr.Zero)
                {
                    Thread.Sleep(5000);
                    continue;
                }

                unsafe
                {
                    ParseSnapshot((byte*)pView);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ShmReader] ReaderLoop exception: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                // Unmap and close handle EVERY cycle — never hold a persistent view.
                if (pView != IntPtr.Zero)
                    UnmapViewOfFile(pView);
                if (hMap != IntPtr.Zero)
                    CloseHandle(hMap);
            }

            Thread.Sleep(2000);
        }
    }

    private unsafe void ParseSnapshot(byte* p)
    {
        int magic = *(int*)(p + OffMagic);
        if (magic != MagicValue)
            return;

        int counter = *(int*)(p + OffCounter);
        if (counter == _lastCounter)
        {
            // No new data, but still ping so IsAlive stays true.
            BrokerPushReceiver.Instance.Ping();
            return;
        }
        _lastCounter = counter;

        int version = *(int*)(p + OffVersion);

        // Read CPU
        double cpuTemp = *(double*)(p + OffCpuTemp);
        string source = ReadString(p + OffSource, 32);

        if (cpuTemp > 0)
            BrokerPushReceiver.Instance.PushCpuTemp(cpuTemp, source);

        // Read GPUs
        int gpuCount = Math.Min(*(int*)(p + OffGpuCount), MaxGpus);
        for (int i = 0; i < gpuCount; i++)
        {
            byte* gpuPtr = p + OffGpuBase + (i * GpuEntrySize);
            string name = ReadString(gpuPtr, GpuNameLen);
            double temp = *(double*)(gpuPtr + GpuTempOff);
            double usage = *(double*)(gpuPtr + GpuUsageOff);
            double memUsed = *(double*)(gpuPtr + GpuMemUsedOff);
            double memTotal = *(double*)(gpuPtr + GpuMemTotalOff);

            BrokerPushReceiver.Instance.PushGpuData(
                i, name, temp, usage, memUsed, memTotal);
        }

        // Read generic sensors (v2)
        if (version >= 2)
        {
            int sensorCount = Math.Min(*(int*)(p + OffSensorCount), MaxSensors);
            var sensors = new List<BrokerSensorEntry>(sensorCount);

            for (int i = 0; i < sensorCount; i++)
            {
                byte* sPtr = p + OffSensorBase + (i * SensorEntrySize);
                int tag = *(int*)(sPtr + SensorTagOff);
                string name = ReadString(sPtr + SensorNameOff, 32);
                double value = *(double*)(sPtr + SensorValueOff);
                string unit = ReadString(sPtr + SensorUnitOff, 16);
                int hwTag = *(int*)(sPtr + SensorHwOff);

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

    private static unsafe string ReadString(byte* src, int maxBytes)
    {
        int len = 0;
        while (len < maxBytes && src[len] != 0)
            len++;
        return len > 0 ? Encoding.UTF8.GetString(src, len) : "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        try { _readerThread.Join(3000); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ShmReader] Join timeout: {ex.Message}"); }
    }
}
