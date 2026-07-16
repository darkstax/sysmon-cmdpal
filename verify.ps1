[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

$PackageName = "darkstax.SysPulseforCommandPalette"
$TaskName = "SysMonBroker"
$BrokerExe = Join-Path $env:ProgramFiles "SysMonCmdPal\Broker\SysMonBroker.exe"

function Format-TaskResult {
    param([long]$Result)

    $unsignedResult = [uint32]$Result

    $label = switch ($unsignedResult) {
        0 { "Success"; break }
        267008 { "Ready"; break }      # 0x41300
        267009 { "Running"; break }    # 0x41301
        267010 { "Disabled"; break }   # 0x41302
        267011 { "Has not run"; break } # 0x41303
        267012 { "No more runs"; break } # 0x41304
        267013 { "No trigger"; break } # 0x41305
        267014 { "Terminated"; break } # 0x41306
        267015 { "No valid triggers"; break } # 0x41307
        267016 { "Event triggers unavailable"; break } # 0x41308
        default { "Unknown" }
    }

    return ("{0} (0x{1:X8}, {2})" -f $Result, $unsignedResult, $label)
}

Write-Host "=== Package Count Check ===" -ForegroundColor Cyan
$pkgs = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue)
Write-Host "Installed packages: $($pkgs.Count)" -ForegroundColor $(if ($pkgs.Count -eq 1) { 'Green' } else { 'Red' })
foreach ($p in $pkgs) {
    Write-Host "  $($p.PackageFullName)"
}

Write-Host ""
Write-Host "=== Extension Registration ===" -ForegroundColor Cyan
$pkg = $pkgs | Select-Object -First 1
if ($pkg) {
    $manifest = Get-AppxPackageManifest -Package $pkg.PackageFullName
    $extensions = $manifest.Package.Applications.Application.Extensions
    if ($extensions) {
        foreach ($ext in $extensions.Extension) {
            Write-Host "  Extension: $($ext.Category) / $($ext.Name)" -ForegroundColor Green
        }
    }
}

Write-Host ""
Write-Host "=== Broker Install Location ===" -ForegroundColor Cyan
if (Test-Path $BrokerExe) {
    $f = Get-Item $BrokerExe
    Write-Host "  $($f.FullName) ($([math]::Round($f.Length/1KB,1)) KB)" -ForegroundColor Green
} else {
    Write-Host "  Missing: $BrokerExe" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Broker Scheduled Task ===" -ForegroundColor Cyan
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) {
    Write-Host "  Task: $TaskName" -ForegroundColor Green
    foreach ($a in $task.Actions) {
        Write-Host "  Execute: $($a.Execute)"
        Write-Host "  WorkingDirectory: $($a.WorkingDirectory)"
    }
    Write-Host "  Principal: $($task.Principal.UserId)"
    Write-Host "  LogonType: $($task.Principal.LogonType)"
    Write-Host "  RunLevel: $($task.Principal.RunLevel)"

    if ($task.Actions[0].Execute -ieq $BrokerExe) {
        Write-Host "  Broker path OK" -ForegroundColor Green
    } else {
        Write-Host "  Broker path mismatch" -ForegroundColor Red
    }
} else {
    Write-Host "  Task not found" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== Broker ACL ===" -ForegroundColor Cyan
$brokerDir = Split-Path $BrokerExe -Parent
if (Test-Path $brokerDir) {
    & icacls $brokerDir
} else {
    Write-Host "  Broker directory not found" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== Broker Runtime Diagnostics ===" -ForegroundColor Cyan
$brokerProcesses = @(Get-Process -Name "SysMonBroker" -ErrorAction SilentlyContinue)
if ($brokerProcesses.Count -gt 0) {
    foreach ($p in $brokerProcesses) {
        Write-Host "  Process: PID=$($p.Id) Path=$($p.Path)" -ForegroundColor Green
    }
} else {
    Write-Host "  Process not running" -ForegroundColor Yellow
}

try {
    $taskInfo = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction Stop
    Write-Host "  Last task run: $($taskInfo.LastRunTime)"
    Write-Host "  Last task result: $(Format-TaskResult $taskInfo.LastTaskResult)"
    Write-Host "  Next task run: $($taskInfo.NextRunTime)"
} catch {
    Write-Host "  Task runtime info unavailable: $($_.Exception.Message)" -ForegroundColor Yellow
}

$logPath = Join-Path $env:ProgramData "SysMonCmdPal\Logs\broker.log"
if (Test-Path $logPath) {
    Write-Host "  Broker log tail: $logPath"
    Get-Content $logPath -Tail 12 | ForEach-Object { Write-Host "    $_" }
} else {
    Write-Host "  Broker log not found: $logPath" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== Broker Shared Memory Header ===" -ForegroundColor Cyan
try {
    Add-Type -AssemblyName System.IO.MemoryMappedFiles
    $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting(
        "Global\SysMonBrokerShm",
        [System.IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
    try {
        $accessor = $mmf.CreateViewAccessor(0, 16384, [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
        try {
            $magic = $accessor.ReadInt32(0)
            $version = $accessor.ReadInt32(4)
            $counter = $accessor.ReadInt32(8)
            $sequence = $accessor.ReadInt32(12)
            $sensorCount = $accessor.ReadInt32(360)
            $extensionMagic = $accessor.ReadInt32(16364)
            $instanceId = $accessor.ReadUInt64(16368)
            $publishMs = $accessor.ReadInt64(16376)
            Write-Host ("  Magic: 0x{0:X8}" -f $magic) -ForegroundColor $(if ($magic -eq 0x5342524B) { "Green" } else { "Red" })
            Write-Host "  Version: $version"
            Write-Host "  Counter: $counter"
            Write-Host "  Sensor count: $sensorCount"
            Write-Host "  Commit sequence: $sequence" -ForegroundColor $(if (($sequence -band 1) -eq 0) { "Green" } else { "Red" })
            Write-Host ("  Extension: 0x{0:X8}" -f $extensionMagic) -ForegroundColor $(if ($extensionMagic -eq 0x31584D53) { "Green" } else { "Yellow" })

            if ($extensionMagic -eq 0x31584D53) {
                Write-Host ("  Broker instance: 0x{0:X16}" -f $instanceId)
                Write-Host "  Monotonic publish ms: $publishMs"
                Write-Host "  Waiting 3 seconds to verify live commits..."
                Start-Sleep -Seconds 3

                $sequenceAfter = $accessor.ReadInt32(12)
                $counterAfter = $accessor.ReadInt32(8)
                $instanceAfter = $accessor.ReadUInt64(16368)
                $publishAfter = $accessor.ReadInt64(16376)
                $counterAdvanced = $counterAfter -ne $counter
                $publishAdvanced = $publishAfter -gt $publishMs
                $instanceStable = $instanceAfter -eq $instanceId
                $sequenceCommitted = ($sequenceAfter -band 1) -eq 0

                Write-Host "  Counter after wait: $counterAfter" -ForegroundColor $(if ($counterAdvanced) { "Green" } else { "Red" })
                Write-Host "  Commit sequence after wait: $sequenceAfter" -ForegroundColor $(if ($sequenceCommitted) { "Green" } else { "Red" })
                Write-Host "  Instance stable: $instanceStable" -ForegroundColor $(if ($instanceStable) { "Green" } else { "Red" })
                Write-Host "  Publish time advanced: $publishAdvanced" -ForegroundColor $(if ($publishAdvanced) { "Green" } else { "Red" })
            } else {
                Write-Host "  Legacy Broker protocol detected; stable-commit verification unavailable" -ForegroundColor Yellow
            }
        } finally {
            $accessor.Dispose()
        }
    } finally {
        $mmf.Dispose()
    }
} catch [System.IO.FileNotFoundException] {
    Write-Host "  Shared memory not found" -ForegroundColor Yellow
} catch {
    Write-Host "  Shared memory read failed: $($_.Exception.Message)" -ForegroundColor Red
}
