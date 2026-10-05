// Copyright (c) 2026 SysMonCmdPal
// 网卡分类器 — 硬件网卡白名单识别（主路径）+ 注册表绑定表读取接缝。
//
// 判据反转：旧实现靠关键词黑名单排除虚拟接口，无法枚举未来的虚拟/镜像/隧道接口
// （卡巴斯基 NDIS 6 Filter 镜像接口曾导致流量翻倍）。本文件改为「白名单识别」：
// 只有真实硬件总线（PCI\/USB\/PCIENUM\）判 PhysicalHardware，其余前缀一律 Virtual。
//
// 数据来源：HKLM\SYSTEM\CurrentControlSet\Control\Network\{4D36E972-...}\<InterfaceGuid>\Connection\PnpInstanceId
// （InterfaceGuid == NetworkInterface.Id）。纯 Win32 只读，无 COM/无 WMI —— 出货 Release 的
// trimming 会禁用 built-in COM，故不得走 System.Management（见 GpuSensorReader 的既有裁决）。
//
// 保守方向：注册表读失败 / 接口不在绑定表内 ⇒ Unknown（不是 Virtual），交由旧黑名单降级判定。
// 宁可漏过滤退回旧逻辑，也不可把真网卡误判成虚拟导致速度恒为 0。

using System.Net.NetworkInformation;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Microsoft.Win32;

namespace SysMonCmdPal;

/// <summary>
/// 网卡三态判定结果。
/// 公开可见性（而非 internal）：xUnit 测试类必须 public，public 测试方法的
/// InlineData 参数类型也须 public —— 与既有 GpuKind 同一处理方式。
/// </summary>
public enum NicClassification
{
    /// <summary>注册表查不到 / PnpInstanceID 为空 —— 交旧黑名单降级判定。</summary>
    Unknown = 0,

    /// <summary>PnpInstanceID 命中硬件总线白名单（PCI\ / PCIENUM\ / USB\）。</summary>
    PhysicalHardware = 1,

    /// <summary>PnpInstanceID 存在但非硬件总线前缀 —— 虚拟/隧道/蓝牙/虚拟交换机等。</summary>
    Virtual = 2,
}

/// <summary>单个网卡的判定输入（脱离 NetworkInterface 对象，便于纯函数单测）。</summary>
internal readonly record struct NicCandidate(
    string Id,
    OperationalStatus Status,
    NetworkInterfaceType Type,
    string? Description,
    string? Name,
    long Speed,
    NicClassification Classification);

/// <summary>
/// 接口在设置页的可用性分类 —— 同时决定三件事：是否正常列入选项、
/// 已选时是否补回列表、以及补回时用哪个标注。
///
/// 为什么需要分类而不是一个 bool：bool 无法区分「临时断连（可恢复，值得保住用户选择）」
/// 与「永久不可用（镜像，绝不可选）」和「已连接但采集不到（不能谎称未连接）」，
/// 会把镜像也当成"临时不可用"补回列表（t3 round-3 finding）。
///
/// 公开可见性（而非 internal）：xUnit 测试类必须 public，public 测试方法的
/// InlineData 参数类型也须 public —— 与既有 NicClassification/GpuKind 同一处理方式。
/// </summary>
public enum NicUsability
{
    /// <summary>可生效：正常列入选项，无标注。</summary>
    Effective = 0,

    /// <summary>
    /// 未连接（OperationalStatus != Up）。可恢复 ⇒ 已选时补回列表并标「（未连接）」，
    /// 保住用户的选择意图，避免一次短暂断连就永久丢失。
    /// </summary>
    Disconnected = 1,

    /// <summary>
    /// 已连接但当前采集不到（speed ≤ 0，或类型不是 Ethernet/Wireless80211）。
    /// **不**标「（未连接）」—— 那与事实不符（接口确实是 Up 的）；
    /// 也不补回列表（无法给出准确标注），由 ResolveNicSelection 收敛为 auto。
    /// </summary>
    NotCollectable = 2,

    /// <summary>
    /// 永久不可用：过滤器镜像接口。与父网卡字节计数重复，选中必致流量翻倍
    /// （见 <see cref="NetworkMonitor.IsFilterMirror"/>）⇒ 永不列入、永不补回，
    /// 已选则收敛为 auto。
    /// </summary>
    PermanentlyUnavailable = 3,
}

/// <summary>设置页选择项的数据源（脱离 NetworkInterface 对象，便于纯函数单测）。</summary>
/// <param name="Usability">
/// 可用性分类。只有 <see cref="NicUsability.Effective"/> 会正常列入选项 ——
/// 宿主机实测 65 个接口里仅 20 个可生效，其余 45 个选中是静默 no-op。
/// </param>
internal readonly record struct NicChoiceSource(
    string Id,
    string Name,
    string Description,
    NicClassification Classification,
    NicUsability Usability = NicUsability.Effective);

/// <summary>注册表绑定表读取接缝：生产 = HKLM 只读；测试 = fixture 字典注入。</summary>
internal interface INicBindingProvider
{
    /// <summary>返回该接口的设备实例 ID；接口不在绑定表内或值缺失时返回 null。</summary>
    string? GetPnpInstanceId(string interfaceGuid);
}

/// <summary>
/// 生产实现：读 <c>HKLM\SYSTEM\CurrentControlSet\Control\Network\{4D36E972…}</c> 绑定表。
/// 表整体缓存 10 秒（与 NetworkMonitor 的接口缓存 TTL 同量级），避免每秒重复打开注册表。
/// </summary>
internal sealed class RegistryNicBindingProvider : INicBindingProvider
{
    internal const string NetworkClassKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";

    private static readonly TimeSpan TableTtl = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private Dictionary<string, string?>? _table;
    private DateTime _tableTime = DateTime.MinValue;

    public string? GetPnpInstanceId(string interfaceGuid)
    {
        if (string.IsNullOrWhiteSpace(interfaceGuid))
            return null;

        var table = GetTable();
        return table.TryGetValue(interfaceGuid, out var value) ? value : null;
    }

    private Dictionary<string, string?> GetTable()
    {
        lock (_sync)
        {
            if (_table is not null && (DateTime.UtcNow - _tableTime) < TableTtl)
                return _table;

            _table = ReadTable();
            _tableTime = DateTime.UtcNow;
            return _table;
        }
    }

    /// <summary>
    /// 读整张绑定表。任何异常 ⇒ 空表 ⇒ 全部接口判 Unknown ⇒ 降级旧黑名单逻辑
    /// （最坏情况 = 重构前行为，不会更差）。
    /// </summary>
    internal static Dictionary<string, string?> ReadTable()
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(NetworkClassKeyPath);
            if (classKey is null)
                return result;

            foreach (var subKeyName in classKey.GetSubKeyNames())
            {
                // 绑定表内接口子键名恒为 {GUID}；其余子键（如 "Descriptions"）跳过。
                if (subKeyName.Length == 0 || subKeyName[0] != '{')
                    continue;

                using var connection = classKey.OpenSubKey($@"{subKeyName}\Connection");
                result[subKeyName] = connection?.GetValue("PnpInstanceId") as string;
            }
        }
        catch
        {
            // 读失败 ⇒ 空表（保守降级）
        }

        return result;
    }
}

/// <summary>网卡分类与选择判据（全部纯函数，可单测）。</summary>
internal static class NetworkInterfaceClassifier
{
    /// <summary>settings.json 中「主网卡」选择键（camelCase，与 btopPath 同风格）。</summary>
    internal const string SelectedNicsKey = "selectedNicGuids";

    /// <summary>「自动」选择值 —— 完全交给自动判定（白名单 + 降级黑名单）。</summary>
    internal const string AutoSelectionValue = "auto";

    /// <summary>
    /// 硬件总线白名单。只有这些前缀判 PhysicalHardware，其余一律 Virtual。
    /// USB\ 入白名单的依据：USB 网卡 / 手机 USB 共享（RNDIS）是真实物理链路；
    /// 极少数 USB 虚拟网桥会被判硬件，由设置页手动选择兜底。
    /// </summary>
    private static readonly string[] HardwareBusPrefixes =
    [
        @"PCI\",
        @"PCIENUM\",
        @"USB\",
    ];

    /// <summary>
    /// 前缀判据（唯一硬判据）。与 GpuIdentityService.IsPhysicalInstanceId 同一判据族，
    /// 额外承认 USB\（网卡走 USB 总线是常态，GPU 不是）。
    /// </summary>
    internal static NicClassification ClassifyPnpInstanceId(string? pnpInstanceId)
    {
        if (string.IsNullOrWhiteSpace(pnpInstanceId))
            return NicClassification.Unknown;

        string value = pnpInstanceId.Trim().ToUpperInvariant();
        foreach (var prefix in HardwareBusPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal))
                return NicClassification.PhysicalHardware;
        }

        // 非白名单前缀一律虚拟/非硬件 —— 新增虚拟接口天然被覆盖，不再依赖关键词补丁。
        return NicClassification.Virtual;
    }

    /// <summary>
    /// 解析 settings.json 里的选择值。空 / null / "auto" ⇒ 空集合（= 自动模式）。
    /// 容忍分号分隔的多值形态（当前 UI 单选只写一个 GUID，多选化时无需改此处）。
    /// </summary>
    internal static IReadOnlySet<string> ParseSelectedGuids(string? raw)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals(AutoSelectionValue, StringComparison.OrdinalIgnoreCase))
                continue;

            // 只接受合法 GUID（NetworkInterface.Id 形如 "{A844F74B-...}"，带花括号）。
            if (Guid.TryParse(part, out _))
                result.Add(part);
        }

        return result;
    }

    /// <summary>
    /// 设置页枚举：只列**可生效**的接口 + 类型标注，硬件在前、虚拟在后，首项恒为「自动」。
    ///
    /// 为什么过滤：宿主机实测 65 个接口里只有 20 个过硬门槛（Up + Ethernet/Wireless80211
    /// + speed &gt; 0），其余 45 个（NotPresent 的 WAN Miniport、Down 的 filter 镜像等）
    /// 选中后是静默 no-op —— 用户以为选了、实际不生效，是纯粹的误导。
    ///
    /// <paramref name="currentValue"/> 例外：用户**已选**且属于
    /// <see cref="NicUsability.Disconnected"/>（临时断连，可恢复）的接口会以
    /// <paramref name="unavailableSuffix"/> 标注补回列表。若不补，它会被判为"已消失"
    /// 而把用户的选择静默重置成 auto —— 用户明明只想监控某张卡，一次短暂断连就永久丢失该意图。
    ///
    /// 另外两类**不**补回（t3 round-3 finding）：
    ///   · <see cref="NicUsability.PermanentlyUnavailable"/>（过滤器镜像）：选中必致流量翻倍，
    ///     补回等于把它又变成一个可选项 ⇒ 直接让 ResolveNicSelection 收敛为 auto。
    ///   · <see cref="NicUsability.NotCollectable"/>（Up 但 speed ≤ 0 / 类型不符）：
    ///     接口确实是 Up 的，标「（未连接）」与事实不符 ⇒ 同样不补回、收敛为 auto。
    /// </summary>
    internal static List<ChoiceSetSetting.Choice> BuildNicChoices(
        IReadOnlyList<NicChoiceSource> interfaces,
        Func<NicClassification, string> kindLabel,
        string autoTitle,
        string? currentValue = null,
        string unavailableSuffix = "")
    {
        var choices = new List<ChoiceSetSetting.Choice>
        {
            new(autoTitle, AutoSelectionValue),
        };

        foreach (var nic in interfaces
            .Where(i => i.Usability == NicUsability.Effective)
            .OrderBy(i => KindRank(i.Classification))
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            choices.Add(BuildChoice(nic, kindLabel, suffix: ""));
        }

        // 已选且"临时断连（可恢复）"的接口：补回列表并标注，保住用户的选择意图。
        // 镜像与"已连接但采集不到"都不在此列 —— 前者永不可选，后者无法给出准确标注。
        var selected = ParseSelectedGuids(currentValue);
        foreach (var nic in interfaces)
        {
            if (nic.Usability != NicUsability.Disconnected || !selected.Contains(nic.Id))
                continue;

            choices.Add(BuildChoice(nic, kindLabel, suffix: unavailableSuffix));
        }

        return choices;
    }

    /// <summary>
    /// 判定接口在设置页的可用性 —— **唯一判据来源**，生产枚举与单测共用。
    ///
    /// 顺序有讲究：先判镜像（永不可用优先于一切），再判是否 Up（决定是否可恢复），
    /// 最后判是否可采集（speed/type）。这个顺序保证镜像永远不会被当成"临时断连"
    /// 而补回列表（t3 round-3 finding 的根因）。
    /// </summary>
    internal static NicUsability ClassifyUsability(
        OperationalStatus status,
        NetworkInterfaceType type,
        long speed,
        string? description,
        string? name)
    {
        // 镜像接口：与父网卡字节计数重复，选中必致流量翻倍 ⇒ 永不可用（不可恢复）。
        if (NetworkMonitor.IsFilterMirror(description, name))
            return NicUsability.PermanentlyUnavailable;

        // 未连接：可恢复 ⇒ 已选时补回并标「（未连接）」。
        if (status != OperationalStatus.Up)
            return NicUsability.Disconnected;

        // 已连接但采集不到（speed ≤ 0 或类型不符）：不可标「未连接」（与事实不符）。
        if (speed <= 0 || type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
            return NicUsability.NotCollectable;

        return NicUsability.Effective;
    }

    private static ChoiceSetSetting.Choice BuildChoice(
        NicChoiceSource nic,
        Func<NicClassification, string> kindLabel,
        string suffix)
    {
        string kind = kindLabel(nic.Classification) + suffix;
        string description = Truncate(nic.Description, 40);
        string title = string.IsNullOrWhiteSpace(description)
            ? $"{nic.Name} — {kind}"
            : $"{nic.Name} — {kind} — {description}";

        return new ChoiceSetSetting.Choice(title, nic.Id);
    }

    /// <summary>
    /// 选择值在选项列表重建后的收敛：至少一个已选 GUID 仍存在 ⇒ 保留原值；
    /// 全部失效（网卡拔出/驱动卸载）⇒ 回退 auto，避免"选中集合全不存在"导致速度恒为 0。
    /// </summary>
    internal static string ResolveNicSelection(
        string? currentValue,
        IReadOnlyList<ChoiceSetSetting.Choice> choices,
        string autoValue)
    {
        var selected = ParseSelectedGuids(currentValue);
        if (selected.Count == 0)
            return autoValue;

        foreach (var guid in selected)
        {
            foreach (var choice in choices)
            {
                if (string.Equals(choice.Value, guid, StringComparison.OrdinalIgnoreCase))
                    return currentValue!;
            }
        }

        return autoValue;
    }

    private static int KindRank(NicClassification kind) => kind switch
    {
        NicClassification.PhysicalHardware => 0,
        NicClassification.Unknown => 1,
        _ => 2,
    };

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= max ? value : value[..max] + "…";
    }
}
