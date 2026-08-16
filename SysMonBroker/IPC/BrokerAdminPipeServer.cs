using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SysMonBroker.Logging;

namespace SysMonBroker.IPC;

/// <summary>
/// 命名管道服务端 — 管理员权限代理(协议 btop4win-broker-ipc.md §2)。
/// 服务端以管理员/SYSTEM 运行(计划任务), 代理执行普通用户无法完成的操作
/// (如终止管理员进程 TERMINATE)。
///
/// 实现说明: 全部使用 Win32 API(CreateNamedPipeW + ConnectNamedPipe + ReadFile/WriteFile)。
/// 原因: .NET 10 移除了 PipeSecurity 托管 API(SetAccessControl / 带 PipeSecurity 的构造),
/// 而 CreateNamedPipeW 返回的句柄只有 GENERIC_READ|GENERIC_WRITE, 事后 SetSecurityInfo 无
/// WRITE_DAC 权限(实测 ERROR_ACCESS_DENIED)。因此协议 §2.1 的 SDDL 只能在创建时经
/// SECURITY_ATTRIBUTES 注入 → 必须用原生句柄 + 同步 IO。
///
/// 线程模型: 维护 InstanceCount 个监听实例(协议 §2.1 要求 ≥4), 每个实例一个线程池任务
/// 同步等待连接; 连接处理完关闭句柄并回填新实例。不阻塞传感器主循环。
///
/// 安全要点(协议 §2.2/§2.3/§2.8):
///   - 首条消息必须是 AUTH, 否则拒绝(ERROR_ACCESS_DENIED)并断开;
///   - AUTH 失败 → 返回错误后断开, 客户端不重试;
///   - 非法 Magic 直接断开; 载荷长度上限 4096; 单连接串行处理。
/// </summary>
internal sealed class BrokerAdminPipeServer : IDisposable
{
    private const int HeaderSize = 12;
    private const uint PipeReadTimeoutMs = 5000; // 读超时(客户端读写超时 3s, 服务端留余量)

    private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_READMODE_BYTE = 0x00000000;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint ERROR_PIPE_CONNECTED = 535;
    private const uint ERROR_NO_DATA = 232;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly List<SafePipeHandle> _listeners = [];
    private Thread? _thread;
    private bool _disposed;
    private int _firstInstance = 1;

    /// <summary>启动监听(幂等)。</summary>
    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "BrokerAdminPipe" };
        _thread.Start();
    }

    /// <summary>停止监听并释放全部实例(幂等)。关闭句柄会使阻塞中的 ConnectNamedPipe/ReadFile 立即失败。</summary>
    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        List<SafePipeHandle> copy;
        lock (_sync) { copy = [.. _listeners]; _listeners.Clear(); }
        foreach (var h in copy)
        {
            try { h.Dispose(); } catch { }
        }
        _thread?.Join(1000);
    }

    public void Dispose() => Stop();

    private void AcceptLoop()
    {
        Log($"AdminPipe: listening on {BrokerAdminPipe.PipeName} (instances={BrokerAdminPipe.InstanceCount})");
        try
        {
            for (int i = 0; i < BrokerAdminPipe.InstanceCount; i++)
                SpawnListener();
        }
        catch (Exception ex)
        {
            Log($"AdminPipe: listener creation failed: {ex.Message}");
        }
        _cts.Token.WaitHandle.WaitOne();
        Log("AdminPipe: stopped");
    }

    private void SpawnListener()
    {
        SafePipeHandle handle;
        try
        {
            handle = CreateInstance();
        }
        catch (Exception ex)
        {
            Log($"AdminPipe: create instance failed: {ex.Message}");
            return;
        }

        lock (_sync) _listeners.Add(handle);
        if (_cts.IsCancellationRequested)
        {
            lock (_sync) _listeners.Remove(handle);
            handle.Dispose();
            return;
        }
        ThreadPool.QueueUserWorkItem(_ => ServeInstance(handle));
    }

    /// <summary>创建带协议 SDDL 的管道实例。首实例带 FILE_FLAG_FIRST_PIPE_INSTANCE, 后续不带。</summary>
    private SafePipeHandle CreateInstance()
    {
        bool first = Interlocked.Exchange(ref _firstInstance, 0) == 1;
        uint openMode = PIPE_ACCESS_DUPLEX;
        if (first) openMode |= FILE_FLAG_FIRST_PIPE_INSTANCE;

        IntPtr sd = IntPtr.Zero;
        IntPtr sa = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(
                    BrokerAdminPipe.PipeSddl, 1 /*SDDL_REVISION_1*/, out sd, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var secAttr = new SECURITY_ATTRIBUTES
            {
                nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = 0,
            };
            sa = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
            Marshal.StructureToPtr(secAttr, sa, false);

            IntPtr handle = CreateNamedPipeW(
                BrokerAdminPipe.PipeName,
                openMode,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
                BrokerAdminPipe.InstanceCount,
                4096, 4096, 0, sa);
            if (handle == new IntPtr(-1) || handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            return new SafePipeHandle(handle, ownsHandle: true);
        }
        finally
        {
            if (sa != IntPtr.Zero) Marshal.FreeHGlobal(sa);
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    /// <summary>单个实例生命周期: 等待连接 → 处理 → 回填新实例。</summary>
    private void ServeInstance(SafePipeHandle handle)
    {
        try
        {
            IntPtr raw = handle.DangerousGetHandle();

            // 同步等待客户端连接(阻塞; Stop 时句柄被关闭 → 返回失败)
            if (!ConnectNamedPipe(raw, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_PIPE_CONNECTED) // 客户端在 ConnectNamedPipe 前已连接(正常竞争)
                {
                    if (!_cts.IsCancellationRequested)
                        Log($"AdminPipe: ConnectNamedPipe failed: {err}");
                    return;
                }
            }

            // wait 模式下设置读超时(非零超时对 ReadFile 生效)
            SetNamedPipeHandleState(raw, IntPtr.Zero, IntPtr.Zero, PipeReadTimeoutMs);
            HandleConnection(raw);
        }
        catch (Exception ex)
        {
            Log($"AdminPipe: connection error: {ex.Message}");
        }
        finally
        {
            try { handle.Dispose(); } catch { }
            lock (_sync) _listeners.Remove(handle);
            if (!_cts.IsCancellationRequested) SpawnListener(); // 回填
        }
    }

    private void HandleConnection(IntPtr raw)
    {
        bool authed = false;
        while (true)
        {
            // ---- 读请求头(12B): [magic][cmd][len] ----
            byte[] header = new byte[HeaderSize];
            if (!ReadExactly(raw, header, HeaderSize)) break;
            uint magic = BitConverter.ToUInt32(header, 0);
            uint cmd = BitConverter.ToUInt32(header, 4);
            uint len = BitConverter.ToUInt32(header, 8);

            if (magic != BrokerAdminPipe.Magic)
            {
                Log("AdminPipe: invalid magic, disconnecting");
                break;
            }
            if (len > BrokerAdminPipe.MaxPayload)
            {
                Log($"AdminPipe: oversized payload ({len}), disconnecting");
                WriteResponse(raw, BrokerAdminPipe.ErrorInvalidParameter, "payload too large");
                break;
            }

            byte[] payload = len > 0 ? new byte[len] : [];
            if (len > 0 && !ReadExactly(raw, payload, (int)len)) break;

            // ---- 首条必须 AUTH(协议 §2.2) ----
            if (!authed)
            {
                if (cmd != (uint)BrokerAdminPipe.Cmd.Auth)
                {
                    Log("AdminPipe: command before AUTH, rejected");
                    WriteResponse(raw, BrokerAdminPipe.ErrorAccessDenied, "AUTH required");
                    break;
                }
                uint status = HandleAuth(raw, payload);
                WriteResponse(raw, status, status == 0 ? null : "AUTH failed");
                if (status != 0) break;
                authed = true;
                continue;
            }

            // ---- 已鉴权: 命令分发 ----
            uint result = 0;
            string? text = null;
            switch ((BrokerAdminPipe.Cmd)cmd)
            {
                case BrokerAdminPipe.Cmd.Ping:
                    break; // status=0

                case BrokerAdminPipe.Cmd.Terminate:
                    result = HandleTerminate(payload, out text);
                    break;

                case BrokerAdminPipe.Cmd.ServiceControl:
                    // 协议 §2.6 保留命令, btop4win 未接线; 占位返回不支持并记录
                    result = BrokerAdminPipe.ErrorNotSupported;
                    text = "SERVICE_CONTROL not implemented (reserved)";
                    Log($"AdminPipe: SERVICE_CONTROL (reserved) called, len={len}");
                    break;

                default:
                    result = BrokerAdminPipe.ErrorInvalidParameter;
                    text = $"unknown command {cmd}";
                    break;
            }
            if (!WriteResponse(raw, result, text)) break;
        }
    }

    /// <summary>AUTH(协议 §2.4/§2.5): [pid u32][sha256 hex 64B], 白名单校验 + DevMode 放行。</summary>
    private uint HandleAuth(IntPtr raw, byte[] payload)
    {
        if (payload.Length != 4 + 64) return BrokerAdminPipe.ErrorAccessDenied;
        uint pid = BitConverter.ToUInt32(payload, 0);
        string hash = Encoding.ASCII.GetString(payload, 4, 64);
        if (!IsHex64(hash)) return BrokerAdminPipe.ErrorAccessDenied;

        // 加固: AUTH 载荷中的 pid 必须与真实客户端进程一致(协议未强制, 防伪造)
        if (!IsClientPid(raw, pid))
        {
            Log($"AdminPipe: AUTH pid mismatch (claimed={pid})");
            return BrokerAdminPipe.ErrorAccessDenied;
        }

        if (DevModeVerifier.IsDevModeActive())
        {
            Log($"AdminPipe: AUTH OK (DevMode active, pid={pid})");
            return 0;
        }
        if (BrokerHashWhitelist.Contains(hash))
        {
            Log($"AdminPipe: AUTH OK (whitelist, pid={pid}, hash={hash[..8]}...)");
            return 0;
        }
        Log($"AdminPipe: AUTH denied (pid={pid}, hash={hash[..8]}...)");
        return BrokerAdminPipe.ErrorAccessDenied;
    }

    /// <summary>TERMINATE(协议 §2.4): [pid u32], 以管理员权限 Kill。</summary>
    private uint HandleTerminate(byte[] payload, out string? text)
    {
        text = null;
        if (payload.Length != 4)
        {
            text = "invalid payload";
            return BrokerAdminPipe.ErrorInvalidParameter;
        }
        uint pid = BitConverter.ToUInt32(payload, 0);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            process.Kill(entireProcessTree: false);
            Log($"AdminPipe: TERMINATE pid={pid} OK");
            return 0;
        }
        catch (ArgumentException)
        {
            text = $"process {pid} not found";
            return BrokerAdminPipe.ErrorNotFound;
        }
        catch (InvalidOperationException)
        {
            text = $"process {pid} already exited";
            return BrokerAdminPipe.ErrorNotFound;
        }
        catch (Win32Exception ex)
        {
            // 权限不足等: 原样上报 Win32 错误码(协议 §2.7)
            text = $"terminate failed: {ex.Message}";
            Log($"AdminPipe: TERMINATE pid={pid} failed: {ex.Message} (0x{ex.NativeErrorCode:X8})");
            return (uint)ex.NativeErrorCode;
        }
    }

    /// <summary>响应头(12B) + 可选 UTF-8 错误文本(协议 §2.3)。</summary>
    private static bool WriteResponse(IntPtr raw, uint status, string? text)
    {
        byte[] payload = text != null ? Encoding.UTF8.GetBytes(text) : [];
        if (payload.Length > BrokerAdminPipe.MaxPayload)
            payload = payload[..BrokerAdminPipe.MaxPayload];

        byte[] header = new byte[HeaderSize];
        BitConverter.GetBytes(BrokerAdminPipe.Magic).CopyTo(header, 0);
        BitConverter.GetBytes(status).CopyTo(header, 4);
        BitConverter.GetBytes((uint)payload.Length).CopyTo(header, 8);

        uint written;
        if (!WriteFile(raw, header, (uint)header.Length, out written, IntPtr.Zero)) return false;
        if (payload.Length == 0) return true;
        return WriteFile(raw, payload, (uint)payload.Length, out written, IntPtr.Zero);
    }

    private static bool ReadExactly(IntPtr raw, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            uint read;
            if (!ReadFile(raw, buffer, (uint)(count - offset), out read, IntPtr.Zero)) return false;
            if (read == 0) return false;
            offset += (int)read;
        }
        return true;
    }

    private static bool IsHex64(string s)
    {
        if (s.Length != 64) return false;
        foreach (char c in s)
        {
            bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    private static bool IsClientPid(IntPtr raw, uint claimed)
    {
        try
        {
            return GetNamedPipeClientProcessId(raw, out uint actual) && actual == claimed;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string sddl, uint sddlRevision, out IntPtr securityDescriptor, out uint errorCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateNamedPipeW(
        string lpName, uint dwOpenMode, uint dwPipeMode,
        int nMaxInstances, int nOutBufferSize, int nInBufferSize,
        uint nDefaultTimeOut, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConnectNamedPipe(IntPtr hNamedPipe, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetNamedPipeHandleState(IntPtr hNamedPipe, IntPtr lpMode, IntPtr lpMaxCollectionCount, uint nTimeOut);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipeHandle, out uint clientProcessId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static void Log(string msg) => BrokerLogger.Log(msg);
}
