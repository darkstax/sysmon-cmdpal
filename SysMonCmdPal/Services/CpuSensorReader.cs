// Copyright (c) 2026 SysMonCmdPal
// CPU 温度读取器 — v1.5 三层回退
// 回退链: Broker 共享内存 → HWiNFO 共享内存 → ACPI ThermalZone

using System;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

public readonly record struct CpuTempResult(double Temperature, string Source)
{
    public bool IsValid => Temperature > 0;
    public static CpuTempResult None => new(-1, "无");
}

internal static class CpuSensorReader
{
    public static CpuTempResult Read()
    {
        // 1. Broker 共享内存推送（最高精度，需 SysMonBroker 以管理员运行）
        var broker = BrokerPushReceiver.Instance;
        if (broker.TryGetAvailableSnapshot(out var brokerSnap))
        {
            if (brokerSnap.CpuTemperature > 0)
                return new CpuTempResult(brokerSnap.CpuTemperature, brokerSnap.CpuSource);
        }

        // 2. HWiNFO 共享内存（用户态，不需要管理员权限）
        var hwinfo = HwinfoSharedMemoryReader.Instance;
        if (hwinfo.IsAvailable)
        {
            try
            {
                var (temp, label) = hwinfo.ReadCpuTemp();
                if (temp > 0)
                    return new CpuTempResult(temp, $"HWiNFO ({label})");
            }
            catch (Exception ex)
            {
                SensorLogger.ForceLog($"CPU HWiNFO 异常: {ex.Message}");
            }
        }

        // 3. ACPI ThermalZone（Windows 原生，精度差 5-15°C）
        try
        {
            if (ThermalZoneReader.Instance.IsAvailable)
            {
                double temp = ThermalZoneReader.Instance.ReadCpuTemp();
                if (temp > 0)
                    return new CpuTempResult(temp, "ThermalZone");
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"CPU ThermalZone 异常: {ex.Message}");
        }

        SensorLogger.ForceLog("CPU: 所有数据源不可用");
        return CpuTempResult.None;
    }
}
