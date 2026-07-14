// Copyright (c) 2026 SysMonCmdPal
// Coordinates one explicitly confirmed Broker uninstall and exposes UI-safe status.

namespace SysMonCmdPal;

internal enum BrokerUninstallPhase
{
    Idle,
    AwaitingElevation,
    Uninstalling,
    Succeeded,
    Failed,
}

internal enum BrokerUninstallFailure
{
    None,
    ElevationCanceled,
    UninstallationFailed,
    Unexpected,
}

internal readonly record struct BrokerUninstallSnapshot(
    BrokerUninstallPhase Phase,
    BrokerUninstallFailure Failure,
    bool IsInstalled)
{
    public bool IsBusy => Phase is BrokerUninstallPhase.AwaitingElevation
        or BrokerUninstallPhase.Uninstalling;
}

internal interface IBrokerUninstaller
{
    bool IsInstalled { get; }

    Task<BrokerUninstallFailure> UninstallAsync(Action<BrokerUninstallPhase> reportProgress);
}

internal sealed class BrokerUninstaller : IBrokerUninstaller
{
    public bool IsInstalled => BrokerUninstallElevation.IsInstalled;

    public async Task<BrokerUninstallFailure> UninstallAsync(
        Action<BrokerUninstallPhase> reportProgress)
    {
        BrokerUninstallElevationResult result = await BrokerUninstallElevation.UninstallAsync(
            () => reportProgress(BrokerUninstallPhase.Uninstalling)).ConfigureAwait(false);
        return result switch
        {
            BrokerUninstallElevationResult.Succeeded => BrokerUninstallFailure.None,
            BrokerUninstallElevationResult.Canceled => BrokerUninstallFailure.ElevationCanceled,
            _ => BrokerUninstallFailure.UninstallationFailed,
        };
    }
}

internal sealed class BrokerUninstallController
{
    private readonly object _sync = new();
    private readonly IBrokerUninstaller _uninstaller;
    private BrokerUninstallSnapshot _snapshot;

    public BrokerUninstallController()
        : this(new BrokerUninstaller())
    {
    }

    internal BrokerUninstallController(IBrokerUninstaller uninstaller)
    {
        _uninstaller = uninstaller;
        _snapshot = new BrokerUninstallSnapshot(
            BrokerUninstallPhase.Idle,
            BrokerUninstallFailure.None,
            uninstaller.IsInstalled);
    }

    public event EventHandler? StatusChanged;

    public BrokerUninstallSnapshot Snapshot
    {
        get
        {
            lock (_sync)
                return _snapshot;
        }
    }

    public bool TryStart()
    {
        lock (_sync)
        {
            if (_snapshot.IsBusy)
                return false;

            _snapshot = new BrokerUninstallSnapshot(
                BrokerUninstallPhase.AwaitingElevation,
                BrokerUninstallFailure.None,
                _uninstaller.IsInstalled);
        }

        RaiseStatusChanged();
        _ = Task.Run(RunUninstallAsync);
        return true;
    }

    private async Task RunUninstallAsync()
    {
        BrokerUninstallFailure failure;
        try
        {
            failure = await _uninstaller.UninstallAsync(ReportProgress).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SensorLogger.ForceLog($"[BrokerUninstaller] Unhandled failure: {ex.GetType().Name}");
            failure = BrokerUninstallFailure.Unexpected;
        }

        Publish(
            failure == BrokerUninstallFailure.None
                ? BrokerUninstallPhase.Succeeded
                : BrokerUninstallPhase.Failed,
            failure);
    }

    private void ReportProgress(BrokerUninstallPhase phase)
        => Publish(phase, BrokerUninstallFailure.None);

    private void Publish(BrokerUninstallPhase phase, BrokerUninstallFailure failure)
    {
        lock (_sync)
            _snapshot = new BrokerUninstallSnapshot(phase, failure, _uninstaller.IsInstalled);

        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        try
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A disconnected CmdPal host must not abort an in-flight uninstall.
        }
    }
}
