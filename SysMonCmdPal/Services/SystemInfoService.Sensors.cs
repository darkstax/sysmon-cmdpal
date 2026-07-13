using System;
using System.Diagnostics;
using System.Linq;

namespace SysMonCmdPal;

public partial class SystemInfoService
{
    private static void TryReadSensors(ref SystemSnapshot snapshot)
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
                // 主 GPU：优先独显（有独立显存），其次有负载的，最后温度最高的
                var primary = gpuResults
                    .OrderByDescending(g => g.MemoryTotalMB > 0 ? 1 : 0)  // 有独立显存 = 独显优先
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
            Debug.WriteLine($"[SysMon] TryReadSensors 异常: {ex.GetType().Name}: {ex.Message}");
            snapshot.CpuTemperature = -1;
            snapshot.Backend = SensorBackend.None;
            snapshot.BackendNote = Loc.Format("Backend.Exception", ex.Message);
            snapshot.Gpu = new GpuInfo { UsagePercent = -1, Temperature = -1 };
            snapshot.Gpus = [];
        }
    }
}
