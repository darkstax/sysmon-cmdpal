using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using SysMonBroker.Logging;

namespace SysMonBroker.IPC;

/// <summary>
/// 命名管道服务端 — 管理员权限代理(协议 btop4win-broker-ipc.md §2)。
/// 服务端以管理员运行(计划任务 RunLevel=Highest), 代理执行普通用户无法完成的操作
/// (如终止管理员进程 TERMINATE)。
///
/// 线程模型: 独立后台线程维护 InstanceCount 个监听实例(协议 §2.1 要求 ≥4);
/// 连接处理在独立 Task 中进行, 不阻塞传感器主循环。
///
/// 安全要点(协议 §2.2/§2.3/§2.8):
///   - 首条消息必须是 AUTH, 否则拒绝(ERROR_ACCESS_DENIED)并断开;
///   - AUTH 失败 → 返回错误后断开, 客户端不重试;
///   - 非法 Magic 直接断开; 载荷长度上限 4096; 单连接串行处理。
/// </summary>
internal sealed class BrokerAdminPipeServer : IDisposable
{
    private const int HeaderSize = 12;
    private const int IoTimeoutMs = 5000;

    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly object _sync = new();
    private readonly List<NamedPipeServerStream> _listeners = [];
    private Thread? _thread;
    private bool _disposed;

    /// <summary>启动监听(幂等)。</summary>
    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "BrokerAdminPipe" };
        _thread.Start();
    }

    /// <summary>停止监听并释放全部实例(幂等)。</summary>
    public void Stop()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _stopped.Set();

        List<NamedPipeServerStream> copy;
        lock (_sync) { copy = [.. _listeners]; _listeners.Clear(); }
        foreach (var s in copy)
        {
            try { s.Dispose(); } catch { }
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
                AddListener();
        }
        catch (Exception ex)
        {
            Log($"AdminPipe: listener creation failed: {ex.Message}");
        }
        _stopped.Wait();
        Log("AdminPipe: stopped");
    }

    private void AddListener()
    {
        NamedPipeServerStream server;
        try
        {
            server = new NamedPipeServerStream(
                BrokerAdminPipe.PipeName,
                PipeDirection.InOut,
                BrokerAdminPipe.InstanceCount,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096, 4096);
            server.SetAccessControl(BuildPipeSecurity()); // 必须在连接前应用
        }
        catch (Exception ex)
        {
            Log($"AdminPipe: create instance failed: {ex.Message}");
            return;
        }

        lock (_sync) _listeners.Add(server);
        try
        {
            server.BeginWaitForConnection(OnConnected, server);
        }
        catch (Exception ex)
        {
            Log($"AdminPipe: begin accept failed: {ex.Message}");
            lock (_sync) _listeners.Remove(server);
            try { server.Dispose(); } catch { }
        }
    }

    private void OnConnected(IAsyncResult ar)
    {
        if (ar.AsyncState is not NamedPipeServerStream server) return;
        try { server.EndWaitForConnection(ar); }
        catch { return; } // 被 Stop() Dispose 或连接失败

        lock (_sync) _listeners.Remove(server);
        if (!_cts.IsCancellationRequested) AddListener(); // 回填监听实例

        Task.Run(() => HandleConnection(server));
    }

    private void HandleConnection(NamedPipeServerStream server)
    {
        try
        {
            server.ReadTimeout = IoTimeoutMs;
            server.WriteTimeout = IoTimeoutMs;

            bool authed = false;
            while (true)
            {
                // ---- 读请求头(12B): [magic][cmd][len] ----
                byte[] header = new byte[HeaderSize];
                if (!ReadExactly(server, header, HeaderSize)) break;
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
                    WriteResponse(server, BrokerAdminPipe.ErrorInvalidParameter, "payload too large");
                    break;
                }

                byte[] payload = len > 0 ? new byte[len] : [];
                if (len > 0 && !ReadExactly(server, payload, (int)len)) break;

                // ---- 首条必须 AUTH(协议 §2.2) ----
                if (!authed)
                {
                    if (cmd != (uint)BrokerAdminPipe.Cmd.Auth)
                    {
                        Log("AdminPipe: command before AUTH, rejected");
                        WriteResponse(server, BrokerAdminPipe.ErrorAccessDenied, "AUTH required");
                        break;
                    }
                    uint status = HandleAuth(server, payload);
                    WriteResponse(server, status, status == 0 ? null : "AUTH failed");
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
                if (!WriteResponse(server, result, text)) break;
            }
        }
        catch (Exception ex)
        {
            Log($"AdminPipe: connection error: {ex.Message}");
        }
        finally
        {
            try { server.Dispose(); } catch { }
        }
    }

    /// <summary>AUTH(协议 §2.4/§2.5): [pid u32][sha256 hex 64B], 白名单校验 + DevMode 放行。</summary>
    private uint HandleAuth(NamedPipeServerStream server, byte[] payload)
    {
        if (payload.Length != 4 + 64) return BrokerAdminPipe.ErrorAccessDenied;
        uint pid = BitConverter.ToUInt32(payload, 0);
        string hash = Encoding.ASCII.GetString(payload, 4, 64);
        if (!IsHex64(hash)) return BrokerAdminPipe.ErrorAccessDenied;

        // 加固: AUTH 载荷中的 pid 必须与真实客户端进程一致(协议未强制, 防伪造)
        if (!IsClientPid(server, pid))
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
    private static bool WriteResponse(NamedPipeServerStream server, uint status, string? text)
    {
        byte[] payload = text != null ? Encoding.UTF8.GetBytes(text) : [];
        if (payload.Length > BrokerAdminPipe.MaxPayload)
            payload = payload[..BrokerAdminPipe.MaxPayload];

        byte[] header = new byte[HeaderSize];
        BitConverter.GetBytes(BrokerAdminPipe.Magic).CopyTo(header, 0);
        BitConverter.GetBytes(status).CopyTo(header, 4);
        BitConverter.GetBytes((uint)payload.Length).CopyTo(header, 8);
        try
        {
            server.Write(header, 0, header.Length);
            if (payload.Length > 0) server.Write(payload, 0, payload.Length);
            server.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadExactly(NamedPipeServerStream server, byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n;
            try { n = server.Read(buffer, offset, count - offset); }
            catch { return false; }
            if (n <= 0) return false;
            offset += n;
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

    private static bool IsClientPid(NamedPipeServerStream server, uint claimed)
    {
        try
        {
            return GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out uint actual)
                && actual == claimed;
        }
        catch
        {
            return false;
        }
    }

    private static PipeSecurity BuildPipeSecurity()
    {
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm(BrokerAdminPipe.PipeSddl);
        return security;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipeHandle, out uint clientProcessId);

    private static void Log(string msg) => BrokerLogger.Log(msg);
}
