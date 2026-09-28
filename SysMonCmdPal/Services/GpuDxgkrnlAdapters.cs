// Copyright (c) 2026 SysMonCmdPal
// T2-2（P1-2）：COM-free 的 gdi32 侧 GPU adapter 枚举 + 显存查询 + 身份归属。
//
// 为什么不走 DXGI（GpuAdapterEnumerator）：
//   出货 Release 产物启用 trimming/AOT，t12 实测裁决该配置下 BuiltInCOM 被整体禁用，
//   [ComImport] 的 DXGI 工厂/适配器取不到（本机活体亦证 DXGI EnumAdapters1 恒 0 adapter）。
//   而 gdi32!D3DKMT* 系列是**纯 P/Invoke、零 COM**，本机活体实测能枚举到 adapter 与显存段
//   （探针 ~/t5-probe/FINDINGS.md：D3DKMTEnumAdapters2 hr=0 返回 6 adapter；RTX4060 非 aperture
//   段 Resident=3243MB 与 t4 活体 HWiNFO dGPU MemUsed=3243.027MB 交叉吻合）。
//   ⇒ 这否证了 t12 对本机「COM-free 枚举 unverifiable」的判断（t12 只测了 DXGI raw vtable，未测 gdi32）。
//
// 与「唯一枚举出口」约束：本类是 GPU 用户态回退层（D3DKMT 利用率 + 显存、PDH 名称映射）**共用**的
//   adapter 来源，取代原先依赖已死 DXGI/COM 枚举器的做法。它**不是**第二条 DXGI COM interop 链
//   （纯 gdi32 P/Invoke，形态对齐仓内先例 D3dkmtGpuReader/PdChargerDetector）；**不是**第二套名称过滤
//   （adapter 身份/物理判据一律复用 t4 的 GpuIdentityService：SetupDi + PNPDeviceID 前缀）。
//   DXGI 的 DedicatedVideoMemory 仍作身份表的**可选辅助**信号（GpuIdentityService.DedicatedMemorySource），
//   与本类的段显存是「辅助 vs 主源」关系，二者不冲突。
//
// 结构体布局权威来源：Windows SDK 10.0.26100 shared/d3dkmthk.h
//   · D3DKMT_HANDLE = UINT（4 字节，**不是** IntPtr；原 D3dkmtGpuReader 用 IntPtr 是错的）；
//   · C_ASSERT(sizeof(D3DKMT_QUERYSTATISTICS)==0x328==808)：QueryResult union 仅 776 字节，
//     QueryElement(SegmentId/NodeOrdinal) 在**偏移 800**（原 D3dkmtGpuReader 用 byte[800]+NodeId
//     把 NodeId 推到 824，驱动永远读成 NodeOrdinal=0 —— 每条 node 查询实际都打到了 node0）。
//   · SEGMENT_INFORMATION(Win8+ 64 位)：CommitLimit@0 BytesCommitted@8 BytesResident@16 Aperture@40。
//   · D3DKMT_ADAPTERREGISTRYINFO = 4×WCHAR[MAX_PATH=260]，首项 AdapterString **逐字等于** SetupDi
//     SPDRP_DEVICEDESC ⇒ 精确 JOIN 到 t4 身份表；不可 JOIN（QueryAdapterInfo 返回 0xC0000034）者为
//     渲染节点副本/隐藏 adapter ⇒ 保守排除（绝不命名、绝不填显存）。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SysMonCmdPal;

/// <summary>一个 GPU 显存段（SEGMENT_INFORMATION 关键字段快照）。</summary>
internal readonly record struct GpuMemorySegment(uint Aperture, ulong CommitLimit, ulong BytesCommitted, ulong BytesResident);

/// <summary>COM-free 枚举并可归属的物理 GPU adapter。</summary>
internal sealed class GpuDxgkAdapter
{
    public uint LuidLow { get; init; }
    public int LuidHigh { get; init; }
    public required string Name { get; init; }
    public GpuKind Kind { get; init; }
    public ulong MemoryTotalBytes { get; init; }
    public ulong MemoryUsedBytes { get; init; }
    public double MemoryTotalMB => MemoryTotalBytes / (1024.0 * 1024.0);
    public double MemoryUsedMB => MemoryUsedBytes / (1024.0 * 1024.0);
    public (uint, int) LuidKey => (LuidLow, LuidHigh);
}

internal static class GpuDxgkrnlAdapters
{
    // SEGMENT_INFORMATION 字段偏移（SDK 10.0.26100，Win8+ 64 位分支）
    internal const int SegOffCommitLimit = 0;
    internal const int SegOffBytesCommitted = 8;
    internal const int SegOffBytesResident = 16;
    internal const int SegOffAperture = 40;   // 前有三个 ULONGLONG(24) + D3DKMT_QUERYSTATISTICS_MEMORY(16) = 40

    private static readonly object _lock = new();
    private static List<GpuDxgkAdapter>? _cached;
    private static DateTime _cacheTime = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static string _lastFingerprint = "";

    /// <summary>
    /// 枚举所有可归属物理 GPU adapter（含本地显存）。30s 缓存。逐适配器容错。
    /// identities 默认走 t4 的 GpuIdentityService.Default（SetupDi，零 COM）。
    /// </summary>
    public static List<GpuDxgkAdapter> GetAdapters(GpuIdentityService? identities = null)
    {
        lock (_lock)
        {
            if (_cached != null && (DateTime.UtcNow - _cacheTime) < CacheTtl)
                return _cached;
        }

        var ids = identities ?? GpuIdentityService.Default;
        var result = new List<GpuDxgkAdapter>();
        try
        {
            foreach (var luid in EnumerateRawLuids())
            {
                GpuDxgkAdapter? a = null;
                try { a = BuildAdapter(luid, ids); }
                catch (Exception ex)
                {
                    SensorLogger.Log($"[GPU-DXGK] adapter 0x{luid.HighPart:X8}_{luid.LowPart:X8} build failed: {ex.GetType().Name}");
                }
                if (a != null) result.Add(a);
            }
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[GPU-DXGK] enumerate threw {ex.GetType().Name}: {Trim(ex.Message)}");
        }

        lock (_lock)
        {
            _cached = result;
            _cacheTime = DateTime.UtcNow;
        }

        // 计数指纹（低频/状态跃变才打）：可枚举 adapter 数、归属成功数、排除数
        LogState($"adapters={result.Count} attributed={result.Count}" +
                 $" names=[{string.Join(", ", result.ConvertAll(x => $"{x.Name}({x.Kind}:{x.MemoryTotalMB / 1024.0:F0}GB)"))}]");
        return result;
    }

    /// <summary>测试缝隙：强制下次 GetAdapters 重新枚举。</summary>
    internal static void InvalidateCacheForTests()
    {
        lock (_lock) { _cached = null; _cacheTime = DateTime.MinValue; }
    }

    // ==================== 纯函数（buffer 可驱动，零硬件） ====================

    /// <summary>
    /// 从显存段选「本地显存」= 非 aperture 段中 CommitLimit 最大者；used 取 Resident。
    /// 无本地段 ⇒ null（保守，绝不拿 aperture/系统内存冒充显存）。纯函数。
    /// </summary>
    internal static (ulong TotalBytes, ulong UsedBytes)? SelectLocalVramMemory(IReadOnlyList<GpuMemorySegment> segments)
    {
        GpuMemorySegment? best = null;
        foreach (var s in segments)
        {
            if (s.Aperture != 0) continue;              // aperture 段 = 系统内存孔径映射，不是本地显存
            if (s.CommitLimit == 0) continue;
            if (best is null || s.CommitLimit > best.Value.CommitLimit)
                best = s;
        }
        if (best is null) return null;
        return (best.Value.CommitLimit, best.Value.BytesResident);
    }

    /// <summary>
    /// adapterString→身份（复用 t4 SetupDi 表，EXACT 名相等；表内只有 PCI\VEN_* 物理卡）。
    /// 不可归属（渲染节点副本/隐藏/虚拟卡名不在物理表）⇒ null ⇒ 调用方保守排除。纯函数（除查表）。
    /// </summary>
    internal static GpuDeviceIdentity? ResolveIdentity(string? adapterString, GpuIdentityService identities)
    {
        if (string.IsNullOrWhiteSpace(adapterString)) return null;
        return identities.FindByName(adapterString);
    }

    /// <summary>
    /// 归属 + 显存落定（纯函数）：集显按 t4 语义如实报 0 共享显存（本机 APU 本地段仅 239/244MB WDDM
    /// 预留，非真实 VRAM，如实上报会显示误导读数）；独显/未知取选中的本地段。name 为空⇒不产出（保守排除）。
    /// </summary>
    internal static GpuDxgkAdapter? Attribute(string? name, GpuKind kind, uint luidLow, int luidHigh,
                                             IReadOnlyList<GpuMemorySegment> segments)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (kind == GpuKind.Integrated)
            return new GpuDxgkAdapter { LuidLow = luidLow, LuidHigh = luidHigh, Name = name, Kind = kind, MemoryTotalBytes = 0, MemoryUsedBytes = 0 };

        var mem = SelectLocalVramMemory(segments);
        return new GpuDxgkAdapter
        {
            LuidLow = luidLow,
            LuidHigh = luidHigh,
            Name = name,
            Kind = kind,
            MemoryTotalBytes = mem?.TotalBytes ?? 0,
            MemoryUsedBytes = mem?.UsedBytes ?? 0,
        };
    }

    // ==================== P/Invoke 层（生产；测试不触） ====================

    // internal：测试可用 Marshal.SizeOf/OffsetOf 确定性断言布局（零硬件）
    [StructLayout(LayoutKind.Sequential)]
    internal struct LUID { public uint LowPart; public int HighPart; }

    // D3DKMT_ADAPTERINFO: hAdapter(UINT4) LUID(8) NumOfSources(4) bPrecise(BOOL4) = 20
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DKMT_ADAPTERINFO { public uint hAdapter; public LUID AdapterLuid; public uint NumOfSources; public uint bPrecise; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }

    // SDK: { LUID AdapterLuid; D3DKMT_HANDLE hAdapter; } = 12
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMLUID { public LUID AdapterLuid; public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pData; public uint DataSize; }

    // D3DKMT_QUERYSTATISTICS（internal：测试可用 Marshal.SizeOf/OffsetOf 确定性断言布局，零硬件）
    // 对齐 SDK C_ASSERT(sizeof==0x328==808)，QueryElement(SegmentId/NodeOrdinal) 落偏移 800。
    // 原 D3dkmtGpuReader 用 byte[800]+NodeId 把 NodeId 推到 824 ⇒ 驱动恒读 node0，本布局已修正。
    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DKMT_QUERYSTATISTICS
    {
        public int Type;
        public LUID AdapterLuid;
        private uint _pad;                                   // 补 IntPtr 的 8 字节对齐间隙
        public IntPtr ProcessHandle;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 776)]
        public byte[] QueryResult;                           // union 区 24..799
        public uint QueryElement;                            // @800：SegmentId / NodeOrdinal
        public uint QueryElementHigh;                        // @804
    }

    private const int StatsTypeAdapter = 0;
    private const int StatsTypeNode = 5;
    private const int StatsTypeSegment = 3;
    private const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
    private const int RegistryInfoBytes = 4 * 260 * 2;

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 Arg);
    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID Arg);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER Arg);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO Arg);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryStatistics(ref D3DKMT_QUERYSTATISTICS Arg);

    private static IEnumerable<LUID> EnumerateRawLuids()
    {
        int size = Marshal.SizeOf<D3DKMT_ADAPTERINFO>();
        IntPtr arr = Marshal.AllocHGlobal(size * 16);
        try
        {
            for (int i = 0; i < size * 16; i++) Marshal.WriteByte(arr, i, 0);
            var e2 = new D3DKMT_ENUMADAPTERS2 { NumAdapters = 16, pAdapters = arr };
            int hr = D3DKMTEnumAdapters2(ref e2);
            if (hr != 0) { LogState($"D3DKMTEnumAdapters2 hr=0x{hr:X8}"); return Array.Empty<LUID>(); }
            uint count = Math.Min(e2.NumAdapters, 16u);
            var list = new List<LUID>((int)count);
            for (uint i = 0; i < count; i++)
                list.Add(Marshal.PtrToStructure<D3DKMT_ADAPTERINFO>(arr + (int)(i * size)).AdapterLuid);
            return list;
        }
        finally { Marshal.FreeHGlobal(arr); }
    }

    private static GpuDxgkAdapter? BuildAdapter(LUID luid, GpuIdentityService identities)
    {
        var of = new D3DKMT_OPENADAPTERFROMLUID { AdapterLuid = luid };
        if (D3DKMTOpenAdapterFromLuid(ref of) != 0) return null;
        try
        {
            string? adapterString = QueryAdapterString(of.hAdapter);
            var identity = ResolveIdentity(adapterString, identities);
            if (identity is null) return null;   // 不可归属（副本/隐藏/虚拟）⇒ 保守排除（不命名、不填显存）

            // kind 复用 t4 单一来源判据（虚拟压制 → 身份/角色 → 名称辅助）；表内只有 PCI\VEN_* 物理卡
            GpuKind kind = identities.Classify(new GpuInfo { Name = identity.Name });

            var segments = kind == GpuKind.Integrated
                ? Array.Empty<GpuMemorySegment>()
                : ReadSegments(luid, of.hAdapter);

            return Attribute(identity.Name, kind, luid.LowPart, luid.HighPart, segments);
        }
        finally
        {
            var cl = new D3DKMT_CLOSEADAPTER { hAdapter = of.hAdapter };
            D3DKMTCloseAdapter(ref cl);
        }
    }

    private static string? QueryAdapterString(uint hAdapter)
    {
        IntPtr buf = Marshal.AllocHGlobal(RegistryInfoBytes);
        try
        {
            for (int k = 0; k < RegistryInfoBytes; k++) Marshal.WriteByte(buf, k, 0);
            var qi = new D3DKMT_QUERYADAPTERINFO { hAdapter = hAdapter, Type = KMTQAITYPE_ADAPTERREGISTRYINFO, pData = buf, DataSize = RegistryInfoBytes };
            if (D3DKMTQueryAdapterInfo(ref qi) != 0) return null;   // 0xC0000034 = registry info 不可得（副本/隐藏）
            return Marshal.PtrToStringUni(buf, 260)?.TrimEnd('\0');
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static IReadOnlyList<GpuMemorySegment> ReadSegments(LUID luid, uint hAdapter)
    {
        var ad = new D3DKMT_QUERYSTATISTICS { Type = StatsTypeAdapter, AdapterLuid = luid, ProcessHandle = IntPtr.Zero, QueryResult = new byte[776] };
        if (D3DKMTQueryStatistics(ref ad) != 0) return Array.Empty<GpuMemorySegment>();
        uint nbSegments = BitConverter.ToUInt32(ad.QueryResult, 0);   // ADAPTER_INFORMATION.NbSegments

        var segs = new List<GpuMemorySegment>((int)Math.Min(nbSegments, 32u));
        for (uint s = 0; s < Math.Min(nbSegments, 32u); s++)
        {
            var sq = new D3DKMT_QUERYSTATISTICS { Type = StatsTypeSegment, AdapterLuid = luid, ProcessHandle = IntPtr.Zero, QueryResult = new byte[776] };
            sq.QueryElement = s;   // SegmentId @800（原实现偏移错⇒这里已修正）
            if (D3DKMTQueryStatistics(ref sq) != 0) continue;
            segs.Add(new GpuMemorySegment(
                BitConverter.ToUInt32(sq.QueryResult, SegOffAperture),
                BitConverter.ToUInt64(sq.QueryResult, SegOffCommitLimit),
                BitConverter.ToUInt64(sq.QueryResult, SegOffBytesCommitted),
                BitConverter.ToUInt64(sq.QueryResult, SegOffBytesResident)));
        }
        _ = hAdapter;
        return segs;
    }

    /// <summary>
    /// 读某 adapter 某 engine node 的 RunningTime（100ns ticks）。供 D3dkmtGpuReader 利用率采样。
    /// 用修正后的 808 字节布局（NodeOrdinal 落偏移 800），原实现把 NodeId 推到 824 恒读 node0。
    /// </summary>
    public static bool TryReadNodeRunningTime(uint luidLow, int luidHigh, int nodeOrdinal, out ulong runningTime100ns)
    {
        runningTime100ns = 0;
        if (nodeOrdinal < 0) return false;
        var q = new D3DKMT_QUERYSTATISTICS
        {
            Type = StatsTypeNode,
            AdapterLuid = new LUID { LowPart = luidLow, HighPart = luidHigh },
            ProcessHandle = IntPtr.Zero,
            QueryResult = new byte[776],
            QueryElement = (uint)nodeOrdinal,   // NodeOrdinal @800
        };
        if (D3DKMTQueryStatistics(ref q) != 0) return false;
        runningTime100ns = BitConverter.ToUInt64(q.QueryResult, 0);   // NODE_INFORMATION.RunningTime @0
        return true;
    }

    private static string Trim(string s) => s.Length <= 160 ? s : s[..160] + "…";

    private static void LogState(string message)
    {
        lock (_lock)
        {
            if (string.Equals(_lastFingerprint, message, StringComparison.Ordinal)) return;
            _lastFingerprint = message;
        }
        try { SensorLogger.ForceLog($"[GPU-DXGK] {message}"); }
        catch { /* 日志失败不得影响采集 */ }
    }
}
