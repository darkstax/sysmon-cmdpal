// Copyright (c) 2026 SysMonCmdPal
// 网络速度采集器 — 按物理接口独立计算，EMA 平滑，异常值过滤
// 从 SystemInfoService 拆分而来

using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;

namespace SysMonCmdPal;

/// <summary>
/// 网络速度采集器。按物理接口独立计算 delta，EMA 平滑，排除虚拟/隧道/蓝牙接口。
/// </summary>
internal sealed class NetworkMonitor
{
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

    /// <summary>只保留物理硬件接口（真实网卡），排除虚拟/隧道/蓝牙/filter driver 绑定</summary>
    public static List<NetworkInterface> GetPhysicalInterfaces()
    {
        lock (_ifaceCacheLock)
        {
            if (_cachedInterfaces != null && (DateTime.UtcNow - _interfaceCacheTime) < InterfaceCacheTtl)
                return _cachedInterfaces;
        }

        var result = new List<NetworkInterface>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;
            if (ni.NetworkInterfaceType is not
                (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211))
                continue;

            var desc = ni.Description ?? "";
            var name = ni.Name ?? "";

            // 排除虚拟/隧道/蓝牙/软件接口
            if (desc.Contains("Hyper-V") || desc.Contains("vEthernet") ||
                desc.Contains("WSL") || desc.Contains("Virtual") ||
                desc.Contains("Loopback") || desc.Contains("Teredo") ||
                desc.Contains("ISATAP") ||
                desc.Contains("Bluetooth") ||          // 蓝牙 PAN
                desc.Contains("Wi-Fi Direct") ||        // WiFi 直连虚拟适配器
                desc.Contains("Wintun") ||              // VPN 隧道驱动
                desc.Contains("Meta") ||                // Meta 隧道
                desc.Contains("TAP-Windows") ||         // OpenVPN TAP
                desc.Contains("Tunnel") ||              // 各种隧道
                name.Contains("Bluetooth"))             // 蓝牙（名称匹配兜底）
                continue;

            // 排除 Windows filter driver 绑定（同一物理网卡的多个虚拟层）
            // 这些绑定会重复上报相同的流量计数器，导致速度被多次累加
            if (name.Contains("-WFP") ||
                name.Contains("-Native WiFi Filter") ||
                name.Contains("-QoS Packet Scheduler"))
                continue;

            // 只保留有真实硬件的接口（有非零 Speed）
            if (ni.Speed <= 0)
                continue;

            result.Add(ni);
        }
        lock (_ifaceCacheLock)
        {
            _cachedInterfaces = result;
            _interfaceCacheTime = DateTime.UtcNow;
        }
        return result;
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
            bool doLog = _netLogCount < NetLogLimit;

            if (doLog)
            {
                NetLog($"--- cycle {_netLogCount} --- interfaces={interfaces.Count}");
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
}
