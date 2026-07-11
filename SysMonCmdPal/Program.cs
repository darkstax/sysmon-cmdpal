// Copyright (c) 2026 SysMonCmdPal
// System Monitor extension for PowerToys Command Palette

using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CommandPalette.Extensions;

namespace SysMonCmdPal;

public class Program
{
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
            Console.WriteLine("SysMonCmdPal: Not launched as COM server. Use -RegisterProcessAsComServer.");
        }
    }
}
