$PackageName = "darkstax.SysPulseforCommandPalette"
$TaskName = "SysMonBroker"
$BrokerExe = Join-Path $env:ProgramFiles "SysMonCmdPal\Broker\SysMonBroker.exe"

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
