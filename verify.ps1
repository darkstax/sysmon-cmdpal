Write-Host "=== Package Count Check ===" -ForegroundColor Cyan
$pkgs = Get-AppxPackage -Name '*SysMonCmdPal*'
Write-Host "Installed packages: $($pkgs.Count)" -ForegroundColor $(if ($pkgs.Count -eq 1) { 'Green' } else { 'Red' })
foreach ($p in $pkgs) {
    Write-Host "  $($p.PackageFullName)"
}

Write-Host ""
Write-Host "=== Extension Registration ===" -ForegroundColor Cyan
$ext = Get-AppxPackage -Name '*SysMonCmdPal*' | Select-Object -First 1
if ($ext) {
    $manifest = Get-AppxPackageManifest -Package $ext.PackageFullName
    $extensions = $manifest.Package.Applications.Application.Extensions
    if ($extensions) {
        foreach ($ext2 in $extensions.Extension) {
            Write-Host "  Extension: $($ext2.Category) / $($ext2.Name)" -ForegroundColor Green
        }
    }
}

Write-Host ""
Write-Host "=== Broker Install Location ===" -ForegroundColor Cyan
$installLoc = (Get-AppxPackage -Name '*SysMonCmdPal*' | Select-Object -First 1).InstallLocation
$brokerFiles = Get-ChildItem (Join-Path $installLoc "Broker") -ErrorAction SilentlyContinue
foreach ($f in $brokerFiles) {
    Write-Host "  $($f.Name) ($([math]::Round($f.Length/1KB,1)) KB)" -ForegroundColor White
}
