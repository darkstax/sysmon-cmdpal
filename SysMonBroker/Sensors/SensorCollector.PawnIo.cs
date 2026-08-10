using Microsoft.Win32;

namespace SysMonBroker.Sensors;

public sealed partial class SensorCollector
{
    private static bool CheckPawnIoInstalled()
    {
        // R11: registry access may throw (permissions, redirection); treat any failure as
        // "not installed" so the broker falls back to LHM user-mode sensors.
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
        try
        {
            if (TryReadVersion(Registry.LocalMachine, key)) return true;
            using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return TryReadVersion(hklm64, key);
        }
        catch (Exception ex)
        {
            SysMonBroker.Logging.BrokerLogger.Log(
                $"sensor: PawnIO registry probe failed ({ex.GetType().Name}: {ex.Message}); treating as not installed");
            return false;
        }
    }

    private static bool TryReadVersion(RegistryKey root, string subkeyPath)
    {
        using var sub = root.OpenSubKey(subkeyPath);
        return sub?.GetValue("DisplayVersion") is string s
            && Version.TryParse(s, out var v)
            && v >= new Version(2, 0, 0);
    }

    private string DetermineCpuSource()
    {
        if (!PawnIoInstalled) return "Broker_LHM";
        var cpuName = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "";
        if (cpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase))
            return "Broker_SMU";
        if (cpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return "Broker_MSR";
        return "Broker_LHM";
    }
}
