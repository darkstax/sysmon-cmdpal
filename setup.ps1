# SysMonCmdPal Setup Script
# 轻量获取 PowerToys 扩展 SDK（sparse checkout，只拉 ~0.6MB）

param(
    [string]$SdkPath = "..\PowerToys-sdk",
    [string]$Branch = "main"
)

$ErrorActionPreference = "Stop"

Write-Host "=== SysMonCmdPal Setup ===" -ForegroundColor Cyan
Write-Host ""

# Check prerequisites
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "[ERROR] .NET SDK not found. Install from https://dotnet.microsoft.com/download" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] .NET SDK $(dotnet --version)" -ForegroundColor Green

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host "[ERROR] Git not found" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] Git $(git --version)" -ForegroundColor Green

# Setup PowerToys SDK via sparse checkout
$absSdkPath = Join-Path $PSScriptRoot $SdkPath | Resolve-Path -ErrorAction SilentlyContinue
if (-not $absSdkPath) {
    $absSdkPath = Join-Path $PSScriptRoot $SdkPath
}

if (Test-Path (Join-Path $absSdkPath "src\modules\cmdpal\extensionsdk\Microsoft.CommandPalette.Extensions.Toolkit")) {
    Write-Host "[OK] PowerToys SDK found at: $absSdkPath" -ForegroundColor Green
} else {
    Write-Host "[INFO] Fetching PowerToys SDK via sparse checkout (~2MB download)..." -ForegroundColor Yellow
    Write-Host "       Only extensionsdk + build props are pulled, not the full 2GB repo." -ForegroundColor DarkGray

    if (Test-Path $absSdkPath) {
        Remove-Item -Recurse -Force $absSdkPath
    }

    # Blobless clone (metadata only, no file content yet)
    git clone --filter=blob:none --no-checkout --depth 1 --branch $Branch `
        https://github.com/microsoft/PowerToys.git $absSdkPath

    Push-Location $absSdkPath

    # Sparse checkout: only pull these paths
    git sparse-checkout init --no-cone
    git sparse-checkout set `
        'src/modules/cmdpal/extensionsdk/*' `
        'src/Common.Dotnet.CsWinRT.props' `
        'src/Common.Dotnet.AotCompatibility.props'

    git checkout
    Pop-Location

    $sizeMB = [math]::Round(((Get-ChildItem $absSdkPath -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 2)
    Write-Host "[OK] PowerToys SDK ready ($sizeMB MB)" -ForegroundColor Green
}

Write-Host ""
Write-Host "=== Setup complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Open SysMonCmdPal.sln in Visual Studio 2022" -ForegroundColor White
Write-Host "     (requires: .NET desktop development + Desktop C++ workloads)" -ForegroundColor DarkGray
Write-Host "  2. Build -> Deploy SysMonCmdPal" -ForegroundColor White
Write-Host "  3. In Command Palette, run 'Reload Command Palette extensions'" -ForegroundColor White
Write-Host ""
Write-Host "Note: Building the SDK requires the C++ toolchain (vcxproj -> .winmd)." -ForegroundColor Yellow
Write-Host "      Install 'Desktop development with C++' in Visual Studio Installer if needed." -ForegroundColor Yellow
Write-Host ""
Write-Host "Set env var POWERTOYS_REPO to override SDK path:" -ForegroundColor DarkGray
Write-Host "  `$env:POWERTOYS_REPO = 'D:\src\PowerToys-sdk'" -ForegroundColor DarkGray
