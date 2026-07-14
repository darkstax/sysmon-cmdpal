[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

$PackageName = "darkstax.SysPulseforCommandPalette"
$TaskName = "SysMonBroker"
$BrokerExe = Join-Path $env:ProgramFiles "SysMonCmdPal\Broker\SysMonBroker.exe"

function Format-TaskResult {
    param([int]$Result)

    $label = switch ($Result) {
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

    return ("{0} (0x{0:X8}, {1})" -f $Result, $label)
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
        $accessor = $mmf.CreateViewAccessor(0, 384, [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
        try {
            $magic = $accessor.ReadInt32(0)
            $version = $accessor.ReadInt32(4)
            $counter = $accessor.ReadInt32(8)
            $sensorCount = $accessor.ReadInt32(360)
            Write-Host ("  Magic: 0x{0:X8}" -f $magic) -ForegroundColor $(if ($magic -eq 0x5342524B) { "Green" } else { "Red" })
            Write-Host "  Version: $version"
            Write-Host "  Counter: $counter"
            Write-Host "  Sensor count: $sensorCount"
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
