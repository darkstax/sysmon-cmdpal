// Copyright (c) 2026 SysMonCmdPal
// Broker diagnostics page — read-only status snapshot for troubleshooting.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using SysMonCmdPal.Broker;

namespace SysMonCmdPal;

internal sealed partial class BrokerDiagnosticsPage : ContentPage
{
    private readonly CopyTextCommand _copyCommand = new(string.Empty);

    public BrokerDiagnosticsPage()
    {
        Icon = new IconInfo("");
        Title = Loc.Get("BrokerDiagnostics.PageTitle");
        Name = "BrokerDiagnostics";
        Commands = [new CommandContextItem(_copyCommand) { Title = Loc.Get("BrokerDiagnostics.CopyDiagnostics") }];
    }

    public override IContent[] GetContent()
    {
        var snap = BrokerPushReceiver.Instance.Snapshot;
        var diag = SharedMemoryReader.Diagnostics;
        int pid = BrokerDetector.GetBrokerPid();
        _copyCommand.Text = BuildCopyText(snap, diag, pid);

        return [new MarkdownContent(BuildMarkdown(snap, diag, pid))];
    }

    private static string BuildMarkdown(
        BrokerSensorSnapshot snap,
        SharedMemoryReaderDiagnostics diag,
        int pid)
    {
        string running = pid > 0 ? Loc.Get("Common.Yes") : Loc.Get("Common.No");
        string connected = diag.IsConnected ? Loc.Get("Common.Yes") : Loc.Get("Common.No");
        string alive = snap.IsAlive ? Loc.Get("Common.Yes") : Loc.Get("Common.No");
        string fresh = snap.IsFresh ? Loc.Get("Common.Yes") : Loc.Get("Common.No");
        string lastRead = FormatUtc(diag.LastReadUtc);
        string lastPush = FormatUtc(snap.LastPush);
        string lastPing = FormatUtc(snap.LastPing);
        string error = string.IsNullOrWhiteSpace(diag.LastError)
            ? Loc.Get("Common.None")
            : diag.LastError;

        return $"""
        ## {Loc.Get("BrokerDiagnostics.PageTitle")}

        | {Loc.Get("Common.Metric")} | {Loc.Get("Common.Value")} |
        |---|---|
        | {Loc.Get("BrokerDiagnostics.ProcessRunning")} | {running} |
        | {Loc.Get("BrokerDiagnostics.ProcessId")} | {(pid > 0 ? pid.ToString() : Loc.Get("Common.NA"))} |
        | {Loc.Get("BrokerDiagnostics.SharedMemoryConnected")} | {connected} |
        | {Loc.Get("BrokerDiagnostics.BrokerAlive")} | {alive} |
        | {Loc.Get("BrokerDiagnostics.DataFresh")} | {fresh} |
        | {Loc.Get("BrokerDiagnostics.Version")} | {diag.LastVersion} |
        | {Loc.Get("BrokerDiagnostics.Counter")} | {diag.LastCounter} |
        | {Loc.Get("BrokerDiagnostics.SensorCount")} | {diag.LastSensorCount} |
        | {Loc.Get("BrokerDiagnostics.GpuCount")} | {snap.Gpus.Count} |
        | {Loc.Get("BrokerDiagnostics.LastRead")} | {lastRead} |
        | {Loc.Get("BrokerDiagnostics.LastPush")} | {lastPush} |
        | {Loc.Get("BrokerDiagnostics.LastPing")} | {lastPing} |
        | {Loc.Get("BrokerDiagnostics.LastError")} | {error} |
        """;
    }

    private static string BuildCopyText(
        BrokerSensorSnapshot snap,
        SharedMemoryReaderDiagnostics diag,
        int pid)
    {
        return string.Join(Environment.NewLine,
        [
            $"ProcessRunning={pid > 0}",
            $"ProcessId={(pid > 0 ? pid.ToString() : Loc.Get("Common.NA"))}",
            $"SharedMemoryConnected={diag.IsConnected}",
            $"BrokerAlive={snap.IsAlive}",
            $"DataFresh={snap.IsFresh}",
            $"Version={diag.LastVersion}",
            $"Counter={diag.LastCounter}",
            $"SensorCount={diag.LastSensorCount}",
            $"GpuCount={snap.Gpus.Count}",
            $"LastRead={FormatUtc(diag.LastReadUtc)}",
            $"LastPush={FormatUtc(snap.LastPush)}",
            $"LastPing={FormatUtc(snap.LastPing)}",
            $"LastError={(string.IsNullOrWhiteSpace(diag.LastError) ? Loc.Get("Common.None") : diag.LastError)}",
        ]);
    }

    private static string FormatUtc(DateTime value) =>
        value == DateTime.MinValue
            ? Loc.Get("Common.NA")
            : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
