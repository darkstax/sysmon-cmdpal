// Copyright (c) 2026 SysMonCmdPal
// 磁盘信息采集器 — DriveInfo + PerformanceCounter IO 速度
// 从 SystemInfoService 拆分而来

using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.Json;

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

    // P3: 缓存物理磁盘查询 30 秒 — 物理磁盘结构极少变化（热插拔除外）
    private PhysicalDiskInfo[]? _cachedPhysicalDisks;
    private DateTime _physDiskCacheTime = DateTime.MinValue;
    private static readonly TimeSpan PhysDiskCacheTtl = TimeSpan.FromSeconds(30);

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
        // P3: 30 秒缓存 — 物理磁盘结构极少变化
        if (_cachedPhysicalDisks != null && (DateTime.UtcNow - _physDiskCacheTime) < PhysDiskCacheTtl)
        {
            // 仍需更新 IO 速度（从最新的逻辑磁盘数据汇总）。复制缓存后再更新，
            // 避免修改已经发布给 UI 的 SystemSnapshot 数组实例。
            var snapshot = ClonePhysicalDisks(_cachedPhysicalDisks);
            UpdatePhysicalDiskIO(snapshot, logicalDisks);
            return snapshot;
        }

        try
        {
            // 1. 查询所有物理磁盘（P3: 合并 DeviceID 到第一次查询，消除重复 WMI 调用）
            var physDisks = new List<PhysicalDiskInfo>();
            var diskDriveLetters = new List<List<string>>();
            using (var searcher = new ManagementObjectSearcher(
                "SELECT Model, SerialNumber, Size, InterfaceType, Index, PNPDeviceID, MediaType, DeviceID FROM Win32_DiskDrive"))
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
                        diskDriveLetters.Add(GetLogicalDriveLetters(disk));
                    }
                    finally { disk.Dispose(); }
                }
            }

            if (physDisks.Count == 0) return [];

            // 4. 把逻辑分区挂到物理磁盘上 + 汇总 IO
            for (int i = 0; i < physDisks.Count; i++)
            {
                var letters = diskDriveLetters[i];
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

            var result = physDisks.ToArray();
            _cachedPhysicalDisks = result;
            _physDiskCacheTime = DateTime.UtcNow;
            SensorLogger.ForceLog($"[DiskMonitor] physical disks result: count={result.Length}, items={string.Join(" | ", result.Select(d => $"{d.Model}:{d.InterfaceType}:parts={d.Partitions.Length}"))}");
            return result;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[DiskMonitor] ReadPhysicalDisks failed: {ex}");
            Debug.WriteLine($"[SysMon] ReadPhysicalDisks: {ex.Message}");
            var fallback = ReadPhysicalDisksViaPowerShell(logicalDisks);
            if (fallback.Length > 0)
            {
                _cachedPhysicalDisks = fallback;
                _physDiskCacheTime = DateTime.UtcNow;
                SensorLogger.ForceLog($"[DiskMonitor] PowerShell fallback result: count={fallback.Length}, items={string.Join(" | ", fallback.Select(d => $"{d.Model}:{d.InterfaceType}:parts={d.Partitions.Length}"))}");
            }
            else
            {
                SensorLogger.ForceLog("[DiskMonitor] PowerShell fallback returned no disks");
            }

            return fallback;
        }
    }

    private static List<string> GetLogicalDriveLetters(ManagementObject disk)
    {
        var driveLetters = new List<string>();

        try
        {
            using var partitions = disk.GetRelated("Win32_DiskPartition");
            foreach (ManagementObject part in partitions)
            {
                try
                {
                    using var logicalDisks = part.GetRelated("Win32_LogicalDisk");
                    foreach (ManagementObject logicalDisk in logicalDisks)
                    {
                        try
                        {
                            var driveLetter = logicalDisk["DeviceID"] as string; // e.g. "C:"
                            if (!string.IsNullOrEmpty(driveLetter))
                                driveLetters.Add(driveLetter + "\\");
                        }
                        finally { logicalDisk.Dispose(); }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SysMon] LogicalDisk assoc: {ex.Message}");
                }
                finally { part.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] Disk partition assoc: {ex.Message}");
        }

        return driveLetters;
    }

    private static PhysicalDiskInfo[] ReadPhysicalDisksViaPowerShell(DiskInfo[] logicalDisks)
    {
        const string script = """
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
function Resolve-BusType($pnp, $iface, $media, $model) {
    if ($null -eq $pnp) { $pnp = '' }
    if ($null -eq $iface) { $iface = '' }
    if ($null -eq $media) { $media = '' }
    if ($null -eq $model) { $model = '' }
    $p = $pnp.ToUpperInvariant()
    $i = $iface.ToUpperInvariant()
    $m = $media.ToUpperInvariant()
    $modelUpper = $model.ToUpperInvariant()
    if ($p.Contains('NVME') -or $modelUpper.Contains('NVME')) { return 'NVMe' }
    if ($p.StartsWith('USB')) { if ($i -eq 'SCSI') { return 'USB (UASP)' } else { return 'USB' } }
    if ($p.StartsWith('SCSI') -and $i -eq 'SCSI' -and $m.Contains('EXTERNAL')) { return 'USB (UASP)' }
    if ($modelUpper.Contains('SSD') -and $i -eq 'SCSI') { return 'SATA' }
    if ([string]::IsNullOrWhiteSpace($iface)) { return '—' }
    return $iface
}
$rows = foreach ($disk in Get-CimInstance Win32_DiskDrive) {
    $letters = @()
    $size = 0
    if ($null -ne $disk.Size) { $size = [int64]$disk.Size }
    try {
        foreach ($part in Get-CimAssociatedInstance -InputObject $disk -ResultClassName Win32_DiskPartition) {
            foreach ($ld in Get-CimAssociatedInstance -InputObject $part -ResultClassName Win32_LogicalDisk) {
                if ($ld.DeviceID) { $letters += [string]$ld.DeviceID }
            }
        }
    } catch {}
    [pscustomobject]@{
        Model = [string]$disk.Model
        SerialNumber = [string]$disk.SerialNumber
        Size = $size
        InterfaceType = Resolve-BusType $disk.PNPDeviceID $disk.InterfaceType $disk.MediaType $disk.Model
        DriveLetters = @($letters)
    }
}
$rows | ConvertTo-Json -Compress -Depth 5
""";

        try
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p == null) return [];

            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(8000))
            {
                try { p.Kill(entireProcessTree: true); }
                catch { }
                SensorLogger.ForceLog("[DiskMonitor] PowerShell fallback timed out");
                return [];
            }

            if (p.ExitCode != 0)
            {
                SensorLogger.ForceLog($"[DiskMonitor] PowerShell fallback failed exit={p.ExitCode}: {stderr}");
                return [];
            }

            return ParsePhysicalDisksJson(stdout, logicalDisks);
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[DiskMonitor] PowerShell fallback exception: {ex}");
            return [];
        }
    }

    private static PhysicalDiskInfo[] ParsePhysicalDisksJson(string json, DiskInfo[] logicalDisks)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        using var doc = JsonDocument.Parse(json);
        var rows = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToArray()
            : [doc.RootElement];

        var result = new List<PhysicalDiskInfo>();
        foreach (var row in rows)
        {
            var letters = ReadDriveLetters(row);
            var matched = logicalDisks
                .Where(d => letters.Contains(d.Name.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase))
                .ToArray();

            double read = 0, write = 0;
            foreach (var p in matched)
            {
                if (p.ReadBytesPerSec > 0) read += p.ReadBytesPerSec;
                if (p.WriteBytesPerSec > 0) write += p.WriteBytesPerSec;
            }

            result.Add(new PhysicalDiskInfo
            {
                Model = GetJsonString(row, "Model", "Unknown Disk"),
                SerialNumber = GetJsonString(row, "SerialNumber", ""),
                TotalBytes = GetJsonInt64(row, "Size"),
                InterfaceType = GetJsonString(row, "InterfaceType", "—"),
                Partitions = matched,
                ReadBytesPerSec = read,
                WriteBytesPerSec = write,
            });
        }

        return result.ToArray();
    }

    private static HashSet<string> ReadDriveLetters(JsonElement row)
    {
        var letters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!row.TryGetProperty("DriveLetters", out var driveLetters))
            return letters;

        if (driveLetters.ValueKind == JsonValueKind.Array)
        {
            foreach (var letter in driveLetters.EnumerateArray())
            {
                var s = letter.GetString();
                if (!string.IsNullOrWhiteSpace(s)) letters.Add(s);
            }
        }
        else if (driveLetters.ValueKind == JsonValueKind.String)
        {
            var s = driveLetters.GetString();
            if (!string.IsNullOrWhiteSpace(s)) letters.Add(s);
        }

        return letters;
    }

    private static string GetJsonString(JsonElement row, string property, string fallback) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static long GetJsonInt64(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.TryGetInt64(out long result)
            ? result
            : 0;

    /// <summary>P3: 缓存命中时只更新 IO 速度（从最新逻辑磁盘数据汇总）</summary>
    private static PhysicalDiskInfo[] ClonePhysicalDisks(PhysicalDiskInfo[] disks)
    {
        var copy = new PhysicalDiskInfo[disks.Length];
        for (int i = 0; i < disks.Length; i++)
        {
            copy[i] = disks[i];
            copy[i].Partitions = disks[i].Partitions.ToArray();
        }
        return copy;
    }

    private static void UpdatePhysicalDiskIO(PhysicalDiskInfo[] disks, DiskInfo[] logicalDisks)
    {
        for (int i = 0; i < disks.Length; i++)
        {
            double readSum = 0, writeSum = 0;
            foreach (var p in disks[i].Partitions)
            {
                var matchIdx = Array.FindIndex(logicalDisks, d => string.Equals(d.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (matchIdx >= 0)
                {
                    if (logicalDisks[matchIdx].ReadBytesPerSec > 0) readSum += logicalDisks[matchIdx].ReadBytesPerSec;
                    if (logicalDisks[matchIdx].WriteBytesPerSec > 0) writeSum += logicalDisks[matchIdx].WriteBytesPerSec;
                }
            }
            var pdi = disks[i];
            pdi.ReadBytesPerSec = readSum;
            pdi.WriteBytesPerSec = writeSum;
            disks[i] = pdi;
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

        // M6 + L5: 异常时销毁并移除计数器，下次调用会重建。
        // 首次 NextValue() 返回 0 是正常的，不当作异常处理。
        try { di.ReadBytesPerSec = counters.Read?.NextValue() ?? -1; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] Disk read IO ({driveLetter}): {ex.Message}");
            InvalidateDiskCounter(driveLetter);
        }
        try { di.WriteBytesPerSec = counters.Write?.NextValue() ?? -1; }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] Disk write IO ({driveLetter}): {ex.Message}");
            InvalidateDiskCounter(driveLetter);
        }
    }

    /// <summary>M6: 销毁并移除损坏的磁盘 IO 计数器，下次读取时重建</summary>
    private void InvalidateDiskCounter(string driveLetter)
    {
        lock (_diskIOLock)
        {
            if (_diskIOCounters.TryGetValue(driveLetter, out var c))
            {
                c.Read?.Dispose();
                c.Write?.Dispose();
                _diskIOCounters.Remove(driveLetter);
            }
        }
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
                using var coll = searcher.Get(); // L4: dispose ManagementObjectCollection
                _hasThunderboltCache = coll.Count > 0;
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
