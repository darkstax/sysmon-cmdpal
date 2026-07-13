using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SysMonCmdPal;

internal static class SystemMemoryReader
{
    public static void Read(ref SystemSnapshot snapshot)
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX();
            memStatus.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (GlobalMemoryStatusEx(ref memStatus))
            {
                snapshot.MemoryTotalBytes = (long)memStatus.ullTotalPhys;
                snapshot.MemoryUsedBytes = (long)(memStatus.ullTotalPhys - memStatus.ullAvailPhys);
                snapshot.MemoryUsed = memStatus.dwMemoryLoad;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SysMon] ReadMemory (GlobalMemoryStatusEx): {ex.Message} — memory unavailable");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
