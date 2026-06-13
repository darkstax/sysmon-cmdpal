# SysMonBroker Setup Script
# Install/Uninstall high-precision broker service
param(
    [ValidateSet("Install", "Uninstall")]
    [string]$Action = "Install",
    [string]$SourceDir = ""
)

$ErrorActionPreference = "Stop"
$TargetDir = Join-Path $env:ProgramData "SysMonCmdPal"
$BrokerExe = Join-Path $TargetDir "SysMonBroker.exe"
$TaskName = "SysMonBroker"
$LogFile = Join-Path $env:ProgramData "broker_setup.log"

function Log($msg) {
    $line = "$(Get-Date -Format 'HH:mm:ss.fff') $msg"
    try { Add-Content $LogFile $line -ErrorAction SilentlyContinue } catch {}
    Write-Host $line
}

try {
    Log "=== setup-broker.ps1 started: Action=$Action SourceDir=$SourceDir ==="
    Log "User=$env:USERNAME Elevated=$([Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"

    if ($Action -eq "Install") {
        $srcExe = Join-Path $SourceDir "SysMonBroker.exe"
        if (-not (Test-Path $srcExe)) {
            Log "FATAL: SysMonBroker.exe not found in $SourceDir"
            exit 1
        }

        if (-not (Test-Path $TargetDir)) {
            New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
            Log "Created directory: $TargetDir"
        }

        # Stop existing
        Get-Process SysMonBroker -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        $et = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($et) {
            Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
            Log "Removed existing task"
        }

        # Remove old exe then copy new
        if (Test-Path $BrokerExe) {
            Remove-Item $BrokerExe -Force -ErrorAction Stop
            Log "Removed old exe"
        }
        Copy-Item $srcExe $BrokerExe -ErrorAction Stop
        Log "Copied SysMonBroker.exe ($([math]::Round((Get-Item $BrokerExe).Length/1MB,1)) MB)"

        # Register task
        $a = New-ScheduledTaskAction -Execute $BrokerExe -WorkingDirectory $TargetDir
        $t = New-ScheduledTaskTrigger -AtLogOn
        $p = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive
        $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
        Register-ScheduledTask -TaskName $TaskName -Action $a -Trigger $t -Principal $p -Settings $s -Description "SysMonCmdPal broker" -Force | Out-Null
        Log "Task registered"

        # Start
        Start-ScheduledTask -TaskName $TaskName
        Start-Sleep -Seconds 3
        $state = (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue).State
        Log "Task state: $state"
        $proc = Get-Process SysMonBroker -ErrorAction SilentlyContinue
        if ($proc) { Log "Process running: PID $($proc.Id)" } else { Log "WARNING: Process not running" }
        Log "=== Install complete ==="

    } elseif ($Action -eq "Uninstall") {
        $et = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($et) {
            Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
            Log "Task removed"
        }
        Get-Process SysMonBroker -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        if (Test-Path $BrokerExe) { Remove-Item $BrokerExe -Force -ErrorAction SilentlyContinue }
        Log "=== Uninstall complete ==="
    }

    exit 0
} catch {
    Log "ERROR: $($_.Exception.Message)"
    Log "STACK: $($_.ScriptStackTrace)"
    exit 1
}
