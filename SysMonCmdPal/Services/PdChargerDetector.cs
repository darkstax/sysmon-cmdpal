// Copyright (c) 2026 SysMonCmdPal
// USB-C / PD 充电检测 — 多路径探测（不只 USB4）
// 非管理员，通过 SetupAPI 枚举设备存在性判断

using System.Runtime.InteropServices;

namespace SysMonCmdPal;

/// <summary>
/// USB-C / PD 充电状态检测。检测当前是否通过 USB-C/PD 充电（布尔判断，无法获取 PD 协商详情）。
/// 多路径探测：UCSI ACPI 设备 + USB4 VIRTUAL_POWER_PDO + UCM 连接器 + CAD 设备。
/// </summary>
internal static class PdChargerDetector
{
    private static bool? _cached;

    /// <summary>当前是否检测到 USB-C/PD 充电环境（设备存在）。不代表当前一定在充电。</summary>
    public static bool IsUsbCEnvironment
    {
        get
        {
            if (_cached is { } v) return v;
            _cached = Detect();
            return _cached.Value;
        }
    }

    /// <summary>
    /// 检测 USB-C/PD 充电环境。满足任一条件即认为本机具备 USB-C 充电能力：
    /// 1. ACPI USBC000 设备存在（UCM-UCSI ACPI，USB Type-C 连接器管理）
    /// 2. USB4 VIRTUAL_POWER_PDO 存在（USB4 连接管理器创建的电源 PDO）
    /// 3. UCM 类驱动设备存在（USB Connector Manager）
    /// 4. ROOT\CAD 设备存在（Connector Attached Device，PD 连接器关联）
    /// </summary>
    private static bool Detect()
    {
        try
        {
            // 用 SetupDi 枚举所有设备，检查是否存在 USB-C/PD 相关设备 ID
            Guid nullGuid = Guid.Empty;
            IntPtr devs = SetupDiGetClassDevs(ref nullGuid, null, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
            if (devs == INVALID_HANDLE_VALUE) return false;

            try
            {
                var devInfo = new SP_DEVINFO_DATA();
                devInfo.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

                for (uint i = 0; SetupDiEnumDeviceInfo(devs, i, ref devInfo); i++)
                {
                    // 取设备实例 ID
                    string? instanceId = GetDeviceInstanceId(devs, ref devInfo);
                    if (string.IsNullOrEmpty(instanceId)) continue;

                    string id = instanceId.ToUpperInvariant();

                    // 路径 1: ACPI USBC000 (UCM-UCSI ACPI Device)
                    if (id.Contains(@"ACPI\USBC")) return true;

                    // 路径 2: USB4 VIRTUAL_POWER_PDO
                    if (id.Contains(@"USB4\VIRTUAL_POWER_PDO")) return true;

                    // 路径 3: UCM 类设备 (USB Connector Manager)
                    if (id.Contains(@"UCM") || id.Contains(@"UCSI")) return true;

                    // 路径 4: CAD (Connector Attached Device)
                    if (id.StartsWith(@"ROOT\CAD")) return true;

                    // 路径 5: Type-C / PD 标识
                    if (id.Contains("TYPEC") || id.Contains(@"USB\TYPEC")) return true;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devs);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PdDetector] Detect: {ex.Message}");
        }
        return false;
    }

    private static string? GetDeviceInstanceId(IntPtr devs, ref SP_DEVINFO_DATA did)
    {
        // SetupDiGetDeviceInstanceIdA — ANSI 版，够用
        SetupDiGetDeviceInstanceIdA(devs, ref did, null, 0, out uint required);
        if (required == 0) return null;
        var buf = new byte[required];
        if (!SetupDiGetDeviceInstanceIdA(devs, ref did, buf, (uint)buf.Length, out _))
            return null;
        return System.Text.Encoding.ASCII.GetString(buf, 0, (int)required - 1); // -1 去掉末尾 null
    }

    // ============== P/Invoke ==============
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint DIGCF_ALLCLASSES = 0x00000040;
    private const uint DIGCF_PRESENT = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string? Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdA(
        IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA did,
        byte[]? DeviceInstanceId, uint PropertyBufferSize, out uint RequiredSize);

    [DllImport("setupapi.dll")]
    private static extern void SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);
}
