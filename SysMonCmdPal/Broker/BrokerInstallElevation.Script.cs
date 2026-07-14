// Copyright (c) 2026 SysMonCmdPal
// Contains the elevated installer PowerShell payload.

namespace SysMonCmdPal;

internal static partial class BrokerInstallElevation
{
    private const string InstallerScript = """
$ErrorActionPreference = 'Stop'
$source = '__SOURCE__'
$expectedHash = '__SHA256__'
$expectedMachine = [UInt16]__MACHINE__
$userSid = '__USER_SID__'
$uninstallScriptBase64 = '__UNINSTALL_SCRIPT_BASE64__'
$programFiles = [IO.Path]::GetFullPath($env:ProgramFiles)
$programData = [IO.Path]::GetFullPath($env:ProgramData)
$targetRoot = Join-Path $programFiles 'SysMonCmdPal'
$targetDirectory = Join-Path $targetRoot 'Broker'
$targetExe = Join-Path $targetDirectory 'SysMonBroker.exe'
$uninstallScript = Join-Path $targetDirectory 'Uninstall-SysMonBroker.ps1'
$dataRoot = Join-Path $programData 'SysMonCmdPal'
$logDirectory = Join-Path $dataRoot 'Logs'
$taskName = 'SysMonBroker'
$arpBasePath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall'
$arpKeyName = 'SysMonCmdPalBroker'
$transactionId = [Guid]::NewGuid().ToString('N')
$stagedExe = Join-Path $targetDirectory ("SysMonBroker.exe.new-{0}" -f $transactionId)
$backupExe = Join-Path $targetDirectory ("SysMonBroker.exe.backup-{0}" -f $transactionId)
$stagedUninstall = Join-Path $targetDirectory ("Uninstall-SysMonBroker.ps1.new-{0}" -f $transactionId)
$backupUninstall = Join-Path $targetDirectory ("Uninstall-SysMonBroker.ps1.backup-{0}" -f $transactionId)
$taskBackupPath = Join-Path $targetDirectory ("SysMonBroker.task.backup-{0}.xml" -f $transactionId)
$administratorSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
$systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$usersSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
$oldTask = $null
$oldTaskXml = $null
$oldTaskWasRunning = $false
$hadOldExe = $false
$hadOldUninstall = $false
$arpSnapshot = $null
$mutationStarted = $false
$exeChanged = $false
$uninstallChanged = $false
$arpChanged = $false
$transactionCommitted = $false
$preserveBackups = $false
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

function Assert-DirectoryNotReparse([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer) { throw 'An owned directory path is not a directory.' }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'An owned directory path is a reparse point.'
    }
}

function Assert-LeafNotReparse([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'An owned file path is a reparse point.'
    }
}

function New-ProtectedDirectorySecurity {
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($administratorSid)
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $systemSid,
        [Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        $propagation,
        $allow))
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $administratorSid,
        [Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        $propagation,
        $allow))
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $usersSid,
        [Security.AccessControl.FileSystemRights]::ReadAndExecute,
        $inheritance,
        $propagation,
        $allow))
    return $security
}

function Assert-ProtectedDirectoryAcl([string]$Path) {
    $actual = Get-Acl -LiteralPath $Path -ErrorAction Stop
    if (-not $actual.AreAccessRulesProtected) { throw 'Directory ACL inheritance is enabled.' }
    if ($actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $administratorSid.Value) {
        throw 'Directory owner validation failed.'
    }

    $rules = @($actual.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if ($rules.Count -ne 3) { throw 'Directory ACL contains unexpected rules.' }
    $expectedRights = @{
        $systemSid.Value = [int][Security.AccessControl.FileSystemRights]::FullControl
        $administratorSid.Value = [int][Security.AccessControl.FileSystemRights]::FullControl
        $usersSid.Value = [int][Security.AccessControl.FileSystemRights]::ReadAndExecute
    }
    $expectedInheritance = [int](
        [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit)

    foreach ($rule in $rules) {
        $sid = $rule.IdentityReference.Value
        if (-not $expectedRights.ContainsKey($sid)) { throw 'Directory ACL identity validation failed.' }
        if ($rule.IsInherited -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow) {
            throw 'Directory ACL rule type validation failed.'
        }
        if ([int]$rule.FileSystemRights -ne $expectedRights[$sid] -or
            [int]$rule.InheritanceFlags -ne $expectedInheritance -or
            $rule.PropagationFlags -ne [Security.AccessControl.PropagationFlags]::None) {
            throw 'Directory ACL rights validation failed.'
        }
        $expectedRights.Remove($sid)
    }
    if ($expectedRights.Count -ne 0) { throw 'Directory ACL is missing a required rule.' }
}

function Initialize-ProtectedDirectoryChain([string]$BasePath, [string[]]$Segments) {
    $base = ConvertTo-NormalizedPath $BasePath
    $current = $base
    foreach ($segment in $Segments) {
        if ([string]::IsNullOrWhiteSpace($segment) -or $segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) {
            throw 'Invalid owned directory segment.'
        }
        $current = Join-Path $current $segment
        $existing = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($existing) {
            Assert-DirectoryNotReparse $current
        } else {
            [IO.Directory]::CreateDirectory($current) | Out-Null
            Assert-DirectoryNotReparse $current
        }

        Set-Acl -LiteralPath $current -AclObject (New-ProtectedDirectorySecurity) -ErrorAction Stop
        Assert-DirectoryNotReparse $current
        Assert-ProtectedDirectoryAcl $current
    }
}

function Test-PeExecutable([string]$Path, [UInt16]$Machine) {
    $stream = $null
    $reader = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        if ($stream.Length -lt 128 -or $stream.Length -gt 268435456) { return $false }
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) { return $false }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        if ($peOffset -lt 0x40 -or $peOffset -gt ($stream.Length - 26)) { return $false }
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) { return $false }
        if ($reader.ReadUInt16() -ne $Machine) { return $false }
        $stream.Position = $peOffset + 20
        $optionalHeaderSize = $reader.ReadUInt16()
        $characteristics = $reader.ReadUInt16()
        $optionalHeaderMagic = $reader.ReadUInt16()
        return $optionalHeaderSize -ge 2 -and
            $optionalHeaderMagic -eq 0x020B -and
            ($characteristics -band 0x0002) -ne 0 -and
            ($characteristics -band 0x2000) -eq 0
    } catch {
        return $false
    } finally {
        if ($reader) { $reader.Dispose() }
        elseif ($stream) { $stream.Dispose() }
    }
}

function Assert-ManagedTaskAction([object]$Task, [string]$ExpectedExe, [string]$ExpectedWorkingDirectory) {
    $actions = @($Task.Actions)
    if ($actions.Count -ne 1) { throw 'The Broker task must contain exactly one action.' }
    $action = $actions[0]
    if (-not (Test-PathEquals $action.Execute $ExpectedExe)) {
        throw 'The Broker task action points outside the managed Broker path.'
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$action.Arguments)) {
        throw 'The Broker task action contains unexpected arguments.'
    }
    if (-not (Test-PathEquals $action.WorkingDirectory $ExpectedWorkingDirectory)) {
        throw 'The Broker task working directory is not managed.'
    }
}

function Test-IsSystemPrincipal([string]$UserId) {
    try {
        $sid = if ($UserId -match '^S-1-') {
            [Security.Principal.SecurityIdentifier]::new($UserId)
        } else {
            [Security.Principal.NTAccount]::new($UserId).Translate(
                [Security.Principal.SecurityIdentifier])
        }
        return $sid.Value -eq $systemSid.Value
    } catch {
        return $false
    }
}

function Assert-SystemTaskModel([object]$Task) {
    Assert-ManagedTaskAction $Task $targetExe $targetDirectory
    if (-not (Test-IsSystemPrincipal ([string]$Task.Principal.UserId)) -or
        $Task.Principal.RunLevel.ToString() -ne 'Highest' -or
        $Task.Principal.LogonType.ToString() -ne 'ServiceAccount') {
        throw 'The Broker task principal validation failed.'
    }
    $triggers = @($Task.Triggers)
    if ($triggers.Count -ne 1 -or
        $triggers[0].CimClass.CimClassName -ne 'MSFT_TaskLogonTrigger' -or
        -not [string]::IsNullOrWhiteSpace([string]$triggers[0].UserId)) {
        throw 'The Broker task trigger is not an all-users logon trigger.'
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

function Get-ArpSnapshot {
    $base = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($arpBasePath, $false)
    if (-not $base) { return $null }
    try {
        $key = $base.OpenSubKey($arpKeyName, $false)
        if (-not $key) { return $null }
        try {
            $values = @()
            foreach ($name in $key.GetValueNames()) {
                $values += [PSCustomObject]@{
                    Name = $name
                    Kind = $key.GetValueKind($name)
                    Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                }
            }
            return [PSCustomObject]@{ Values = $values }
        } finally {
            $key.Dispose()
        }
    } finally {
        $base.Dispose()
    }
}

function Restore-ArpSnapshot([object]$Snapshot) {
    $base = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($arpBasePath, $true)
    try {
        try { $base.DeleteSubKeyTree($arpKeyName, $false) } catch [ArgumentException] { }
        if ($null -eq $Snapshot) { return }
        $key = $base.CreateSubKey($arpKeyName, $true)
        try {
            foreach ($entry in @($Snapshot.Values)) {
                $key.SetValue($entry.Name, $entry.Value, $entry.Kind)
            }
        } finally {
            $key.Dispose()
        }
    } finally {
        $base.Dispose()
    }
}

function Register-ArpEntry {
    $windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $uninstallCommand = '"{0}" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{1}"' -f `
        $windowsPowerShell, $uninstallScript
    $version = (Get-Item -LiteralPath $targetExe -ErrorAction Stop).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) { $version = '1.5.0' }
    $estimatedSize = [int][Math]::Ceiling((Get-Item -LiteralPath $targetExe).Length / 1KB)
    $base = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($arpBasePath, $true)
    try {
        $key = $base.CreateSubKey($arpKeyName, $true)
        try {
            $key.SetValue('DisplayName', 'SysMonCmdPal Broker', [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('DisplayVersion', $version, [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('Publisher', 'darkstax', [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('InstallLocation', $targetDirectory, [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('DisplayIcon', $targetExe, [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('UninstallString', $uninstallCommand, [Microsoft.Win32.RegistryValueKind]::String)
            $key.SetValue('NoModify', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('NoRepair', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('EstimatedSize', $estimatedSize, [Microsoft.Win32.RegistryValueKind]::DWord)
            $key.SetValue('InstallDate', (Get-Date -Format 'yyyyMMdd'), [Microsoft.Win32.RegistryValueKind]::String)
        } finally {
            $key.Dispose()
        }
    } finally {
        $base.Dispose()
    }
}

function Restore-ManagedFile(
    [string]$BackupPath,
    [string]$TargetPath,
    [bool]$HadOriginal,
    [bool]$Changed) {
    if (-not $Changed) { return }
    if ($HadOriginal) {
        if (-not (Test-Path -LiteralPath $BackupPath -PathType Leaf)) {
            throw 'A transaction backup is missing.'
        }
        if (Test-Path -LiteralPath $TargetPath -PathType Leaf) {
            [IO.File]::Replace($BackupPath, $TargetPath, $null, $true)
        } else {
            [IO.File]::Move($BackupPath, $TargetPath)
        }
    } elseif (Test-Path -LiteralPath $TargetPath) {
        Remove-Item -LiteralPath $TargetPath -Force -ErrorAction Stop
    }
}

function Restore-Task {
    $currentTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($currentTask) {
        Assert-ManagedTaskAction $currentTask $targetExe $targetDirectory
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    }
    if ($oldTaskXml) {
        Register-ScheduledTask -TaskName $taskName -Xml $oldTaskXml -Force | Out-Null
    } elseif ($currentTask) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
    }
}

try {
    if (-not (Test-IsAdministrator)) { exit 30 }
    if ($userSid -notmatch '^S-1-[0-9-]+$') { exit 30 }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { exit 20 }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -cne $expectedHash) { exit 20 }
    if (-not (Test-PeExecutable -Path $source -Machine $expectedMachine)) { exit 21 }

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

    $oldTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($oldTask) {
        Assert-ManagedTaskAction $oldTask $targetExe $targetDirectory
        $oldTaskWasRunning = $oldTask.State.ToString() -eq 'Running'
    }

    Initialize-ProtectedDirectoryChain $programFiles @('SysMonCmdPal', 'Broker')
    Initialize-ProtectedDirectoryChain $programData @('SysMonCmdPal', 'Logs')
    Assert-LeafNotReparse $targetExe
    Assert-LeafNotReparse $uninstallScript

    $hadOldExe = Test-Path -LiteralPath $targetExe -PathType Leaf
    $hadOldUninstall = Test-Path -LiteralPath $uninstallScript -PathType Leaf
    $arpSnapshot = Get-ArpSnapshot
    if ($oldTask) {
        $oldTaskXml = Export-ScheduledTask -TaskName $taskName -ErrorAction Stop
        [IO.File]::WriteAllText(
            $taskBackupPath,
            $oldTaskXml,
            [Text.UTF8Encoding]::new($false))
    }

    Copy-Item -LiteralPath $source -Destination $stagedExe -ErrorAction Stop
    Assert-LeafNotReparse $stagedExe
    if ((Get-FileHash -LiteralPath $stagedExe -Algorithm SHA256).Hash -cne $expectedHash) { exit 20 }
    if (-not (Test-PeExecutable -Path $stagedExe -Machine $expectedMachine)) { exit 21 }
    $uninstallBytes = [Convert]::FromBase64String($uninstallScriptBase64)
    $uninstallStream = [IO.File]::Open(
        $stagedUninstall,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try { $uninstallStream.Write($uninstallBytes, 0, $uninstallBytes.Length) }
    finally { $uninstallStream.Dispose() }
    Assert-LeafNotReparse $stagedUninstall

    $mutationStarted = $true
    if ($oldTask) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    }
    Stop-OwnedBrokerProcesses

    if ($hadOldExe) {
        [IO.File]::Replace($stagedExe, $targetExe, $backupExe, $true)
    } else {
        [IO.File]::Move($stagedExe, $targetExe)
    }
    $exeChanged = $true
    Unblock-File -LiteralPath $targetExe -ErrorAction SilentlyContinue
    Assert-LeafNotReparse $targetExe
    if ((Get-FileHash -LiteralPath $targetExe -Algorithm SHA256).Hash -cne $expectedHash) { throw 'Installed hash validation failed.' }
    if (-not (Test-PeExecutable -Path $targetExe -Machine $expectedMachine)) { throw 'Installed executable validation failed.' }

    $taskAction = New-ScheduledTaskAction -Execute $targetExe -WorkingDirectory $targetDirectory
    $taskTrigger = New-ScheduledTaskTrigger -AtLogOn
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -RunLevel Highest -LogonType ServiceAccount
    $taskSettings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask `
        -TaskName $taskName `
        -Action $taskAction `
        -Trigger $taskTrigger `
        -Principal $taskPrincipal `
        -Settings $taskSettings `
        -Description 'SysMonCmdPal Broker for elevated hardware sensor access' `
        -Force | Out-Null
    $registeredTask = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
    Assert-SystemTaskModel $registeredTask

    $healthStart = [DateTime]::UtcNow.AddSeconds(-2)
    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    $healthDeadline = [DateTime]::UtcNow.AddSeconds(30)
    $healthyProcess = $null
    while ([DateTime]::UtcNow -lt $healthDeadline -and -not $healthyProcess) {
        Start-Sleep -Milliseconds 250
        foreach ($candidate in @(Get-OwnedBrokerProcesses)) {
            try {
                if (-not $candidate.HasExited -and $candidate.StartTime.ToUniversalTime() -ge $healthStart) {
                    $healthyProcess = $candidate
                    break
                }
            } catch { }
        }
    }
    if (-not $healthyProcess) { throw 'The scheduled Broker process failed path validation.' }
    Assert-SystemTaskModel (Get-ScheduledTask -TaskName $taskName -ErrorAction Stop)

    if ($hadOldUninstall) {
        [IO.File]::Replace($stagedUninstall, $uninstallScript, $backupUninstall, $true)
    } else {
        [IO.File]::Move($stagedUninstall, $uninstallScript)
    }
    $uninstallChanged = $true
    Assert-LeafNotReparse $uninstallScript

    $arpChanged = $true
    Register-ArpEntry
    $transactionCommitted = $true
    exit 0
} catch {
    if ($mutationStarted) {
        $rollbackFailed = $false
        try {
            $currentTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            if ($currentTask) {
                Assert-ManagedTaskAction $currentTask $targetExe $targetDirectory
                Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            }
            Stop-OwnedBrokerProcesses
        } catch { $rollbackFailed = $true }
        try { Restore-ManagedFile $backupExe $targetExe $hadOldExe $exeChanged }
        catch { $rollbackFailed = $true }
        try { Restore-ManagedFile $backupUninstall $uninstallScript $hadOldUninstall $uninstallChanged }
        catch { $rollbackFailed = $true }
        if ($arpChanged) {
            try { Restore-ArpSnapshot $arpSnapshot }
            catch { $rollbackFailed = $true }
        }
        try { Restore-Task }
        catch { $rollbackFailed = $true }
        if ($oldTaskWasRunning -and $oldTaskXml) {
            try { Start-ScheduledTask -TaskName $taskName -ErrorAction Stop }
            catch { $rollbackFailed = $true }
        }
        $preserveBackups = $rollbackFailed
    }
    exit 30
} finally {
    foreach ($temporaryPath in @($stagedExe, $stagedUninstall)) {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
    }
    if (-not $preserveBackups) {
        foreach ($backupPath in @($backupExe, $backupUninstall, $taskBackupPath)) {
            if (Test-Path -LiteralPath $backupPath) {
                Remove-Item -LiteralPath $backupPath -Force -ErrorAction SilentlyContinue
            }
        }
    }
    if ($maintenanceMutexAcquired) {
        try { $maintenanceMutex.ReleaseMutex() } catch { }
    }
    if ($maintenanceMutex) { $maintenanceMutex.Dispose() }
}
""";
}
