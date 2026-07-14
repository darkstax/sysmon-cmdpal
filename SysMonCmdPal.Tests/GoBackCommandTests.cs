// Copyright (c) 2026 SysMonCmdPal

using Microsoft.CommandPalette.Extensions;
using Xunit;

namespace SysMonCmdPal.Tests;

public sealed class GoBackCommandTests
{
    [Fact]
    public void Invoke_RunsCleanupAndReturnsGoBack()
    {
        bool cleanedUp = false;
        var command = new GoBackCommand(() => cleanedUp = true);

        ICommandResult result = command.Invoke();

        Assert.True(cleanedUp);
        Assert.Equal(CommandResultKind.GoBack, result.Kind);
    }
}
