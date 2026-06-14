// SysMonCmdPal/Broker/BrokerDetector.cs
// 检测 SysMonBroker 进程是否在运行

using System;
using System.Diagnostics;

namespace SysMonCmdPal.Broker;

/// <summary>检测 SysMonBroker 进程是否在运行</summary>
public static class BrokerDetector
{
    private const string BrokerProcessName = "SysMonBroker";

    /// <summary>SysMonBroker 进程是否正在运行</summary>
    public static bool IsBrokerRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(BrokerProcessName);
            return processes.Length > 0;
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
            var processes = Process.GetProcessesByName(BrokerProcessName);
            return processes.Length > 0 ? processes[0].Id : -1;
        }
        catch { return -1; }
    }
}
