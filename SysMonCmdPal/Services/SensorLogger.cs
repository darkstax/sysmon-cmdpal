// Copyright (c) 2026 SysMonCmdPal
// 共享传感器日志工具

using System;
using System.Diagnostics;
using System.IO;

namespace SysMonCmdPal;

internal static class SensorLogger
{
    private static string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysMonCmdPal", "sensor_backend.log");

    [Conditional("DEBUG")]
    public static void Log(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { /* ignore */ }
    }

    public static void ForceLog(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { /* ignore */ }
    }
}
