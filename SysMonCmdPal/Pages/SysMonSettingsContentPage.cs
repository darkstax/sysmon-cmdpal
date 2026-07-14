// Copyright (c) 2026 SysMonCmdPal
// Combines Toolkit value settings with an actionable Broker installation form.

using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class SysMonSettingsContentPage : ContentPage
{
    private readonly Microsoft.CommandPalette.Extensions.Toolkit.Settings _valueSettings;
    private readonly BrokerInstallSettingsForm _brokerInstallForm;

    public SysMonSettingsContentPage(
        Microsoft.CommandPalette.Extensions.Toolkit.Settings valueSettings,
        BrokerInstallController brokerInstallController)
    {
        _valueSettings = valueSettings;
        _brokerInstallForm = new BrokerInstallSettingsForm(brokerInstallController);
        _brokerInstallForm.ContentChanged += (_, _) => RaiseItemsChanged();

        Name = Loc.Get("Settings.PageTitle");
        Title = Loc.Get("Settings.PageTitle");
        Icon = new IconInfo(SysMonIcons.Settings);
        Commands = [PageNavigation.BackContextItem()];
    }

    public override IContent[] GetContent()
        => [.. _valueSettings.ToContent(), _brokerInstallForm];
}

internal sealed partial class BrokerInstallSettingsForm : FormContent
{
    private const string InstallAction = "installBroker";
    private const string UninstallAction = "uninstallBroker";
    private readonly BrokerInstallController _controller;
    private readonly BrokerUninstallController _uninstallController;
    private readonly BrokerInstallCommand _installCommand;
    private readonly BrokerUninstallCommand _uninstallCommand;
    private BrokerOperation _lastOperation;

    public BrokerInstallSettingsForm(BrokerInstallController controller)
    {
        _controller = controller;
        _uninstallController = new BrokerUninstallController();
        _installCommand = new BrokerInstallCommand(
            controller,
            () => _uninstallController.Snapshot.IsBusy);
        _uninstallCommand = new BrokerUninstallCommand(
            _uninstallController,
            () => controller.Snapshot.IsBusy);
        TemplateJson = BuildTemplateJson(
            controller.Snapshot,
            _uninstallController.Snapshot,
            BrokerOperation.None);
        controller.StatusChanged += OnInstallStatusChanged;
        _uninstallController.StatusChanged += OnUninstallStatusChanged;
    }

    public event EventHandler? ContentChanged;

    public override ICommandResult SubmitForm(string inputs, string data)
    {
        bool installRequested = ContainsInstallAction(inputs) || ContainsInstallAction(data);
        bool uninstallRequested = ContainsUninstallAction(inputs) || ContainsUninstallAction(data);
        if (installRequested == uninstallRequested)
            return CommandResult.KeepOpen();

        BrokerInstallSnapshot snapshot = _controller.Snapshot;
        BrokerUninstallSnapshot uninstallSnapshot = _uninstallController.Snapshot;
        if (snapshot.IsBusy || uninstallSnapshot.IsBusy)
            return CommandResult.ShowToast(Loc.Get("BrokerInstall.AlreadyRunning"));

        if (uninstallRequested)
        {
            if (!IsBrokerInstalled(snapshot, uninstallSnapshot, _lastOperation))
                return CommandResult.ShowToast(Loc.Get("BrokerUninstall.NotInstalled"));

            return CommandResult.Confirm(new ConfirmationArgs
            {
                Title = Loc.Get("BrokerUninstall.ConfirmTitle"),
                Description = Loc.Get("BrokerUninstall.ConfirmDescription"),
                PrimaryCommand = _uninstallCommand,
                IsPrimaryCommandCritical = true,
            });
        }

        string architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "ARM64",
            _ => Loc.Get("BrokerInstall.UnknownArchitecture"),
        };

        return CommandResult.Confirm(new ConfirmationArgs
        {
            Title = Loc.Get("BrokerInstall.ConfirmTitle"),
            Description = Loc.Format("BrokerInstall.ConfirmDescription", architecture),
            PrimaryCommand = _installCommand,
            IsPrimaryCommandCritical = true,
        });
    }

    internal static bool ContainsInstallAction(string? json)
        => ContainsAction(json, InstallAction);

    internal static bool ContainsUninstallAction(string? json)
        => ContainsAction(json, UninstallAction);

    private static bool ContainsAction(string? json, string expectedAction)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            return JsonNode.Parse(json)?["action"]?.GetValue<string>() == expectedAction;
        }
        catch
        {
            return false;
        }
    }

    internal static string BuildTemplateJson(BrokerInstallSnapshot snapshot)
        => BuildTemplateJson(snapshot, default, BrokerOperation.Install);

    private static string BuildTemplateJson(
        BrokerInstallSnapshot snapshot,
        BrokerUninstallSnapshot uninstallSnapshot,
        BrokerOperation lastOperation)
    {
        bool isBusy = snapshot.IsBusy || uninstallSnapshot.IsBusy;
        bool isInstalled = IsBrokerInstalled(snapshot, uninstallSnapshot, lastOperation);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", "http://adaptivecards.io/schemas/adaptive-card.json");
            writer.WriteString("type", "AdaptiveCard");
            writer.WriteString("version", "1.5");
            writer.WriteStartArray("body");
            WriteTextBlock(writer, Loc.Get("BrokerInstall.Title"), heading: true);
            WriteTextBlock(writer, Loc.Get("BrokerInstall.Description"));
            WriteTextBlock(
                writer,
                GetStatusText(snapshot, uninstallSnapshot, lastOperation),
                subtle: true);
            writer.WriteEndArray();
            writer.WriteStartArray("actions");
            WriteSubmitAction(
                writer,
                GetButtonText(snapshot),
                "positive",
                InstallAction,
                !isBusy);
            if (isInstalled)
            {
                WriteSubmitAction(
                    writer,
                    GetUninstallButtonText(uninstallSnapshot),
                    "destructive",
                    UninstallAction,
                    !isBusy);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteSubmitAction(
        Utf8JsonWriter writer,
        string title,
        string style,
        string action,
        bool isEnabled)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "Action.Submit");
        writer.WriteString("title", title);
        writer.WriteString("style", style);
        writer.WriteBoolean("isEnabled", isEnabled);
        writer.WriteStartObject("data");
        writer.WriteString("action", action);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteTextBlock(
        Utf8JsonWriter writer,
        string text,
        bool heading = false,
        bool subtle = false)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "TextBlock");
        writer.WriteString("text", text);
        writer.WriteBoolean("wrap", true);
        if (heading)
        {
            writer.WriteString("weight", "Bolder");
            writer.WriteString("size", "Medium");
        }
        else
        {
            writer.WriteString("spacing", "Small");
        }

        if (subtle)
            writer.WriteBoolean("isSubtle", true);
        writer.WriteEndObject();
    }

    private static string GetButtonText(BrokerInstallSnapshot snapshot)
    {
        return snapshot.Phase switch
        {
            BrokerInstallPhase.CheckingRelease => Loc.Get("BrokerInstall.ButtonChecking"),
            BrokerInstallPhase.Downloading => Loc.Get("BrokerInstall.ButtonDownloading"),
            BrokerInstallPhase.AwaitingElevation => Loc.Get("BrokerInstall.ButtonAwaitingElevation"),
            BrokerInstallPhase.Installing => Loc.Get("BrokerInstall.ButtonInstalling"),
            BrokerInstallPhase.Failed => Loc.Get("BrokerInstall.ButtonRetry"),
            BrokerInstallPhase.Succeeded => Loc.Get("BrokerInstall.ButtonUpdate"),
            _ when snapshot.IsInstalled => Loc.Get("BrokerInstall.ButtonUpdate"),
            _ => Loc.Get("BrokerInstall.ButtonInstall"),
        };
    }

    private static string GetUninstallButtonText(BrokerUninstallSnapshot snapshot)
    {
        return snapshot.Phase switch
        {
            BrokerUninstallPhase.AwaitingElevation => Loc.Get("BrokerUninstall.ButtonAwaitingElevation"),
            BrokerUninstallPhase.Uninstalling => Loc.Get("BrokerUninstall.ButtonUninstalling"),
            _ => Loc.Get("BrokerUninstall.ButtonUninstall"),
        };
    }

    private static string GetStatusText(
        BrokerInstallSnapshot snapshot,
        BrokerUninstallSnapshot uninstallSnapshot,
        BrokerOperation lastOperation)
    {
        if (lastOperation == BrokerOperation.Uninstall)
        {
            return uninstallSnapshot.Phase switch
            {
                BrokerUninstallPhase.AwaitingElevation => Loc.Get("BrokerUninstall.StatusAwaitingElevation"),
                BrokerUninstallPhase.Uninstalling => Loc.Get("BrokerUninstall.StatusUninstalling"),
                BrokerUninstallPhase.Succeeded => Loc.Get("BrokerUninstall.StatusSucceeded"),
                BrokerUninstallPhase.Failed => GetUninstallFailureText(uninstallSnapshot.Failure),
                _ => Loc.Get("BrokerInstall.StatusNotInstalled"),
            };
        }

        return snapshot.Phase switch
        {
            BrokerInstallPhase.CheckingRelease => Loc.Get("BrokerInstall.StatusChecking"),
            BrokerInstallPhase.Downloading => Loc.Get("BrokerInstall.StatusDownloading"),
            BrokerInstallPhase.AwaitingElevation => Loc.Get("BrokerInstall.StatusAwaitingElevation"),
            BrokerInstallPhase.Installing => Loc.Get("BrokerInstall.StatusInstalling"),
            BrokerInstallPhase.Succeeded => Loc.Get("BrokerInstall.StatusSucceeded"),
            BrokerInstallPhase.Failed => GetFailureText(snapshot.Failure),
            _ when snapshot.IsInstalled => Loc.Get("BrokerInstall.StatusInstalled"),
            _ => Loc.Get("BrokerInstall.StatusNotInstalled"),
        };
    }

    private static string GetFailureText(BrokerInstallFailure failure)
    {
        string key = failure switch
        {
            BrokerInstallFailure.Network => "BrokerInstall.FailureNetwork",
            BrokerInstallFailure.NoRelease => "BrokerInstall.FailureNoRelease",
            BrokerInstallFailure.NoCompatibleAsset => "BrokerInstall.FailureNoCompatibleAsset",
            BrokerInstallFailure.DownloadInvalid => "BrokerInstall.FailureDownloadInvalid",
            BrokerInstallFailure.UnsupportedArchitecture => "BrokerInstall.FailureUnsupportedArchitecture",
            BrokerInstallFailure.ElevationCanceled => "BrokerInstall.FailureElevationCanceled",
            BrokerInstallFailure.InstallationFailed => "BrokerInstall.FailureInstallationFailed",
            _ => "BrokerInstall.FailureUnexpected",
        };
        return Loc.Get(key);
    }

    private static string GetUninstallFailureText(BrokerUninstallFailure failure)
    {
        return Loc.Get(failure switch
        {
            BrokerUninstallFailure.ElevationCanceled => "BrokerUninstall.FailureElevationCanceled",
            BrokerUninstallFailure.UninstallationFailed => "BrokerUninstall.FailureUninstallationFailed",
            _ => "BrokerUninstall.FailureUnexpected",
        });
    }

    private static bool IsBrokerInstalled(
        BrokerInstallSnapshot installSnapshot,
        BrokerUninstallSnapshot uninstallSnapshot,
        BrokerOperation lastOperation)
    {
        return lastOperation switch
        {
            BrokerOperation.Install => installSnapshot.IsInstalled,
            BrokerOperation.Uninstall => uninstallSnapshot.IsInstalled,
            _ => installSnapshot.IsInstalled || uninstallSnapshot.IsInstalled,
        };
    }

    private void OnInstallStatusChanged(object? sender, EventArgs e)
    {
        _lastOperation = BrokerOperation.Install;
        RefreshTemplate();
    }

    private void OnUninstallStatusChanged(object? sender, EventArgs e)
    {
        _lastOperation = BrokerOperation.Uninstall;
        RefreshTemplate();
    }

    private void RefreshTemplate()
    {
        TemplateJson = BuildTemplateJson(
            _controller.Snapshot,
            _uninstallController.Snapshot,
            _lastOperation);
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    private enum BrokerOperation
    {
        None,
        Install,
        Uninstall,
    }
}
