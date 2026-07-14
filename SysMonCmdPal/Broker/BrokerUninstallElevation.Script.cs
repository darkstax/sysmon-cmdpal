// Copyright (c) 2026 SysMonCmdPal
// Owns the embedded PowerShell source for the independently installed Broker uninstaller.

namespace SysMonCmdPal;

internal static partial class BrokerUninstallElevation
{
    private const string UninstallScript = """
param([switch]$Elevated)

$ErrorActionPreference = 'Stop'
$programFiles = [IO.Path]::GetFullPath($env:ProgramFiles)
$programData = [IO.Path]::GetFullPath($env:ProgramData)
$targetRoot = Join-Path $programFiles 'SysMonCmdPal'
$targetDirectory = Join-Path $targetRoot 'Broker'
$targetExe = Join-Path $targetDirectory 'SysMonBroker.exe'
$expectedScript = Join-Path $targetDirectory 'Uninstall-SysMonBroker.ps1'
$dataRoot = Join-Path $programData 'SysMonCmdPal'
$logDirectory = Join-Path $dataRoot 'Logs'
$taskName = 'SysMonBroker'
$arpBasePath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
$arpKeyName = 'SysMonCmdPalBroker'
$maintenanceMutex = $null
$maintenanceMutexAcquired = $false

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function ConvertTo-NormalizedPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'A required path is empty.' }
    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    if ($expanded -notmatch '^[A-Za-z]:\\') { throw 'A required path is not absolute.' }
    return [IO.Path]::GetFullPath($expanded).TrimEnd('\')
}

function Test-PathEquals([string]$Left, [string]$Right) {
    try {
        return [string]::Equals(
            (ConvertTo-NormalizedPath $Left),
            (ConvertTo-NormalizedPath $Right),
            [StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Assert-OwnedDirectoryChain([string]$BasePath, [string[]]$Segments) {
    $current = ConvertTo-NormalizedPath $BasePath
    foreach ($segment in $Segments) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) { continue }
        $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
        if (-not $item.PSIsContainer -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'An owned directory path is unsafe.'
        }
    }
}

function Assert-OwnedTree([string]$Root) {
    if (-not (Test-Path -LiteralPath $Root)) { return }
    $rootItem = Get-Item -LiteralPath $Root -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'An owned tree root is unsafe.'
    }

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($rootItem.FullName)
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($child in @(Get-ChildItem -LiteralPath $directory -Force -ErrorAction Stop)) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'An owned tree contains a reparse point.'
            }
            if ($child.PSIsContainer) { $pending.Push($child.FullName) }
        }
    }
}

function Assert-ManagedTaskAction([object]$Task) {
    $actions = @($Task.Actions)
    if ($actions.Count -ne 1) { throw 'The Broker task must contain exactly one action.' }
    $action = $actions[0]
    if (-not (Test-PathEquals $action.Execute $targetExe)) {
        throw 'The Broker task action points outside the managed Broker path.'
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$action.Arguments)) {
        throw 'The Broker task action contains unexpected arguments.'
    }
    if (-not (Test-PathEquals $action.WorkingDirectory $targetDirectory)) {
        throw 'The Broker task working directory is not managed.'
    }
}

function Get-OwnedBrokerProcesses {
    foreach ($process in @(Get-Process -Name 'SysMonBroker' -ErrorAction SilentlyContinue)) {
        $processPath = $null
        try { $processPath = $process.Path } catch { }
        if ($processPath -and (Test-PathEquals $processPath $targetExe)) {
            $process
        }
    }
}

function Stop-OwnedBrokerProcesses {
    foreach ($process in @(Get-OwnedBrokerProcesses)) {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        try { $process.WaitForExit(5000) | Out-Null } catch { }
    }
    if (@(Get-OwnedBrokerProcesses).Count -ne 0) {
        throw 'A managed Broker process could not be stopped.'
    }
}

function Remove-ArpEntry {
    $base = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($arpBasePath, $true)
    if (-not $base) { return }
    try {
        try { $base.DeleteSubKeyTree($arpKeyName, $false) } catch [ArgumentException] { }
    } finally {
        $base.Dispose()
    }
}

function Remove-EmptyOwnedDirectory([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    if (@(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop).Count -eq 0) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
    }
}

try {
    if (-not (Test-PathEquals $PSCommandPath $expectedScript)) { exit 30 }
    Assert-OwnedDirectoryChain $programFiles @('SysMonCmdPal', 'Broker')
    $scriptItem = Get-Item -LiteralPath $expectedScript -Force -ErrorAction Stop
    if (($scriptItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { exit 30 }

    if (-not (Test-IsAdministrator)) {
        if ($Elevated) { exit 30 }
        $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $powerShell
        $startInfo.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Elevated' -f $expectedScript
        $startInfo.UseShellExecute = $true
        $startInfo.Verb = 'runas'
        $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $child = [Diagnostics.Process]::Start($startInfo)
        if (-not $child) { exit 30 }
        try {
            if (-not $child.WaitForExit(120000)) {
                try { $child.Kill() } catch { }
                exit 30
            }
            exit $child.ExitCode
        } finally {
            $child.Dispose()
        }
    }

    $createdNew = $false
    $maintenanceMutex = [Threading.Mutex]::new(
        $false,
        'Global\SysMonCmdPalBrokerMaintenance',
        [ref]$createdNew)
    try {
        $maintenanceMutexAcquired = $maintenanceMutex.WaitOne(0)
    } catch [Threading.AbandonedMutexException] {
        $maintenanceMutexAcquired = $true
    }
    if (-not $maintenanceMutexAcquired) { throw 'Broker maintenance is already running.' }

    Assert-OwnedDirectoryChain $programFiles @('SysMonCmdPal', 'Broker')
    Assert-OwnedDirectoryChain $programData @('SysMonCmdPal', 'Logs')
    Assert-OwnedTree $targetDirectory
    Assert-OwnedTree $logDirectory

    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task) { Assert-ManagedTaskAction $task }

    if ($task) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    }
    Stop-OwnedBrokerProcesses

    if ($task) {
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
        Assert-ManagedTaskAction $task
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
    }
    Remove-ArpEntry

    Set-Location -LiteralPath $env:TEMP
    if (Test-Path -LiteralPath $logDirectory) {
        Remove-Item -LiteralPath $logDirectory -Recurse -Force -ErrorAction Stop
    }
    Remove-EmptyOwnedDirectory $dataRoot
    if (Test-Path -LiteralPath $targetDirectory) {
        Remove-Item -LiteralPath $targetDirectory -Recurse -Force -ErrorAction Stop
    }
    Remove-EmptyOwnedDirectory $targetRoot

    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { exit 30 }
    exit 0
} catch {
    exit 30
} finally {
    if ($maintenanceMutexAcquired) {
        try { $maintenanceMutex.ReleaseMutex() } catch { }
    }
    if ($maintenanceMutex) { $maintenanceMutex.Dispose() }
}
""";
}
