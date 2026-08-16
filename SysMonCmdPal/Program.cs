// Copyright (c) 2026 SysMonCmdPal
// System Monitor extension for PowerToys Command Palette

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace SysMonCmdPal;

public class Program
{
    // MessageBox 标志：OK 按钮 + 信息图标 + 置前 + 置顶（无人值守时也可见）
    private const uint MbOk = 0x00000000;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbSetForeground = 0x00010000;
    private const uint MbTopmost = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "-RegisterProcessAsComServer")
        {
            using var server = new ExtensionServer();
            using var extensionDisposedEvent = new ManualResetEvent(false);

            var extensionInstance = new SysMonExtension(extensionDisposedEvent);
            server.RegisterExtension(() => extensionInstance);

            // Keep process alive until extension is disposed
            extensionDisposedEvent.WaitOne();
        }
        else
        {
            // 无参数启动（商店认证测试会直接启动主入口）：
            // 扩展没有独立界面，进程原先立即退出，容易被判定为"启动即崩溃"。
            // 改为在 STA 线程上弹出使用说明对话框，进程存活至用户关闭对话框。
            ShowUsageNotice();
        }
    }

    /// <summary>在 STA 线程上显示使用说明对话框，进程保持存活直到用户关闭。</summary>
    private static void ShowUsageNotice()
    {
        const string text =
            "SysPulse 是 PowerToys 命令面板扩展，没有独立界面。\n" +
            "请从 PowerToys Command Palette 中使用（搜索 System Monitor / 系统监控）。\n\n" +
            "SysPulse is a Command Palette extension with no standalone UI.\n" +
            "Open PowerToys Command Palette and search \"System Monitor\" / \"系统监控\".";
        const string caption = "SysPulse for Command Palette";

        var thread = new Thread(() =>
        {
            MessageBoxW(IntPtr.Zero, text, caption, MbOk | MbIconInformation | MbSetForeground | MbTopmost);
        })
        {
            IsBackground = false,
            Name = "UsageNotice",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
