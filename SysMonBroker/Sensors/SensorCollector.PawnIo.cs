using Microsoft.Win32;

namespace SysMonBroker.Sensors;

public sealed partial class SensorCollector
{
    private static bool CheckPawnIoInstalled()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";
        if (TryReadVersion(Registry.LocalMachine, key)) return true;
        using var hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        return TryReadVersion(hklm64, key);
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
