// Copyright (c) 2026 SysMonCmdPal
// Starts the explicitly confirmed Broker installation without blocking CmdPal.

using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class BrokerInstallCommand : InvokableCommand
{
    private readonly BrokerInstallController _controller;
    private readonly Func<bool> _otherOperationIsBusy;

    public BrokerInstallCommand(
        BrokerInstallController controller,
        Func<bool>? otherOperationIsBusy = null)
    {
        _controller = controller;
        _otherOperationIsBusy = otherOperationIsBusy ?? (() => false);
        Id = "install-sysmon-broker";
        Name = Loc.Get("BrokerInstall.ConfirmAction");
        Icon = new IconInfo(SysMonIcons.Add);
    }

    public override CommandResult Invoke()
    {
        if (_otherOperationIsBusy())
            return CommandResult.ShowToast(Loc.Get("BrokerInstall.AlreadyRunning"));

        return _controller.TryStart()
            ? CommandResult.ShowToast(Loc.Get("BrokerInstall.Started"))
            : CommandResult.ShowToast(Loc.Get("BrokerInstall.AlreadyRunning"));
    }
}
