// Copyright (c) 2026 SysMonCmdPal
// 共享传感器日志工具

using System;
using System.Diagnostics;
using System.IO;

namespace SysMonCmdPal;

internal static class SensorLogger
{
    private const long MaxLogSize = 10 * 1024 * 1024; // 10MB

    private static string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SysMonCmdPal", "sensor_backend.log");

    private static void CheckRotate()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > MaxLogSize)
            {
                var backupPath = LogPath + ".old";
                if (File.Exists(backupPath)) File.Delete(backupPath);
                File.Move(LogPath, backupPath);
            }
        }
        catch { /* ignore rotation errors */ }
    }

    [Conditional("DEBUG")]
    public static void Log(string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            CheckRotate();
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
            CheckRotate();
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
        }
        catch { /* ignore */ }
    }
}
