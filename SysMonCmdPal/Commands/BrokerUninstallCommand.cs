// Copyright (c) 2026 SysMonCmdPal
// Starts the explicitly confirmed Broker uninstall without blocking CmdPal.

using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class BrokerUninstallCommand : InvokableCommand
{
    private readonly BrokerUninstallController _controller;
    private readonly Func<bool> _otherOperationIsBusy;

    public BrokerUninstallCommand(
        BrokerUninstallController controller,
        Func<bool>? otherOperationIsBusy = null)
    {
        _controller = controller;
        _otherOperationIsBusy = otherOperationIsBusy ?? (() => false);
        Id = "uninstall-sysmon-broker";
        Name = Loc.Get("BrokerUninstall.ConfirmAction");
        Icon = new IconInfo(SysMonIcons.Remove);
    }

    public override CommandResult Invoke()
    {
        if (_otherOperationIsBusy())
            return CommandResult.ShowToast(Loc.Get("BrokerInstall.AlreadyRunning"));

        return _controller.TryStart()
            ? CommandResult.ShowToast(Loc.Get("BrokerUninstall.Started"))
            : CommandResult.ShowToast(Loc.Get("BrokerInstall.AlreadyRunning"));
    }
}
