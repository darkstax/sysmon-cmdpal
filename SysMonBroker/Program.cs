// SysMonBroker — LHM thin-shell + Shared Memory IPC + COM Server (v2.1)
// Runs as admin. Provides:
//   1. SharedMemory v2 → Plugin (MSIX CmdPal extension)
//   2. COM Local Server → btop4win (full process list + sensors, no admin needed)
//   3. JSON snapshot → any consumer
//
// Usage:
//   SysMonBroker.exe                  — normal mode (SHM + COM + JSON)
//   SysMonBroker.exe --com-server     — COM server mode (launched by CoCreateInstance)
//   SysMonBroker.exe --register       — register COM classes then exit
//   SysMonBroker.exe --unregister     — unregister COM classes then exit
//   SysMonBroker.exe --devmode-on     — enable DevMode at runtime (dev build + marker required)
//   SysMonBroker.exe --devmode-off    — disable DevMode at runtime

using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SysMonBroker.COM;
using SysMonBroker.IPC;
using SysMonBroker.Logging;
using SysMonBroker.Sensors;

namespace SysMonBroker;

internal static class Program
{
    // ---- COM class object registration ----
    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        ref Guid rclsid,
        IntPtr pUnk,
        uint dwClsContext, uint flags, out uint dwRegister);
    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private const uint CLSCTX_LOCAL_SERVER = 4;
    private const uint REGCLS_MULTIPLEUSE = 1;
    private const uint COINIT_MULTITHREADED = 0;

    // ---- Sensor state (shared between main loop and COM server) ----
    // Thread safety: writer (main loop) + reader (COM server on threadpool).
    // Double reads/writes are not atomic, but torn reads of a double are benign
    // for sensor data (at worst a transient garbage value that refreshes next cycle).
    // No lock — sensor data is hot path and staleness is acceptable.
    private static double _lastCpuTemp = -1;
    private static volatile string _lastCpuSource = "None";
    private static volatile List<GpuReading> _lastGpus = [];
    private static volatile List<SensorEntry> _lastSensors = [];

    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Log($"FATAL: {ex?.Message}\nFATAL: {ex?.StackTrace}");
            Thread.Sleep(500);
        };

        // ---- Command-line dispatch ----
        if (args.Contains("--register"))
        {
            Log("Registering COM server...");
            BrokerComServer.RegisterComServer();
            Console.WriteLine("COM server registered.");
            return 0;
        }
        if (args.Contains("--unregister"))
        {
            Log("Unregistering COM server...");
            BrokerComServer.UnregisterComServer();
            Console.WriteLine("COM server unregistered.");
            return 0;
        }

        if (args.Contains("--devmode-on") || args.Contains("--devmode-off"))
        {
            return DevModeToggle(args.Contains("--devmode-on"));
        }

        bool isComServer = args.Contains("--com-server");
        Log($"=== SysMonBroker v2.2 starting (mode={(isComServer ? "COM" : "standalone")}) ===");

        // ---- Self-register COM on first run ----
        try { BrokerComServer.RegisterComServer(); }
        catch (Exception ex) { Log($"COM auto-register: {ex.Message}"); }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        SensorCollector? collector = null;
        BrokerSharedMemory? shm = null;
        BrokerComServer? comServer = null;
        uint comCookie = 0;

        try
        {
            if (isComServer)
            {
                Log("COM server mode — reading sensors from SharedMemory");
                Log("Opening SharedMemory (read-only)...");
                try
                {
                    var mmf = MemoryMappedFile.OpenExisting(BrokerSharedMemory.MapName,
                        MemoryMappedFileRights.Read);
                    var eventHandle = EventWaitHandle.OpenExisting(BrokerSharedMemory.EventName);
                    Log($"SharedMemory: {BrokerSharedMemory.MapName} (read-only OK)");

                    var shmThread = new Thread(() =>
                    {
                        byte[] srcBuf = new byte[32];
                        byte[] nameBuf = new byte[32];
                        byte[] sNameBuf = new byte[32];
                        byte[] unitBuf = new byte[16];

                        using var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                        ReadShm(acc, srcBuf, nameBuf, sNameBuf, unitBuf);

                        while (!cts.Token.IsCancellationRequested)
                        {
                            eventHandle.WaitOne(3000);
                            ReadShm(acc, srcBuf, nameBuf, sNameBuf, unitBuf);
                        }
                    })
                    { IsBackground = true, Name = "ShmReader" };
                    shmThread.Start();

                    cts.Token.Register(() => eventHandle.Set());
                }
                catch (Exception ex)
                {
                    Log($"SharedMemory open failed: {ex.Message} — COM server will return stale data");
                }
            }
            else
            {
                // Standalone mode: LHM sensor collection + SHM writer
                Log("Creating SensorCollector (LHM Computer.Open)...");
                collector = new SensorCollector();
                Log($"SensorCollector ready. PawnIO: {(collector.PawnIoInstalled ? "installed" : "not installed — user-mode sensors only")}");

                Log("Creating SharedMemory v2...");
                shm = new BrokerSharedMemory();
                Log($"SharedMemory: {BrokerSharedMemory.MapName} (size={BrokerSharedMemory.MapSize})");
            }

            // ---- COM Server setup ----
            comServer = new BrokerComServer();
            comServer.CpuTempProvider = () => (Volatile.Read(ref _lastCpuTemp), _lastCpuSource);
            comServer.GpuProvider = () => _lastGpus;
            comServer.SensorProvider = () => _lastSensors;
            Log("COM server ready (IBrokerService + IBrokerProcessService + IBrokerSensorService)");

            // ---- Register COM class object with SCM (v2.2: standalone also registers) ----
            // Standalone mode: register so CoCreateInstance connects directly (no --com-server launch needed)
            // COM server mode: register for SCM-launched instances (no SHM, standalone handles that)
            {
                int initHr = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
                if (initHr < 0)
                {
                    Log($"FATAL: CoInitializeEx failed with HRESULT 0x{initHr:X8}");
                    return initHr;
                }

                Guid brokerClsid = new(BrokerGuids.BrokerServiceClsid);
                IntPtr pUnk = Marshal.GetIUnknownForObject(new BrokerClassFactory(comServer));
                int hr = CoRegisterClassObject(
                    ref brokerClsid,
                    pUnk,
                    CLSCTX_LOCAL_SERVER,
                    REGCLS_MULTIPLEUSE,
                    out comCookie);
                if (hr < 0)
                {
                    Log($"FATAL: CoRegisterClassObject failed with HRESULT 0x{hr:X8}");
                    CoUninitialize();
                    return hr;
                }
                Log($"CoRegisterClassObject OK, cookie={comCookie} (mode={(isComServer ? "COM" : "standalone")})");
            }

            // JSON snapshot path
            var snapshotPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SysMonCmdPal", "sensor_snapshot.json");

            int cycle = 0;

            while (!cts.Token.IsCancellationRequested)
            {
                cycle++;
                try
                {
                    if (!isComServer && collector != null)
                    {
                        var (cpuTemp, cpuSource, gpus, sensors) = collector.ReadAll();

                        Volatile.Write(ref _lastCpuTemp, cpuTemp);
                        _lastCpuSource = cpuSource;
                        _lastGpus = gpus;
                        _lastSensors = sensors;

                        shm?.Write(cpuTemp, cpuSource, gpus, sensors);

                        if (cycle % 10 == 1)
                            WriteSnapshot(snapshotPath, cpuTemp, cpuSource, gpus, sensors);

                        if (cycle <= 3 || cycle % 30 == 1)
                        {
                            Log($"cycle={cycle} cpu={cpuTemp:F1}°C [{cpuSource}] gpus={gpus.Count} " +
                                $"sensors={sensors.Count}");
                            if (cycle <= 2)
                            {
                                foreach (var g in gpus)
                                    Log($"  gpu: {g.Name} temp={g.TempCelsius:F1}°C load={g.UsagePercent:F0}%");
                                var byCategory = sensors.GroupBy(s => s.Tag)
                                    .OrderBy(g => g.Key)
                                    .Select(g => $"{ShmTagName(g.Key)}:{g.Count()}");
                                Log($"  sensors: {string.Join(", ", byCategory)}");
                            }
                        }
                    }
                    else if (isComServer && cycle <= 3)
                    {
                        // COM server: log periodically to show we're alive
                        double temp = Volatile.Read(ref _lastCpuTemp);
                        Log($"cycle={cycle} cpu={temp:F1}°C [{_lastCpuSource}] (from SHM)");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"Cycle error: {ex.Message}");
                }

                // COM server mode: exit when idle for 10+ minutes
                if (isComServer && cycle > 30 &&
                    (DateTime.UtcNow.Ticks - BrokerComServer.LastComCallTicks) > TimeSpan.FromMinutes(10).Ticks)
                {
                    Log("COM server: idle for 10+ minutes, shutting down");
                    break;
                }

                Thread.Sleep(2000);
            }
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
        finally
        {
            Log("Shutting down...");
            if (comCookie != 0)
            {
                CoRevokeClassObject(comCookie);
                Log($"CoRevokeClassObject({comCookie}) OK");
            }
            comServer?.Dispose();
            shm?.Dispose();
            collector?.Dispose();
            CoUninitialize();
            Log("=== SysMonBroker stopped ===");
            BrokerLogger.Flush();
        }

        return 0;
    }

    // ---- SHM Reader (COM server mode) ----

    private static void ReadShm(MemoryMappedViewAccessor acc,
        byte[] srcBuf, byte[] nameBuf, byte[] sNameBuf, byte[] unitBuf)
    {
        if (acc.ReadInt32(0) != BrokerSharedMemory.MagicValue)
            return;

        double cpuTemp = acc.ReadDouble(16);
        acc.ReadArray(24, srcBuf, 0, 32);
        string cpuSrc = Encoding.UTF8.GetString(srcBuf).TrimEnd('\0');

        int gpuCount = Math.Min(acc.ReadInt32(56), BrokerSharedMemory.MaxGpus);
        var gpus = new List<Sensors.GpuReading>(gpuCount);
        for (int i = 0; i < gpuCount; i++)
        {
            int bOff = 60 + i * 72;
            acc.ReadArray(bOff, nameBuf, 0, 32);
            string gName = Encoding.UTF8.GetString(nameBuf).TrimEnd('\0');
            gpus.Add(new Sensors.GpuReading(gName,
                acc.ReadDouble(bOff + 32), acc.ReadDouble(bOff + 40),
                acc.ReadDouble(bOff + 48), acc.ReadDouble(bOff + 56)));
        }

        int sensorCount = Math.Min(acc.ReadInt32(360), BrokerSharedMemory.MaxSensors);
        var sensors = new List<IPC.SensorEntry>(sensorCount);
        for (int i = 0; i < sensorCount; i++)
        {
            int sOff = 364 + i * 64;
            int tag = acc.ReadInt32(sOff);
            acc.ReadArray(sOff + 4, sNameBuf, 0, 32);
            string sName = Encoding.UTF8.GetString(sNameBuf).TrimEnd('\0');
            double val = acc.ReadDouble(sOff + 36);
            acc.ReadArray(sOff + 44, unitBuf, 0, 16);
            string unit = Encoding.UTF8.GetString(unitBuf).TrimEnd('\0');
            int hwTag = acc.ReadInt32(sOff + 60);
            sensors.Add(new IPC.SensorEntry(tag, sName, val, unit, hwTag));
        }

        Volatile.Write(ref _lastCpuTemp, cpuTemp);
        _lastCpuSource = cpuSrc;
        _lastGpus = gpus;
        _lastSensors = sensors;
    }

    // ---- JSON Snapshot ----

    private static void WriteSnapshot(string path, double cpuTemp, string cpuSource,
        List<GpuReading> gpus, List<SensorEntry> sensors)
    {
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using var sw = new StreamWriter(path + ".tmp");
            sw.WriteLine("{");
            sw.WriteLine($"  \"timestamp\": \"{DateTime.UtcNow:O}\",");
            sw.WriteLine($"  \"cpu\": {{ \"temp\": {cpuTemp:F1}, \"source\": \"{cpuSource}\" }},");

            sw.WriteLine("  \"gpus\": [");
            for (int i = 0; i < gpus.Count; i++)
            {
                var g = gpus[i];
                sw.Write($"    {{ \"name\": \"{JsonEscape(g.Name)}\", \"temp\": {g.TempCelsius:F1}, \"usage\": {g.UsagePercent:F1}, \"memUsed\": {g.MemUsedMB:F0}, \"memTotal\": {g.MemTotalMB:F0} }}");
                if (i < gpus.Count - 1) sw.Write(",");
                sw.WriteLine();
            }
            sw.WriteLine("  ],");

            sw.WriteLine("  \"sensors\": [");
            for (int i = 0; i < sensors.Count; i++)
            {
                var s = sensors[i];
                sw.Write($"    {{ \"tag\": {s.Tag}, \"cat\": \"{ShmTagName(s.Tag)}\", \"name\": \"{JsonEscape(s.Name)}\", \"value\": {s.Value:F2}, \"unit\": \"{s.Unit}\" }}");
                if (i < sensors.Count - 1) sw.Write(",");
                sw.WriteLine();
            }
            sw.WriteLine("  ]");
            sw.WriteLine("}");
            sw.Flush();

            if (File.Exists(path)) File.Delete(path);
            File.Move(path + ".tmp", path);
        }
        catch (Exception ex)
        {
            Log($"Snapshot write error: {ex.Message}");
        }
    }

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static string ShmTagName(int tag) => tag switch
    {
        0 => "CpuTemp", 1 => "CpuLoad", 2 => "CpuClock", 3 => "CpuPower", 4 => "CpuVoltage",
        5 => "GpuTemp", 6 => "GpuLoad", 7 => "GpuClock", 8 => "GpuPower",
        9 => "GpuMemory", 10 => "GpuFan", 11 => "GpuVoltage",
        12 => "MbTemp", 13 => "MbFan", 14 => "MbVoltage",
        15 => "StorageTemp", 16 => "StorageLoad",
        _ => $"Unknown({tag})",
    };

    // --devmode-on / --devmode-off: 检查 dev build + marker, 然后创建/删除 .devmode_on 文件
    // 用文件 flag 而非管道 IPC, 因 standalone 和 COM server 是不同进程, 需共享状态
    static int DevModeToggle(bool enable)
    {
        string? devRepoPath = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyMetadataAttribute>()?.Value;

        if (string.IsNullOrEmpty(devRepoPath))
        {
            Console.Error.WriteLine("[-] Not a dev build (no DevRepoPath embedded)");
            Console.Error.WriteLine("    Build with: dotnet build -p:Dev=true");
            return 1;
        }

        string markerPath = Path.Combine(devRepoPath!, ".devmode_marker");
        if (!File.Exists(markerPath))
        {
            Console.Error.WriteLine($"[-] Marker file not found: {markerPath}");
            Console.Error.WriteLine("    Create it: touch .devmode_marker (in project dir, gitignored)");
            return 1;
        }

        bool ok = DevModeVerifier.SetRuntimeOverride(enable);
        if (ok)
        {
            Console.WriteLine($"[+] DevMode {(enable ? "ON" : "OFF")} — file flag updated");
            Console.WriteLine($"    All broker processes (standalone + COM server) will read this on next call");
            return 0;
        }
        else
        {
            Console.Error.WriteLine("[-] DevMode toggle failed");
            return 1;
        }
    }

    static void Log(string msg) => BrokerLogger.Log(msg);
}
