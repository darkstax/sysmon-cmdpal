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
        var brokerSnap = BrokerPushReceiver.Instance.Snapshot;
        if (brokerSnap.IsFresh && brokerSnap.AllSensors.Count > 0)
        {
            double cpuPower = 0;
            double gpuPower = 0;
            var gpuParts = new System.Collections.Generic.List<string>();

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
                    // GPU：每个硬件取最大值（GPU Core/GPU Package 是总功率，GPU SoC 是子项）
                    // 按 HardwareTag 区分不同 GPU，每个 GPU 只取一个最大功率
                    string hwKey = s.HardwareTag.ToString();
                    // 只取 GPU Core / GPU Package（总功率），跳过 SoC 等子项
                    if (s.Name.Contains("Core", System.StringComparison.OrdinalIgnoreCase) ||
                        s.Name.Contains("Package", System.StringComparison.OrdinalIgnoreCase))
                    {
                        gpuPower += s.Value;
                        string hwName = s.HardwareTag switch
                        {
                            ShmLayout.HwGpuNvidia => "dGPU",
                            ShmLayout.HwGpuAmd => "iGPU",
                            _ => "GPU"
                        };
                        gpuParts.Add($"{hwName} {s.Value:F1}");
                    }
                }
            }

            if (cpuPower > 0)
            {
                double total = cpuPower + gpuPower;
                var detailParts = new System.Collections.Generic.List<string> { $"CPU {cpuPower:F1}" };
                detailParts.AddRange(gpuParts);
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
