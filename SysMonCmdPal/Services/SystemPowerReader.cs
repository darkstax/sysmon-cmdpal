// Copyright (c) 2026 SysMonCmdPal
// 系统功耗读取器 — 回退链: Broker (LHM RAPL) → HWiNFO
// 返回 CPU + GPU 功率总和，双重供电时让用户看到完整系统功耗

using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

public readonly record struct SystemPowerResult(double Power, string Source)
{
    public bool IsValid => Power > 0;
    public static SystemPowerResult None => new(-1, "无");
}

internal static class SystemPowerReader
{
    /// <summary>
    /// 读取系统功耗 (W) = CPU 功率 + GPU 功率。回退链：
    /// 1. Broker 共享内存 (LHM, 管理员) — CpuPower + GpuPower
    /// 2. HWiNFO 共享内存 (用户态) — CPU Package Power
    /// </summary>
    public static SystemPowerResult Read()
    {
        // 1. Broker 共享内存（LHM 全量传感器）
        var broker = BrokerPushReceiver.Instance;
        bool isBrokerAvailable = broker.TryGetAvailableSnapshot(out var brokerSnap);
        if (isBrokerAvailable && brokerSnap.AllSensors.Count > 0)
        {
            double cpuPower = 0;
            var gpuByHardware = new System.Collections.Generic.Dictionary<int, double>();

            foreach (var s in brokerSnap.AllSensors)
            {
                if (s.Tag == ShmLayout.TagCpuPower && s.Value > 0)
                {
                    // CPU：只取 "Package"（总功率），跳过 Core 子项
                    if (s.Name.Contains("Package", System.StringComparison.OrdinalIgnoreCase))
                        cpuPower = s.Value;
                }
                else if (s.Tag == ShmLayout.TagGpuPower && s.Value > 0)
                {
                    // GPU：每个硬件实例取最大值（GPU Core/GPU Package 是总功率，GPU SoC 是子项）
                    // HardwareTag 低 8 位是硬件类型，高位是同类型实例 index。
                    // 只取 GPU Core / GPU Package（总功率），跳过 SoC 等子项
                    if (s.Name.Contains("Core", System.StringComparison.OrdinalIgnoreCase) ||
                        s.Name.Contains("Package", System.StringComparison.OrdinalIgnoreCase))
                    {
                        if (!gpuByHardware.TryGetValue(s.HardwareTag, out double current) || s.Value > current)
                            gpuByHardware[s.HardwareTag] = s.Value;
                    }
                }
            }

            if (cpuPower > 0)
            {
                double gpuPower = gpuByHardware.Values.Sum();
                double total = cpuPower + gpuPower;
                var detailParts = new System.Collections.Generic.List<string> { $"CPU {cpuPower:F1}" };
                foreach (var kvp in gpuByHardware.OrderBy(kvp => kvp.Key))
                {
                    int hwType = ShmLayout.HardwareTypeFromTag(kvp.Key);
                    int hwInstance = ShmLayout.HardwareInstanceFromTag(kvp.Key);
                    string hwName = hwType switch
                    {
                        ShmLayout.HwGpuNvidia => "dGPU",
                        ShmLayout.HwGpuAmd => "AMD GPU",
                        ShmLayout.HwGpuIntel => "iGPU",
                        _ => "GPU"
                    };
                    if (hwInstance > 0)
                        hwName = $"{hwName} #{hwInstance + 1}";
                    detailParts.Add($"{hwName} {kvp.Value:F1}");
                }
                string detail = string.Join(" + ", detailParts);
                return new SystemPowerResult(total, $"Broker ({detail})");
            }
        }

        // 2. HWiNFO 共享内存（用户态，CPU Package Power）
        var hwinfo = HwinfoSharedMemoryReader.Instance;
        if (hwinfo.IsAvailable)
        {
            try
            {
                var (cpup, cpuLabel) = hwinfo.ReadCpuPower();
                var (gpup, gpuDetail) = hwinfo.ReadGpuPower();
                if (cpup > 0)
                {
                    double total = cpup + (gpup > 0 ? gpup : 0);
                    string detail = gpup > 0
                        ? $"CPU {cpup:F1} + GPU {gpuDetail}"
                        : $"CPU {cpup:F1}";
                    return new SystemPowerResult(total, $"HWiNFO ({detail})");
                }
            }
            catch (Exception ex)
            {
                SensorLogger.ForceLog($"SystemPower HWiNFO 异常: {ex.Message}");
            }
        }

        return SystemPowerResult.None;
    }
}
