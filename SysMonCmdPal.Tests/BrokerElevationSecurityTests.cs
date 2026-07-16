// Copyright (c) 2026 SysMonCmdPal
// Pure generation and parameter tests for the elevated Broker lifecycle scripts.

using System.Runtime.InteropServices;
using Xunit;

namespace SysMonCmdPal.Tests;

public class BrokerElevationSecurityTests
{
    private const string SourcePath = @"C:\Temp\Broker Payload\SysMonBroker.exe";
    private const string UserSid = "S-1-5-21-1000";
    private static readonly string Sha256 = new('A', 64);

    [Fact]
    public void InstallerScript_RebuildsAndVerifiesProtectedDirectoryAcls()
    {
        string script = BuildInstallerScript();

        Assert.Contains("$security = [Security.AccessControl.DirectorySecurity]::new()", script);
        Assert.Contains("$security.SetAccessRuleProtection($true, $false)", script);
        Assert.Contains("$security.SetOwner($administratorSid)", script);
        Assert.Contains("[Security.AccessControl.FileSystemRights]::FullControl", script);
        Assert.Contains("[Security.AccessControl.FileSystemRights]::ReadAndExecute", script);
        Assert.Contains("$actual.GetAccessRules($true, $true", script);
        Assert.Contains("Assert-ProtectedDirectoryAcl $current", script);
        Assert.Contains("Assert-DirectoryNotReparse $current", script);
        Assert.DoesNotContain("icacls.exe", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerScript_UsesOneAllUsersSystemTaskAndPathHealthCheck()
    {
        string script = BuildInstallerScript();

        Assert.Contains("$taskTrigger = New-ScheduledTaskTrigger -AtLogOn", script);
        Assert.Contains(
            "New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest -LogonType ServiceAccount",
            script);
        Assert.Contains("$triggers.Count -ne 1", script);
        Assert.Contains("MSFT_TaskLogonTrigger", script);
        Assert.Contains("Assert-SystemTaskModel $registeredTask", script);
        Assert.Contains("$candidate.StartTime.ToUniversalTime() -ge $healthStart", script);
        Assert.Contains("Test-PathEquals $processPath $targetExe", script);
        Assert.Contains("Stop-Process -Id $process.Id", script);
        Assert.DoesNotContain("New-ScheduledTaskPrincipal -UserId $userSid", script);
        Assert.DoesNotContain(
            "Get-Process -Name 'SysMonBroker' -ErrorAction SilentlyContinue |",
            script);
    }

    [Fact]
    public void InstallerScript_RequiresStableAdvancingV2SharedMemoryAfterProcessValidation()
    {
        string script = BuildInstallerScript();

        Assert.Contains("function Wait-BrokerSharedMemoryHealthy([DateTime]$Deadline)", script);
        Assert.Contains("$sequenceBefore = $accessor.ReadInt32(12)", script);
        Assert.Contains("if (($sequenceBefore -band 1) -ne 0)", script);
        Assert.Contains("$sequenceAfter = $accessor.ReadInt32(12)", script);
        Assert.Contains("$sequenceBefore -eq $sequenceAfter", script);
        Assert.Contains("($sequenceAfter -band 1) -eq 0", script);
        Assert.Contains("$magic -eq 0x5342524B", script);
        Assert.Contains("$version -eq 2", script);
        Assert.Contains("$extensionMagic -eq 0x31584D53", script);
        Assert.Contains("$instanceId -ne 0", script);
        Assert.Contains("$publishMs -gt 0", script);
        Assert.Contains("$baseline.InstanceId -eq $instanceId", script);
        Assert.Contains("$baseline.Counter -ne $counter", script);
        Assert.Contains("$publishMs -gt $baseline.PublishMs", script);

        AssertOrdered(
            script,
            "if (-not $healthyProcess) { throw 'The scheduled Broker process failed path validation.' }",
            "Wait-BrokerSharedMemoryHealthy ([DateTime]::UtcNow.AddSeconds(30))",
            "Assert-SystemTaskModel (Get-ScheduledTask -TaskName $taskName -ErrorAction Stop)");
    }

    [Fact]
    public void InstallerScript_BacksUpAndRollsBackManagedExeAndTask()
    {
        string script = BuildInstallerScript();

        Assert.Contains("Export-ScheduledTask -TaskName $taskName", script);
        Assert.Contains("[IO.File]::Replace($stagedExe, $targetExe, $backupExe, $true)", script);
        Assert.Contains("Register-ScheduledTask -TaskName $taskName -Xml $oldTaskXml -Force", script);
        Assert.Contains("Restore-ManagedFile $backupExe $targetExe", script);
        Assert.Contains("Assert-ManagedTaskAction $oldTask $targetExe $targetDirectory", script);
        Assert.Contains("$preserveBackups = $rollbackFailed", script);
    }

    [Fact]
    public void InstallerScript_InstallsStandaloneUninstallerAndArpEntry()
    {
        string script = BuildInstallerScript();

        Assert.Contains("$uninstallScriptBase64 = '", script);
        Assert.DoesNotContain("__UNINSTALL_SCRIPT_BASE64__", script);
        Assert.Contains("Uninstall-SysMonBroker.ps1", script);
        Assert.Contains("$key.SetValue('UninstallString'", script);
        Assert.Contains("SysMonCmdPalBroker", script);
    }

    [Fact]
    public void UninstallScript_PreflightsTaskAndOwnedTreesBeforeDeletion()
    {
        string script = BrokerUninstallElevation.BuildUninstallScript();
        int taskPreflight = script.IndexOf(
            "if ($task) { Assert-ManagedTaskAction $task }",
            StringComparison.Ordinal);
        int firstDeletion = script.IndexOf(
            "Unregister-ScheduledTask",
            StringComparison.Ordinal);

        Assert.True(taskPreflight >= 0 && firstDeletion > taskPreflight);
        Assert.Contains("Assert-OwnedTree $targetDirectory", script);
        Assert.Contains("Assert-OwnedTree $logDirectory", script);
        Assert.Contains("Test-PathEquals $processPath $targetExe", script);
        Assert.Contains("Stop-Process -Id $process.Id", script);
        Assert.Contains("$base.DeleteSubKeyTree($arpKeyName, $false)", script);
        Assert.Contains("Remove-Item -LiteralPath $targetDirectory -Recurse", script);
        Assert.DoesNotContain("Remove-Item -LiteralPath $targetRoot -Recurse", script);
    }

    [Fact]
    public void MaintenanceScripts_ShareCrossProcessMutex()
    {
        string installScript = BuildInstallerScript();
        string uninstallScript = BrokerUninstallElevation.BuildUninstallScript();

        foreach (string script in new[] { installScript, uninstallScript })
        {
            Assert.Contains("Global\\SysMonCmdPalBrokerMaintenance", script);
            Assert.Contains("$maintenanceMutex.WaitOne(0)", script);
            Assert.Contains("$maintenanceMutex.ReleaseMutex()", script);
        }
    }

    [Fact]
    public void PowerShellArguments_KeepEncodedCommandAndScriptPathAsSingleArguments()
    {
        const string encodedCommand = "QQBCAEMAPQ==";
        string scriptPath = @"C:\Program Files\SysMonCmdPal\Broker\Uninstall-SysMonBroker.ps1";

        string[] installArguments = BrokerInstallElevation.BuildPowerShellArguments(encodedCommand);
        string[] uninstallArguments = BrokerUninstallElevation.BuildPowerShellArguments(scriptPath);

        Assert.Equal(encodedCommand, installArguments[^1]);
        Assert.Equal("-EncodedCommand", installArguments[^2]);
        Assert.Equal(scriptPath, uninstallArguments[^2]);
        Assert.Equal("-Elevated", uninstallArguments[^1]);
        Assert.DoesNotContain('"', uninstallArguments[^2]);
    }

    [Theory]
    [InlineData("", "S-1-5-21-1000", Architecture.X64)]
    [InlineData("ABC", "S-1-5-21-1000", Architecture.X64)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "not-a-sid", Architecture.X64)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "S-1-5-21-1000", Architecture.X86)]
    public void InstallerParameters_RejectInvalidHashSidOrArchitecture(
        string sha256,
        string userSid,
        Architecture architecture)
    {
        Assert.Throws<ArgumentException>(() => BrokerInstallElevation.BuildInstallerScript(
            SourcePath,
            sha256,
            architecture,
            userSid));
    }

    [Theory]
    [InlineData("{\"action\":\"uninstallBroker\"}", true)]
    [InlineData("{\"action\":\"installBroker\"}", false)]
    [InlineData("{\"other\":\"uninstallBroker\"}", false)]
    [InlineData("not-json", false)]
    public void SettingsForm_RecognizesOnlyUninstallAction(string data, bool expected)
    {
        Assert.Equal(expected, BrokerInstallSettingsForm.ContainsUninstallAction(data));
    }

    [Fact]
    public async Task UninstallController_ExceptionPublishesNonBusyFailedState()
    {
        var controller = new BrokerUninstallController(new ThrowingUninstaller());
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StatusChanged += (_, _) =>
        {
            if (controller.Snapshot.Phase == BrokerUninstallPhase.Failed)
                failed.TrySetResult();
        };

        Assert.True(controller.TryStart());
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(BrokerUninstallPhase.Failed, controller.Snapshot.Phase);
        Assert.Equal(BrokerUninstallFailure.Unexpected, controller.Snapshot.Failure);
        Assert.False(controller.Snapshot.IsBusy);
    }

    private static string BuildInstallerScript()
        => BrokerInstallElevation.BuildInstallerScript(
            SourcePath,
            Sha256,
            Architecture.X64,
            UserSid);

    private static void AssertOrdered(string text, params string[] fragments)
    {
        int previousIndex = -1;
        foreach (string fragment in fragments)
        {
            int index = text.IndexOf(fragment, previousIndex + 1, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Expected script fragment after index {previousIndex}: {fragment}");
            previousIndex = index;
        }
    }

    private sealed class ThrowingUninstaller : IBrokerUninstaller
    {
        public bool IsInstalled => true;

        public Task<BrokerUninstallFailure> UninstallAsync(
            Action<BrokerUninstallPhase> reportProgress)
            => throw new InvalidOperationException("test failure");
    }
}
