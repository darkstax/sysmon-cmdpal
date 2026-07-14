// Copyright (c) 2026 SysMonCmdPal
// Coordinates one explicitly requested Broker install and exposes UI-safe status.

namespace SysMonCmdPal;

internal enum BrokerInstallPhase
{
    Idle,
    CheckingRelease,
    Downloading,
    AwaitingElevation,
    Installing,
    Succeeded,
    Failed,
}

internal enum BrokerInstallFailure
{
    None,
    Network,
    NoRelease,
    NoCompatibleAsset,
    DownloadInvalid,
    UnsupportedArchitecture,
    ElevationCanceled,
    Canceled,
    InstallationFailed,
    Unexpected,
}

internal readonly record struct BrokerInstallSnapshot(
    BrokerInstallPhase Phase,
    BrokerInstallFailure Failure,
    bool IsInstalled)
{
    public bool IsBusy => Phase is BrokerInstallPhase.CheckingRelease
        or BrokerInstallPhase.Downloading
        or BrokerInstallPhase.AwaitingElevation
        or BrokerInstallPhase.Installing;
}

internal interface IBrokerInstaller
{
    bool IsInstalled { get; }

    Task<BrokerInstallFailure> InstallAsync(
        Action<BrokerInstallPhase> reportProgress,
        CancellationToken cancellationToken);
}

internal sealed class BrokerInstallController
{
    private readonly object _sync = new();
    private readonly IBrokerInstaller _installer;
    private BrokerInstallSnapshot _snapshot;

    public BrokerInstallController()
        : this(new BrokerInstaller())
    {
    }

    internal BrokerInstallController(IBrokerInstaller installer)
    {
        _installer = installer;
        _snapshot = new BrokerInstallSnapshot(
            BrokerInstallPhase.Idle,
            BrokerInstallFailure.None,
            ReadIsInstalled(installer, fallback: false));
    }

    public event EventHandler? StatusChanged;

    public BrokerInstallSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return _snapshot;
        }
    }

    public bool TryStart(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_snapshot.IsBusy)
                return false;

            _snapshot = new BrokerInstallSnapshot(
                BrokerInstallPhase.CheckingRelease,
                BrokerInstallFailure.None,
                ReadIsInstalled(_installer, _snapshot.IsInstalled));
        }

        RaiseStatusChanged();
        _ = Task.Run(() => RunInstallAsync(cancellationToken));
        return true;
    }

    private async Task RunInstallAsync(CancellationToken cancellationToken)
    {
        BrokerInstallFailure failure;
        try
        {
            failure = await _installer.InstallAsync(ReportProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            failure = BrokerInstallFailure.Canceled;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[BrokerInstaller] Unhandled failure: {ex.GetType().Name}");
            failure = BrokerInstallFailure.Unexpected;
        }

        Publish(
            failure == BrokerInstallFailure.None
                ? BrokerInstallPhase.Succeeded
                : BrokerInstallPhase.Failed,
            failure);
    }

    private void ReportProgress(BrokerInstallPhase phase)
        => Publish(phase, BrokerInstallFailure.None);

    private void Publish(BrokerInstallPhase phase, BrokerInstallFailure failure)
    {
        lock (_sync)
            _snapshot = new BrokerInstallSnapshot(
                phase,
                failure,
                ReadIsInstalled(_installer, _snapshot.IsInstalled));

        RaiseStatusChanged();
    }

    private static bool ReadIsInstalled(IBrokerInstaller installer, bool fallback)
    {
        try
        {
            return installer.IsInstalled;
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[BrokerInstaller] Installed-state failure: {ex.GetType().Name}");
            return fallback;
        }
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A disconnected CmdPal host must not abort an in-flight install.
        }
    }
}
