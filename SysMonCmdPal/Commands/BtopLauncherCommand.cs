// Copyright (c) 2026 SysMonCmdPal
// 启动 btop4win 的快捷命令。通过 scoop 路径查找可执行文件。

using System.Diagnostics;
using System.IO;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class BtopLauncherCommand : InvokableCommand
{
    private static readonly string[] BtopPaths = [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "apps", "btop4win", "current", "btop4win.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "btop4win.exe"),
        @"C:\Program Files\btop4win\btop4win.exe",
    ];

    public BtopLauncherCommand()
    {
        Id = "btop4win";
        Name = "启动 btop4win";
    }

    public override CommandResult Invoke()
    {
        foreach (var path in BtopPaths)
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return CommandResult.KeepOpen();
            }
        }
        return CommandResult.ShowToast("btop4win 未找到。通过 scoop install btop4win 安装。");
    }
}
