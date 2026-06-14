// Copyright (c) 2026 SysMonCmdPal
// SysMonBroker — 高精度硬件数据 Broker 服务
// 以管理员权限运行，通过 PawnIO 驱动读取 SMU/MSR，
// 通过命名管道向 MSIX 应用提供精准温度数据。
//
// 协议:
//   管道名: SysMonCmdPal
//   请求: [byte cmd]  1=AMD Tctl  2=Intel Package Temp  3=CPU Power
//   响应: [int32 status][double value][int32 source]
//     status: 0=OK  1=NotAvailable  2=Timeout
//     source: 1=SMU  2=MSR  0=None

using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SysMonBroker
{

internal static class Program
{
    private const string PipeName = "SysMonCmdPal";
    private static readonly object _lock = new();

    // Cached sensor data
    private static double _amdTctl = -1;
    private static double _intelTemp = -1;
    private static double _cpuPower = -1;
    private static DateTime _lastRefresh = DateTime.MinValue;
    private static bool _isAmd;
    private static bool _isIntel;

    private static PawnIOWrapper? _io;
    private static bool _smuReady;
    private static uint _smuTableVersion;
    private static int _tjMax = 100;
    private static int _tctlIndex = -1; // Auto-detected on first read
    private static List<GpuData> _gpuData = new();

    static int Main(string[] args)
    {
        // 全局未捕获异常保护 — 记录崩溃日志再退出
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log($"FATAL: Unhandled exception: {ex?.Message}");
            Log($"FATAL: Stack: {ex?.StackTrace}");
            if (ex?.InnerException != null)
            {
                Log($"FATAL: Inner: {ex.InnerException.Message}");
            }
            // 允许进程正常退出以记录日志
            Thread.Sleep(500);
        };

        Log("SysMonBroker starting...");

        if (!InitPawnIO())
        {
            Log("FATAL: PawnIO initialization failed. Exiting.");
            return 1;
        }

        Log($"Initialized: AMD={_isAmd} Intel={_isIntel} SMU={_smuReady}");

        // Background refresh thread — updates PM table every 2 seconds
        var refreshThread = new Thread(RefreshLoop)
        {
            IsBackground = true,
            Name = "SensorRefresh"
        };
        refreshThread.Start();

        // Main loop — serve named pipe requests
        Log($"Listening on pipe: {PipeName}");
        while (true)
        {
            try
            {
                var pipe = CreateOpenPipe(PipeName);
                if (pipe == null)
                {
                    Log("CreateOpenPipe returned null, retrying in 1s");
                    Thread.Sleep(1000);
                    continue;
                }

                pipe.WaitForConnection();
                ThreadPool.QueueUserWorkItem(_ => HandleClient(pipe));
            }
            catch (Exception ex)
            {
                Log($"Pipe accept error: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    private static void HandleClient(NamedPipeServerStream pipe)
    {
        try
        {
            var buf = new byte[1];
            if (pipe.Read(buf, 0, 1) != 1) return;

            byte cmd = buf[0];
            double value = -1;
            int status = 1; // NotAvailable
            int source = 0;

            lock (_lock)
            {
                switch (cmd)
                {
                    case 1: // AMD Tctl/Tdie
                        if (_isAmd && _amdTctl > 0)
                        {
                            value = _amdTctl;
                            status = 0;
                            source = 1;
                        }
                        break;
                    case 2: // Intel Package Temp
                        if (_isIntel && _intelTemp > 0)
                        {
                            value = _intelTemp;
                            status = 0;
                            source = 2;
                        }
                        break;
                    case 3: // CPU Power (best effort)
                        if (_cpuPower > 0)
                        {
                            value = _cpuPower;
                            status = 0;
                            source = _isAmd ? 1 : 2;
                        }
                        break;
                    case 4: // GPU data — write variable-length blob then return
                        lock (_lock)
                        {
                            using var ms = new MemoryStream();
                            using var bw = new BinaryWriter(ms);
                            bw.Write(_gpuData.Count);
                            foreach (var gpu in _gpuData)
                            {
                                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(gpu.Name + "\0");
                                bw.Write(nameBytes.Length);
                                bw.Write(nameBytes);
                                bw.Write(gpu.Temperature);
                                bw.Write(gpu.UsagePercent);
                                bw.Write(gpu.MemoryUsedMB);
                                bw.Write(gpu.MemoryTotalMB);
                            }
                            bw.Flush();
                            var blob = ms.ToArray();
                            pipe.Write(blob, 0, blob.Length);
                            pipe.Flush();
                        }
                        return; // Skip standard 16-byte response
                }
            }

            // Standard response for cmd 1-3: [int32 status][double value][int32 source]
            var response = new byte[16];
            BitConverter.TryWriteBytes(response.AsSpan(0, 4), status);
            BitConverter.TryWriteBytes(response.AsSpan(4, 8), value);
            BitConverter.TryWriteBytes(response.AsSpan(12, 4), source);
            pipe.Write(response, 0, 16);
            pipe.Flush();
        }
        catch (Exception ex)
        {
            Log($"Client error: {ex.Message}");
        }
        finally
        {
            try { pipe.Disconnect(); } catch { }
            try { pipe.Dispose(); } catch { }
        }
    }

    private static void RefreshLoop()
    {
        while (true)
        {
            try
            {
                lock (_lock)
                {
                    if (_isAmd && _smuReady)
                    {
                        double t = ReadAmdTctl();
                        if (t > 0) _amdTctl = t;

                        double p = ReadAmdPower();
                        if (p > 0) _cpuPower = p;
                    }
                    else if (_isIntel && _io != null)
                    {
                        double t = ReadIntelTemp();
                        if (t > 0) _intelTemp = t;
                    }
                    _lastRefresh = DateTime.UtcNow;
                }

                // GPU refresh (independent of CPU)
                try
                {
                    var raw = GpuReader.ReadAllGpus();
                    lock (_lock)
                    {
                        _gpuData = GpuReader.FilterActiveGpus(raw);
                    }
                }
                catch (Exception ex)
                {
                    Log($"GPU refresh error: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"Refresh error: {ex.Message}");
            }

            // COM 推送到 Plugin
            PushToPlugin();

            Thread.Sleep(2000);
        }
    }

    // ==================== COM Push to Plugin ====================

    private static PushClient? _pushClient;
    private static bool _pushConnected;

    private static void PushToPlugin()
    {
        try
        {
            _pushClient ??= new PushClient();

            if (!_pushConnected)
                _pushConnected = _pushClient.Connect();

            if (!_pushConnected) return;

            // 推送 CPU 温度
            lock (_lock)
            {
                double cpuTemp = _amdTctl > 0 ? _amdTctl : _intelTemp;
                string cpuSource = _amdTctl > 0 ? "Broker_SMU" : (_intelTemp > 0 ? "Broker_MSR" : "");
                if (cpuTemp > 0)
                    _pushClient.PushCpuTemp(cpuTemp, cpuSource);
            }

            // 推送 GPU 数据
            lock (_lock)
            {
                for (int i = 0; i < _gpuData.Count; i++)
                {
                    var g = _gpuData[i];
                    _pushClient.PushGpuData(i, g.Name, g.Temperature,
                        g.UsagePercent, g.MemoryUsedMB, g.MemoryTotalMB);
                }
            }

            // 心跳
            _pushClient.Ping();
        }
        catch (Exception ex)
        {
            Log($"PushToPlugin error: {ex.Message}");
            _pushConnected = false;
        }
    }

    // ==================== Named Pipe with Open Security ====================

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafePipeHandle CreateNamedPipe(
        string lpName, uint dwOpenMode, uint dwPipeMode,
        uint nMaxInstances, uint nOutBufferSize, uint nInBufferSize,
        uint nDefaultTimeOut, IntPtr lpSecurityAttributes);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string StringSecurityDescriptor, uint StringSDRevision,
        out IntPtr SecurityDescriptor, out UIntPtr Size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_READMODE_BYTE = 0x00000000;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint PIPE_UNLIMITED_INSTANCES = 255;

    /// <summary>
    /// Creates a named pipe with an "Everyone" security descriptor so that
    /// AppContainer-sandboxed MSIX apps can connect.
    /// </summary>
    private static NamedPipeServerStream? CreateOpenPipe(string name)
    {
        // SDDL: DACL allows Generic All to Everyone (WD)
        const string sddl = "D:(A;;GA;;;WD)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                sddl, 1, out IntPtr sd, out _))
        {
            Log($"ConvertSDDL failed: {Marshal.GetLastWin32Error()}");
            return null;
        }

        var saPin = GCHandle.Alloc(
            new byte[Marshal.SizeOf<SECURITY_ATTRIBUTES>()], GCHandleType.Pinned);
        try
        {
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = false,
            };
            Marshal.StructureToPtr(sa, saPin.AddrOfPinnedObject(), false);

            var handle = CreateNamedPipe(
                $@"\\.\pipe\{name}",
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                PIPE_UNLIMITED_INSTANCES,
                4096, 4096, 0,
                saPin.AddrOfPinnedObject());

            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                Log($"CreateNamedPipe failed: error {err}");
                return null;
            }

            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        }
        finally
        {
            saPin.Free();
            LocalFree(sd);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    // ==================== PawnIO Init ====================

    private static bool InitPawnIO()
    {
        try
        {
            _io = new PawnIOWrapper();
            var result = _io.Connect();
            if (result != PawnIOWrapper.ConnectResult.OK)
            {
                Log($"PawnIO Connect: {result} (error {(int)result})");
                return false;
            }

            // Detect CPU type and load appropriate module
            bool isAmdCpu = IsAmdCpu();

            if (isAmdCpu)
            {
                var data = LoadEmbeddedResource("RyzenSMU.bin");
                if (data == null || !_io.LoadModule(data))
                {
                    Log("LoadModule RyzenSMU.bin failed");
                    return false;
                }

                ulong[] ver = new ulong[1];
                if (!_io.Execute("ioctl_get_smu_version", null, ver))
                {
                    Log("ioctl_get_smu_version failed");
                    return false;
                }

                _smuTableVersion = (uint)ver[0];
                _isAmd = true;
                _smuReady = true;
                Log($"AMD SMU ready, version=0x{_smuTableVersion:X8}");
            }
            else
            {
                var data = LoadEmbeddedResource("IntelMSR.bin");
                if (data == null || !_io.LoadModule(data))
                {
                    Log("LoadModule IntelMSR.bin failed");
                    return false;
                }

                // Read TjMax
                if (ReadMsr(0x1A2, out ulong target))
                {
                    int tj = (int)((target >> 24) & 0x7F);
                    if (tj > 0 && tj < 150) _tjMax = tj;
                }

                _isIntel = true;
                Log($"Intel MSR ready, TjMax={_tjMax}°C");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log($"InitPawnIO exception: {ex.Message}");
            return false;
        }
    }

    // ==================== AMD SMU ====================

    private static double ReadAmdTctl()
    {
        if (_io == null || !_smuReady) return -1;
        try
        {
            ulong[] resolveOut = new ulong[2];
            if (!_io.Execute("ioctl_resolve_pm_table", null, resolveOut)) return -1;
            uint tableVer = (uint)resolveOut[0];

            // Update PM table twice to get fresh values
            _io.Execute("ioctl_update_pm_table", null, null);
            Thread.Sleep(100);
            if (!_io.Execute("ioctl_update_pm_table", null, null)) return -1;
            Thread.Sleep(200);

            ulong[] words = new ulong[64];
            if (!_io.Execute("ioctl_read_pm_table", null, words)) return -1;

            ReadOnlySpan<float> floats = MemoryMarshal.Cast<ulong, float>(words);

            // Auto-detect Tctl index on first read
            if (_tctlIndex < 0)
            {
                _tctlIndex = DetectTctlIndex(floats, tableVer);
                Log($"Auto-detected Tctl index: {_tctlIndex} (SMU v0x{tableVer:X8})");

                // Dump PM table for diagnosis
                DumpPmTable(floats);
            }

            if (_tctlIndex < 0 || floats.Length <= _tctlIndex) return -1;

            double temp = floats[_tctlIndex];
            return temp > 0 && temp < 150 ? temp : -1;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Auto-detect the Tctl/Tdie index in the PM table by measuring which
    /// entry has the largest fluctuation between two reads.
    /// Real temperature fluctuates; temperature walls (TjMax=95°C) are constant.
    /// Falls back to known index table for known SMU versions.
    /// </summary>
    private static int DetectTctlIndex(ReadOnlySpan<float> floats, uint tableVer)
    {
        // First, try known index table
        int knownIdx = GetKnownTctlIndex(tableVer);
        if (knownIdx >= 0)
        {
            float v = floats[knownIdx];
            if (v > 20 && v < 90)
                return knownIdx;
        }

        // Second read to measure fluctuation
        Thread.Sleep(300);
        if (!_io!.Execute("ioctl_update_pm_table", null, null)) return knownIdx >= 0 ? knownIdx : 16;
        Thread.Sleep(200);
        ulong[] words2 = new ulong[64];
        if (!_io.Execute("ioctl_read_pm_table", null, words2)) return knownIdx >= 0 ? knownIdx : 16;
        ReadOnlySpan<float> floats2 = MemoryMarshal.Cast<ulong, float>(words2);

        // Find index with largest fluctuation in 30-90°C range (CPU idle >30°C, TjMax >=90°C)
        int bestIdx = 16; // fallback
        float bestDelta = 0;
        for (int i = 0; i < floats.Length && i < floats2.Length; i++)
        {
            if (floats[i] <= 30 || floats[i] >= 90) continue; // skip non-temperature fields and TjMax
            float delta = Math.Abs(floats[i] - floats2[i]);
            if (delta > bestDelta)
            {
                bestDelta = delta;
                bestIdx = i;
            }
        }

        if (bestDelta >= 0.2f)
        {
            Log($"PM table auto-scan: best_idx={bestIdx} delta={bestDelta:F2}°C");
            return bestIdx;
        }

        // No fluctuation found, use known or fallback
        return knownIdx >= 0 ? knownIdx : 16;
    }

    /// <summary>Known Tctl indices for specific SMU table versions</summary>
    private static int GetKnownTctlIndex(uint tableVersion)
    {
        uint hi = tableVersion >> 16;
        return hi switch
        {
            0x1E or 0x64 => 22,
            0x37 or 0x3F or 0x40 or 0x4C or 0x5D or 0x65 => 16,
            0x54 or 0x62 => 10,
            0x45 => 17, // Dragon Range (Ryzen 9 7945HX) — confirmed via PM Table dump
            _ => -1, // unknown — use auto-detect
        };
    }

    /// <summary>Dump all PM table float values for diagnosis</summary>
    private static void DumpPmTable(ReadOnlySpan<float> floats)
    {
        Log("=== PM Table dump ===");
        for (int i = 0; i < Math.Min(floats.Length, 64); i++)
        {
            if (floats[i] != 0)
                Log($"  [{i,2}] = {floats[i],8:F1}");
        }
        Log("=== End PM Table dump ===");
    }

    private static double ReadAmdPower()
    {
        if (_io == null || !_smuReady) return -1;
        try
        {
            ulong[] resolveOut = new ulong[2];
            if (!_io.Execute("ioctl_resolve_pm_table", null, resolveOut)) return -1;
            uint tableVer = (uint)resolveOut[0];

            ulong[] words = new ulong[64];
            if (!_io.Execute("ioctl_read_pm_table", null, words)) return -1;

            ReadOnlySpan<float> floats = MemoryMarshal.Cast<ulong, float>(words);
            // SPL (Sustainable Power Limit) is always at index 0
            return floats.Length > 0 && floats[0] > 0 ? floats[0] : -1;
        }
        catch { return -1; }
    }

    // ==================== Intel MSR ====================

    private static double ReadIntelTemp()
    {
        if (_io == null) return -1;
        try
        {
            if (!ReadMsr(0x19C, out ulong status)) return -1;
            int readout = (int)((status >> 16) & 0x7F);
            if (readout == 0) return -1;
            return _tjMax - readout;
        }
        catch { return -1; }
    }

    private static bool ReadMsr(uint msr, out ulong value)
    {
        value = 0;
        if (_io == null) return false;
        var output = new ulong[1];
        if (!_io.Execute("ioctl_read_msr", new ulong[] { msr }, output)) return false;
        value = output[0];
        return true;
    }

    // ==================== Helpers ====================

    private static bool IsAmdCpu()
    {
        try
        {
            // Check processor vendor via environment variable or registry
            var cpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
            return cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static byte[]? LoadEmbeddedResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var fullName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        if (fullName == null) return null;
        using var stream = asm.GetManifestResourceStream(fullName);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static void Log(string msg)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SysMonCmdPal", "broker.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }
} // class Program

    // ==================== PawnIO Wrapper (self-contained) ====================

    public sealed class PawnIOWrapper : IDisposable
    {
        private const int FN_LEN = 32;
        private const uint DEV_TYPE = 41394u << 16;

        private enum Ctl : uint
        {
            Load = DEV_TYPE | (0x821 << 2),
            Execute = DEV_TYPE | (0x841 << 2),
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFile(string n, uint acc, uint share,
            IntPtr sec, uint disp, uint fl, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr dev, Ctl code,
            byte[] inB, uint inSz, byte[] outB, uint outSz, out uint ret, IntPtr ovl);

        [DllImport("kernel32.dll", CallingConvention = CallingConvention.Winapi)]
        static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle dev, Ctl code,
            [In] byte[] inB, uint inSz, [Out] byte[] outB, uint outSz,
            out uint ret, IntPtr ovl);

        private IntPtr _raw = IntPtr.Zero;
        private Microsoft.Win32.SafeHandles.SafeFileHandle? _safe;
        private bool _loaded, _disposed;

        public bool IsConnected => _raw != IntPtr.Zero && _raw.ToInt64() != -1;
        public bool IsModuleLoaded => _loaded && _safe != null && !_safe.IsInvalid;

        public enum ConnectResult { OK, NotInstalled, AccessDenied, OtherError }

        public ConnectResult Connect()
        {
            if (IsConnected) return ConnectResult.OK;
            const string path = @"\\?\GLOBALROOT\Device\PawnIO";
            _raw = CreateFile(path, 0xC0000000u, 0x3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (_raw == IntPtr.Zero || _raw.ToInt64() == -1)
            {
                int err = Marshal.GetLastWin32Error();
                _raw = IntPtr.Zero;
                return err switch
                {
                    2 or 3 => ConnectResult.NotInstalled,
                    5 => ConnectResult.AccessDenied,
                    _ => ConnectResult.OtherError,
                };
            }
            return ConnectResult.OK;
        }

        public bool LoadModule(byte[] data)
        {
            if (!IsConnected || data == null || data.Length == 0) return false;
            bool ok = DeviceIoControl(_raw, Ctl.Load, data, (uint)data.Length, null!, 0, out _, IntPtr.Zero);
            if (!ok) return false;
            _safe = new Microsoft.Win32.SafeHandles.SafeFileHandle(_raw, ownsHandle: true);
            _raw = IntPtr.Zero;
            _loaded = true;
            return true;
        }

        public bool Execute(string functionName, ulong[]? input, ulong[]? output)
        {
            if (!IsModuleLoaded) return false;
            byte[] nameBytes = System.Text.Encoding.ASCII.GetBytes(functionName);
            int inputCount = input?.Length ?? 0;
            byte[] buffer = new byte[FN_LEN + inputCount * 8];
            Buffer.BlockCopy(nameBytes, 0, buffer, 0, Math.Min(nameBytes.Length, FN_LEN - 1));
            if (input != null && inputCount > 0)
            {
                byte[] inputBytes = new byte[inputCount * 8];
                Buffer.BlockCopy(input, 0, inputBytes, 0, inputBytes.Length);
                Buffer.BlockCopy(inputBytes, 0, buffer, FN_LEN, inputBytes.Length);
            }
            int outputCount = output?.Length ?? 0;
            byte[] outputBuffer = outputCount > 0 ? new byte[outputCount * 8] : null!;
            bool ok = DeviceIoControl(_safe!, Ctl.Execute, buffer, (uint)buffer.Length,
                                      outputBuffer!, (uint)(outputBuffer?.Length ?? 0), out uint bytesReturned, IntPtr.Zero);
            if (ok && output != null && outputBuffer != null && bytesReturned > 0)
                Buffer.BlockCopy(outputBuffer, 0, output, 0, (int)Math.Min(bytesReturned, (uint)(outputCount * 8)));
            return ok;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _loaded = false;
            _safe?.Close();
            if (_raw != IntPtr.Zero && _raw.ToInt64() != -1) CloseHandle(_raw);
            _disposed = true;
        }
    }
}