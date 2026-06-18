// SysMonBroker/IPC/BrokerSharedMemory.cs
// Broker side: writes sensor data to a named MemoryMappedFile (v2 layout)
// v2: adds generic sensor array for full LHM data passthrough
//
// Memory ordering contract:
//   Writer: writes all data fields, then Thread.MemoryBarrier(), then writes counter.
//   Reader: reads counter first to detect changes, then reads data fields.
//
// v2.2: Shared memory ACL via P/Invoke (Everyone read, Admin write)
//   SDDL: D:(A;;GR;;;WD)(A;;GA;;;BA)

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SysMonBroker.IPC;

/// <summary>Generic sensor entry for shared memory</summary>
public sealed record SensorEntry(int Tag, string Name, double Value, string Unit, int HardwareTag);

public sealed class BrokerSharedMemory : IDisposable
{
    public const string MapName = "SysMonBrokerShm";
    public const string EventName = "SysMonBrokerEvent";
    public const int MagicValue = 0x5342524B;
    public const int MapVersion = 2;
    public const int MapSize = 16384;
    public const int MaxGpus = 4;
    public const int MaxSensors = 128;

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

    // SDDL: Everyone read, Administrators full control
    // v2.2: 通过 SetSecurityInfo 在打开后设置 DACL（兼容已有 MMF 对象）
    private const string Sddl = "D:(A;;GR;;;WD)(A;;GA;;;BA)";

    private readonly MemoryMappedFile _mmf;
    private readonly EventWaitHandle _event;
    private int _counter;
    private bool _disposed;

    // ---- P/Invoke for security ----

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor,
        uint stringSDRevision,
        out IntPtr securityDescriptor,
        IntPtr securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr pSecurityDescriptor,
        out bool lpbDaclPresent,
        out IntPtr lpbDacl,
        out bool lpbDaclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(
        IntPtr handle,
        uint ObjectType,
        uint SecurityInfo,
        IntPtr psidOwner,
        IntPtr psidGroup,
        IntPtr pDacl,
        IntPtr pSacl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const uint SE_KERNEL_OBJECT = 6;
    private const uint DACL_SECURITY_INFORMATION = 4;

    public BrokerSharedMemory()
    {
        // v2.2: 先创建/打开 MMF，再通过 SetSecurityInfo 设置 DACL
        // 这样即使 MMF 已存在（旧 Broker 实例残留），也能更新 ACL
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, MapSize, MemoryMappedFileAccess.ReadWrite);

        ApplySecurityDescriptor();

        _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

        using var accessor = _mmf.CreateViewAccessor();
        accessor.Write(OffMagic, MagicValue);
        accessor.Write(OffVersion, MapVersion);
    }

    /// <summary>通过 SetSecurityInfo 将 SDDL DACL 应用到 MMF 内核对象</summary>
    private void ApplySecurityDescriptor()
    {
        IntPtr sd = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                Sddl, 1, out sd, IntPtr.Zero))
                return; // 转换失败，静默降级（保持默认 ACL）

            if (!GetSecurityDescriptorDacl(sd, out bool daclPresent, out IntPtr dacl, out _))
                return;

            if (!daclPresent) return;

            // 获取 MMF 的原生句柄
            IntPtr handle = _mmf.SafeMemoryMappedFileHandle.DangerousGetHandle();

            // 将 DACL 设置到内核对象上
            SetSecurityInfo(handle, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
        }
        finally
        {
            if (sd != IntPtr.Zero)
                LocalFree(sd);
        }
    }

    public unsafe void Write(double cpuTemp, string source, List<Sensors.GpuReading> gpus,
        List<SensorEntry> sensors)
    {
        if (_disposed) return;

        using var accessor = _mmf.CreateViewAccessor();
        byte* ptr = null;
        try
        {
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);

            _counter++;
            *(int*)(ptr + OffMagic) = MagicValue;
            *(int*)(ptr + OffVersion) = MapVersion;

            // CPU
            *(double*)(ptr + OffCpuTemp) = cpuTemp;
            WriteString(ptr + OffSource, source, 32);

            // GPU
            int gpuCount = Math.Min(gpus.Count, MaxGpus);
            *(int*)(ptr + OffGpuCount) = gpuCount;
            for (int i = 0; i < gpuCount; i++)
            {
                byte* gpuPtr = ptr + OffGpuBase + (i * GpuEntrySize);
                WriteString(gpuPtr, gpus[i].Name, GpuNameLen);
                *(double*)(gpuPtr + GpuTempOff) = gpus[i].TempCelsius;
                *(double*)(gpuPtr + GpuUsageOff) = gpus[i].UsagePercent;
                *(double*)(gpuPtr + GpuMemUsedOff) = gpus[i].MemUsedMB;
                *(double*)(gpuPtr + GpuMemTotalOff) = gpus[i].MemTotalMB;
            }

            // Timestamp
            *(long*)(ptr + OffTimestamp) = DateTime.UtcNow.Ticks;

            // Generic sensors (v2)
            int sensorCount = Math.Min(sensors.Count, MaxSensors);
            *(int*)(ptr + OffSensorCount) = sensorCount;
            for (int i = 0; i < sensorCount; i++)
            {
                byte* sPtr = ptr + OffSensorBase + (i * SensorEntrySize);
                var s = sensors[i];
                *(int*)(sPtr + SensorTagOff) = s.Tag;
                WriteString(sPtr + SensorNameOff, s.Name, 32);
                *(double*)(sPtr + SensorValueOff) = s.Value;
                WriteString(sPtr + SensorUnitOff, s.Unit, 16);
                *(int*)(sPtr + SensorHwOff) = s.HardwareTag;
            }

            Thread.MemoryBarrier();
            *(int*)(ptr + OffCounter) = _counter;
        }
        finally
        {
            if (ptr != null)
                accessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }

        _event.Set();
    }

    private static unsafe void WriteString(byte* dest, string? value, int maxBytes)
    {
        for (int i = 0; i < maxBytes; i++)
            dest[i] = 0;
        if (string.IsNullOrEmpty(value)) return;
        var bytes = Encoding.UTF8.GetBytes(value);
        int len = Math.Min(bytes.Length, maxBytes - 1);
        for (int i = 0; i < len; i++)
            dest[i] = bytes[i];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mmf.Dispose();
        _event.Dispose();
    }
}
