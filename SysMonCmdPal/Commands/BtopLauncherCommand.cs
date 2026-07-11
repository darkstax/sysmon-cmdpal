// Copyright (c) 2026 SysMonCmdPal
// 启动 btop4win 的快捷命令。支持 scoop 路径、PATH 环境变量和自定义安装位置。

using System.Diagnostics;
using System.IO;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class BtopLauncherCommand : InvokableCommand
{
    // 候选可执行文件名（scoop 包名可能是 btop 或 btop4win）
    private static readonly string[] ExeNames = ["btop.exe", "btop4win.exe"];

    // 硬编码的常见安装路径（作为最后兜底）
    private static readonly string[] KnownPaths = [
        // scoop: btop-lhm (实际包名)
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "btop-lhm", "current", "btop.exe"),
        // scoop: btop4win (可能的包名)
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "btop4win", "current", "btop4win.exe"),
        // scoop shims
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "btop.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "btop4win.exe"),
        @"C:\Program Files\btop4win\btop4win.exe",
        @"C:\Program Files\btop4win\btop.exe",
    ];

    // 终端程序候选路径（btop 是 TUI，需要在终端里运行）
    private static readonly string[] TerminalPaths = [
        @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_8wekyb3d8bbwe\wt.exe",
        @"C:\Program Files (x86)\WindowsApps\Microsoft.WindowsTerminal_8wekyb3d8bbwe\wt.exe",
        "wt.exe",
    ];

    public BtopLauncherCommand()
    {
        Id = "btop4win";
        Name = Loc.Get("Btop.Name");
    }

    public override CommandResult Invoke()
    {
        string? btopExe = FindBtopExe();
        if (btopExe == null)
            return CommandResult.ShowToast(Loc.Get("Btop.NotFound"));

        // 尝试通过 Windows Terminal 启动（TUI 程序需要终端）
        string? wt = FindWindowsTerminal();
        if (wt != null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(wt)
                {
                    Arguments = $"new-tab --suppressApplicationTitle --title btop --cmdline \"{btopExe}\"",
                    UseShellExecute = false,
                });
                return CommandResult.KeepOpen();
            }
            catch { /* fallback to direct launch */ }
        }

        // 直接启动（如果 wt 不可用）
        try
        {
            Process.Start(new ProcessStartInfo(btopExe) { UseShellExecute = true });
            return CommandResult.KeepOpen();
        }
        catch (Exception ex)
        {
            return CommandResult.ShowToast(Loc.Format("Btop.LaunchFailed", ex.Message));
        }
    }

    private static string? FindBtopExe()
    {
        // 1. 检查硬编码路径
        foreach (var path in KnownPaths)
        {
            if (File.Exists(path)) return path;
        }

        // 2. 搜索 PATH 环境变量
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            foreach (var name in ExeNames)
            {
                var full = Path.Combine(dir.Trim(), name);
                if (File.Exists(full)) return full;
            }
        }

        return null;
    }

    private static string? FindWindowsTerminal()
    {
        foreach (var wt in TerminalPaths)
        {
            if (wt.Contains(Path.DirectorySeparatorChar) || wt.Contains(Path.AltDirectorySeparatorChar))
            {
                if (File.Exists(wt)) return wt;
            }
            else
            {
                // 搜索 PATH
                var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
                foreach (var dir in pathDirs)
                {
                    var full = Path.Combine(dir.Trim(), wt);
                    if (File.Exists(full)) return full;
                }
            }
        }
        return null;
    }
}
