// SysMonBroker/PushClient.cs
// Broker 侧 COM 客户端 — 查找 Plugin 的 COM 对象并推送数据
// 使用 late-binding (dynamic) 避免需要共享程序集

using System;
using System.Runtime.InteropServices;

namespace SysMonBroker;

/// <summary>
/// 查找并调用 SysMonCmdPal Plugin 的 ISysMonBrokerPush COM 接口。
/// Broker 以管理员运行，通过 COM 跨进程调用 AppContainer 内的 Plugin。
/// 使用 late-binding (dynamic) 避免需要引用 Plugin 程序集。
/// </summary>
public sealed class PushClient : IDisposable
{
    // Plugin 的 COM CLSID（与 SysMonExtension.cs 中的 Guid 一致）
    private static readonly Guid CLSID_SysMonExtension =
        new("A1B2C3D4-E5F6-7890-ABCD-EF1234567890");

    private dynamic? _proxy;
    private bool _connected;

    /// <summary>尝试连接到 Plugin 的 COM 对象</summary>
    public bool Connect()
    {
        if (_connected) return true;

        try
        {
            // CoCreateInstance — 查找已注册的 COM 对象
            var hr = CoCreateInstance(
                ref CLSID_SysMonExtension,
                null,
                4, // CLSCTX_LOCAL_SERVER
                new Guid("00000000-0000-0000-C000-000000000046"), // IID_IUnknown
                out object obj);

            if (hr == 0 && obj != null)
            {
                _proxy = obj;
                _connected = true;
                Log("PushClient: connected to Plugin COM object");
                return true;
            }

            Log($"PushClient: CoCreateInstance failed, hr=0x{hr:X8}");
        }
        catch (Exception ex)
        {
            Log($"PushClient: Connect error: {ex.Message}");
        }

        return false;
    }

    /// <summary>推送 CPU 温度</summary>
    public void PushCpuTemp(double celsius, string source)
    {
        if (!_connected) return;
        try { _proxy.PushCpuTemp(celsius, source); }
        catch (Exception ex) { Log($"PushCpuTemp error: {ex.Message}"); Disconnect(); }
    }

    /// <summary>推送 GPU 数据</summary>
    public void PushGpuData(int index, string name, double temp,
        double usage, double memUsed, double memTotal)
    {
        if (!_connected) return;
        try { _proxy.PushGpuData(index, name, temp, usage, memUsed, memTotal); }
        catch (Exception ex) { Log($"PushGpuData error: {ex.Message}"); Disconnect(); }
    }

    /// <summary>心跳</summary>
    public void Ping()
    {
        if (!_connected) return;
        try { _proxy.Ping(); }
        catch (Exception ex) { Log($"Ping error: {ex.Message}"); Disconnect(); }
    }

    private void Disconnect()
    {
        _connected = false;
        if (_proxy != null)
        {
            try { Marshal.ReleaseComObject(_proxy); } catch { }
            _proxy = null;
        }
        Log("PushClient: disconnected, will retry");
    }

    public void Dispose()
    {
        Disconnect();
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsCtx,
        ref Guid riid, out object ppv);

    private static void Log(string msg)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
    }
}
