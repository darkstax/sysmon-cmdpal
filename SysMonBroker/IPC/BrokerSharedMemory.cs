// SysMonBroker/IPC/BrokerSharedMemory.cs
// Broker side: writes sensor data to a named file mapping (v2 layout)
// v2: adds generic sensor array for full LHM data passthrough
//
// P/Invoke version: uses CreateFileMapping/MapViewOfFile directly instead of
// managed MemoryMappedFile API. The managed API's persistent view locked the
// plugin reader with "file in use" errors. Native API gives full control.
//
// Memory ordering contract:
//   Legacy reader: counter is still written after every data field.
//   Extended reader: reads the commit sequence before/after copying; odd or
//   changed values mean the snapshot was in flight and must be retried.
//
// ACL: D:P(A;;GR;;;BU)(A;;GA;;;BA)(A;;GA;;;SY) — Users read, Admins/System full control
// SensorEntry.HardwareTag packs hardware type in the low byte and same-type
// instance index in the upper bits, so multiple GPUs of the same vendor stay distinct.
//
// Compatible v2 extension (the legacy layout and map size stay unchanged):
//   12..15     int32 commit sequence (odd = writing, even = committed)
//   16364..67  int32 extension magic/version
//   16368..75  uint64 Broker instance ID
//   16376..83  int64 monotonic publish time (Environment.TickCount64 milliseconds)

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SysMonBroker.IPC;

/// <summary>Generic sensor entry for shared memory. HardwareTag = type | (instance << 8).</summary>
public sealed record SensorEntry(int Tag, string Name, double Value, string Unit, int HardwareTag);

public sealed class BrokerAlreadyRunningException : InvalidOperationException
{
    public BrokerAlreadyRunningException()
        : base("Another SysMonBroker already owns the shared-memory writer lease.")
    {
    }
}

public sealed class BrokerWriterConflictException : InvalidOperationException
{
    public BrokerWriterConflictException()
        : base("Shared memory was modified by another Broker writer.")
    {
    }
}

public sealed class BrokerSharedMemory : IDisposable
{
    public const string MapName = @"Global\SysMonBrokerShm";
    public const string EventName = @"Global\SysMonBrokerEvent";
    public const string WriterMutexName = @"Global\SysMonBrokerWriter";
    public const int MagicValue = 0x5342524B;
    public const int MapVersion = 2;
    public const int MapSize = 16384;
    public const int MaxGpus = 4;
    public const int MaxSensors = 250;

    // v2 offsets
    private const int OffMagic = 0;
    private const int OffVersion = 4;
    private const int OffCounter = 8;
    private const int OffCommitSequence = 12;
    private const int OffCpuTemp = 16;
    private const int OffSource = 24;
    private const int OffGpuCount = 56;
    private const int OffGpuBase = 60;
    private const int OffTimestamp = 348;
    private const int OffSensorCount = 360;
    private const int OffSensorBase = 364;

    // v2 compatible extension in the 20-byte tail after SensorEntry[250].
    private const int OffExtensionMagic = 16364;
    private const int OffInstanceId = 16368;
    private const int OffMonotonicPublishMs = 16376;
    private const int ExtensionMagicValue = 0x31584D53; // "SMX1"

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

    private const string Sddl = "D:P(A;;GR;;;BU)(A;;GA;;;BA)(A;;GA;;;SY)";
    private const int ERROR_ALREADY_EXISTS = 183;

    private IntPtr _hWriterMutex = IntPtr.Zero;
    private bool _ownsWriterMutex;
    private IntPtr _hMap = IntPtr.Zero;
    private IntPtr _pView = IntPtr.Zero;
    private EventWaitHandle? _event;
    private int _counter;
    private int _commitSequence;
    private long _lastUtcTimestamp;
    private long _lastMonotonicPublishMs;
    private bool _disposed;

    public ulong InstanceId { get; } = CreateInstanceId();

    // ---- P/Invoke ----

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMapping(IntPtr hFile,
        ref SECURITY_ATTRIBUTES lpFileMappingAttributes, uint flProtect,
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateMutex(ref SECURITY_ATTRIBUTES lpMutexAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialOwner, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutex(IntPtr hMutex);

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
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_ABANDONED = 0x00000080;
    private const uint WAIT_TIMEOUT = 0x00000102;
    private const uint WAIT_FAILED = 0xFFFFFFFF;

    private const uint SE_KERNEL_OBJECT = 6;
    private const uint DACL_SECURITY_INFORMATION = 4;
    private const uint SECURITY_DESCRIPTOR_REVISION = 1;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public BrokerSharedMemory()
    {
        try
        {
            AcquireWriterMutex();

            bool alreadyExists;
            IntPtr sd = IntPtr.Zero;
            try
            {
                var sa = CreateSecurityAttributes(out sd);
                _hMap = CreateFileMapping(InvalidHandleValue, ref sa, PAGE_READWRITE,
                    0, MapSize, MapName);
                int lastError = Marshal.GetLastWin32Error();
                if (_hMap == IntPtr.Zero)
                    throw new System.ComponentModel.Win32Exception(lastError);
                alreadyExists = lastError == ERROR_ALREADY_EXISTS;
            }
            finally
            {
                if (sd != IntPtr.Zero) LocalFree(sd);
            }

            if (alreadyExists)
                ApplySecurityDescriptor(throwOnFailure: true);

            _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);

            // Map a write view — persistent for the lifetime of this object.
            _pView = MapViewOfFile(_hMap, FILE_MAP_WRITE, 0, 0, (nuint)MapSize);
            if (_pView == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception();

            InitializeHeader();
        }
        catch
        {
            InvalidateExtension();
            _event?.Dispose();
            _event = null;
            ReleaseNativeResources();
            throw;
        }
    }

    private void AcquireWriterMutex()
    {
        IntPtr sd = IntPtr.Zero;
        try
        {
            var sa = CreateSecurityAttributes(out sd);
            _hWriterMutex = CreateMutex(ref sa, bInitialOwner: true, WriterMutexName);
            int lastError = Marshal.GetLastWin32Error();
            if (_hWriterMutex == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(lastError);

            if (lastError != ERROR_ALREADY_EXISTS)
            {
                _ownsWriterMutex = true;
                return;
            }

            uint waitResult = WaitForSingleObject(_hWriterMutex, 0);
            if (waitResult is WAIT_OBJECT_0 or WAIT_ABANDONED)
            {
                _ownsWriterMutex = true;
                return;
            }

            if (waitResult == WAIT_TIMEOUT)
                throw new BrokerAlreadyRunningException();
            if (waitResult == WAIT_FAILED)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            throw new InvalidOperationException($"Unexpected writer mutex wait result: 0x{waitResult:X8}");
        }
        finally
        {
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    private static SECURITY_ATTRIBUTES CreateSecurityAttributes(out IntPtr securityDescriptor)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
            Sddl, SECURITY_DESCRIPTOR_REVISION, out securityDescriptor, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        return new SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = securityDescriptor,
            bInheritHandle = false,
        };
    }

    private unsafe void InitializeHeader()
    {
        byte* p = (byte*)_pView;

        bool validLegacyHeader = *(int*)(p + OffMagic) == MagicValue &&
            *(int*)(p + OffVersion) >= 1;
        int previousCounter = validLegacyHeader ? *(int*)(p + OffCounter) : 0;
        long previousUtcTimestamp = validLegacyHeader ? *(long*)(p + OffTimestamp) : 0;

        bool validExtension = *(int*)(p + OffExtensionMagic) == ExtensionMagicValue;
        _commitSequence = validExtension
            ? Volatile.Read(ref *(int*)(p + OffCommitSequence)) & ~1
            : 0;
        // Initializing a new Broker instance is not a sensor-data commit. Keep
        // the legacy counter unchanged so older readers wait for the first
        // real Write() instead of publishing the empty initialization payload.
        _counter = previousCounter;
        _lastUtcTimestamp = NextUtcTimestamp(previousUtcTimestamp);
        _lastMonotonicPublishMs = NextMonotonicPublishMs(0);

        BeginCommit(p);
        *(int*)(p + OffMagic) = MagicValue;
        *(int*)(p + OffVersion) = MapVersion;
        *(double*)(p + OffCpuTemp) = -1;
        WriteString(p + OffSource, "None", 32);
        *(int*)(p + OffGpuCount) = 0;
        *(long*)(p + OffTimestamp) = _lastUtcTimestamp;
        *(int*)(p + OffSensorCount) = 0;
        WriteExtension(p);
        CompleteCommit(p);

        _event?.Set();
    }

    private void ApplySecurityDescriptor(bool throwOnFailure)
    {
        IntPtr sd = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                Sddl, SECURITY_DESCRIPTOR_REVISION, out sd, IntPtr.Zero))
            {
                if (throwOnFailure) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return;
            }

            if (!GetSecurityDescriptorDacl(sd, out bool daclPresent, out IntPtr dacl, out _))
            {
                if (throwOnFailure) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return;
            }
            if (!daclPresent) return;

            uint result = SetSecurityInfo(_hMap, SE_KERNEL_OBJECT, DACL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
            if (result != 0 && throwOnFailure)
                throw new System.ComponentModel.Win32Exception((int)result);
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

        EnsureWriterOwnership(p);
        BeginCommit(p);

        _counter = NextCounter(_counter);
        _lastUtcTimestamp = NextUtcTimestamp(_lastUtcTimestamp);
        _lastMonotonicPublishMs = NextMonotonicPublishMs(_lastMonotonicPublishMs);

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

        *(long*)(p + OffTimestamp) = _lastUtcTimestamp;

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

        WriteExtension(p);
        CompleteCommit(p);

        _event?.Set();
    }

    private unsafe void EnsureWriterOwnership(byte* p)
    {
        int commitSequence = Volatile.Read(ref *(int*)(p + OffCommitSequence));
        Thread.MemoryBarrier();

        bool ownsSnapshot = commitSequence == _commitSequence &&
            *(int*)(p + OffExtensionMagic) == ExtensionMagicValue &&
            *(ulong*)(p + OffInstanceId) == InstanceId &&
            *(long*)(p + OffMonotonicPublishMs) == _lastMonotonicPublishMs &&
            *(int*)(p + OffCounter) == _counter &&
            *(long*)(p + OffTimestamp) == _lastUtcTimestamp;

        Thread.MemoryBarrier();
        if (!ownsSnapshot || Volatile.Read(ref *(int*)(p + OffCommitSequence)) != commitSequence)
            throw new BrokerWriterConflictException();
    }

    private unsafe void BeginCommit(byte* p)
    {
        int nextOdd = unchecked((_commitSequence & ~1) + 1);
        _commitSequence = nextOdd;
        Volatile.Write(ref *(int*)(p + OffCommitSequence), nextOdd);
        Thread.MemoryBarrier();
    }

    private unsafe void WriteExtension(byte* p)
    {
        *(int*)(p + OffExtensionMagic) = ExtensionMagicValue;
        *(ulong*)(p + OffInstanceId) = InstanceId;
        *(long*)(p + OffMonotonicPublishMs) = _lastMonotonicPublishMs;
    }

    private unsafe void CompleteCommit(byte* p)
    {
        // Keep the legacy counter as the last field visible to v2 readers.
        Thread.MemoryBarrier();
        Volatile.Write(ref *(int*)(p + OffCounter), _counter);

        // Extended readers only accept the snapshot after the sequence is even.
        Thread.MemoryBarrier();
        _commitSequence = unchecked(_commitSequence + 1);
        Volatile.Write(ref *(int*)(p + OffCommitSequence), _commitSequence);
    }

    private static int NextCounter(int counter)
    {
        int next = unchecked(counter + 1);
        return next == 0 ? 1 : next;
    }

    private static long NextUtcTimestamp(long previous)
    {
        long now = DateTime.UtcNow.Ticks;
        return previous >= now && previous < DateTime.MaxValue.Ticks
            ? previous + 1
            : now;
    }

    private static long NextMonotonicPublishMs(long previous)
    {
        long now = Environment.TickCount64;
        return now > previous ? now : unchecked(previous + 1);
    }

    private static ulong CreateInstanceId()
    {
        ulong instanceId = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong)));
        return instanceId == 0 ? 1 : instanceId;
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

        InvalidateExtension();
        _event?.Dispose();
        _event = null;
        ReleaseNativeResources();
    }

    private unsafe void InvalidateExtension()
    {
        if (_pView == IntPtr.Zero) return;

        byte* p = (byte*)_pView;
        int current = Volatile.Read(ref *(int*)(p + OffCommitSequence));
        Thread.MemoryBarrier();

        // A writer conflict means the map may already belong to another Broker.
        // Never invalidate an extension published by a different instance.
        if ((current & 1) != 0 ||
            *(int*)(p + OffExtensionMagic) != ExtensionMagicValue ||
            *(ulong*)(p + OffInstanceId) != InstanceId)
        {
            return;
        }

        Thread.MemoryBarrier();
        if (Volatile.Read(ref *(int*)(p + OffCommitSequence)) != current)
            return;

        int odd = unchecked((current & ~1) + 1);
        Volatile.Write(ref *(int*)(p + OffCommitSequence), odd);
        Thread.MemoryBarrier();
        Volatile.Write(ref *(int*)(p + OffExtensionMagic), 0);
        Thread.MemoryBarrier();
        Volatile.Write(ref *(int*)(p + OffCommitSequence), unchecked(odd + 1));
    }

    private void ReleaseNativeResources()
    {
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
        if (_hWriterMutex != IntPtr.Zero)
        {
            if (_ownsWriterMutex)
            {
                ReleaseMutex(_hWriterMutex);
                _ownsWriterMutex = false;
            }
            CloseHandle(_hWriterMutex);
            _hWriterMutex = IntPtr.Zero;
        }
    }
}
