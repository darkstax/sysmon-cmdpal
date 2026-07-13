using Microsoft.Win32;

namespace SysMonCmdPal;

public partial class SystemInfoService
{
    public static string CpuName { get; } = ReadCpuName();

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string ?? "";
        }
        catch { return ""; }
    }
}
