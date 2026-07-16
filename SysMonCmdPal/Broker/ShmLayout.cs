// SysMonCmdPal.Broker / SysMonBroker.IPC — 共享内存布局常量
// 此文件在 Broker 和 Plugin 之间共享（手动同步）
// 修改时必须同时更新两端！

using System.Collections.Generic;

namespace SysMonCmdPal.Broker;

/// <summary>共享内存布局常量和传感器分类标签</summary>
internal static class ShmLayout
{
    // ---- Map names ----
    public const string MapName = @"Global\SysMonBrokerShm";
    public const string EventName = @"Global\SysMonBrokerEvent";
    public const string LegacyMapName = "SysMonBrokerShm";
    public const string LegacyEventName = "SysMonBrokerEvent";

    public static IReadOnlyList<string> MapNames { get; } =
    [
        MapName,
        LegacyMapName,
    ];

    // ---- Magic & Version ----
    public const int MagicValue = 0x5342524B; // "SBRK"
    public const int MinimumSupportedVersion = 1;
    public const int Version = 2;             // v2: 增加显式版本和全量传感器数组

    // ---- Map sizing ----
    public const int MapSize = 16384;         // 16KB (was 4096 in v1)
    public const int LegacyMapSize = 4096;
    public const int MaxGpus = 4;
    public const int MaxSensors = 250;

    // ---- v2 offsets ----
    public const int OffMagic = 0;            // int32
    public const int OffVersion = 4;          // int32
    public const int OffCounter = 8;          // int32 commit counter
    public const int OffCommitSequence = 12;  // int32: odd writing, even committed
    public const int OffCpuTemp = 16;         // double
    public const int OffSource = 24;          // char[32] UTF-8
    public const int OffGpuCount = 56;        // int32
    public const int OffGpuBase = 60;         // GpuEntry[MaxGpus], 72 bytes each
    // GPU area ends at 60 + 4*72 = 348
    public const int OffTimestamp = 348;      // int64 (Ticks)

    // ---- v2: Generic sensor array ----
    public const int OffSensorCount = 360;    // int32
    public const int OffSensorBase = 364;     // SensorEntry[MaxSensors], 64 bytes each
    // Sensor area ends at 364 + 250*64 = 16364

    // ---- v2 compatible extension in the 20-byte map tail ----
    public const int OffExtensionMagic = 16364;       // int32: versioned extension magic
    public const int OffInstanceId = 16368;           // uint64 Broker process instance
    public const int OffMonotonicPublishMs = 16376;   // int64 Environment.TickCount64
    public const int ExtensionMagicValue = 0x31584D53; // "SMX1"

    // ---- v1 offsets (no explicit version or generic sensor array) ----
    public const int V1OffMagic = 0;          // int32
    public const int V1OffCounter = 4;        // int32 commit counter
    public const int V1OffCpuTemp = 8;        // double
    public const int V1OffSource = 16;        // char[32] UTF-8
    public const int V1OffGpuCount = 48;      // int32
    public const int V1OffGpuBase = 52;       // GpuEntry[MaxGpus], 72 bytes each
    public const int V1OffTimestamp = 340;    // int64 (Ticks)
    public const int V1MinimumSize = V1OffTimestamp + sizeof(long);

    // ---- GPU entry layout (72 bytes each) ----
    public const int GpuNameLen = 32;         // char[32] UTF-8
    public const int GpuTempOff = 32;         // double
    public const int GpuUsageOff = 40;        // double
    public const int GpuMemUsedOff = 48;      // double
    public const int GpuMemTotalOff = 56;     // double
    public const int GpuEntrySize = 72;

    // ---- Sensor entry layout (64 bytes each) ----
    public const int SensorTagOff = 0;        // int32: category tag
    public const int SensorNameOff = 4;       // char[32] UTF-8
    public const int SensorValueOff = 36;     // double
    public const int SensorUnitOff = 44;      // char[16] UTF-8
    public const int SensorHardwareOff = 60;  // int32: type | (same-type instance << 8)
    public const int SensorEntrySize = 64;

    // ---- Sensor category tags (matches original SensorCategory enum) ----
    public const int TagCpuTemp = 0;
    public const int TagCpuLoad = 1;
    public const int TagCpuClock = 2;
    public const int TagCpuPower = 3;
    public const int TagCpuVoltage = 4;
    public const int TagGpuTemp = 5;
    public const int TagGpuLoad = 6;
    public const int TagGpuClock = 7;
    public const int TagGpuPower = 8;
    public const int TagGpuMemory = 9;
    public const int TagGpuFan = 10;
    public const int TagGpuVoltage = 11;
    public const int TagMbTemp = 12;
    public const int TagMbFan = 13;
    public const int TagMbVoltage = 14;
    public const int TagStorageTemp = 15;
    public const int TagStorageLoad = 16;

    // ---- Hardware type tags ----
    public const int HwCpu = 0;
    public const int HwGpuNvidia = 1;
    public const int HwGpuAmd = 2;
    public const int HwGpuIntel = 3;
    public const int HwMotherboard = 4;
    public const int HwStorage = 5;

    // ---- Sensor browse categories ----
    public const int BrowseCategoryTemperature = 0;
    public const int BrowseCategoryLoad = 1;
    public const int BrowseCategoryPower = 2;
    public const int BrowseCategoryFan = 3;
    public const int BrowseCategoryFrequency = 4;
    public const int BrowseCategoryVoltage = 5;
    public const int BrowseCategoryStorage = 6;

    public static IReadOnlyList<int> BrowseCategories { get; } =
    [
        BrowseCategoryTemperature,
        BrowseCategoryLoad,
        BrowseCategoryPower,
        BrowseCategoryFan,
        BrowseCategoryFrequency,
        BrowseCategoryVoltage,
        BrowseCategoryStorage,
    ];

    public static int HardwareTypeFromTag(int packedHardwareTag) => packedHardwareTag & 0xFF;

    public static int HardwareInstanceFromTag(int packedHardwareTag) =>
        (packedHardwareTag >> 8) & 0xFFFF;

    /// <summary>将硬件类型字符串转为 tag</summary>
    public static int HardwareTag(string hwType) => hwType switch
    {
        "Cpu" => HwCpu,
        "GpuNvidia" => HwGpuNvidia,
        "GpuAmd" => HwGpuAmd,
        "GpuIntel" => HwGpuIntel,
        "Motherboard" => HwMotherboard,
        "Storage" => HwStorage,
        _ => -1,
    };

    /// <summary>将 LHM SensorType + HardwareType 映射到分类 tag</summary>
    public static int CategorizeSensor(string hwType, string sensorType, string sensorName)
    {
        return hwType switch
        {
            "Cpu" => sensorType switch
            {
                "Temperature" => TagCpuTemp,
                "Load" => TagCpuLoad,
                "Clock" => TagCpuClock,
                "Power" => TagCpuPower,
                "Voltage" => TagCpuVoltage,
                _ => -1,
            },
            "GpuNvidia" or "GpuAmd" or "GpuIntel" => sensorType switch
            {
                "Temperature" => TagGpuTemp,
                "Load" => TagGpuLoad,
                "Clock" => TagGpuClock,
                "Power" => TagGpuPower,
                "SmallData" when sensorName.Contains("Memory") => TagGpuMemory,
                "Fan" or "Control" => TagGpuFan,
                "Voltage" => TagGpuVoltage,
                _ => -1,
            },
            "Motherboard" => sensorType switch
            {
                "Temperature" => TagMbTemp,
                "Fan" or "Control" => TagMbFan,
                "Voltage" => TagMbVoltage,
                _ => -1,
            },
            "Storage" => sensorType switch
            {
                "Temperature" => TagStorageTemp,
                "Load" => TagStorageLoad,
                _ => -1,
            },
            _ => -1,
        };
    }

    /// <summary>Tag → localized category name</summary>
    public static string TagName(int tag) => tag switch
    {
        TagCpuTemp => Loc.Get("SensorCategory.CpuTemp"),
        TagCpuLoad => Loc.Get("SensorCategory.CpuLoad"),
        TagCpuClock => Loc.Get("SensorCategory.CpuClock"),
        TagCpuPower => Loc.Get("SensorCategory.CpuPower"),
        TagCpuVoltage => Loc.Get("SensorCategory.CpuVoltage"),
        TagGpuTemp => Loc.Get("SensorCategory.GpuTemp"),
        TagGpuLoad => Loc.Get("SensorCategory.GpuLoad"),
        TagGpuClock => Loc.Get("SensorCategory.GpuClock"),
        TagGpuPower => Loc.Get("SensorCategory.GpuPower"),
        TagGpuMemory => Loc.Get("SensorCategory.GpuMemory"),
        TagGpuFan => Loc.Get("SensorCategory.GpuFan"),
        TagGpuVoltage => Loc.Get("SensorCategory.GpuVoltage"),
        TagMbTemp => Loc.Get("SensorCategory.MbTemp"),
        TagMbFan => Loc.Get("SensorCategory.MbFan"),
        TagMbVoltage => Loc.Get("SensorCategory.MbVoltage"),
        TagStorageTemp => Loc.Get("SensorCategory.StorageTemp"),
        TagStorageLoad => Loc.Get("SensorCategory.StorageLoad"),
        _ => Loc.Get("Common.Unknown"),
    };

    /// <summary>Tag → 单位</summary>
    public static string TagUnit(int tag) => tag switch
    {
        TagCpuTemp or TagGpuTemp or TagMbTemp or TagStorageTemp => "°C",
        TagCpuLoad or TagGpuLoad or TagStorageLoad => "%",
        TagCpuClock or TagGpuClock => "MHz",
        TagCpuPower or TagGpuPower => "W",
        TagCpuVoltage or TagGpuVoltage or TagMbVoltage => "V",
        TagGpuMemory => "MB",
        TagGpuFan or TagMbFan => "RPM",
        _ => "",
    };

    public static string BrowseCategoryName(int category) => category switch
    {
        BrowseCategoryTemperature => Loc.Get("SensorBrowse.Temperature"),
        BrowseCategoryLoad => Loc.Get("SensorBrowse.Load"),
        BrowseCategoryPower => Loc.Get("SensorBrowse.Power"),
        BrowseCategoryFan => Loc.Get("SensorBrowse.Fan"),
        BrowseCategoryFrequency => Loc.Get("SensorBrowse.Frequency"),
        BrowseCategoryVoltage => Loc.Get("SensorBrowse.Voltage"),
        BrowseCategoryStorage => Loc.Get("SensorBrowse.Storage"),
        _ => Loc.Get("Common.Unknown"),
    };

    public static int BrowseCategoryRepresentativeTag(int category) => category switch
    {
        BrowseCategoryTemperature => TagCpuTemp,
        BrowseCategoryLoad => TagCpuLoad,
        BrowseCategoryPower => TagCpuPower,
        BrowseCategoryFan => TagGpuFan,
        BrowseCategoryFrequency => TagCpuClock,
        BrowseCategoryVoltage => TagCpuVoltage,
        BrowseCategoryStorage => TagStorageTemp,
        _ => -1,
    };

    public static bool BrowseCategoryMatchesTag(int category, int tag) => category switch
    {
        BrowseCategoryTemperature => tag is TagCpuTemp or TagGpuTemp or TagMbTemp,
        BrowseCategoryLoad => tag is TagCpuLoad or TagGpuLoad,
        BrowseCategoryPower => tag is TagCpuPower or TagGpuPower,
        BrowseCategoryFan => tag is TagGpuFan or TagMbFan,
        BrowseCategoryFrequency => tag is TagCpuClock or TagGpuClock,
        BrowseCategoryVoltage => tag is TagCpuVoltage or TagGpuVoltage or TagMbVoltage,
        BrowseCategoryStorage => tag is TagGpuMemory or TagStorageTemp or TagStorageLoad,
        _ => false,
    };
}
