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

    /// <summary>header 字段的载体（8 个 int32，字段名与原局部变量一一对应）。</summary>
    /// <remarks>
    /// 刻意保持 <c>private</c>：一旦提成 internal，测试就会倾向于直接构造它、绕过
    /// 「内存字节驱动」这条唯一的验证路径（见文件头 §1 的设计意图）。data 不进本结构
    /// （<see cref="ReadOnlySpan{T}"/> 字段会污染生命周期），而是逐层单独传参。
    /// </remarks>
    private readonly record struct HwinfoHeader(
        int Version1, int Version2,
        int UnitsOffset, int UnitSize, int UnitCount,
        int EntryOffset, int EntrySize, int EntryCount);

    /// <summary>
    /// 纯函数解析。fail-fast 语义（不等式，不把本机 460/392 当必需值）：
    ///  - entries 面：entrySize 必须容得下 EntryValue+8，否则**整表不可信**（返回 false）；
    ///  - units 面：unitSize 必须容得下 UnitName+128，且 units 数组不得越过 entries 起点，
    ///    否则只禁用 units 解析面（UnitsAvailable=false + 原因），entries 面照常可用。
    /// </summary>
    /// <remarks>
    /// 本方法只做**编排**，四段决策各自独立成方法：
    /// <see cref="ValidateHeader"/>（header 是否可信）→ <see cref="ReadHeader"/>（读取字段）
    /// → <see cref="ValidateEntrySize"/>（entries 几何）→ <see cref="ParseEntries"/>（物化 entries）
    /// → <see cref="TryParseUnits"/>（units 面，可单独降级）。
    /// ⚠ entries 面**先于** units 面解析是语义的一部分，不得改成「校验全部前置」：
    /// units 面坏不影响 entries 面（UnitsAvailable=false 但 entries 照常可用）。
    /// </remarks>
    public static bool TryParse(ReadOnlySpan<byte> data, out HwinfoLayoutSnapshot? snapshot, out string failure)
    {
        snapshot = null;
        if (!ValidateHeader(data, out failure))
            return false;

        HwinfoHeader header = ReadHeader(data);
        if (!ValidateEntrySize(in header, data.Length, out failure))
            return false;

        System.Collections.Generic.List<HwinfoEntryDescriptor> entries = ParseEntries(data, in header);
        // units 面：失败只降级该面（UnitsAvailable=false + 原因），绝不使整表返回 false。
        bool unitsOk = TryParseUnits(data, in header, out var units, out string unitsReason);

        snapshot = new HwinfoLayoutSnapshot
        {
            Version1 = header.Version1,
            Version2 = header.Version2,
            EntryOffset = header.EntryOffset,
            EntrySize = header.EntrySize,
            EntryCount = header.EntryCount,
            Entries = entries,
            UnitsAvailable = unitsOk,
            UnitsUnavailableReason = unitsOk ? "" : unitsReason,
            Units = unitsOk ? units : [],
        };
        return true;
    }

    /// <summary>
    /// header 是否可信：长度门槛（须容下 EntryCount 字段）+ 签名。
    /// 任一不成立 ⇒ **全表不可信**（整表 return false）。
    /// </summary>
    private static bool ValidateHeader(ReadOnlySpan<byte> data, out string failure)
    {
        if (data.Length < (HdrEntryCount + 1) * 4)
        {
            failure = $"section too small for header ({data.Length}B)";
            return false;
        }
        if (BitConverter.ToUInt32(data.Slice(0, 4)) != Signature)
        {
            // 两个子串（"signature" / "unexpected"）都被断言，勿改文案。
            failure = $"signature 0x{BitConverter.ToUInt32(data.Slice(0, 4)):X8} unexpected";
            return false;
        }

        failure = "";
        return true;
    }

    /// <summary>读取 header 八个字段（纯读取，不含校验；调用前须先过 <see cref="ValidateHeader"/>）。</summary>
    private static HwinfoHeader ReadHeader(ReadOnlySpan<byte> data) => new(
        Version1: BitConverter.ToInt32(data.Slice(HdrVersion1 * 4, 4)),
        Version2: BitConverter.ToInt32(data.Slice(HdrVersion2 * 4, 4)),
        UnitsOffset: BitConverter.ToInt32(data.Slice(HdrUnitsOffset * 4, 4)),
        UnitSize: BitConverter.ToInt32(data.Slice(HdrUnitSize * 4, 4)),
        UnitCount: BitConverter.ToInt32(data.Slice(HdrUnitCount * 4, 4)),
        EntryOffset: BitConverter.ToInt32(data.Slice(HdrEntryOffset * 4, 4)),
        EntrySize: BitConverter.ToInt32(data.Slice(HdrEntrySize * 4, 4)),
        EntryCount: BitConverter.ToInt32(data.Slice(HdrEntryCount * 4, 4)));

    /// <summary>
    /// entries 面几何校验，**三条判据的顺序即优先级**（各文案被不同用例分别断言）：
    /// 旧范围检查 → value 字段交叉校验 → entries 数组是否越出 section。
    /// 任一不成立 ⇒ **全表不可信**（整表 return false）。
    /// </summary>
    private static bool ValidateEntrySize(in HwinfoHeader header, int dataLength, out string failure)
    {
        if (!IsEntryLayoutInRange(in header))
        {
            failure = $"entry layout out of range: offset={header.EntryOffset} size={header.EntrySize} count={header.EntryCount}";
            return false;
        }
        // 关键 fail-fast（此前缺失）：记录必须容得下被硬编码读取的 value 字段
        if (header.EntrySize < EntryValue + sizeof(double))
        {
            failure = $"entrySize={header.EntrySize} < required {EntryValue + sizeof(double)} (value 字段偏移交叉校验失败)";
            return false;
        }

        return EntriesFitSection(in header, dataLength, out failure);
    }

    /// <summary>旧范围检查（语义保持）：entrySize/entryOffset/entryCount 的松散上下界。</summary>
    private static bool IsEntryLayoutInRange(in HwinfoHeader header) =>
        header.EntrySize >= 160 && header.EntrySize <= 1024 &&
        header.EntryOffset > 0 && header.EntryOffset <= 65536 &&
        header.EntryCount > 0 && header.EntryCount <= 4096;

    /// <summary>entries 数组是否容得下（<c>long</c> 乘法防溢出）。</summary>
    private static bool EntriesFitSection(in HwinfoHeader header, int dataLength, out string failure)
    {
        long entriesEnd = (long)header.EntryOffset + (long)header.EntrySize * header.EntryCount;
        failure = entriesEnd > dataLength
            ? $"entries array exceeds section ({entriesEnd} > {dataLength})"
            : "";
        return failure.Length == 0;
    }

    /// <summary>
    /// 物化 entries 数组。几何已由 <see cref="ValidateEntrySize"/> 保证，
    /// 故本方法**必然成功**、无需失败路径（这是拆分后不再是「114 行线性流」的关键）。
    /// </summary>
    private static System.Collections.Generic.List<HwinfoEntryDescriptor> ParseEntries(
        ReadOnlySpan<byte> data, in HwinfoHeader header)
    {
        var entries = new System.Collections.Generic.List<HwinfoEntryDescriptor>(header.EntryCount);
        for (int i = 0; i < header.EntryCount; i++)
        {
            int b = header.EntryOffset + header.EntrySize * i;
            entries.Add(new HwinfoEntryDescriptor
            {
                Index = i,
                Type = BitConverter.ToInt32(data.Slice(b + EntryType, 4)),
                SensorIndex = BitConverter.ToInt32(data.Slice(b + EntrySensorIndex, 4)),
                Label = ReadAsciiZ(data.Slice(b + EntryLabel, HwinfoStrLen)),
                Value = BitConverter.ToDouble(data.Slice(b + EntryValue, sizeof(double))),
            });
        }
        return entries;
    }

    /// <summary>
    /// units 面（**可单独降级**的面）：几何校验 → 逐条读取。
    /// 失败时 <paramref name="units"/> 回退为空列表（不泄漏半成品），
    /// 且调用方据此只把 <c>UnitsAvailable</c> 置 false，entries 面不受影响。
    /// </summary>
    private static bool TryParseUnits(
        ReadOnlySpan<byte> data, in HwinfoHeader header,
        out System.Collections.Generic.List<HwinfoUnitRecord> units, out string reason)
    {
        units = [];
        if (!ValidateUnitsArray(in header, out reason))
            return false;
        if (ReadUnits(data, in header, units, out reason))
            return true;

        // 兜底串（原实现的防御分支，语义保持）
        if (string.IsNullOrEmpty(reason))
            reason = "units parse aborted";
        return false;
    }

    /// <summary>
    /// units 数组的几何校验（纯函数，不碰 data），失败时给出降级原因。
    /// </summary>
    /// <remarks>
    /// ⚠ **四条 else-if 的顺序即优先级**，每条文案被不同用例分别断言，调序立刻红：
    /// 1) header 缺失/非法 → 2) unitSize 容不下 name → 3) 越过 entries 起点 → 4) 覆盖 header。
    /// 第 4 条排在「越界」之后是有意的：用例 <c>UnitsOffsetOverlapsHeader</c> 把 unitsOffset 改 20
    /// （unitSize 392 / count 1 ⇒ 20+392=412 ≤ 440，前三条都不中）才会落到它，这条推导勿丢。
    /// </remarks>
    private static bool ValidateUnitsArray(in HwinfoHeader header, out string reason)
    {
        long unitsEnd = (long)header.UnitsOffset + (long)header.UnitSize * header.UnitCount;
        if (header.UnitsOffset <= 0 || header.UnitSize <= 0 || header.UnitCount <= 0 || header.UnitCount > 4096)
            reason = $"units header absent/invalid: offset={header.UnitsOffset} size={header.UnitSize} count={header.UnitCount}";
        else if (header.UnitSize < UnitName + HwinfoStrLen)
            reason = $"unitSize={header.UnitSize} < required {UnitName + HwinfoStrLen} (name 字段交叉校验失败)";
        // 本机实测恰等于 entryOffset（48+392×17=6712）；写成不等式校验，不当巧合
        else if (unitsEnd > header.EntryOffset)
            reason = $"units array overruns entries start ({unitsEnd} > {header.EntryOffset})";
        else if (header.UnitsOffset < (HdrEntryCount + 1) * 4)
            reason = $"unitsOffset={header.UnitsOffset} overlaps header";
        else
            reason = "";

        return reason.Length == 0;
    }

    /// <summary>
    /// 逐条读取 units；任一条的 name 字段越出 section 即中止（已加入的条目由调用方丢弃）。
    /// </summary>
    /// <remarks>
    /// ⚠ 越界分支（<c>unit[N] name field exceeds section</c>）在**当前上游几何校验下不可达**——
    /// 这是重构前就存在的死分支，本次仅原样保留、未删：
    /// unitSize ≥ UnitName+HwinfoStrLen=136（<see cref="ValidateUnitsArray"/>）且
    /// unitsOffset + unitSize×unitCount ≤ entryOffset（同处「overruns」判据）
    /// ⇒ b_max = unitsOffset + unitSize×(unitCount−1) ≤ entryOffset − 136；
    /// 而 entryOffset ≤ dataLength − 292（<see cref="EntriesFitSection"/> 配合 entrySize≥292、
    /// entryCount≥1）⇒ b_max + 136 ≤ dataLength − 292 &lt; dataLength，判据恒不成立。
    /// 保留它是防御性冗余（未来若放宽上游不等式即重新生效），删除属独立决策、超出本次重构范围。
    /// </remarks>
    private static bool ReadUnits(
        ReadOnlySpan<byte> data, in HwinfoHeader header,
        System.Collections.Generic.List<HwinfoUnitRecord> units, out string reason)
    {
        reason = "";
        for (int u = 0; u < header.UnitCount; u++)
        {
            int b = header.UnitsOffset + header.UnitSize * u;
            if (b + UnitName + HwinfoStrLen > data.Length)
            {
                reason = $"unit[{u}] name field exceeds section";
                return false;
            }
            units.Add(NewUnitRecord(data, b, u));
        }
        return true;
    }

    /// <summary>读取第 <paramref name="index"/> 条 unit 记录（边界已由 <see cref="ReadUnits"/> 保证）。</summary>
    private static HwinfoUnitRecord NewUnitRecord(ReadOnlySpan<byte> data, int b, int index) => new()
    {
        Index = index,
        Id = BitConverter.ToInt32(data.Slice(b + UnitId, 4)),
        Instance = BitConverter.ToInt32(data.Slice(b + UnitInstance, 4)),
        Name = ReadAsciiZ(data.Slice(b + UnitName, HwinfoStrLen)),
    };

    /// <summary>读以 NUL 结尾的 ANSI/UTF-8 字段（与旧 ReadLabel 语义一致：截断到首个 0）。</summary>
    internal static string ReadAsciiZ(ReadOnlySpan<byte> field)
    {
        int len = 0;
        while (len < field.Length && field[len] != 0) len++;
        return len > 0 ? Encoding.UTF8.GetString(field.Slice(0, len)) : "";
    }
}
