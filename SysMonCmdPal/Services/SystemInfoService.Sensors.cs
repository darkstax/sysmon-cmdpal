using System;
using System.Diagnostics;
using System.Linq;

namespace SysMonCmdPal;

public partial class SystemInfoService
{
    /// <summary>
    /// T1-1: 由 <see cref="SensorInfoSource"/> 作为采集源调用。分型回退链（§3 约束 1）：
    /// GPU = Broker SHM → HWiNFO → D3DKMT → PDH（ThermalZone 不得进入）；
    /// CPU 温度 = Broker SHM → HWiNFO → ThermalZone → None。
    /// GPU 主卡选择、BackendNote 拼接与异常哨兵写入语义完全未改动，
    /// 仅由 private 提为 internal 以供源适配器复用。
    /// </summary>
    internal static void TryReadSensors(ref SystemSnapshot snapshot)
    {
        try
        {
            var cpuResult = CpuSensorReader.Read();
            var gpuResults = GpuSensorReader.ReadAll();

            snapshot.CpuTemperature = cpuResult.Temperature;
            snapshot.Backend = cpuResult.Source switch
            {
                string s when s.StartsWith("Broker") => SensorBackend.Broker,
                string s when s.Contains("HWiNFO") => SensorBackend.HWiNFO,
                "ThermalZone" => SensorBackend.ThermalZone,
                _ => SensorBackend.None,
            };

            // HWiNFO 12h 重置检测
            var hwinfo = HwinfoSharedMemoryReader.Instance;
            snapshot.HwinfoNearReset = hwinfo.IsNearResetWindow;
            snapshot.HwinfoTimeRemaining = hwinfo.TimeUntilReset;

            // 构建多 GPU 数组
            if (gpuResults.Count > 0)
            {
                snapshot.Gpus = gpuResults.Select(r => new GpuInfo
                {
                    Name = r.Name,
                    UsagePercent = r.UsagePercent,
                    Temperature = r.Temperature,
                    MemoryUsedMB = r.MemoryUsedMB,
                    MemoryTotalMB = r.MemoryTotalMB,
                }).ToArray();
                // 主 GPU：优先独显，其次有负载的，最后温度最高的。
                // T2-3（P1-3）：独显判定改由单一身份来源 GpuIdentityService 给出的 Kind 决定，
                // 不再用「MemoryTotalMB > 0 = 有独立显存 = 独显」这一名称/显存猜测——
                // 本机活体实测 APU 集显 unit 也携带 "GPU D3D Memory Dedicated"（WDDM 分段），
                // 旧判据会把集显选成主卡；Kind 同时保证虚拟卡（ROOT\* 前缀判据）
                // 不会凭显存读数抢主卡。Kind 全部 Unknown 时退回旧的显存优先序，行为不劣化。
                var primary = gpuResults
                    .OrderByDescending(g => g.Kind == GpuKind.Discrete ? 1 : 0)
                    .ThenByDescending(g => g.MemoryTotalMB > 0 ? 1 : 0)   // 身份未判定时的兼容兜底
                    .ThenByDescending(g => g.UsagePercent > 0 ? 1 : 0)
                    .ThenByDescending(g => g.Temperature)
                    .First();
                snapshot.Gpu = new GpuInfo
                {
                    Name = primary.Name,
                    UsagePercent = primary.UsagePercent,
                    Temperature = primary.Temperature,
                    MemoryUsedMB = primary.MemoryUsedMB,
                    MemoryTotalMB = primary.MemoryTotalMB,
                };
            }
            else
            {
                snapshot.Gpus = [];
                snapshot.Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 };
            }

            // 后端描述
            string gpuSource = gpuResults.Count > 0 ? gpuResults[0].Source : "无";
            snapshot.BackendNote = cpuResult.Source == gpuSource
                ? (cpuResult.Source == "无" ? Loc.Get("Backend.BothUnavailable") : Loc.Format("Backend.DataSource", cpuResult.Source))
                : $"CPU: {cpuResult.Source}, GPU: {gpuSource}" +
                  (snapshot.Gpus.Length > 1 ? Loc.Format("Backend.GpuCount", snapshot.Gpus.Length) : "");

            // HWiNFO 12h 警告追加到后端描述
            if (snapshot.HwinfoNearReset && snapshot.Backend == SensorBackend.HWiNFO)
            {
                var remaining = snapshot.HwinfoTimeRemaining;
                snapshot.BackendNote += remaining.TotalMinutes > 0
                    ? Loc.Format("Backend.HwinfoWarningSoon", (int)remaining.TotalMinutes)
                    : Loc.Get("Backend.HwinfoExpired");
            }
        }
        catch (Exception ex)
        {
            // 诊断（T2-1 必做项）：CPU/GPU 同时全灭时，原实现只有 Debug.WriteLine，
            // Release 宿主里看不到任何原因指纹。文案不含路径（§6 本地化/权限约束）。
            Debug.WriteLine($"[SysMon] TryReadSensors 异常: {ex.GetType().Name}: {ex.Message}");
            SensorLogger.ForceLog($"[Sensors] TryReadSensors 异常: {ex.GetType().Name}: " +
                                  $"{(ex.Message.Length <= 160 ? ex.Message : ex.Message[..160] + "…")}");
            snapshot.CpuTemperature = -1;
            snapshot.Backend = SensorBackend.None;
            snapshot.BackendNote = Loc.Format("Backend.Exception", ex.Message);
            snapshot.Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 };
            snapshot.Gpus = [];
        }
    }
}
