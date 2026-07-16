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
        Icon = new IconInfo(SysMonIcons.Diagnostics);
        Title = Loc.Get("BrokerDiagnostics.PageTitle");
        Name = "BrokerDiagnostics";
        Commands =
        [
            PageNavigation.BackContextItem(),
            new CommandContextItem(_copyCommand) { Title = Loc.Get("BrokerDiagnostics.CopyDiagnostics") },
        ];
    }

    public override IContent[] GetContent()
    {
        var broker = BrokerPushReceiver.Instance;
        bool isUsable = broker.TryGetAvailableSnapshot(out var snap);
        var diag = SharedMemoryReader.Diagnostics;
        int pid = BrokerDetector.GetBrokerPid();
        _copyCommand.Text = BuildCopyText(snap, diag, pid, isUsable);

        return [new MarkdownContent(BuildMarkdown(snap, diag, pid, isUsable))];
    }

    private static string BuildMarkdown(
        BrokerSensorSnapshot snap,
        SharedMemoryReaderDiagnostics diag,
        int pid,
        bool isUsable)
    {
        string running = pid > 0 ? Loc.Get("Common.Yes") : Loc.Get("Common.No");
        string connected = diag.IsConnected ? Loc.Get("Common.Yes") : Loc.Get("Common.No");
        string usable = isUsable ? Loc.Get("Common.Yes") : Loc.Get("Common.No");
        string protocolValid = diag.IsProtocolValid ? Loc.Get("Common.Yes") : Loc.Get("Common.No");
        string stalled = diag.IsStalled ? Loc.Get("Common.Yes") : Loc.Get("Common.No");
        string instanceId = diag.LastInstanceId == 0
            ? Loc.Get("Common.NA")
            : $"0x{diag.LastInstanceId:X16}";
        string lastRead = FormatUtc(diag.LastReadUtc);
        string lastCommit = FormatUtc(diag.LastCommitUtc);
        string lastPush = FormatUtc(snap.LastPush);
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
        | {Loc.Get("BrokerDiagnostics.BrokerUsable")} | {usable} |
        | {Loc.Get("BrokerDiagnostics.ProtocolValid")} | {protocolValid} |
        | {Loc.Get("BrokerDiagnostics.Stalled")} | {stalled} |
        | {Loc.Get("BrokerDiagnostics.MapName")} | {diag.ActiveMapName} |
        | {Loc.Get("BrokerDiagnostics.Version")} | {diag.LastVersion} |
        | {Loc.Get("BrokerDiagnostics.Counter")} | {diag.LastCounter} |
        | {Loc.Get("BrokerDiagnostics.CommitSequence")} | {(diag.UsesCommitSequence ? diag.LastCommitSequence.ToString() : Loc.Get("Common.NA"))} |
        | {Loc.Get("BrokerDiagnostics.InstanceId")} | {instanceId} |
        | {Loc.Get("BrokerDiagnostics.RestartCount")} | {diag.RestartCount} |
        | {Loc.Get("BrokerDiagnostics.SensorCount")} | {diag.LastSensorCount} |
        | {Loc.Get("BrokerDiagnostics.GpuCount")} | {snap.Gpus.Count} |
        | {Loc.Get("BrokerDiagnostics.LastRead")} | {lastRead} |
        | {Loc.Get("BrokerDiagnostics.LastCommit")} | {lastCommit} |
        | {Loc.Get("BrokerDiagnostics.LastPush")} | {lastPush} |
        | {Loc.Get("BrokerDiagnostics.LastError")} | {error} |
        """;
    }

    private static string BuildCopyText(
        BrokerSensorSnapshot snap,
        SharedMemoryReaderDiagnostics diag,
        int pid,
        bool isUsable)
    {
        return string.Join(Environment.NewLine,
        [
            $"ProcessRunning={pid > 0}",
            $"ProcessId={(pid > 0 ? pid.ToString() : Loc.Get("Common.NA"))}",
            $"SharedMemoryConnected={diag.IsConnected}",
            $"BrokerUsable={isUsable}",
            $"ProtocolValid={diag.IsProtocolValid}",
            $"Stalled={diag.IsStalled}",
            $"MapName={diag.ActiveMapName}",
            $"Version={diag.LastVersion}",
            $"Counter={diag.LastCounter}",
            $"CommitSequence={(diag.UsesCommitSequence ? diag.LastCommitSequence.ToString() : Loc.Get("Common.NA"))}",
            $"InstanceId={(diag.LastInstanceId == 0 ? Loc.Get("Common.NA") : $"0x{diag.LastInstanceId:X16}")}",
            $"RestartCount={diag.RestartCount}",
            $"SensorCount={diag.LastSensorCount}",
            $"GpuCount={snap.Gpus.Count}",
            $"LastRead={FormatUtc(diag.LastReadUtc)}",
            $"LastCommit={FormatUtc(diag.LastCommitUtc)}",
            $"LastPush={FormatUtc(snap.LastPush)}",
            $"LastError={(string.IsNullOrWhiteSpace(diag.LastError) ? Loc.Get("Common.None") : diag.LastError)}",
        ]);
    }

    private static string FormatUtc(DateTime value) =>
        value == DateTime.MinValue
            ? Loc.Get("Common.NA")
            : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}
