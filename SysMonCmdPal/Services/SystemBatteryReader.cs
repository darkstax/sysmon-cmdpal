using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SysMonCmdPal;

internal static class SystemBatteryReader
{
    public static void Read(ref SystemSnapshot snapshot)
    {
        try
        {
            if (GetSystemPowerStatus(out var pwr))
            {
                snapshot.BatterySaverOn = pwr.SystemStatusFlag == 1;
                snapshot.BatteryLifeSeconds = pwr.BatteryLifeTime;

                int flag = pwr.BatteryFlag;
                const int BatteryFlagCharging = 8;

                bool hasBattery = HasSystemBattery(flag);

                if (hasBattery)
                {
                    snapshot.BatteryPercent = pwr.BatteryLifePercent > 100 ? -1 : pwr.BatteryLifePercent;

                    bool chargingBit = (flag & BatteryFlagCharging) != 0;
                    bool acOnline = pwr.ACLineStatus == 1;
                    var wmi = BatteryQueryService.Instance.GetStatus();
                    bool draining = wmi is { IsValid: true, IsDraining: true };

                    if (chargingBit && draining)
                        snapshot.BatteryStatus = "dual";
                    else if (chargingBit)
                        snapshot.BatteryStatus = "charging";
                    else if (acOnline && !draining)
                        snapshot.BatteryStatus = "full";
                    else if (draining || !acOnline)
                        snapshot.BatteryStatus = "discharging";
                    else
                        snapshot.BatteryStatus = "unknown";
                }
                else
                {
                    snapshot.BatteryPercent = -1;
                    snapshot.BatteryStatus = "no battery";
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] ReadBattery: {ex.Message}");
            snapshot.BatteryPercent = -1;
            snapshot.BatteryStatus = "unknown";
        }
    }

    public static bool HasSystemBattery(int batteryFlag)
    {
        const int BatteryFlagNoSystemBattery = 128;
        const int BatteryFlagUnknown = 255;

        return (batteryFlag & BatteryFlagNoSystemBattery) == 0 &&
            batteryFlag != BatteryFlagUnknown;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
        public byte SystemStatusFlag;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
