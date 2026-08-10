// SysMonCmdPal/Broker/BrokerDetector.cs
// 检测 SysMonBroker 进程是否在运行

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SysMonCmdPal.Broker;

/// <summary>检测 SysMonBroker 进程是否在运行</summary>
public static class BrokerDetector
{
    private const string BrokerProcessName = "SysMonBroker";
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint WaitTimeout = 0x00000102; // WAIT_TIMEOUT：进程仍在运行

    /// <summary>SysMonBroker 进程是否正在运行</summary>
    public static bool IsBrokerRunning()
    {
        try
        {
            return GetBrokerPidCore() > 0;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[BrokerDetector] 检测异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>获取 Broker 进程 PID（未运行返回 -1）</summary>
    public static int GetBrokerPid()
    {
        try
        {
            return GetBrokerPidCore();
        }
        catch { return -1; }
    }

    private static int GetBrokerPidCore()
    {
        string expectedPath = Path.GetFullPath(global::SysMonCmdPal.BrokerInstaller.BrokerPath);
        foreach (Process process in Process.GetProcessesByName(BrokerProcessName))
        {
            using (process)
            {
                string? processPath = TryGetProcessPath(process.Id, out bool confirmedAlive);
                if (!confirmedAlive)
                    continue;

                if (processPath != null && string.Equals(
                    Path.GetFullPath(processPath),
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return process.Id;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// 打开候选 PID 并确认其存活。进程刚退出的快照窗口内 PID 可能已被回收，
    /// 此时读取到的路径不可信；<paramref name="confirmedAlive"/> 为 false 时调用方必须跳过。
    /// </summary>
    private static string? TryGetProcessPath(int processId, out bool confirmedAlive)
    {
        using SafeProcessHandle handle = OpenProcess(
            ProcessQueryLimitedInformation,
            bInheritHandle: false,
            processId);
        if (handle.IsInvalid)
        {
            confirmedAlive = false;
            SensorLogger.ForceLog(
                $"[BrokerDetector] OpenProcess 失败 PID={processId}，Win32 错误码={Marshal.GetLastWin32Error()}");
            return null;
        }

        // WaitForSingleObject(hProcess, 0)：返回 WAIT_OBJECT_0 表示进程已退出。
        confirmedAlive = WaitForSingleObject(handle, 0) == WaitTimeout;
        if (!confirmedAlive)
            return null;

        var path = new StringBuilder(32768);
        int length = path.Capacity;
        return QueryFullProcessImageName(handle, 0, path, ref length)
            ? path.ToString(0, length)
            : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
        int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle hProcess,
        uint dwFlags,
        StringBuilder lpExeName,
        ref int lpdwSize);
}
