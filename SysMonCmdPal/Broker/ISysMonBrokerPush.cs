// SysMonCmdPal/Broker/ISysMonBrokerPush.cs
// COM 接口：Broker → Plugin 推送传感器数据
// Broker 以管理员运行，通过 COM 调用此接口向 AppContainer 内的 Plugin 推送数据

using System;
using System.Runtime.InteropServices;

namespace SysMonCmdPal.Broker;

/// <summary>
/// Broker 推送接口。Broker 作为 COM 客户端调用此接口。
/// Plugin 作为 COM Server 暴露此接口。
/// </summary>
[ComVisible(true)]
[Guid("F1A2B3C4-D5E6-7890-ABCD-EF1234567891")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISysMonBrokerPush
{
    /// <summary>推送 CPU 温度</summary>
    /// <param name="celsius">温度（摄氏度），不可用时传 -1</param>
    /// <param name="source">数据源标识（如 "Broker_SMU", "Broker_MSR"）</param>
    void PushCpuTemp(double celsius, string source);

    /// <summary>推送 GPU 数据</summary>
    /// <param name="gpuIndex">GPU 索引（0-based）</param>
    /// <param name="name">GPU 名称</param>
    /// <param name="tempCelsius">温度，不可用传 -1</param>
    /// <param name="usagePercent">负载百分比，不可用传 -1</param>
    /// <param name="memUsedMB">显存已用 MB</param>
    /// <param name="memTotalMB">显存总量 MB</param>
    void PushGpuData(int gpuIndex, string name, double tempCelsius,
        double usagePercent, double memUsedMB, double memTotalMB);

    /// <summary>心跳 — Broker 定期调用表示存活</summary>
    void Ping();
}
