# SysMonCmdPal Clean Deploy Script
# 清理旧的 MSIX 包和缓存，然后重新安装
# 用法: powershell -ExecutionPolicy Bypass -File deploy.ps1

$ErrorActionPreference = "Stop"

Write-Host "=== SysMonCmdPal Clean Deploy ===" -ForegroundColor Cyan
Write-Host ""

# Step 1: Remove all existing SysMonCmdPal packages
Write-Host "[1/4] Removing old packages..." -ForegroundColor Yellow
$oldPkgs = Get-AppxPackage -Name '*SysMonCmdPal*' -ErrorAction SilentlyContinue
if ($oldPkgs) {
    foreach ($pkg in $oldPkgs) {
        Write-Host "  Removing: $($pkg.PackageFullName)"
        Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction SilentlyContinue
    }
    Write-Host "  Done." -ForegroundColor Green
} else {
    Write-Host "  No old packages found." -ForegroundColor DarkGray
}

# Step 2: Clean PowerToys CmdPal extension cache
Write-Host "[2/4] Cleaning CmdPal cache..." -ForegroundColor Yellow
$cachePaths = @(
    "$env:LOCALAPPDATA\Microsoft\PowerToys\CmdPal"
)
foreach ($cp in $cachePaths) {
    if (Test-Path $cp) {
        Write-Host "  Cleaning: $cp"
        Remove-Item $cp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host "  Done." -ForegroundColor Green

# Step 3: Find and install latest package
Write-Host "[3/4] Installing latest package..." -ForegroundColor Yellow

# Search for the latest AppPackage in build output
$buildDir = Join-Path $PSScriptRoot "SysMonCmdPal\bin"
$searchPaths = @(
    (Join-Path $buildDir "x64\Release\net10.0-windows10.0.26100.0\win-x64\AppPackages"),
    (Join-Path $buildDir "x64\Debug\net10.0-windows10.0.26100.0\win-x64\AppPackages")
)

$msixFile = $null
foreach ($sp in $searchPaths) {
    if (Test-Path $sp) {
        $found = Get-ChildItem $sp -Filter "*.msix" -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($found) {
            $msixFile = $found
            break
        }
    }
}

if (-not $msixFile) {
    Write-Host "  [ERROR] No .msix file found. Build the project first!" -ForegroundColor Red
    Write-Host "  Run: dotnet build SysMonCmdPal\SysMonCmdPal.csproj -c Release" -ForegroundColor DarkGray
    exit 1
}

Write-Host "  Found: $($msixFile.FullName)"
Write-Host "  Size: $([math]::Round($msixFile.Length / 1MB, 1)) MB"

# Install the package
Add-AppxPackage -Path $msixFile.FullName -ErrorAction Stop
Write-Host "  Installed successfully." -ForegroundColor Green

# Step 4: Verify installation
Write-Host "[4/4] Verifying..." -ForegroundColor Yellow
$newPkg = Get-AppxPackage -Name '*SysMonCmdPal*' | Select-Object -First 1
if ($newPkg) {
    Write-Host "  Package: $($newPkg.PackageFullName)" -ForegroundColor Green
    Write-Host "  Version: $($newPkg.Version)" -ForegroundColor Green
    
    # Check Public folder exists in installed package
    $publicPath = Join-Path $newPkg.InstallLocation "Public"
    if (Test-Path $publicPath) {
        Write-Host "  Public folder: OK" -ForegroundColor Green
    } else {
        Write-Host "  Public folder: MISSING" -ForegroundColor Red
    }

    # Check Broker files
    $brokerDir = Join-Path $newPkg.InstallLocation "Broker"
    if (Test-Path $brokerDir) {
        Write-Host "  Broker folder: OK" -ForegroundColor Green
        $brokerExe = Join-Path $brokerDir "SysMonBroker.exe"
        $brokerScript = Join-Path $brokerDir "setup-broker.ps1"
        if (Test-Path $brokerExe) {
            $size = [math]::Round((Get-Item $brokerExe).Length / 1MB, 1)
            Write-Host "    SysMonBroker.exe: OK ($size MB)" -ForegroundColor Green
        } else {
            Write-Host "    SysMonBroker.exe: MISSING" -ForegroundColor Red
        }
        if (Test-Path $brokerScript) {
            Write-Host "    setup-broker.ps1: OK" -ForegroundColor Green
        } else {
            Write-Host "    setup-broker.ps1: MISSING" -ForegroundColor Red
        }
    } else {
        Write-Host "  Broker folder: MISSING" -ForegroundColor Red
    }
} else {
    Write-Host "  [ERROR] Package not found after install!" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "=== Deploy complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next: restart PowerToys or run 'Reload Command Palette extensions'" -ForegroundColor White
Write-Host "      in Command Palette to refresh the extension list." -ForegroundColor DarkGray
