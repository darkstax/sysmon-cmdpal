// Copyright (c) 2026 SysMonCmdPal
// 磁盘信息采集器 — DriveInfo + PerformanceCounter IO 速度
// 从 SystemInfoService 拆分而来

using System.Diagnostics;
using System.Management;

namespace SysMonCmdPal;

/// <summary>
/// 磁盘信息采集器。枚举固定/可移动驱动器，附加 IO 读写速度。
/// PerformanceCounter 按驱动器惰性创建并复用。
/// 物理磁盘通过 WMI Win32_DiskDrive 查询，关联逻辑分区。
/// </summary>
internal sealed class DiskMonitor
{
    private readonly Dictionary<string, (PerformanceCounter? Read, PerformanceCounter? Write)> _diskIOCounters = new();
    private readonly object _diskIOLock = new();

    public DiskInfo[] Read()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(d =>
                {
                    string label = "";
                    try { label = d.VolumeLabel; }
                    catch (Exception ex) { Debug.WriteLine($"[SysMon] VolumeLabel ({d.Name}): {ex.Message}"); }

                    var di = new DiskInfo
                    {
                        Name = d.Name,
                        VolumeLabel = label,
                        TotalBytes = d.TotalSize,
                        FreeBytes = d.AvailableFreeSpace,
                        UsedPercent = Math.Round((double)(d.TotalSize - d.AvailableFreeSpace) / d.TotalSize * 100, 1),
                        ReadBytesPerSec = -1,
                        WriteBytesPerSec = -1,
                    };

                    // Attach IO speed via PerformanceCounter (lazy-create, reused)
                    ReadDiskIO(d.Name.TrimEnd('\\'), ref di);

                    return di;
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] ReadDisks: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 查询物理磁盘及其关联分区。WMI 关联链:
    /// Win32_DiskDrive → Win32_DiskDriveToDiskPartition → Win32_DiskPartition
    ///   → Win32_LogicalDiskToPartition → Win32_LogicalDisk
    /// </summary>
    public PhysicalDiskInfo[] ReadPhysicalDisks(DiskInfo[] logicalDisks)
    {
        try
        {
            // 1. 查询所有物理磁盘
            var physDisks = new List<PhysicalDiskInfo>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Model, SerialNumber, Size, InterfaceType, Index, PNPDeviceID, MediaType FROM Win32_DiskDrive"))
            using (var collection = searcher.Get())
            {
                foreach (ManagementObject disk in collection)
                {
                    try
                    {
                        long size = 0;
                        try { size = Convert.ToInt64(disk["Size"]); }
                        catch { }

                        var pnpId = disk["PNPDeviceID"] as string ?? "";
                        var iface = disk["InterfaceType"] as string ?? "";
                        var mediaType = disk["MediaType"] as string ?? "";

                        var pdi = new PhysicalDiskInfo
                        {
                            Model = disk["Model"] as string ?? "Unknown Disk",
                            SerialNumber = disk["SerialNumber"] as string ?? "",
                            TotalBytes = size,
                            InterfaceType = ResolveBusType(pnpId, iface, mediaType, disk["Model"] as string ?? ""),
                            Partitions = [],
                            ReadBytesPerSec = 0,
                            WriteBytesPerSec = 0,
                        };
                        physDisks.Add(pdi);
                    }
                    finally { disk.Dispose(); }
                }
            }

            if (physDisks.Count == 0) return [];

            // 2. 查询 Win32_DiskDriveToDiskPartition 关联表，建立 物理磁盘 DeviceID → 分区 列表
            //    ASSOCIATORS OF {Win32_DiskDrive.Index=N} 语法无效，必须用 DeviceID
            var diskDeviceIds = new string[physDisks.Count];
            // 需要重新查 DeviceID（第一次查询没取这个字段）
            using (var idSearcher = new ManagementObjectSearcher(
                "SELECT Index, DeviceID FROM Win32_DiskDrive"))
            using (var idCollection = idSearcher.Get())
            {
                foreach (ManagementObject disk in idCollection)
                {
                    try
                    {
                        var idx = disk["Index"] as uint? ?? 0;
                        var devId = disk["DeviceID"] as string ?? "";
                        if ((int)idx < diskDeviceIds.Length)
                            diskDeviceIds[(int)idx] = devId;
                    }
                    finally { disk.Dispose(); }
                }
            }

            var diskToPartitions = new Dictionary<int, List<string>>(); // physDiskIndex → partition DeviceIDs
            for (int i = 0; i < physDisks.Count; i++)
            {
                var partDeviceIds = new List<string>();
                var physicalDriveId = diskDeviceIds[i];
                if (!string.IsNullOrEmpty(physicalDriveId))
                {
                    try
                    {
                        // ASSOCIATORS OF 直接用原始 DeviceID，不需要转义
                        // (PowerShell 和 C# 测试均确认：转义反斜杠会导致查询失败)
                        using var partSearcher = new ManagementObjectSearcher(
                            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{physicalDriveId}'}} WHERE ResultClass=Win32_DiskPartition");
                        using var parts = partSearcher.Get();
                        foreach (ManagementObject part in parts)
                        {
                            try
                            {
                                var partDeviceId = part["DeviceID"] as string;
                                if (!string.IsNullOrEmpty(partDeviceId))
                                    partDeviceIds.Add(partDeviceId);
                            }
                            finally { part.Dispose(); }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SysMon] Disk partition assoc (index={i}): {ex.Message}");
                    }
                }
                diskToPartitions[i] = partDeviceIds;
            }

            // 3. 查每个分区关联的逻辑磁盘（盘符）
            var diskToDriveLetters = new Dictionary<int, List<string>>();
            for (int i = 0; i < physDisks.Count; i++)
            {
                var driveLetters = new List<string>();
                foreach (var partDeviceId in diskToPartitions.GetValueOrDefault(i) ?? new List<string>())
                {
                    try
                    {
                        // ASSOCIATORS OF 直接用原始 DeviceID，不需要转义
                        using var ldSearcher = new ManagementObjectSearcher(
                            $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partDeviceId}'}} WHERE ResultClass=Win32_LogicalDisk");
                        using var lds = ldSearcher.Get();
                        foreach (ManagementObject ld in lds)
                        {
                            try
                            {
                                var driveLetter = ld["DeviceID"] as string;  // e.g. "C:"
                                if (!string.IsNullOrEmpty(driveLetter))
                                    driveLetters.Add(driveLetter + "\\");
                            }
                            finally { ld.Dispose(); }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SysMon] LogicalDisk assoc (part={partDeviceId}): {ex.Message}");
                    }
                }
                diskToDriveLetters[i] = driveLetters;
            }

            // 4. 把逻辑分区挂到物理磁盘上 + 汇总 IO
            for (int i = 0; i < physDisks.Count; i++)
            {
                var letters = diskToDriveLetters.GetValueOrDefault(i) ?? new List<string>();
                var matched = logicalDisks
                    .Where(d => letters.Contains(d.Name, StringComparer.OrdinalIgnoreCase))
                    .ToArray();

                var pdi = physDisks[i];
                pdi.Partitions = matched;
                // 汇总 IO（跳过 -1 不可用的）
                foreach (var p in matched)
                {
                    if (p.ReadBytesPerSec > 0) pdi.ReadBytesPerSec += p.ReadBytesPerSec;
                    if (p.WriteBytesPerSec > 0) pdi.WriteBytesPerSec += p.WriteBytesPerSec;
                }
                physDisks[i] = pdi;
            }

            return physDisks.ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] ReadPhysicalDisks: {ex.Message}");
            return [];
        }
    }

    private void ReadDiskIO(string driveLetter, ref DiskInfo di)
    {
        (PerformanceCounter? Read, PerformanceCounter? Write) counters;
        lock (_diskIOLock)
        {
            if (!_diskIOCounters.TryGetValue(driveLetter, out counters))
            {
                counters = (CreateIOCounter(driveLetter, "Read"), CreateIOCounter(driveLetter, "Write"));
                _diskIOCounters[driveLetter] = counters;
            }
        }

        try { di.ReadBytesPerSec = counters.Read?.NextValue() ?? -1; }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] Disk read IO ({driveLetter}): {ex.Message}"); }
        try { di.WriteBytesPerSec = counters.Write?.NextValue() ?? -1; }
        catch (Exception ex) { Debug.WriteLine($"[SysMon] Disk write IO ({driveLetter}): {ex.Message}"); }
    }

    private static PerformanceCounter? CreateIOCounter(string drive, string rw)
    {
        try
        {
            return new PerformanceCounter("LogicalDisk", $"Disk {rw} Bytes/sec", drive);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] CreateIOCounter ({drive}/{rw}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 智能判断物理磁盘真实总线/接口类型。
    /// Windows WMI 的 InterfaceType 在 NVMe/UASP/Thunderbolt 上都返回 "SCSI"——
    /// 因为 Windows 存储栈通过 SCSI 层抽象访问这些设备。
    /// PNPDeviceID 也以 "SCSI\" 开头（UASP/Thunderbolt 本质都是 SCSI over 某总线），
    /// 需要多层检测确定真实总线。
    /// </summary>
    private static string ResolveBusType(string pnpDeviceId, string interfaceType, string mediaType, string model)
    {
        var pnpUpper = pnpDeviceId.ToUpperInvariant();
        var modelUpper = model.ToUpperInvariant();
        var ifaceUpper = interfaceType.ToUpperInvariant();
        var mediaUpper = (mediaType ?? "").ToUpperInvariant();

        // 1. PNPDeviceID 含 "NVME" → NVMe（内置或雷电，后续 4b 再区分雷电）
        if (pnpUpper.Contains("NVME"))
        {
            // 4b: 如果是外置硬盘 + 系统有 Thunderbolt 控制器 → 雷电 NVMe
            if (mediaUpper.Contains("EXTERNAL") && HasThunderboltController())
                return "Thunderbolt (NVMe)";
            return "NVMe";
        }

        // 2. PNPDeviceID 以 "USB" 或 "USBSTOR" 开头 → USB 外置盘
        if (pnpUpper.StartsWith("USB"))
            return ifaceUpper.Equals("SCSI") ? "USB (UASP)" : "USB";

        // 3. 型号名暗示 NVMe
        if (modelUpper.Contains("NVME"))
        {
            if (mediaUpper.Contains("EXTERNAL") && HasThunderboltController())
                return "Thunderbolt (NVMe)";
            return "NVMe";
        }

        // 4. PNPDeviceID 以 "SCSI\" 开头 + InterfaceType=SCSI → 可能是 UASP USB 盘
        if (pnpUpper.StartsWith("SCSI") && ifaceUpper.Equals("SCSI"))
        {
            if (IsUsbDevice(pnpDeviceId, model))
                return "USB (UASP)";
            // 雷电 dock 上的存储设备（非 NVMe，如雷电 SATA 桥）
            if (mediaUpper.Contains("EXTERNAL") && HasThunderboltController())
                return "Thunderbolt";
        }

        // 5. SATA SSD：型号含 SSD + SCSI 抽象层
        if (modelUpper.Contains("SSD"))
        {
            return ifaceUpper.Equals("SCSI") ? "SATA" : interfaceType;
        }

        // 6. fallback
        return string.IsNullOrEmpty(interfaceType) ? "—" : interfaceType;
    }

    private static bool? _hasThunderboltCache;
    private static DateTime _thunderboltCacheTime = DateTime.MinValue;

    /// <summary>
    /// 检测系统是否有 Thunderbolt 控制器（缓存 60s）。
    /// 雷电控制器在 Win32_PnPEntity 中的标识：
    /// - DeviceID 含 "THUNDERBOLT" 或 "TBT"
    /// - PNPClass 为 "ThunderboltController"
    /// </summary>
    private static bool HasThunderboltController()
    {
        lock (_thunderboltLock)
        {
            if (_hasThunderboltCache.HasValue && DateTime.UtcNow - _thunderboltCacheTime < TimeSpan.FromSeconds(60))
                return _hasThunderboltCache.Value;
            _thunderboltCacheTime = DateTime.UtcNow;
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '%THUNDERBOLT%' OR DeviceID LIKE '%TBT%' OR PNPClass = 'ThunderboltController'");
                _hasThunderboltCache = searcher.Get().Count > 0;
            }
            catch { _hasThunderboltCache = false; }
            return _hasThunderboltCache.Value;
        }
    }
    private static readonly object _thunderboltLock = new();

    /// <summary>
    /// 判断该磁盘是否 USB 外置盘。
    /// UASP 盘的 PNPDeviceID 以 SCSI\ 开头，但 Windows 会同时枚举一个 USBSTOR\ 实例。
    /// 通过型号名匹配 USBSTOR\Disk&Ven_XXX 实例来判断。
    /// </summary>
    private static bool IsUsbDevice(string pnpDeviceId, string model)
    {
        try
        {
            // PNPDeviceID 以 USB\ 或 USBSTOR\ 开头 → 直接是 USB
            var pnpUpper = pnpDeviceId.ToUpperInvariant();
            if (pnpUpper.StartsWith("USB") || pnpUpper.StartsWith("USBSTOR"))
                return true;

            // SCSI\ 开头 → 查是否存在匹配型号的 USBSTOR\ 实例
            // PNPDeviceID 格式: SCSI\DISK&VEN_NVME&PROD_SKHYNIX...\5&C75A86F&0&000000
            // 提取 VEN 和 PROD 用于匹配 USBSTOR 实例
            if (!pnpUpper.StartsWith("SCSI")) return false;

            // 提取 VEN_xxxx 和 PROD_xxxx（截到下一个 & 或 \）
            string ven = ExtractToken(pnpDeviceId, "VEN_");
            string prod = ExtractToken(pnpDeviceId, "PROD_");
            if (string.IsNullOrEmpty(ven)) return false;

            // 查 USBSTOR\Disk 实例，匹配 VEN+PROD
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USBSTOR%'");
            foreach (var entity in searcher.Get().Cast<ManagementObject>())
            {
                using (entity)
                {
                    var devId = (entity["DeviceID"] as string ?? "").ToUpperInvariant();
                    if (devId.Contains("VEN_" + ven) && devId.Contains("PROD_" + prod))
                        return true;
                }
            }

            // 备用：查 USB 控制器的 dependent 设备列表
            // Win32_USBControllerDevice.Antecedent=USB控制器, Dependent=子设备
            using var usbSearcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_USBControllerDevice");
            foreach (var usb in usbSearcher.Get().Cast<ManagementObject>())
            {
                using (usb)
                {
                    var dep = usb["Dependent"] as string;
                    if (dep == null) continue;
                    // dep 格式: \\PCNAME\root\cimv2:Win32_PnPEntity.DeviceID="USB\\..."
                    var depUpper = dep.ToUpperInvariant();
                    if (depUpper.Contains("VEN_" + ven) && depUpper.Contains("PROD_" + prod))
                        return true;
                }
            }

            return false;
        }
        catch { return false; }
    }

    /// <summary>从 PNPDeviceID 提取 VEN_xxxx 或 PROD_xxxx 的值部分</summary>
    private static string ExtractToken(string pnpId, string prefix)
    {
        var upper = pnpId.ToUpperInvariant();
        int idx = upper.IndexOf(prefix);
        if (idx < 0) return "";
        int start = idx + prefix.Length;
        int end = start;
        while (end < pnpId.Length && pnpId[end] != '&' && pnpId[end] != '\\')
            end++;
        return pnpId.Substring(start, end - start);
    }
}
