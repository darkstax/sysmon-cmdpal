// Copyright (c) 2026 SysMonCmdPal

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace SysMonCmdPal;

internal sealed partial class GoBackCommand : InvokableCommand
{
    private readonly Action? _beforeGoBack;

    public GoBackCommand(Action? beforeGoBack = null)
    {
        _beforeGoBack = beforeGoBack;
        Name = Loc.Get("Common.Back");
        Icon = new IconInfo(SysMonIcons.Back);
    }

    public override ICommandResult Invoke()
    {
        _beforeGoBack?.Invoke();
        return CommandResult.GoBack();
    }
}

internal static class PageNavigation
{
    public static CommandContextItem BackContextItem(Action? beforeGoBack = null) =>
        new(new GoBackCommand(beforeGoBack))
        {
            Title = Loc.Get("Common.Back"),
            Icon = new IconInfo(SysMonIcons.Back),
        };

    public static ListItem BackListItem(Action? beforeGoBack = null) =>
        new(new GoBackCommand(beforeGoBack))
        {
            Title = Loc.Get("Common.Back"),
            Icon = new IconInfo(SysMonIcons.Back),
        };
}
