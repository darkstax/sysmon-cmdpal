// Copyright (c) 2026 SysMonCmdPal
// T1-1: 静态读取器 → ISystemInfoSource 适配器。
// 采集逻辑零改动 — 仅把原来 SystemInfoService.Refresh() 内的直调用法统一为可注册的源对象。

namespace SysMonCmdPal;

/// <summary>内存指标源（SystemMemoryReader: GlobalMemoryStatusEx，内部吞异常语义不变）。</summary>
internal sealed class MemoryInfoSource : ISystemInfoSource
{
    public void ReadInto(ref SystemSnapshot snapshot) => SystemMemoryReader.Read(ref snapshot);
}

/// <summary>电池指标源（SystemBatteryReader: GetSystemPowerStatus + WMI 充电状态判定，内部吞异常语义不变）。</summary>
internal sealed class BatteryInfoSource : ISystemInfoSource
{
    public void ReadInto(ref SystemSnapshot snapshot) => SystemBatteryReader.Read(ref snapshot);
}

/// <summary>
/// 传感器源（分型回退链，见 docs/TECHNICAL_ROADMAP.md §3 约束 1）：
///   GPU      = Broker SHM → HWiNFO → D3DKMT → PDH（ThermalZone 不得进入——ACPI 仅有 CPU 热区）；
///   CPU 温度 = Broker SHM → HWiNFO → ThermalZone → None。
/// 回退链、GPU 主卡选择、BackendNote 拼接与异常哨兵写入全部保留在 TryReadSensors 内，未改动。
/// </summary>
internal sealed class SensorInfoSource : ISystemInfoSource
{
    public void ReadInto(ref SystemSnapshot snapshot) => SystemInfoService.TryReadSensors(ref snapshot);
}
