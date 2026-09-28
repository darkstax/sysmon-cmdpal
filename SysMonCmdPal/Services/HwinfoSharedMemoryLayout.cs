// Copyright (c) 2026 SysMonCmdPal
// HWiNFO 共享内存布局解析层（纯函数，零 IO / 零 COM / 零 WMI）
//
// 为什么单独成文件：
//  1. T2-1 的「units 归属」需要先可靠地解析 units 数组，而布局校验必须能
//     被内存 buffer 驱动测试覆盖（本仓此前对 HWiNFO 的测试用法只有 DisableHwinfo()
//     这一条「关掉链路」的路径，从未构造过 HWiNFO buffer）。
//  2. §7 声称的「格式变更自动禁用」在过去从未真正实现：旧校验只做
//     `160 <= entrySize <= 1024` 这类范围检查，从不把 entrySize 与硬编码字段偏移
//     （EntryLabel=12 / EntryValue=284）交叉验证 ⇒ 任何把记录撑大/挪字段的布局变更
//     会**通过校验并按老偏移静默读出垃圾**，而不是 fail-fast。本层补齐交叉校验。
//
// 字段布局来源：本机 HWiNFO v7.3x 活体逐字节解码实测（非任何论坛/逆向帖的断言）：
//   header : 0 signature(0x53695748) / 4 version1 / 8 version2 / 12..16 时间戳
//            20 unitsOffset / 24 unitSize / 28 unitCount
//            32 entriesOffset / 36 entrySize / 40 entryCount / 44 其它
//   unit   : 0 id(int32) / 4 instance(int32) / 8 name(char[128], ANSI/UTF-8)
//            其后为名称的本地化副本（本机共 3 份，记录 392B）
//   entry  : 0 type(int32) / 4 sensor_index(int32，指向 units 数组下标)
//            8 entryId / 12 label(char[128]) / 140 label 本地化副本
//            268 unit-of-measure 字符串(char[16]) —— 本机实测内容是单位串（'MB'/'%'/'°C'），
//            **不是** unit id，因此 entry→unit 的唯一通路是偏移 4 的 sensor_index
//            （这是 T2-1 不能声称「PNPDeviceID 硬关联」的根本原因之一）。
//            284 value / 292 min / 300 max / 308 avg（4×double）

using System;
using System.Text;

namespace SysMonCmdPal;

/// <summary>units 数组中的一项：GPU/CPU 等硬件分组名（稳定身份的来源）。</summary>
internal sealed class HwinfoUnitRecord
{
    public required int Index { get; init; }        // = 其它记录的 SensorIndex 取值
    public required int Id { get; init; }
    public required int Instance { get; init; }
    public required string Name { get; init; }
}

/// <summary>entries 数组中一项的静态描述（type/归属/标签；数值须另行实时读取）。</summary>
internal sealed class HwinfoEntryDescriptor
{
    public required int Index { get; init; }
    public required int Type { get; init; }
    public required int SensorIndex { get; init; }
    public required string Label { get; init; }

    /// <summary>本周期读数（value 字段，偏移 284）。布局校验已保证该偏移落在记录内。</summary>
    public required double Value { get; init; }
}

/// <summary>解析结果：header 信息 + units 面（可单独禁用）+ entries 面。</summary>
internal sealed class HwinfoLayoutSnapshot
{
    public required int Version1 { get; init; }
    public required int Version2 { get; init; }
    public required int EntryOffset { get; init; }
    public required int EntrySize { get; init; }
    public required int EntryCount { get; init; }
    public required System.Collections.Generic.IReadOnlyList<HwinfoEntryDescriptor> Entries { get; init; }

    /// <summary>units 解析面是否可用（任何越界/缺字段 ⇒ false，但 entries 面仍可用）。</summary>
    public bool UnitsAvailable { get; init; }
    public string UnitsUnavailableReason { get; init; } = "";
    public required System.Collections.Generic.IReadOnlyList<HwinfoUnitRecord> Units { get; init; }
}

internal static class HwinfoSharedMemoryLayout
{
    // ---- 与读取端一致的常量 ----
    internal const uint Signature = 0x53695748;   // "HWiS"
    internal const int HwinfoStrLen = 128;

    // header 字段（int32 下标）
    internal const int HdrSignature = 0;
    internal const int HdrVersion1 = 1;
    internal const int HdrVersion2 = 2;
    internal const int HdrUnitsOffset = 5;   // 字节偏移 20
    internal const int HdrUnitSize = 6;      // 字节偏移 24
    internal const int HdrUnitCount = 7;     // 字节偏移 28
    internal const int HdrEntryOffset = 8;   // 字节偏移 32
    internal const int HdrEntrySize = 9;     // 字节偏移 36
    internal const int HdrEntryCount = 10;   // 字节偏移 40

    // entry 内字段偏移
    internal const int EntryType = 0;
    internal const int EntrySensorIndex = 4;
    internal const int EntryId = 8;
    internal const int EntryLabel = 12;
    internal const int EntryUnitOfMeasure = 268;
    internal const int EntryValue = 284;

    // unit 内字段偏移
    internal const int UnitId = 0;
    internal const int UnitInstance = 4;
    internal const int UnitName = 8;

    /// <summary>
    /// 纯函数解析。fail-fast 语义（不等式，不把本机 460/392 当必需值）：
    ///  - entries 面：entrySize 必须容得下 EntryValue+8，否则**整表不可信**（返回 false）；
    ///  - units 面：unitSize 必须容得下 UnitName+128，且 units 数组不得越过 entries 起点，
    ///    否则只禁用 units 解析面（UnitsAvailable=false + 原因），entries 面照常可用。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out HwinfoLayoutSnapshot? snapshot, out string failure)
    {
        snapshot = null;
        failure = "";

        if (data.Length < (HdrEntryCount + 1) * 4)
        {
            failure = $"section too small for header ({data.Length}B)";
            return false;
        }
        if (BitConverter.ToUInt32(data.Slice(0, 4)) != Signature)
        {
            failure = $"signature 0x{BitConverter.ToUInt32(data.Slice(0, 4)):X8} unexpected";
            return false;
        }

        int unitsOffset = BitConverter.ToInt32(data.Slice(HdrUnitsOffset * 4, 4));
        int unitSize = BitConverter.ToInt32(data.Slice(HdrUnitSize * 4, 4));
        int unitCount = BitConverter.ToInt32(data.Slice(HdrUnitCount * 4, 4));
        int entryOffset = BitConverter.ToInt32(data.Slice(HdrEntryOffset * 4, 4));
        int entrySize = BitConverter.ToInt32(data.Slice(HdrEntrySize * 4, 4));
        int entryCount = BitConverter.ToInt32(data.Slice(HdrEntryCount * 4, 4));
        int version1 = BitConverter.ToInt32(data.Slice(HdrVersion1 * 4, 4));
        int version2 = BitConverter.ToInt32(data.Slice(HdrVersion2 * 4, 4));

        // ---- entries 面：旧范围检查（语义保持） + 新增字段交叉校验 ----
        if (entrySize < 160 || entrySize > 1024 ||
            entryOffset <= 0 || entryOffset > 65536 ||
            entryCount <= 0 || entryCount > 4096)
        {
            failure = $"entry layout out of range: offset={entryOffset} size={entrySize} count={entryCount}";
            return false;
        }
        // 关键 fail-fast（此前缺失）：记录必须容得下被硬编码读取的 value 字段
        if (entrySize < EntryValue + sizeof(double))
        {
            failure = $"entrySize={entrySize} < required {EntryValue + sizeof(double)} (value 字段偏移交叉校验失败)";
            return false;
        }
        long entriesEnd = (long)entryOffset + (long)entrySize * entryCount;
        if (entriesEnd > data.Length)
        {
            failure = $"entries array exceeds section ({entriesEnd} > {data.Length})";
            return false;
        }

        var entries = new System.Collections.Generic.List<HwinfoEntryDescriptor>(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            int b = entryOffset + entrySize * i;
            int type = BitConverter.ToInt32(data.Slice(b + EntryType, 4));
            int sensorIndex = BitConverter.ToInt32(data.Slice(b + EntrySensorIndex, 4));
            string label = ReadAsciiZ(data.Slice(b + EntryLabel, HwinfoStrLen));
            double value = BitConverter.ToDouble(data.Slice(b + EntryValue, sizeof(double)));
            entries.Add(new HwinfoEntryDescriptor
            {
                Index = i, Type = type, SensorIndex = sensorIndex, Label = label, Value = value,
            });
        }

        // ---- units 面：可单独禁用 ----
        var units = new System.Collections.Generic.List<HwinfoUnitRecord>();
        bool unitsOk = false;
        string unitsReason = "";
        if (unitsOffset <= 0 || unitSize <= 0 || unitCount <= 0 || unitCount > 4096)
        {
            unitsReason = $"units header absent/invalid: offset={unitsOffset} size={unitSize} count={unitCount}";
        }
        else if (unitSize < UnitName + HwinfoStrLen)
        {
            unitsReason = $"unitSize={unitSize} < required {UnitName + HwinfoStrLen} (name 字段交叉校验失败)";
        }
        else if ((long)unitsOffset + (long)unitSize * unitCount > entryOffset)
        {
            // 本机实测恰等于 entryOffset（48+392×17=6712）；写成不等式校验，不当巧合
            unitsReason = $"units array overruns entries start ({(long)unitsOffset + (long)unitSize * unitCount} > {entryOffset})";
        }
        else if (unitsOffset < (HdrEntryCount + 1) * 4)
        {
            unitsReason = $"unitsOffset={unitsOffset} overlaps header";
        }
        else
        {
            bool allReadable = true;
            for (int u = 0; u < unitCount; u++)
            {
                int b = unitsOffset + unitSize * u;
                if (b + UnitName + HwinfoStrLen > data.Length) { allReadable = false; unitsReason = $"unit[{u}] name field exceeds section"; break; }
                units.Add(new HwinfoUnitRecord
                {
                    Index = u,
                    Id = BitConverter.ToInt32(data.Slice(b + UnitId, 4)),
                    Instance = BitConverter.ToInt32(data.Slice(b + UnitInstance, 4)),
                    Name = ReadAsciiZ(data.Slice(b + UnitName, HwinfoStrLen)),
                });
            }
            unitsOk = allReadable && string.IsNullOrEmpty(unitsReason);
            if (!unitsOk && string.IsNullOrEmpty(unitsReason)) unitsReason = "units parse aborted";
        }

        snapshot = new HwinfoLayoutSnapshot
        {
            Version1 = version1,
            Version2 = version2,
            EntryOffset = entryOffset,
            EntrySize = entrySize,
            EntryCount = entryCount,
            Entries = entries,
            UnitsAvailable = unitsOk,
            UnitsUnavailableReason = unitsOk ? "" : unitsReason,
            Units = unitsOk ? units : [],
        };
        return true;
    }

    /// <summary>读以 NUL 结尾的 ANSI/UTF-8 字段（与旧 ReadLabel 语义一致：截断到首个 0）。</summary>
    internal static string ReadAsciiZ(ReadOnlySpan<byte> field)
    {
        int len = 0;
        while (len < field.Length && field[len] != 0) len++;
        return len > 0 ? Encoding.UTF8.GetString(field.Slice(0, len)) : "";
    }
}
