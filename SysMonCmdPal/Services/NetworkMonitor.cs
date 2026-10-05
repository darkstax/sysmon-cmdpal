// Copyright (c) 2026 SysMonCmdPal
// 网络速度采集器 — 按物理接口独立计算，EMA 平滑，异常值过滤
// 从 SystemInfoService 拆分而来

using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;

namespace SysMonCmdPal;

/// <summary>
/// 网络速度采集器。按物理接口独立计算 delta，EMA 平滑。
///
/// 接口筛选为三层判定链（见 NetworkInterfaceClassifier）：
///   ① 硬门槛：Up + Ethernet/Wireless80211 + speed > 0（不变）
///   ② 手动覆盖：settings.json 的 selectedNicGuids 非 auto 时，只保留用户选中的接口
///   ③ 硬件白名单：PnpInstanceID 前缀命中 PCI\/USB\/PCIENUM\ ⇒ 物理；
///      其余前缀 ⇒ 虚拟；读不到 ⇒ Unknown 降级到下面的关键词黑名单（安全网）
/// </summary>
internal sealed class NetworkMonitor : ISystemInfoSource
{
    /// <summary>
    /// 降级安全网（仅用于 Unknown 判定）：注册表绑定表读不到该接口时的关键词排除表。
    /// 白名单命中时不走这里（避免误杀名字里含 "Virtual" 的真硬件）；
    /// 但 filter 镜像接口见 <see cref="FilterMirrorTokens"/>。
    /// </summary>
    private static readonly string[] ExcludedDescriptionTokens =
    [
        "Hyper-V",
        "vEthernet",
        "WSL",
        "Virtual",
        "Loopback",
        "Teredo",
        "ISATAP",
        "Bluetooth",
        "Wi-Fi Direct",
        "Wintun",
        "Meta",
        "TAP-Windows",
        "Tunnel",
        "NDIS 6 Filter",  // 卡巴斯基等安全软件的 NDIS 过滤器镜像接口
    ];

    private static readonly string[] ExcludedNameTokens =
    [
        "Bluetooth",
        "-WFP",
        "-Native WiFi Filter",
        "-QoS Packet Scheduler",
    ];

    /// <summary>
    /// 过滤器镜像接口专属 token —— belt-and-suspenders。
    ///
    /// 宿主机实测：卡巴斯基 NDIS 6 Filter / WFP / QoS 等镜像接口**根本不在注册表绑定表内**，
    /// 故天然落到 Unknown 走 <see cref="ExcludedDescriptionTokens"/>。但万一某个 Windows 版本
    /// 把镜像接口也写进绑定表并继承父网卡的 PnpInstanceID，白名单就会把它误判成
    /// PhysicalHardware 从而跳过黑名单 —— 流量翻倍问题复现。
    /// 这些 token 绝不可能出现在真硬件网卡描述里，故即使白名单命中也要排除。
    /// </summary>
    private static readonly string[] FilterMirrorTokens =
    [
        "NDIS 6 Filter",
        "WFP Native MAC Layer",
        "WFP 802.3 MAC Layer",
        "QoS Packet Scheduler",
        "Native WiFi Filter",
        "Virtual WiFi Filter Driver",
        "Virtual Switch Extension",
    ];

    private sealed class NetInterfaceState
    {
        public long PrevBytesDown;
        public long PrevBytesUp;
        public DateTime PrevTime;
    }

    private readonly Dictionary<string, NetInterfaceState> _netStates = new();
    private readonly object _netLock = new();
    private double _smoothDown;  // exponential moving average (bytes/sec)
    private double _smoothUp;
    private bool _netSeeded;     // EMA 是否已初始化（首次有效采样后为 true）

    private const double EmaAlpha = 0.4;   // smoothing factor: 0=全平滑 1=无平滑
    private const double MaxReasonableSpeed = 1_250_000_000.0; // ~10 Gbps cap

    // ---- Debug logging ----
    private int _netLogCount;
    private static readonly string _netLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysMonCmdPal", "net_debug.log");

    private static readonly bool _netLogEnabled = IsNetLogEnabled();

    /// <summary>
    /// T1-1: 作为采集源接入 SystemInfoService 注册表。原 Refresh() 里
    /// 「取时间戳 → ReadSpeed → 写 NetDown/NetUp」三步语义完全保留。
    /// </summary>
    public void ReadInto(ref SystemSnapshot snapshot)
    {
        var (down, up) = ReadSpeed(DateTime.UtcNow);
        snapshot.NetDown = down;
        snapshot.NetUp = up;
    }

    /// <summary>首次/重置时：枚举所有物理接口，记录基线字节数</summary>
    public void Seed()
    {
        lock (_netLock)
        {
            _netStates.Clear();
            var now = DateTime.UtcNow;
            foreach (var ni in GetPhysicalInterfaces())
            {
                var stats = ni.GetIPStatistics();
                _netStates[ni.Id] = new NetInterfaceState
                {
                    PrevBytesDown = stats.BytesReceived,
                    PrevBytesUp = stats.BytesSent,
                    PrevTime = now,
                };
            }
            _smoothDown = 0;
            _smoothUp = 0;
            _netSeeded = false;
        }
    }

    // P5: 缓存物理接口列表 10 秒 — 接口很少变化，避免每秒全量枚举
    private static List<NetworkInterface>? _cachedInterfaces;
    private static DateTime _interfaceCacheTime = DateTime.MinValue;
    private static readonly object _ifaceCacheLock = new();
    private static readonly TimeSpan InterfaceCacheTtl = TimeSpan.FromSeconds(10);

    // ---- 网卡分类接缝（生产 = 注册表绑定表；测试注入 fixture）----
    private static INicBindingProvider s_bindingProvider = new RegistryNicBindingProvider();

    /// <summary>测试注入点：替换注册表读取实现。生产路径永远用 RegistryNicBindingProvider。</summary>
    internal static void SetBindingProvider(INicBindingProvider provider)
    {
        lock (_ifaceCacheLock)
        {
            s_bindingProvider = provider;
            _cachedInterfaces = null;
            _interfaceCacheTime = DateTime.MinValue;
        }
    }

    /// <summary>测试辅助：还原生产接缝（含清除手动选择注入）并清空全部缓存。</summary>
    internal static void ResetBindingProvider()
    {
        SetBindingProvider(new RegistryNicBindingProvider());
        SetManualSelectionOverride(null);
    }

    // ---- 手动选择（settings.json 的 selectedNicGuids）----
    private static readonly object ManualSelectionLock = new();
    private static IReadOnlySet<string>? s_manualSelectionCache;
    private static DateTime s_manualSelectionCacheTime = DateTime.MinValue;
    private static readonly TimeSpan ManualSelectionCacheTtl = TimeSpan.FromSeconds(1);

    /// <summary>测试注入点：直接给出选择值（绕过 settings.json 文件）。null = 走文件读取。</summary>
    private static Func<string?>? s_manualSelectionOverride;

    internal static void SetManualSelectionOverride(Func<string?>? provider)
    {
        lock (ManualSelectionLock)
        {
            s_manualSelectionOverride = provider;
            s_manualSelectionCache = null;
            s_manualSelectionCacheTime = DateTime.MinValue;
        }
    }

    /// <summary>只保留物理硬件接口（真实网卡），排除虚拟/隧道/蓝牙/filter driver 绑定。</summary>
    public static List<NetworkInterface> GetPhysicalInterfaces()
    {
        lock (_ifaceCacheLock)
        {
            if (_cachedInterfaces != null && (DateTime.UtcNow - _interfaceCacheTime) < InterfaceCacheTtl)
                return _cachedInterfaces;
        }

        var manualSelection = GetManualSelection();
        var provider = s_bindingProvider;

        var all = NetworkInterface.GetAllNetworkInterfaces();
        var candidates = new List<NicCandidate>(all.Length);
        foreach (var ni in all)
        {
            candidates.Add(new NicCandidate(
                ni.Id,
                ni.OperationalStatus,
                ni.NetworkInterfaceType,
                ni.Description,
                ni.Name,
                ni.Speed,
                NetworkInterfaceClassifier.ClassifyPnpInstanceId(
                    SafeGetPnpInstanceId(provider, ni.Id))));
        }

        var selected = SelectInterfaceIds(candidates, manualSelection);
        var result = all.Where(ni => selected.Contains(ni.Id)).ToList();

        lock (_ifaceCacheLock)
        {
            _cachedInterfaces = result;
            _interfaceCacheTime = DateTime.UtcNow;
        }
        return result;
    }

    /// <summary>
    /// 三层判定链的纯函数核心（全部输入为值对象，可脱离真实网络栈单测）。
    ///   ① 硬门槛：Up + Ethernet/Wireless80211 + speed > 0
    ///   ② 手动覆盖：manualSelection 非空时只保留选中接口 —— 跳过自动判定（白名单/黑名单都不否决），
    ///      这是用户纠正自动误判的唯一出口
    ///   ③ 硬件白名单：PhysicalHardware ⇒ 物理；Virtual ⇒ 排除；Unknown ⇒ 降级关键词黑名单
    ///
    /// 手动选中的网卡**全部消失**时回退自动判定，而不是返回空集合 ——
    /// 空集合会让速度恒为 0，用户看到的是"网络坏了"。与设置页
    /// ResolveNicSelection（全部失效 ⇒ 回退 auto）语义一致。
    /// </summary>
    internal static HashSet<string> SelectInterfaceIds(
        IReadOnlyList<NicCandidate> candidates,
        IReadOnlySet<string> manualSelection)
    {
        var automatic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            if (IsPhysicalInterfaceCandidate(
                c.Status, c.Type, c.Description, c.Name, c.Speed, c.Classification))
                automatic.Add(c.Id);

            if (manualSelection.Contains(c.Id) && PassesHardGate(c.Status, c.Type, c.Speed))
                manual.Add(c.Id);
        }

        if (manualSelection.Count > 0 && manual.Count > 0)
            return manual;

        return automatic;
    }

    private static string? SafeGetPnpInstanceId(INicBindingProvider provider, string interfaceGuid)
    {
        try
        {
            return provider.GetPnpInstanceId(interfaceGuid);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] NicBinding lookup failed: {ex.Message}");
            return null;   // 读失败 ⇒ Unknown ⇒ 降级黑名单
        }
    }

    /// <summary>
    /// 当前手动选择（settings.json 的 selectedNicGuids 键，1s 缓存）。
    /// 空集合 = 自动模式。全部 GUID 失效时也返回空集合（等价回退 auto）。
    /// </summary>
    internal static IReadOnlySet<string> GetManualSelection()
    {
        lock (ManualSelectionLock)
        {
            if (s_manualSelectionCache != null &&
                (DateTime.UtcNow - s_manualSelectionCacheTime) < ManualSelectionCacheTtl)
                return s_manualSelectionCache;

            string? raw;
            if (s_manualSelectionOverride != null)
            {
                raw = s_manualSelectionOverride();
            }
            else
            {
                raw = ReadSelectedNicsFromSettings();
            }

            var parsed = NetworkInterfaceClassifier.ParseSelectedGuids(raw);
            s_manualSelectionCache = parsed;
            s_manualSelectionCacheTime = DateTime.UtcNow;
            return parsed;
        }
    }

    /// <summary>
    /// 直读 settings.json 的 selectedNicGuids 键（JsonNode 解析，AOT/Trim 安全）。
    /// 与 SysMonSettingsManager 实例解耦：NetworkMonitor 在 SystemInfoService 注册表里
    /// 不持有 settings 依赖，直读文件可避免构造顺序耦合。任何异常 ⇒ null（= 自动模式）。
    /// </summary>
    internal static string? ReadSelectedNicsFromSettings()
    {
        try
        {
            var path = SensorChainConfig.ConfigPath;
            if (!File.Exists(path))
                return null;

            return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)
                ?[NetworkInterfaceClassifier.SelectedNicsKey]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>设置变更后调用：清空接口缓存与手动选择缓存，并重新播种基线。</summary>
    internal void InvalidateNicSelection()
    {
        lock (_ifaceCacheLock)
        {
            _cachedInterfaces = null;
            _interfaceCacheTime = DateTime.MinValue;
        }

        lock (ManualSelectionLock)
        {
            s_manualSelectionCache = null;
            s_manualSelectionCacheTime = DateTime.MinValue;
        }

        Seed();
    }

    /// <summary>
    /// 三态判定链的纯函数核心。
    /// classification 语义：
    ///   PhysicalHardware → 白名单命中，跳过关键词黑名单（防误杀真硬件），仅保留 filter 镜像兜底
    ///   Virtual          → 一刀切排除
    ///   null / Unknown   → 现有黑名单逻辑逐字不变（降级安全网）
    /// </summary>
    internal static bool IsPhysicalInterfaceCandidate(
        OperationalStatus status,
        NetworkInterfaceType type,
        string? description,
        string? name,
        long speed,
        NicClassification? classification = null)
    {
        if (!PassesHardGate(status, type, speed))
            return false;

        var desc = description ?? "";
        var ifaceName = name ?? "";

        // 白名单命中 ⇒ 真硬件总线，关键词黑名单不再否决。
        if (classification == NicClassification.PhysicalHardware)
        {
            // 唯一例外：过滤器镜像接口 token（绝不出现在真硬件描述里）。
            // 宿主机实测镜像接口不在绑定表内，但防御未来 Windows 把父网卡 PnpInstanceID
            // 一并写进镜像接口子键的场景 —— 那会导致流量翻倍问题复现。
            return !ContainsAny(desc, FilterMirrorTokens)
                && !ContainsAny(ifaceName, FilterMirrorTokens);
        }

        if (classification == NicClassification.Virtual)
            return false;

        // Unknown（含 classification == null）：现状逻辑逐字保留。
        if (ContainsAny(desc, ExcludedDescriptionTokens))
            return false;
        if (ContainsAny(ifaceName, ExcludedNameTokens))
            return false;

        return true;
    }

    /// <summary>硬门槛（与分类无关，三层共用）：Up + Ethernet/Wireless80211 + speed > 0。</summary>
    internal static bool PassesHardGate(
        OperationalStatus status,
        NetworkInterfaceType type,
        long speed)
    {
        if (status != OperationalStatus.Up)
            return false;
        if (type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
            return false;
        if (speed <= 0)
            return false;

        return true;
    }

    /// <summary>按接口独立计算速度，EMA 平滑，异常值过滤</summary>
    public (double Down, double Up) ReadSpeed(DateTime now)
    {
        try { return ReadSpeedInternal(now); }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] ReadNetSpeed: {ex.Message}"); return (0, 0); }
    }

    private (double Down, double Up) ReadSpeedInternal(DateTime now)
    {
        lock (_netLock)
        {
            var interfaces = GetPhysicalInterfaces();
            double totalDown = 0, totalUp = 0;
            bool interfaceSetChanged = false;

            const int NetLogLimit = 60;
            bool doLog = _netLogEnabled && _netLogCount < NetLogLimit;

            if (doLog)
            {
                NetLog($"--- cycle {_netLogCount} --- interfaces={interfaces.Count}");
                var manual = GetManualSelection();
                NetLog(manual.Count > 0
                    ? $"  mode=manual selected={string.Join(";", manual)}"
                    : "  mode=auto");
                foreach (var ni in interfaces)
                    NetLog($"  iface: {ni.Name} id={ni.Id.Substring(0,8)}");
            }

            // 检查接口集合是否变化
            var currentIds = new HashSet<string>(interfaces.Select(i => i.Id));
            var trackedIds = new HashSet<string>(_netStates.Keys);
            if (!currentIds.SetEquals(trackedIds))
                interfaceSetChanged = true;

            foreach (var ni in interfaces)
            {
                var stats = ni.GetIPStatistics();
                long bytesDown = stats.BytesReceived;
                long bytesUp = stats.BytesSent;

                if (!_netStates.TryGetValue(ni.Id, out var state))
                {
                    // 新出现的接口：记录基线，本次不计入速度
                    _netStates[ni.Id] = new NetInterfaceState
                    {
                        PrevBytesDown = bytesDown,
                        PrevBytesUp = bytesUp,
                        PrevTime = now,
                    };
                    if (doLog) NetLog($"  NEW iface {ni.Name}: baseline bytesDown={bytesDown}");
                    continue;
                }

                double elapsed = (now - state.PrevTime).TotalSeconds;
                if (elapsed < 0.05)
                {
                    if (doLog) NetLog($"  SKIP {ni.Name}: elapsed={elapsed:F4}s too short");
                    continue;
                }

                double downSpeed = (bytesDown - state.PrevBytesDown) / elapsed;
                double upSpeed = (bytesUp - state.PrevBytesUp) / elapsed;

                long deltaDown = bytesDown - state.PrevBytesDown;
                long deltaUp = bytesUp - state.PrevBytesUp;

                if (doLog)
                    NetLog($"  {ni.Name}: elapsed={elapsed:F3}s deltaDown={deltaDown} deltaUp={deltaUp} rawDown={downSpeed:F0}B/s rawUp={upSpeed:F0}B/s");

                // 异常值过滤：计数器回绕、接口重置、或超出合理范围
                if (downSpeed < 0 || downSpeed > MaxReasonableSpeed)
                    downSpeed = 0;
                if (upSpeed < 0 || upSpeed > MaxReasonableSpeed)
                    upSpeed = 0;

                totalDown += downSpeed;
                totalUp += upSpeed;

                // 更新基线
                state.PrevBytesDown = bytesDown;
                state.PrevBytesUp = bytesUp;
                state.PrevTime = now;
            }

            // 清理已消失的接口
            foreach (var id in trackedIds.Except(currentIds))
                _netStates.Remove(id);

            // 接口集合大幅变化时重置平滑
            if (interfaceSetChanged && _netStates.Count == 0)
            {
                _smoothDown = 0;
                _smoothUp = 0;
                _netSeeded = false;
                if (doLog) NetLog("  RESET: all interfaces gone");
                _netLogCount++;
                return (0, 0);
            }

            // EMA 平滑
            if (!_netSeeded)
            {
                // 首次有效采样：直接赋值，不做平滑
                _smoothDown = totalDown;
                _smoothUp = totalUp;
                _netSeeded = true;
            }
            else
            {
                _smoothDown = EmaAlpha * totalDown + (1 - EmaAlpha) * _smoothDown;
                _smoothUp = EmaAlpha * totalUp + (1 - EmaAlpha) * _smoothUp;
            }

            if (doLog)
                NetLog($"  RESULT: totalDown={totalDown:F0} totalUp={totalUp:F0} smoothDown={_smoothDown:F0} smoothUp={_smoothUp:F0}");

            if (_netLogCount < NetLogLimit) _netLogCount++;
            return (_smoothDown, _smoothUp);
        }
    }

    private void NetLog(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(_netLogPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(_netLogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { /* ignore */ }
    }

    private static bool IsNetLogEnabled()
    {
#if DEBUG
        return true;
#else
        var value = Environment.GetEnvironmentVariable("SYSMONCMDPAL_ENABLE_NET_LOG");
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase);
#endif
    }

    private static bool ContainsAny(string value, string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (value.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
