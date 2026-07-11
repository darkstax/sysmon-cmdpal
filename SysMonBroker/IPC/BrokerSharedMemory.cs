// SysMonBroker/IPC/BrokerSharedMemory.cs
// Broker side: writes sensor data to a named file mapping (v2 layout)
// v2: adds generic sensor array for full LHM data passthrough
//
// P/Invoke version: uses CreateFileMapping/MapViewOfFile directly instead of
// managed MemoryMappedFile API. The managed API's persistent view locked the
// plugin reader with "file in use" errors. Native API gives full control.
//
// Memory ordering contract:
//   Writer: writes all data fields, then MemoryBarrier, then writes counter.
//   Reader: reads counter first to detect changes, then reads data fields.
//
// ACL: D:(A;;GR;;;WD)(A;;GA;;;BA) — Everyone read, Admins full control

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

    private const string Sddl = "D:(A;;GR;;;WD)(A;;GA;;;BA)";

    private IntPtr _hMap = IntPtr.Zero;
    private IntPtr _pView = IntPtr.Zero;
    private readonly EventWaitHandle _event;
    private int _counter;
    private bool _disposed;

    // ---- P/Invoke ----

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMapping(IntPtr hFile,
        IntPtr lpFileMappingAttributes, uint flProtect,
        uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject,
        uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow,
        nuint dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor, uint stringSDRevision,
        out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetSecurityDescriptorDacl(
        IntPtr pSecurityDescriptor, out bool lpbDaclPresent,
        out IntPtr lpbDacl, out bool lpbDaclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(IntPtr handle, uint ObjectType,
        uint SecurityInfo, IntPtr psidOwner, IntPtr psidGroup,
        IntPtr pDacl, IntPtr pSacl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_WRITE = 0x0002;

    private const uint SE_KERNEL_OBJECT = 6;
    private const uint DACL_SECURITY_INFORMATION = 4;

    public BrokerSharedMemory()
    {
        // Create or open the named file mapping (read-write, 16KB).
        _hMap = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, PAGE_READWRITE,
            0, MapSize, MapName);
        if (_hMap == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();

        try
        {
            ApplySecurityDescriptor();

            _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

            // Map a write view — persistent for the lifetime of this object.
            _pView = MapViewOfFile(_hMap, FILE_MAP_WRITE, 0, 0, (nuint)MapSize);
            if (_pView == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception();

            unsafe
            {
                byte* p = (byte*)_pView;
                *(int*)(p + OffMagic) = MagicValue;
                *(int*)(p + OffVersion) = MapVersion;
            }
        }
        catch
        {
            // M7: 构造函数异常时释放已创建的资源
            if (_pView != IntPtr.Zero) { UnmapViewOfFile(_pView); _pView = IntPtr.Zero; }
            if (_hMap != IntPtr.Zero) { CloseHandle(_hMap); _hMap = IntPtr.Zero; }
            _event?.Dispose();
            throw;
        }
    }

    private void ApplySecurityDescriptor()
    {
        IntPtr sd = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                Sddl, 1, out sd, IntPtr.Zero))
                return;

            if (!GetSecurityDescriptorDacl(sd, out bool daclPresent, out IntPtr dacl, out _))
                return;
            if (!daclPresent) return;

            SetSecurityInfo(_hMap, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
        }
        finally
        {
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    public unsafe void Write(double cpuTemp, string source,
        List<Sensors.GpuReading> gpus, List<SensorEntry> sensors)
    {
        if (_disposed || _pView == IntPtr.Zero) return;

        byte* p = (byte*)_pView;

        _counter++;
        *(int*)(p + OffMagic) = MagicValue;
        *(int*)(p + OffVersion) = MapVersion;

        *(double*)(p + OffCpuTemp) = cpuTemp;
        WriteString(p + OffSource, source, 32);

        int gpuCount = Math.Min(gpus.Count, MaxGpus);
        *(int*)(p + OffGpuCount) = gpuCount;
        for (int i = 0; i < gpuCount; i++)
        {
            byte* gpuPtr = p + OffGpuBase + (i * GpuEntrySize);
            WriteString(gpuPtr, gpus[i].Name, GpuNameLen);
            *(double*)(gpuPtr + GpuTempOff) = gpus[i].TempCelsius;
            *(double*)(gpuPtr + GpuUsageOff) = gpus[i].UsagePercent;
            *(double*)(gpuPtr + GpuMemUsedOff) = gpus[i].MemUsedMB;
            *(double*)(gpuPtr + GpuMemTotalOff) = gpus[i].MemTotalMB;
        }

        *(long*)(p + OffTimestamp) = DateTime.UtcNow.Ticks;

        int sensorCount = Math.Min(sensors.Count, MaxSensors);
        *(int*)(p + OffSensorCount) = sensorCount;
        for (int i = 0; i < sensorCount; i++)
        {
            byte* sPtr = p + OffSensorBase + (i * SensorEntrySize);
            var s = sensors[i];
            *(int*)(sPtr + SensorTagOff) = s.Tag;
            WriteString(sPtr + SensorNameOff, s.Name, 32);
            *(double*)(sPtr + SensorValueOff) = s.Value;
            WriteString(sPtr + SensorUnitOff, s.Unit, 16);
            *(int*)(sPtr + SensorHwOff) = s.HardwareTag;
        }

        Thread.MemoryBarrier();
        *(int*)(p + OffCounter) = _counter;

        _event.Set();
    }

    private static unsafe void WriteString(byte* dest, string? value, int maxBytes)
    {
        for (int i = 0; i < maxBytes; i++)
            dest[i] = 0;
        if (string.IsNullOrEmpty(value)) return;
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes - 1)
        {
            for (int i = 0; i < bytes.Length; i++)
                dest[i] = bytes[i];
            return;
        }
        // L2: UTF-8 安全截断 — 回退到最后一个完整字符边界，避免产生无效 UTF-8
        int len = maxBytes - 1;
        // UTF-8 后续字节以 10xxxxxx 开头，回退到非后续字节
        while (len > 0 && (bytes[len] & 0xC0) == 0x80)
            len--;
        System.Diagnostics.Debug.WriteLine(
            $"[SHM] WriteString truncated: input={bytes.Length}B, safe={len}B, value='{value}'");
        for (int i = 0; i < len; i++)
            dest[i] = bytes[i];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_pView != IntPtr.Zero)
        {
            UnmapViewOfFile(_pView);
            _pView = IntPtr.Zero;
        }
        if (_hMap != IntPtr.Zero)
        {
            CloseHandle(_hMap);
            _hMap = IntPtr.Zero;
        }
        _event.Dispose();
    }
}
