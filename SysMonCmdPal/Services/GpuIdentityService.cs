// Copyright (c) 2026 SysMonCmdPal
// T2-3（P1-3）：GPU 身份判定统一为单一来源 GpuIdentityService。
//
// 取代此前两套互相独立、可能不一致的判据：
//   - GpuClassifier：纯名称 marker 分类（14 条 marker）；
//   - GpuSensorReader：WMI Win32_VideoController + AdapterRAM 排名分类。
//
// 身份源 = SetupAPI（setupapi.dll）纯 P/Invoke 枚举 Display 类设备：
//   零 COM、零 System.Management。出货 Release 产物启用 trimming/AOT，
//   t12 实测裁决该配置下 BuiltInCOM 被整体禁用（WMI 抛 TypeInitializationException、
//   [ComImport] DXGI 取不到），故 GPU 身份链不得存在「仅 COM 可用才工作」的前提。
//   本机活体实测：SetupDiGetDeviceInstanceIdW 的返回串与 WMI PNPDeviceID 逐字同形；
//   显示名须取 SPDRP_DEVICEDESC（实测 SPDRP_FRIENDLYNAME 为空串）。
//
// 物理/虚拟判据 = **PNPDeviceID 前缀**（PCI\VEN_* 物理；ROOT\* 及其它前缀非物理）。
// 名称 marker 降级为辅助判据：本机实测 "Zako Display Adapter" 不命中任何 marker，
// 纯名称过滤必漏 ⇒ marker 不得作唯一依据。虚拟设备不入身份表，但其名称被登记用于
// 分类压制（避免"身份表里没有 → 回落到名称判据 → 显存>0 误翻 Discrete"）。
//
// 注意：本服务**不**声称与 HWiNFO 做 PNPDeviceID 硬关联 —— HWiNFO 共享内存记录内
// 不存在任何 PNPDeviceID/总线/序列号类可连接键（见 HwinfoSharedMemoryLayout.cs 文件头）。
// unit 名 → 身份表是尽力而为的启发式，不确定即保守不猜。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SysMonCmdPal;

public enum GpuKind
{
    Unknown,
    Integrated,
    Discrete,
}

/// <summary>HWiNFO 分组名前缀携带的角色提示（本机实测写法 'iGPU [#1]: …' / 'dGPU [#0]: …'）。</summary>
internal enum GpuRoleHint
{
    None,
    Integrated,
    Discrete,
}

/// <summary>身份提供者返回的原始显示设备（设备实例 ID + 设备描述）。</summary>
internal readonly record struct GpuDisplayDevice(string PnpDeviceId, string DeviceDesc);

/// <summary>GPU 身份提供者接缝：生产 = SetupDi（零 COM/零 WMI）；测试 = fixture 注入。</summary>
internal interface IGpuIdentityProvider
{
    IReadOnlyList<GpuDisplayDevice> EnumerateDisplayDevices();
}

/// <summary>一张已识别的物理 GPU 身份。</summary>
internal sealed class GpuDeviceIdentity
{
    public required string PnpDeviceId { get; init; }
    public required string Name { get; init; }

    /// <summary>身份表内自增序号，作为归属阶段的"未被消费"标记（避免引用相等误判）。</summary>
    public int Serial { get; init; }

    /// <summary>PNPDeviceID 解析出的 VEN_ 四位大写十六进制；无则空串。</summary>
    public string VendorToken { get; init; } = "";

    /// <summary>
    /// 归属阶段（GpuHwinfoAssociation）写入的 HWiNFO 角色提示。
    /// 每次身份表刷新时清空；仅在同一刷新链上被分类侧读取，最坏情况晚一个周期。
    /// </summary>
    public GpuRoleHint HwinfoRole;

    /// <summary>
    /// DXGI 报告的独立显存（MB）。仅当 COM 可用（Debug/FrameworkDependent 宿主）时有值，
    /// 只作**辅助**信号，不得作独显唯一依据（trim 宿主恒 null）。
    /// </summary>
    public double? DedicatedVideoMemoryMB;

    /// <summary>仅供测试/诊断显示分类落点。</summary>
    internal string LastKindSource = "";
}

/// <summary>GPU 身份与分类的单一来源。UI 图标与传感器归属都从这里取判定。</summary>
// partial：本程序集存在针对该类型的分部声明（CsWinRT 投影生成，与 t12 同一现象）
internal sealed partial class GpuIdentityService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private List<GpuDeviceIdentity> _physical = [];
    private List<string> _virtualNames = [];
    private DateTime _cacheTime = DateTime.MinValue;
    private bool _everRefreshed;

    public IGpuIdentityProvider Provider { get; }

    /// <summary>
    /// 可选的独立显存辅助信号供给（按设备名匹配）。生产实现读 DXGI adapter 枚举结果；
    /// DXGI/COM 在 trim 宿主下整体不可用 ⇒ 供给返回空 ⇒ 该辅助信号为 null，判定退回
    /// 名称/角色/显存读数等 COM-free 依据（契约：不得硬依赖 DXGI DedicatedVideoMemory）。
    /// 测试注入固定字典，绝不触碰 COM。
    /// </summary>
    public Func<IReadOnlyDictionary<string, double>>? DedicatedMemorySource { get; set; }

    public GpuIdentityService(IGpuIdentityProvider provider) => Provider = provider;

    /// <summary>生产默认实例（SetupDi 提供者，零 COM/零 WMI）。</summary>
    public static GpuIdentityService Default { get; } = new(new SetupDiGpuIdentityProvider())
    {
        DedicatedMemorySource = ReadDxgiDedicatedMemory,
    };

    /// <summary>DXGI adapter 独立显存（MB，按设备名）；不可用时返回空表。</summary>
    private static IReadOnlyDictionary<string, double> ReadDxgiDedicatedMemory()
    {
        try
        {
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in GpuAdapterEnumerator.GetAdapters())
                if (!a.IsSoftware && a.DedicatedVideoMemoryBytes > 0)
                    map[a.Name] = a.DedicatedVideoMemoryBytes / (1024.0 * 1024.0);
            return map;
        }
        catch
        {
            // 辅助信号失败绝不影响身份链（COM-free 判据才是主路径）
            return new Dictionary<string, double>();
        }
    }

    /// <summary>
    /// 物理 GPU 身份表（60s TTL）。提供者抛异常/返回空 ⇒ 空表（保守，绝不猜测身份）。
    /// </summary>
    public IReadOnlyList<GpuDeviceIdentity> GetIdentities(bool forceRefresh = false)
    {
        lock (_lock)
        {
            bool stale = !_everRefreshed || (DateTime.UtcNow - _cacheTime) > CacheTtl;
            if (forceRefresh || stale)
            {
                RefreshCore();
                _cacheTime = DateTime.UtcNow;
                _everRefreshed = true;
            }
            return _physical;
        }
    }

    private void RefreshCore()
    {
        var physical = new List<GpuDeviceIdentity>();
        var virtualNames = new List<string>();
        try
        {
            foreach (var dev in Provider.EnumerateDisplayDevices())
            {
                string desc = (dev.DeviceDesc ?? "").Trim();
                string pnp = (dev.PnpDeviceId ?? "").Trim();
                if (string.IsNullOrEmpty(desc) && string.IsNullOrEmpty(pnp)) continue;

                if (!IsPhysicalInstanceId(pnp))
                {
                    // 虚拟设备：不入身份表，但登记名称用于分类压制
                    if (!string.IsNullOrEmpty(desc)) virtualNames.Add(desc);
                    continue;
                }

                physical.Add(new GpuDeviceIdentity
                {
                    Serial = physical.Count,
                    PnpDeviceId = pnp,
                    Name = desc.Length > 0 ? desc : pnp,
                    VendorToken = ParseVendorToken(pnp),
                });
            }
        }
        catch
        {
            physical.Clear();
            virtualNames.Clear();
        }

        // 辅助信号（可选、非硬依赖）：DXGI 可用时按设备名绑定独立显存
        IReadOnlyDictionary<string, double>? dedicated = null;
        if (DedicatedMemorySource is { } src && physical.Count > 0)
        {
            try { dedicated = src(); }
            catch { dedicated = null; }
        }
        if (dedicated is { Count: > 0 })
        {
            foreach (var id in physical)
                if (dedicated.TryGetValue(id.Name, out double mb)) id.DedicatedVideoMemoryMB = mb;
        }

        // 表被重建 ⇒ 丢弃上一周期归属阶段写入的角色提示
        foreach (var id in _physical) id.HwinfoRole = GpuRoleHint.None;
        _physical = physical;
        _virtualNames = virtualNames;
    }

    // ============================ 判据（静态可测） ============================

    /// <summary>
    /// 前缀判据（唯一硬判据）：PCI\VEN_*（含 PCIENUM 下的 PCI 总线形式）视为物理；
    /// ROOT\*、ACPI\*、BTH\* 等一律非物理。空串非物理。
    /// </summary>
    internal static bool IsPhysicalInstanceId(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId)) return false;
        string s = pnpDeviceId.Trim().ToUpperInvariant();
        return s.StartsWith(@"PCI\", StringComparison.Ordinal)
            || s.StartsWith(@"PCIENUM\", StringComparison.Ordinal);
    }

    /// <summary>从 PNPDeviceID 解析 VEN_ 四位大写 token；格式不符返回空串。</summary>
    internal static string ParseVendorToken(string? pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId)) return "";
        string s = pnpDeviceId.ToUpperInvariant();
        int i = s.IndexOf(@"VEN_", StringComparison.Ordinal);
        if (i < 0 || i + 8 > s.Length) return "";
        string tok = s.Substring(i + 4, 4);
        foreach (char c in tok)
            if (!IsHex(c)) return "";
        return tok;

        static bool IsHex(char c) =>
            (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
    }

    /// <summary>
    /// HWiNFO unit 名中的**厂商** token（映射到 VEN_ 值）。
    /// 只认厂商词，不认型号 token —— 本机实测 HWiNFO "AMD Radeon 680M" 的 "680M"
    /// 不出现在 OS 设备名 "AMD Radeon(TM) Graphics" 中，型号匹配必漏。
    /// </summary>
    internal static string? UnitNameVendorToken(string? unitName)
    {
        if (string.IsNullOrEmpty(unitName)) return null;
        string s = unitName.ToUpperInvariant();
        int nvidia = s.IndexOf("NVIDIA", StringComparison.Ordinal);
        int amd = s.IndexOf("AMD", StringComparison.Ordinal);
        int intel = s.IndexOf("INTEL", StringComparison.Ordinal);
        int qual = s.IndexOf("QUALCOMM", StringComparison.Ordinal);

        int best = int.MaxValue;
        string? token = null;
        Consider(nvidia, "10DE");
        Consider(amd, "1002");
        Consider(intel, "8086");
        Consider(qual, "17CB");
        return token;

        void Consider(int idx, string ven)
        {
            if (idx < 0 || idx > best) return;
            best = idx;
            token = ven;
        }
    }

    /// <summary>unit 名前缀角色（'iGPU'/'dGPU'）。COM-free 字符串判据。</summary>
    internal static GpuRoleHint UnitNameRole(string? unitName)
    {
        if (string.IsNullOrWhiteSpace(unitName)) return GpuRoleHint.None;
        string s = unitName.TrimStart().ToUpperInvariant();
        if (s.StartsWith("IGPU", StringComparison.Ordinal)) return GpuRoleHint.Integrated;
        if (s.StartsWith("DGPU", StringComparison.Ordinal)) return GpuRoleHint.Discrete;
        return GpuRoleHint.None;
    }

    // ============================ 分类（P1-3 单一来源） ============================

    /// <summary>名称/显存启发式分类（原 GpuClassifier 逻辑原样迁入，**降级为辅助判据**）。</summary>
    internal static GpuKind ClassifyName(string? name, double memoryTotalMB)
    {
        string n = NormalizeName(name);

        if (ContainsAny(n, UnknownNameMarkers))
            return GpuKind.Unknown;

        if (n.Length > 0 && IsIntegratedName(n))
            return GpuKind.Integrated;

        if (n.Length > 0 && IsDiscreteName(n))
            return GpuKind.Discrete;

        return memoryTotalMB > 0 ? GpuKind.Discrete : GpuKind.Unknown;
    }

    /// <summary>是否被前缀判据认定的虚拟设备名称（来自身份表刷新时登记的名称）。</summary>
    public bool IsKnownVirtualName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        GetIdentities();
        lock (_lock)
        {
            foreach (var v in _virtualNames)
                if (string.Equals(v, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    /// <summary>按显示名（忽略大小写）查身份。</summary>
    public GpuDeviceIdentity? FindByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        foreach (var id in GetIdentities())
            if (string.Equals(id.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                return id;
        return null;
    }

    /// <summary>
    /// 实例级分类：虚拟压制 → 身份（含 HWiNFO 角色 / DXGI 辅助显存）→ 名称辅助判据 → 显存兜底。
    /// </summary>
    public GpuKind Classify(GpuInfo gpu)
    {
        string? name = gpu.Name;
        if (IsKnownVirtualName(name))
            return GpuKind.Unknown;

        var id = FindByName(name);
        if (id != null)
        {
            var (kind, source) = ClassifyIdentity(id, gpu.MemoryTotalMB);
            id.LastKindSource = source;
            return kind;
        }
        return ClassifyName(name, gpu.MemoryTotalMB);
    }

    private static (GpuKind Kind, string Source) ClassifyIdentity(GpuDeviceIdentity id, double memoryTotalMB)
    {
        // 1) 归属阶段拿到的 HWiNFO 角色（COM-free）
        switch (id.HwinfoRole)
        {
            case GpuRoleHint.Integrated: return (GpuKind.Integrated, "HwinfoRole");
            case GpuRoleHint.Discrete: return (GpuKind.Discrete, "HwinfoRole");
        }
        // 2) 名称辅助判据（先于显存兜底：名称是比"有显存读数"更强的证据）
        var byName = ClassifyName(id.Name, memoryTotalMB);
        if (byName != GpuKind.Unknown) return (byName, "NameMarker");
        // 3) DXGI 独立显存辅助信号（仅 COM 可得；不作唯一依据）
        if (id.DedicatedVideoMemoryMB is > 0) return (GpuKind.Discrete, "DxgiDedicatedAux");
        // 4) 未知名字 + 有独立显存读数 → Discrete（保持既有兼容语义）
        if (memoryTotalMB > 0) return (GpuKind.Discrete, "MemoryFallback");
        return (GpuKind.Unknown, "Unknown");
    }

    /// <summary>
    /// 主卡竞选用的"独显优先"信号：只承认可信来源（虚拟压制 + 角色/名称/显存）。
    /// 虚拟设备与身份表外的未知卡不得凭显存读数抢主卡。
    /// </summary>
    public bool LooksDiscrete(GpuInfo gpu) => Classify(gpu) == GpuKind.Discrete;

    // ============================ 图标 ============================

    public string ResolveIcon(GpuInfo gpu) => Classify(gpu) switch
    {
        GpuKind.Integrated => SysMonIcons.GpuIntegrated,
        GpuKind.Discrete => SysMonIcons.GpuDiscrete,
        _ => SysMonIcons.Gpu,
    };

    /// <summary>UI 侧统一入口（GpuItemPage / GpuDetailPage）。</summary>
    public static string GetIcon(GpuInfo gpu) => Default.ResolveIcon(gpu);

    // ==================== 名称辅助 marker（原 GpuClassifier 表） ====================

    private static readonly string[] UnknownNameMarkers =
    [
        "MICROSOFT BASIC DISPLAY",
        "REMOTE DISPLAY",
        "INDIRECT DISPLAY",
        "VIRTUAL DISPLAY",
        "VIRTUAL GRAPHICS",
        "VIRTUAL GPU",
        "VMWARE SVGA",
        "HYPER-V VIDEO",
        "VIRTUALBOX",
        "PARALLELS",
        "CITRIX",
        "VIRTIO GPU",
        "QXL",
        "BOCHS",
    ];

    private static readonly string[] DiscreteNameMarkers =
    [
        "NVIDIA",
        "GEFORCE",
        "QUADRO",
        "TESLA",
        "TITAN",
        "RADEON RX",
        "RADEON PRO",
        "RADEON VII",
        "FIREPRO",
    ];

    private static bool IsIntegratedName(string name)
    {
        if (name.Contains("INTEGRATED", StringComparison.Ordinal) ||
            name.Contains("ADRENO", StringComparison.Ordinal))
        {
            return true;
        }

        if (name.Contains("INTEL", StringComparison.Ordinal) &&
            name.Contains("GRAPHICS", StringComparison.Ordinal) &&
            !IntelDiscreteArcRegex().IsMatch(name))
        {
            return true;
        }

        return name.Contains("RADEON GRAPHICS", StringComparison.Ordinal) ||
            AmdIntegratedGraphicsRegex().IsMatch(name);
    }

    private static bool IsDiscreteName(string name) =>
        IntelDiscreteArcRegex().IsMatch(name) || ContainsAny(name, DiscreteNameMarkers);

    private static string NormalizeName(string? name) => (name ?? string.Empty)
        .Trim()
        .ToUpperInvariant()
        .Replace("(R)", string.Empty, StringComparison.Ordinal)
        .Replace("(TM)", string.Empty, StringComparison.Ordinal);

    private static bool ContainsAny(string value, string[] markers)
    {
        foreach (string marker in markers)
            if (value.Contains(marker, StringComparison.Ordinal))
                return true;
        return false;
    }

    [GeneratedRegex(@"\bARC\s+(?:(?:A|B)\d{3}|PRO)\b", RegexOptions.CultureInvariant)]
    private static partial Regex IntelDiscreteArcRegex();

    [GeneratedRegex(@"\bRADEON\s+(?:(?:RX\s+)?VEGA\s+\d+|\d{3,4}[MS])\b", RegexOptions.CultureInvariant)]
    private static partial Regex AmdIntegratedGraphicsRegex();
}

/// <summary>
/// 生产身份提供者：SetupAPI 纯 P/Invoke 枚举 DEVCLASS_DISPLAY
/// （{4d36e968-e325-11ce-bfc1-08002be10318}），零 COM/零 WMI。
/// P/Invoke 形态对齐仓内先例 Services/PdChargerDetector.cs。
/// </summary>
internal sealed class SetupDiGpuIdentityProvider : IGpuIdentityProvider
{
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");

    public IReadOnlyList<GpuDisplayDevice> EnumerateDisplayDevices()
    {
        var result = new List<GpuDisplayDevice>();
        Guid classGuid = DisplayClassGuid;
        IntPtr set = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, DIGCF_PRESENT);
        if (set == INVALID_HANDLE_VALUE)
        {
            int hr = Marshal.GetLastWin32Error();
            SensorLogger.Log($"[GPU-IDENT] SetupDiGetClassDevs failed err={hr}");
            return result;
        }

        try
        {
            var devInfo = new SP_DEVINFO_DATA();
            devInfo.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref devInfo); i++)
            {
                string instanceId = GetInstanceId(set, ref devInfo);
                string desc = GetRegistryProperty(set, ref devInfo, SPDRP_DEVICEDESC);
                if (string.IsNullOrEmpty(instanceId) && string.IsNullOrEmpty(desc)) continue;
                result.Add(new GpuDisplayDevice(instanceId, desc));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static string GetInstanceId(IntPtr set, ref SP_DEVINFO_DATA devInfo)
    {
        SetupDiGetDeviceInstanceIdW(set, ref devInfo, null, 0, out uint required);
        if (required == 0 || required > 4096) return "";
        var buf = new char[required];
        if (!SetupDiGetDeviceInstanceIdW(set, ref devInfo, buf, (uint)buf.Length, out _))
            return "";
        return new string(buf).TrimEnd('\0');
    }

    private static string GetRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA devInfo, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref devInfo, property, out _, null, 0, out uint required);
        if (required == 0 || required > 8192) return "";
        var buf = new byte[required];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref devInfo, property, out _, buf, (uint)buf.Length, out _))
            return "";
        return Encoding.Unicode.GetString(buf, 0, (int)required).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        char[]? DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint Property,
        out uint RegDataType, byte[]? Buffer, uint BufferSize, out uint RequiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
}
