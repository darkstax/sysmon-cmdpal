// Copyright (c) 2026 SysMonCmdPal
// HWiNFO 共享内存 buffer fixture —— 仿 BrokerTestData.V2Buffer 风格。
//
// 为什么必须新增：T2-1/T2-3 的主战场 GpuSensorReader/ReadGpusFromHwinfo 此前**零覆盖**
// —— 全仓对 HWiNFO 的测试用法只有 BrokerConsumerFallbackTests.DisableHwinfo()
// （反射把 _available 置 false，即只用来「关掉」这条链），从未构造过 HWiNFO buffer。
// 因此「给定 unit/label 序列 → 归属正确」这条判据根本无法进 307 门禁。
//
// 字段偏移全部来自本机（HWiNFO v7.3x / 双 GPU）逐字节活体解码，而非任何论坛/逆向帖断言：
//   header : 0 signature / 4 version1 / 8 version2 / 12,16 时间戳
//            20 unitsOffset / 24 unitSize / 28 unitCount
//            32 entryOffset / 36 entrySize / 40 entryCount
//   unit   : 0 id / 4 instance / 8 name(char[128])，本机记录 392B（名称另有 2 份本地化副本）
//   entry  : 0 type / 4 sensor_index / 8 entryId / 12 label(char[128]) / 268 单位串 / 284 value
//            本机记录 460B
//
// 用例参数采用本机真实字符串（ROOT\DISPLAY\0000、VEN_1002&DEV_1681、
// 'iGPU [#1]: AMD Radeon 680M'、'GPU Temperature' 等），
// 但**不依赖活体 HWiNFO/COM/WMI**：全部为内存字节，CI/无 HWiNFO 环境同样全绿。

using System;
using System.Collections.Generic;
using System.Text;

namespace SysMonCmdPal.Tests;

internal sealed class HwinfoUnitSpec
{
    public int Id;
    public int Instance;
    public string Name = "";
}

internal sealed class HwinfoEntrySpec
{
    public int Type;
    public int SensorIndex;
    public int EntryId;
    public string Label = "";
    public double Value;
    public string Unit = "";
}

internal static class HwinfoTestData
{
    public const uint Signature = HwinfoSharedMemoryLayout.Signature;
    public const int UnitsOffset = 48;
    public const int DefaultUnitSize = 392;
    public const int DefaultEntrySize = 460;

    private static string AsciiZ(string s) => s.Length == 0 ? "" : s;

    public static byte[] BuildSection(
        IReadOnlyList<HwinfoUnitSpec> units,
        IReadOnlyList<HwinfoEntrySpec> entries,
        int unitSize = DefaultUnitSize,
        int entrySize = DefaultEntrySize,
        int version1 = 2,
        int version2 = 1)
    {
        int unitsOffset = UnitsOffset;
        int entryOffset = unitsOffset + unitSize * units.Count;
        int total = entryOffset + entrySize * entries.Count;
        var data = new byte[total];

        WriteI32(data, 0, unchecked((int)Signature));
        WriteI32(data, 4, version1);
        WriteI32(data, 8, version2);
        WriteI32(data, 20, unitsOffset);
        WriteI32(data, 24, unitSize);
        WriteI32(data, 28, units.Count);
        WriteI32(data, 32, entryOffset);
        WriteI32(data, 36, entrySize);
        WriteI32(data, 40, entries.Count);

        for (int u = 0; u < units.Count; u++)
        {
            int b = unitsOffset + unitSize * u;
            if (b + unitSize > data.Length) break;    // 越界由 fail-fast 校验捕获
            WriteI32(data, b + HwinfoSharedMemoryLayout.UnitId, units[u].Id);
            WriteI32(data, b + HwinfoSharedMemoryLayout.UnitInstance, units[u].Instance);
            WriteAscii(data, b + HwinfoSharedMemoryLayout.UnitName, units[u].Name, HwinfoSharedMemoryLayout.HwinfoStrLen);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            int b = entryOffset + entrySize * i;
            if (b + HwinfoSharedMemoryLayout.EntryValue + sizeof(double) > data.Length) break;
            WriteI32(data, b + HwinfoSharedMemoryLayout.EntryType, entries[i].Type);
            WriteI32(data, b + HwinfoSharedMemoryLayout.EntrySensorIndex, entries[i].SensorIndex);
            WriteI32(data, b + HwinfoSharedMemoryLayout.EntryId, entries[i].EntryId);
            WriteAscii(data, b + HwinfoSharedMemoryLayout.EntryLabel, entries[i].Label, HwinfoSharedMemoryLayout.HwinfoStrLen);
            WriteAscii(data, b + HwinfoSharedMemoryLayout.EntryUnitOfMeasure, entries[i].Unit, 16);
            WriteDbl(data, b + HwinfoSharedMemoryLayout.EntryValue, entries[i].Value);
        }

        return data;
    }

    /// <summary>构造 + 解析，断言解析成功并返回快照（units 面状态由调用方断言）。</summary>
    public static HwinfoLayoutSnapshot Snapshot(
        IReadOnlyList<HwinfoUnitSpec> units,
        IReadOnlyList<HwinfoEntrySpec> entries,
        int unitSize = DefaultUnitSize,
        int entrySize = DefaultEntrySize,
        int version1 = 2,
        int version2 = 1)
    {
        byte[] data = BuildSection(units, entries, unitSize, entrySize, version1, version2);
        if (!HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out string failure))
            throw new InvalidOperationException($"fixture layout rejected: {failure}");
        return snapshot!;
    }

    /// <summary>构造 + 解析，返回 TryParse 的结果三元组（fail-fast 用例用）。</summary>
    public static (bool Ok, string Failure, HwinfoLayoutSnapshot? Snapshot) TrySnapshot(
        IReadOnlyList<HwinfoUnitSpec> units,
        IReadOnlyList<HwinfoEntrySpec> entries,
        int unitSize = DefaultUnitSize,
        int entrySize = DefaultEntrySize)
    {
        byte[] data = BuildSection(units, entries, unitSize, entrySize);
        bool ok = HwinfoSharedMemoryLayout.TryParse(data, out var snapshot, out string failure);
        return (ok, failure, snapshot);
    }

    // ==================== GPU 信号快捷构造 ====================

    public const int TypeTemp = 1;
    public const int TypeUsage = 7;
    public const int TypeOther = 8;

    public static HwinfoEntrySpec Temp(int sensorIndex, double value, string label = "GPU Temperature") =>
        new() { Type = TypeTemp, SensorIndex = sensorIndex, EntryId = 0x01000000, Label = label, Value = value, Unit = "°C" };

    public static HwinfoEntrySpec Load(int sensorIndex, double value, string label) =>
        new() { Type = TypeUsage, SensorIndex = sensorIndex, EntryId = 0x07000000, Label = label, Value = value, Unit = "%" };

    public static HwinfoEntrySpec Memory(int sensorIndex, double value, string label) =>
        new() { Type = TypeOther, SensorIndex = sensorIndex, EntryId = 0x08000000, Label = label, Value = value, Unit = "MB" };

    public static HwinfoUnitSpec Unit(string name, int id = -1, int instance = 0) =>
        new() { Name = name, Id = id, Instance = instance };

    /// <summary>本机活体解码的真实双 GPU unit 名（顺序 = units 数组下标 10/11 的语义）。</summary>
    public const string IgpuUnitName = "iGPU [#1]: AMD Radeon 680M";
    public const string DgpuUnitName = "dGPU [#0]: NVIDIA GeForce RTX 4060 Laptop";

    /// <summary>本机 WMI/SetupDi 侧真实设备名（与 HWiNFO 写法不一致，正是启发式要处理的）。</summary>
    public const string IgpuOsName = "AMD Radeon(TM) Graphics";
    public const string DgpuOsName = "NVIDIA GeForce RTX 4060 Laptop GPU";

    /// <summary>本机真实虚拟卡（三条都不命中 GpuClassifier 名称 marker，必须靠前缀判据）。</summary>
    public const string VirtualIdMuMu = @"ROOT\DISPLAY\0000";
    public const string VirtualIdGameViewer = @"ROOT\DISPLAY\0001";
    public const string VirtualIdZako = @"ROOT\DISPLAY\0002";
    public const string VirtualNameZako = "Zako Display Adapter";

    /// <summary>本机真实物理 PNPDeviceID（与 WMI PNPDeviceID 逐字同形，SetupDi 实测）。</summary>
    public const string PhysicalIdAmd = @"PCI\VEN_1002&DEV_1681&SUBSYS_19DD1043&REV_0A\4&31F4EC98&0&0041";
    public const string PhysicalIdNvidia = @"PCI\VEN_10DE&DEV_28E0&SUBSYS_19DD1043&REV_A1\4&792F56&0&0009";

    // ==================== 字节写入 ====================

    private static void WriteI32(byte[] data, int offset, int value)
    {
        if (offset < 0 || offset + 4 > data.Length) return;
        BitConverter.TryWriteBytes(data.AsSpan(offset), value);
    }

    private static void WriteDbl(byte[] data, int offset, double value)
    {
        if (offset < 0 || offset + 8 > data.Length) return;
        BitConverter.TryWriteBytes(data.AsSpan(offset), value);
    }

    private static void WriteAscii(byte[] data, int offset, string text, int fieldLen)
    {
        if (offset < 0 || offset >= data.Length) return;
        byte[] bytes = Encoding.UTF8.GetBytes(AsciiZ(text));
        int n = Math.Min(bytes.Length, fieldLen - 1);
        if (offset + n > data.Length) n = Math.Max(0, data.Length - offset);
        Array.Copy(bytes, 0, data, offset, n);
    }

    // ==================== 身份提供者 fixture（零 COM / 零 WMI） ====================

    internal sealed class FakeGpuIdentityProvider : IGpuIdentityProvider
    {
        private readonly List<GpuDisplayDevice> _devices;
        public int EnumerateCalls { get; private set; }
        public bool ThrowOnEnumerate { get; set; }

        public FakeGpuIdentityProvider(params GpuDisplayDevice[] devices) => _devices = [.. devices];

        /// <summary>本机实测形状：物理 AMD + 物理 NVIDIA + 三张 ROOT\DISPLAY 虚拟卡。</summary>
        public static FakeGpuIdentityProvider LocalMachineLike() => new(
            new GpuDisplayDevice(VirtualIdMuMu, "MuMu Virtual Display Adapter"),
            new GpuDisplayDevice(VirtualIdGameViewer, "GameViewer Virtual Display Adapter"),
            new GpuDisplayDevice(VirtualIdZako, VirtualNameZako),
            new GpuDisplayDevice(PhysicalIdAmd, IgpuOsName),
            new GpuDisplayDevice(PhysicalIdNvidia, DgpuOsName));

        public IReadOnlyList<GpuDisplayDevice> EnumerateDisplayDevices()
        {
            EnumerateCalls++;
            if (ThrowOnEnumerate) throw new InvalidOperationException("provider failed");
            return _devices;
        }
    }

    /// <summary>直接构造身份（跳过提供者，用于设置 HwinfoRole/DXGI 辅助值）。</summary>
    public static GpuDeviceIdentity Identity(int serial, string name, string pnp, GpuRoleHint role = GpuRoleHint.None, double? dedicatedMB = null) =>
        new()
        {
            Serial = serial,
            PnpDeviceId = pnp,
            Name = name,
            VendorToken = GpuIdentityService.ParseVendorToken(pnp),
            HwinfoRole = role,
            DedicatedVideoMemoryMB = dedicatedMB,
        };
}
