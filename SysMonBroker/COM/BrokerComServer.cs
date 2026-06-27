// SysMonBroker/COM/BrokerComServer.cs
// COM 本地服务器实现 — btop4win 通过 CoCreateInstance 连接
// 实现 IBrokerService, IBrokerProcessService, IBrokerSensorService
// 自注册到 HKCR\CLSID\{...}\LocalServer32

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using SysMonBroker.Logging;

namespace SysMonBroker.COM;

/// <summary>
/// Broker COM Class Factory — CoRegisterClassObject 需要 IClassFactory，
/// 不是服务对象本身。
/// </summary>
[ComVisible(true)]
public sealed class BrokerClassFactory : IClassFactory
{
    private readonly BrokerComServer _server;

    public BrokerClassFactory(BrokerComServer server) => _server = server;

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        if (pUnkOuter != IntPtr.Zero)
        {
            ppvObject = IntPtr.Zero;
            return unchecked((int)0x80040004); // CLASS_E_NOAGGREGATION
        }
        ppvObject = Marshal.GetIUnknownForObject(_server);
        // QueryInterface for the requested IID
        int hr = Marshal.QueryInterface(ppvObject, ref riid, out IntPtr ppv);
        Marshal.Release(ppvObject);
        if (hr >= 0) ppvObject = ppv;
        return hr;
    }

    public int LockServer(bool fLock) => 0; // S_OK
}

[ComVisible(true)]
[Guid("00000001-0000-0000-C000-000000000046")] // IID_IClassFactory
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);
    int LockServer(bool fLock);
}

/// <summary>
/// Broker COM 服务器 — 实现所有三个接口。
/// 作为 COM Local Server 运行（独立进程），btop4win 通过 CoCreateInstance 连接。
/// </summary>
[ComVisible(true)]
[Guid(BrokerGuids.BrokerServiceClsid)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class BrokerComServer : IBrokerService
{
    private readonly ProcessCollector _processCollector = new();
    private BrokerProcessEntry[]? _cachedProcesses;
    private DateTime _lastProcessRefresh = DateTime.MinValue;
    private readonly object _processLock = new();

    // 外部注入的传感器数据源（由 Program.cs 主循环更新）
    public Func<(double Temp, string Source)>? CpuTempProvider { get; set; }
    public Func<List<Sensors.GpuReading>>? GpuProvider { get; set; }
    public Func<List<IPC.SensorEntry>>? SensorProvider { get; set; }

    private static long _lastComCallTicks = DateTime.UtcNow.Ticks;

    /// <summary>Called by every public COM method to refresh the idle timer.</summary>
    public static void TouchCom() => _lastComCallTicks = DateTime.UtcNow.Ticks;

    /// <summary>Last time any COM method was called (Ticks).</summary>
    public static long LastComCallTicks => _lastComCallTicks;

    // ---- 安全：硬编码 SHA256 + SSH 签名 devmode + 认证状态 ----
    // RELEASE 模式: DevMode 不可用。
    //   部署前必须把目标 btop.exe 的 SHA256 填入 BtopExeHash 常量,
    //   否则 release 构建会拒绝所有客户端认证。
    // DEV 模式: 开发者通过 --devmode-on 开启 DevMode → 运行 btop4win --register-broker
    //   → broker 将 btop4win 的 hash 追加到 registered_hashes.txt (不落仓库, 跨进程共享)
    //   → 关闭 DevMode 后已注册的 hash 继续有效
    private const string BtopExeHash = "11b38346e3bbe0b417e4b7fb1c7f5ee123eb9b5a737537ece86cfdc58e7c49dc";
    private bool _authenticated;

    // 已注册 hash 存储在 %LOCALAPPDATA%\SysMonCmdPal\registered_hashes.txt (每行一个 hash)
    // 不落仓库, 跨 standalone + COM server 进程共享
    // 用 USERPROFILE 而非 LOCALAPPDATA, 因 COM server 进程的 LOCALAPPDATA 可能不一致
    private static readonly string RegisteredHashesPath = Path.Combine(
        Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "Local", "SysMonCmdPal", "registered_hashes.txt");
    private static readonly object _hashLock = new();

    // ================================================================
    // IBrokerService (flat — 所有方法直接在此接口上)
    // ================================================================

    public bool IsAlive() { TouchCom(); return true; }
    public string GetVersion() { TouchCom(); return "SysMonBroker v2.2 (COM)"; }

    // ---- 进程 ----
    public byte[] GetProcesses()
    {
        TouchCom();
        RequireAuth();
        lock (_processLock)
        {
            if (_cachedProcesses == null ||
                (DateTime.UtcNow - _lastProcessRefresh).TotalSeconds > 2)
            {
                _cachedProcesses = _processCollector.Collect();
                _lastProcessRefresh = DateTime.UtcNow;
            }
            if (_cachedProcesses == null) return [];
            using var ms = new MemoryStream(_cachedProcesses.Length * 1608);
            Span<byte> nameBuf = stackalloc byte[260];
            Span<byte> cmdBuf = stackalloc byte[1024];
            Span<byte> userBuf = stackalloc byte[256];
            foreach (var p in _cachedProcesses)
            {
                WriteLE32(ms, p.Pid); WriteLE32(ms, p.ParentPid); WriteLE32(ms, p.Threads);
                WriteFixed(ms, p.Name, nameBuf);
                WriteFixed(ms, p.CommandLine, cmdBuf);
                WriteFixed(ms, p.UserName, userBuf);
                WriteLE64(ms, (ulong)p.PrivateMemoryBytes);
                WriteLE64(ms, (ulong)BitConverter.DoubleToUInt64Bits(p.CpuPercent));
                WriteLE64(ms, (ulong)p.CreationTime);
                WriteLE64(ms, (ulong)p.KernelTime);
                WriteLE64(ms, (ulong)p.UserTime);
                WriteLE64(ms, (ulong)p.IoReadBytes);
                WriteLE64(ms, (ulong)p.IoWriteBytes);
            }
            return ms.ToArray();
        }
    }

    private static void WriteLE32(MemoryStream ms, uint v) {
        ms.WriteByte((byte)v); ms.WriteByte((byte)(v>>8)); ms.WriteByte((byte)(v>>16)); ms.WriteByte((byte)(v>>24));
    }
    private static void WriteLE64(MemoryStream ms, ulong v) {
        ms.WriteByte((byte)v); ms.WriteByte((byte)(v>>8)); ms.WriteByte((byte)(v>>16)); ms.WriteByte((byte)(v>>24));
        ms.WriteByte((byte)(v>>32)); ms.WriteByte((byte)(v>>40)); ms.WriteByte((byte)(v>>48)); ms.WriteByte((byte)(v>>56));
    }
    private static void WriteFixed(MemoryStream ms, string s, Span<byte> buf)
    {
        buf.Clear();
        if (!string.IsNullOrEmpty(s))
        {
            int len = Encoding.UTF8.GetBytes(s, buf[..^1]);
        }
        ms.Write(buf);
    }

    public int GetProcessCount()
    {
        TouchCom();
        lock (_processLock)
            return _cachedProcesses?.Length ?? 0;
    }

    public void Refresh()
    {
        TouchCom();
        lock (_processLock)
        {
            _cachedProcesses = _processCollector.Collect();
            _lastProcessRefresh = DateTime.UtcNow;
        }
    }

    public int KillProcess(uint pid, uint exitCode)
    {
        TouchCom();
        RequireAuth();
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            proc.Kill();
            Log($"KillProcess({pid}) OK");
            return 0;
        }
        catch (ArgumentException)
        {
            // Process already exited
            return 0;
        }
        catch (Exception ex)
        {
            Log($"KillProcess({pid}) failed: {ex.Message}");
            // Fall back to raw Win32 for more precise error code
            return KillProcessWin32(pid, exitCode);
        }
    }

    // P/Invoke fallback for precise GetLastError
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint PROCESS_TERMINATE = 0x0001;

    private static int KillProcessWin32(uint pid, uint exitCode)
    {
        IntPtr hProcess = OpenProcess(PROCESS_TERMINATE, false, pid);
        if (hProcess == IntPtr.Zero)
            return Marshal.GetLastWin32Error();

        bool ok = TerminateProcess(hProcess, exitCode);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        CloseHandle(hProcess);
        return err;
    }

    // ---- 认证 + 权限门控 ----

    public int Authenticate(uint clientPid, string exeHashHex)
    {
        TouchCom();
        try
        {
            // 截断到 64 字符 (btop 侧 BSTR 可能带 null terminator 导致 65 字符)
            string hashLower = (exeHashHex ?? "").ToLowerInvariant().TrimEnd('\0')[..Math.Min(64, (exeHashHex ?? "").TrimEnd('\0').Length)];

            // DevMode: 接受 + 自动注册 hash 到文件 (供 DevMode 关闭后继续使用)
            if (DevModeVerifier.IsDevModeActive())
            {
                if (hashLower.Length == 64)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(RegisteredHashesPath)!);
                        File.AppendAllText(RegisteredHashesPath, hashLower + "\n");
                        Log($"Hash written to file: {RegisteredHashesPath}");
                    }
                    catch (Exception ex) { Log($"Hash file write failed: {ex.Message}"); }
                }
                _authenticated = true;
                Log($"Auth OK (devmode): PID {clientPid} — hash registered ({hashLower[..Math.Min(8, hashLower.Length)]}...)");
                return 0;
            }

            // 已注册 hash (DevMode 期间注册的, 关闭后仍有效, 跨进程共享)
            if (hashLower.Length == 64 && IsHashRegistered(hashLower))
            {
                _authenticated = true;
                Log($"Auth OK (registered): PID {clientPid} — hash in registered file");
                return 0;
            }

            // 硬编码 hash (release 模式唯一认证路径)
            if (hashLower.Length != 64)
            {
                Log($"Auth DENIED: PID {clientPid} — invalid hash format (len={hashLower.Length})");
                return 1;
            }

            if (!hashLower.Equals(BtopExeHash, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Auth DENIED: PID {clientPid} — hash mismatch " +
                    $"(claimed={hashLower[..8]}..., expected={BtopExeHash[..8]}...)");
                return 1;
            }

            _authenticated = true;
            Log($"Auth OK (hardcoded): PID {clientPid} — hash matches");
            return 0;
        }
        catch (Exception ex)
        {
            Log($"Auth ERROR: PID {clientPid} — {ex.Message}");
            return 3;
        }
    }

    /// <summary>检查当前实例是否已认证。未认证时抛出 COMException。</summary>
    private void RequireAuth()
    {
        if (DevModeVerifier.IsDevModeActive()) return;
        if (!_authenticated)
        {
            Log("Access denied: client not authenticated");
            throw new COMException("Not authenticated. Call Authenticate() first.",
                unchecked((int)0x80070005)); // E_ACCESSDENIED
        }
    }

    // ---- 已注册 hash 文件存储 (跨进程共享) ----

    private static void RegisterHashToFile(string hashLower)
    {
        lock (_hashLock)
        {
            try
            {
                string? dir = Path.GetDirectoryName(RegisteredHashesPath);
                Log($"RegisterHashToFile: path={RegisteredHashesPath}, dir={dir}, hash={hashLower[..8]}");
                if (dir != null) Directory.CreateDirectory(dir);
                var existing = File.Exists(RegisteredHashesPath)
                    ? File.ReadAllLines(RegisteredHashesPath) : [];
                if (!existing.Contains(hashLower))
                {
                    File.AppendAllText(RegisteredHashesPath, hashLower + "\n");
                    Log($"RegisterHashToFile: written OK ({hashLower[..8]}...)");
                }
                else
                {
                    Log($"RegisterHashToFile: already exists ({hashLower[..8]}...)");
                }
            }
            catch (Exception ex) { Log($"RegisterHashToFile error: {ex.Message}"); }
        }
    }

    private static bool IsHashRegistered(string hashLower)
    {
        lock (_hashLock)
        {
            try
            {
                if (!File.Exists(RegisteredHashesPath)) return false;
                return File.ReadAllLines(RegisteredHashesPath).Contains(hashLower);
            }
            catch { return false; }
        }
    }

    // ================================================================
    // IBrokerService 传感器方法
    // ================================================================

    public double GetCpuTemperature()
    {
        TouchCom();
        var result = CpuTempProvider?.Invoke();
        return result?.Temp ?? -1;
    }

    public string GetCpuSource()
    {
        TouchCom();
        var result = CpuTempProvider?.Invoke();
        return result?.Source ?? "None";
    }

    public double GetCpuClock()
    {
        TouchCom();
        return 0;
    }

    public int GetGpuCount()
    {
        TouchCom();
        var gpus = GpuProvider?.Invoke();
        int count = gpus?.Count ?? 0;
        Log($"GetGpuCount() = {count}");
        return count;
    }

    public string GetGpuName(int index)
    {
        TouchCom();
        var gpus = GpuProvider?.Invoke();
        if (gpus == null || index < 0 || index >= gpus.Count) return "";
        Log($"GetGpuName({index}) = {gpus[index].Name}");
        return gpus[index].Name;
    }

    public double GetGpuTemperature(int index)
    {
        TouchCom();
        var gpus = GpuProvider?.Invoke();
        if (gpus == null || index < 0 || index >= gpus.Count) { Log($"GetGpuTemperature({index}) = -1 (gpus={gpus?.Count ?? 0})"); return -1; }
        double t = gpus[index].TempCelsius;
        Log($"GetGpuTemperature({index}) = {t}");
        return t;
    }

    public double GetGpuUsage(int index)
    {
        TouchCom();
        var gpus = GpuProvider?.Invoke();
        if (gpus == null || index < 0 || index >= gpus.Count) return -1;
        return gpus[index].UsagePercent;
    }

    public BrokerSensorEntry[] GetAllSensors()
    {
        TouchCom();
        var sensors = SensorProvider?.Invoke();
        if (sensors == null) return [];

        return sensors.Select(s => new BrokerSensorEntry
        {
            CategoryTag = s.Tag,
            Name = s.Name ?? "",
            Value = s.Value,
            Unit = s.Unit ?? "",
        }).ToArray();
    }

    // ================================================================
    // COM 注册
    // ================================================================

    /// <summary>
    /// 注册 Broker 为 COM Local Server（写入 HKCR\CLSID\...\LocalServer32 + 接口注册）。
    /// 接口注册 (HKCR\Interface\{IID}) 是跨进程 COM 的必要条件。
    /// 需要管理员权限。
    /// </summary>
    public static void RegisterComServer()
    {
        try
        {
            string clsid = BrokerGuids.BrokerServiceClsid;
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

            // CLSID + LocalServer32
            string clsidKey = $@"CLSID\{{{clsid}}}";
            using var key = Registry.ClassesRoot.CreateSubKey(clsidKey);
            key?.SetValue("", "SysMonBroker Service");

            using var serverKey = Registry.ClassesRoot.CreateSubKey($"{clsidKey}\\LocalServer32");
            serverKey?.SetValue("", $"\"{exePath}\" --com-server");

            // 接口注册 — InterfaceIsDual 使用 OLE Automation 通用封送（PSOAInterface）
            RegisterInterface(BrokerGuids.IBrokerServiceIid, "IBrokerService");

            Log($"COM registered: {{{clsid}}} -> {exePath}");
        }
        catch (Exception ex)
        {
            Log($"COM registration failed: {ex.Message}");
        }
    }

    /// <summary>取消注册（CLSID + 接口）</summary>
    public static void UnregisterComServer()
    {
        try
        {
            Registry.ClassesRoot.DeleteSubKeyTree($@"CLSID\{{{BrokerGuids.BrokerServiceClsid}}}", false);
            Registry.ClassesRoot.DeleteSubKeyTree($@"Interface\{{{BrokerGuids.IBrokerServiceIid}}}", false);
            Log("COM unregistered");
        }
        catch { }
    }

    private static void RegisterInterface(string iid, string name)
    {
        string ifaceKey = $@"Interface\{{{iid}}}";
        using var key = Registry.ClassesRoot.CreateSubKey(ifaceKey);
        key?.SetValue("", name);

        // 注册 OLE Automation 通用封送器（PSOAInterface），使 SCM 能在跨进程封送接口
        using var psKey = Registry.ClassesRoot.CreateSubKey($@"{ifaceKey}\ProxyStubClsid32");
        psKey?.SetValue("", "{00020420-0000-0000-C000-000000000046}");
    }

    public void Dispose()
    {
        _processCollector.Dispose();
    }

    private static void Log(string msg) => BrokerLogger.Log("COM", msg);
}
