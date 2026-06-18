// SysMonBroker/COM/BrokerComServer.cs
// COM 本地服务器实现 — btop4win 通过 CoCreateInstance 连接
// 实现 IBrokerService, IBrokerProcessService, IBrokerSensorService
// 自注册到 HKCR\CLSID\{...}\LocalServer32

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SysMonBroker.COM;

/// <summary>
/// Broker COM 服务器 — 实现所有三个接口。
/// 作为 COM Local Server 运行（独立进程），btop4win 通过 CoCreateInstance 连接。
/// </summary>
[ComVisible(true)]
[Guid(BrokerGuids.BrokerServiceClsid)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class BrokerComServer : IBrokerService, IBrokerProcessService, IBrokerSensorService
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
    private const string BtopExeHash = "11b38346e3bbe0b417e4b7fb1c7f5ee123eb9b5a737537ece86cfdc58e7c49dc";
    private bool _authenticated;

    // ================================================================
    // IBrokerService
    // ================================================================

    public IBrokerProcessService GetProcessService() { TouchCom(); return this; }
    public IBrokerSensorService GetSensorService() { TouchCom(); return this; }
    public bool IsAlive() { TouchCom(); return true; }
    public string GetVersion() { TouchCom(); return "SysMonBroker v2.2 (COM)"; }

    // ================================================================
    // IBrokerProcessService
    // ================================================================

    public BrokerProcessEntry[] GetProcesses()
    {
        TouchCom();
        RequireAuth();
        lock (_processLock)
        {
            // 缓存 2 秒内的结果，避免多个客户端频繁调用
            if (_cachedProcesses == null ||
                (DateTime.UtcNow - _lastProcessRefresh).TotalSeconds > 2)
            {
                _cachedProcesses = _processCollector.Collect();
                _lastProcessRefresh = DateTime.UtcNow;
            }
            return _cachedProcesses;
        }
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
            // DevMode: SSH 签名验证 → 跳过 hash 检查
            if (DevModeVerifier.IsDevModeActive())
            {
                _authenticated = true;
                Log($"Auth OK (devmode): PID {clientPid} — SSH signature verified");
                return 0;
            }

            // 验证 hash 格式
            if (string.IsNullOrEmpty(exeHashHex) || exeHashHex.Length != 64)
            {
                Log($"Auth DENIED: PID {clientPid} — invalid hash format");
                return 1;
            }

            // 硬编码 hash 比对
            if (!exeHashHex.Equals(BtopExeHash, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Auth DENIED: PID {clientPid} — hash mismatch " +
                    $"(claimed={exeHashHex[..8]}..., expected={BtopExeHash[..8]}...)");
                return 1;
            }

            _authenticated = true;
            Log($"Auth OK: PID {clientPid} — hash matches");
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

    // ================================================================
    // IBrokerSensorService
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

    public int GetGpuCount()
    {
        TouchCom();
        var gpus = GpuProvider?.Invoke();
        return gpus?.Count ?? 0;
    }

    public string GetGpuName(int index)
    {
        TouchCom();
        var gpus = GpuProvider?.Invoke();
        if (gpus == null || index < 0 || index >= gpus.Count) return "";
        return gpus[index].Name;
    }

    public double GetGpuTemperature(int index)
    {
        TouchCom();
        var gpus = GpuProvider?.Invoke();
        if (gpus == null || index < 0 || index >= gpus.Count) return -1;
        return gpus[index].TempCelsius;
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
    /// 注册 Broker 为 COM Local Server（写入 HKCR\CLSID\...\LocalServer32）。
    /// 需要管理员权限。
    /// </summary>
    public static void RegisterComServer()
    {
        try
        {
            string clsid = BrokerGuids.BrokerServiceClsid;
            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

            string clsidKey = $@"CLSID\{{{clsid}}}";
            string localServerKey = $@"{clsidKey}\LocalServer32";

            using var key = Registry.ClassesRoot.CreateSubKey(clsidKey);
            key?.SetValue("", "SysMonBroker Service");

            using var serverKey = Registry.ClassesRoot.CreateSubKey(localServerKey);
            serverKey?.SetValue("", $"\"{exePath}\" --com-server");

            Log($"COM registered: {{{clsid}}} -> {exePath}");
        }
        catch (Exception ex)
        {
            Log($"COM registration failed: {ex.Message}");
        }
    }

    /// <summary>取消注册</summary>
    public static void UnregisterComServer()
    {
        try
        {
            string clsidKey = $@"CLSID\{{{BrokerGuids.BrokerServiceClsid}}}";
            Registry.ClassesRoot.DeleteSubKeyTree(clsidKey, false);
            Log("COM unregistered");
        }
        catch { }
    }

    public void Dispose()
    {
        _processCollector.Dispose();
    }

    private static void Log(string msg)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SysMonCmdPal", "broker.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} [COM] {msg}\n");
        }
        catch { }
    }
}
