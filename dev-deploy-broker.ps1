param(
    [Parameter(Mandatory = $true)]
    [string]$Source
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

$TaskName = "SysMonBroker"
$TargetRoot = Join-Path $env:ProgramFiles "SysMonCmdPal"
$TargetDirectory = Join-Path $TargetRoot "Broker"
$TargetExe = Join-Path $TargetDirectory "SysMonBroker.exe"
$BackupExe = Join-Path $TargetDirectory "SysMonBroker.exe.dev-backup"
$StagedExe = Join-Path $TargetDirectory "SysMonBroker.exe.dev-new"

function Log([string]$Message) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $Message" -ForegroundColor Cyan
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-IsSystemPrincipal([string]$UserId) {
    try {
        $sid = if ($UserId -match "^S-1-") {
            [Security.Principal.SecurityIdentifier]::new($UserId)
        } else {
            [Security.Principal.NTAccount]::new($UserId).Translate(
                [Security.Principal.SecurityIdentifier])
        }
        return $sid.Value -eq "S-1-5-18"
    } catch {
        return $false
    }
}

function Test-PathEquals([string]$Left, [string]$Right) {
    try {
        return [string]::Equals(
            [IO.Path]::GetFullPath($Left.Trim().Trim('"')),
            [IO.Path]::GetFullPath($Right.Trim().Trim('"')),
            [StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Get-OwnedBrokerProcesses {
    foreach ($process in @(Get-Process -Name "SysMonBroker" -ErrorAction SilentlyContinue)) {
        $processPath = $null
        try { $processPath = $process.Path } catch { }
        if ($processPath -and (Test-PathEquals $processPath $TargetExe)) {
            $process
        }
    }
}

function Stop-OwnedBrokerProcesses {
    foreach ($process in @(Get-OwnedBrokerProcesses)) {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
        try { $process.WaitForExit(5000) | Out-Null } catch { }
    }
}

function Set-BrokerAcl {
    $icacls = Join-Path $env:SystemRoot "System32\icacls.exe"
    & $icacls $TargetRoot "/setowner" "*S-1-5-32-544" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to restore ownership of the Broker directory."
    }

    & $icacls $TargetRoot "/inheritance:r" "/grant:r" `
        "*S-1-5-18:(OI)(CI)F" `
        "*S-1-5-32-544:(OI)(CI)F" `
        "*S-1-5-32-545:(OI)(CI)RX" | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to apply the Broker directory ACL."
    }

    $children = Join-Path $TargetRoot "*"
    if (Test-Path $children) {
        & $icacls $children "/reset" "/T" "/C" | Out-Null
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to restore inherited Broker file ACLs."
        }
    }
}

function Wait-BrokerProcess([DateTime]$Deadline) {
    while ([DateTime]::UtcNow -lt $Deadline) {
        $process = @(Get-OwnedBrokerProcesses) | Select-Object -First 1
        if ($process) { return }
        Start-Sleep -Milliseconds 250
    }

    throw "The development Broker process did not start from the managed path."
}

function Wait-BrokerSharedMemoryHealthy([DateTime]$Deadline) {
    $baseline = $null
    while ([DateTime]::UtcNow -lt $Deadline) {
        $mmf = $null
        $accessor = $null
        try {
            $mmf = [IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting(
                "Global\SysMonBrokerShm",
                [IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
            $accessor = $mmf.CreateViewAccessor(
                0,
                16384,
                [IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)

            $sequenceBefore = $accessor.ReadInt32(12)
            if (($sequenceBefore -band 1) -eq 0) {
                $magic = $accessor.ReadInt32(0)
                $version = $accessor.ReadInt32(4)
                $counter = $accessor.ReadInt32(8)
                $extensionMagic = $accessor.ReadInt32(16364)
                $instanceId = $accessor.ReadUInt64(16368)
                $publishMs = $accessor.ReadInt64(16376)
                $sequenceAfter = $accessor.ReadInt32(12)

                $valid = $sequenceBefore -eq $sequenceAfter -and
                    ($sequenceAfter -band 1) -eq 0 -and
                    $magic -eq 0x5342524B -and
                    $version -eq 2 -and
                    $extensionMagic -eq 0x31584D53 -and
                    $instanceId -ne 0 -and
                    $publishMs -gt 0

                if ($valid) {
                    if ($baseline -and
                        $baseline.InstanceId -eq $instanceId -and
                        $baseline.Counter -ne $counter -and
                        $publishMs -gt $baseline.PublishMs) {
                        return
                    }

                    $baseline = [PSCustomObject]@{
                        InstanceId = $instanceId
                        Counter = $counter
                        PublishMs = $publishMs
                    }
                }
            }
        } catch [IO.FileNotFoundException] {
            $baseline = $null
        } finally {
            if ($accessor) { $accessor.Dispose() }
            if ($mmf) { $mmf.Dispose() }
        }

        Start-Sleep -Milliseconds 250
    }

    throw "The development Broker did not publish advancing SMX1 data."
}

if (-not (Test-IsAdministrator)) {
    throw "dev-deploy-broker.ps1 must run elevated."
}

$Source = [IO.Path]::GetFullPath($Source)
if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
    throw "Broker source executable was not found."
}

$sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash
$oldTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
$oldTaskXml = if ($oldTask) { Export-ScheduledTask -TaskName $TaskName } else { $null }
$oldTaskWasRunning = $oldTask -and $oldTask.State -eq "Running"
$hadOldExe = Test-Path -LiteralPath $TargetExe -PathType Leaf
$deploymentCommitted = $false

try {
    Log "Deploying development Broker as SYSTEM task"
    if ($oldTask) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    }
    Stop-OwnedBrokerProcesses

    New-Item -ItemType Directory -Path $TargetDirectory -Force | Out-Null
    Set-BrokerAcl
    Remove-Item -LiteralPath $StagedExe -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $BackupExe -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $Source -Destination $StagedExe -Force
    Unblock-File -LiteralPath $StagedExe -ErrorAction SilentlyContinue

    if ($hadOldExe) {
        [IO.File]::Replace($StagedExe, $TargetExe, $BackupExe, $true)
    } else {
        [IO.File]::Move($StagedExe, $TargetExe)
    }

    if ((Get-FileHash -LiteralPath $TargetExe -Algorithm SHA256).Hash -cne $sourceHash) {
        throw "Broker hash validation failed after deployment."
    }
    Set-BrokerAcl

    $action = New-ScheduledTaskAction -Execute $TargetExe -WorkingDirectory $TargetDirectory
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest -LogonType ServiceAccount
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $action `
        -Trigger $trigger `
        -Principal $principal `
        -Settings $settings `
        -Description "SysMonCmdPal development Broker" `
        -Force | Out-Null

    $registeredTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    if (-not (Test-IsSystemPrincipal ([string]$registeredTask.Principal.UserId)) -or
        $registeredTask.Principal.RunLevel.ToString() -ne "Highest" -or
        $registeredTask.Principal.LogonType.ToString() -ne "ServiceAccount") {
        throw "The development Broker task principal validation failed."
    }

    Start-ScheduledTask -TaskName $TaskName -ErrorAction Stop
    Wait-BrokerProcess ([DateTime]::UtcNow.AddSeconds(30))
    Wait-BrokerSharedMemoryHealthy ([DateTime]::UtcNow.AddSeconds(30))

    $deploymentCommitted = $true
    Remove-Item -LiteralPath $BackupExe -Force -ErrorAction SilentlyContinue
    Log "Development Broker deployed and SMX1 commits verified"
} finally {
    if (-not $deploymentCommitted) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Stop-OwnedBrokerProcesses
        Set-BrokerAcl

        if ($hadOldExe -and (Test-Path -LiteralPath $BackupExe)) {
            Copy-Item -LiteralPath $BackupExe -Destination $TargetExe -Force
        } elseif (-not $hadOldExe) {
            Remove-Item -LiteralPath $TargetExe -Force -ErrorAction SilentlyContinue
        }

        if ($oldTaskXml) {
            Register-ScheduledTask -TaskName $TaskName -Xml $oldTaskXml -Force | Out-Null
            if ($oldTaskWasRunning) {
                Start-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            }
        } else {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        }
    }

    Remove-Item -LiteralPath $StagedExe -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $BackupExe -Force -ErrorAction SilentlyContinue
}
