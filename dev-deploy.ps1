# SysMonCmdPal 开发部署脚本 — 松散源注册（Loose Registration）
# 不打 MSIX 包，直接从 bin 目录注册，秒级部署
#
# 用法:
#   .\dev-deploy.ps1              # 构建 + 松散注册
#   .\dev-deploy.ps1 -SkipBuild   # 跳过构建，直接注册已有产物
#   .\dev-deploy.ps1 -Uninstall   # 卸载

param(
    [switch]$SkipBuild,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$PackageFamilyName = "darkstax.SysPulseforCommandPalette"
$BinDir = Join-Path $ProjectRoot "SysMonCmdPal\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64"
$ManifestPath = Join-Path $BinDir "AppxManifest.xml"

function Log($msg) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $msg" -ForegroundColor Cyan
}

# ── Uninstall ─────────────────────────────────────────────────

if ($Uninstall) {
    Log "=== 卸载松散源扩展 ==="
    $pkg = Get-AppxPackage -Name $PackageFamilyName -ErrorAction SilentlyContinue
    if ($pkg) {
        Remove-AppxPackage $pkg.PackageFullName -ErrorAction SilentlyContinue
        Log "已移除: $($pkg.PackageFullName)"
    } else {
        Log "未安装"
    }
    # 清理 CmdPal 缓存
    $cacheDir = "$env:LOCALAPPDATA\Microsoft\PowerToys\CmdPal"
    if (Test-Path $cacheDir) {
        Remove-Item $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
        Log "已清理 CmdPal 缓存"
    }
    exit 0
}

# ── Build ─────────────────────────────────────────────────────

if (-not $SkipBuild) {
    Log "=== 构建 (Debug/x64) ==="

    # 优先用 MSBuild（支持 C++ 依赖项目），回退到 dotnet build
    $msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    $mainProj = Join-Path $ProjectRoot "SysMonCmdPal\SysMonCmdPal.csproj"

    if (Test-Path $msbuild) {
        Log "使用 MSBuild..."
        & $msbuild $mainProj /p:Configuration=Debug /p:Platform=x64 `
            /p:VcpkgEnabled=false /p:EnforceCodeStyleInBuild=false `
            /p:RunAnalyzers=false /p:TreatWarningsAsErrors=false `
            /t:Build /v:minimal 2>&1 |
            Where-Object { $_ -match "error " } |
            ForEach-Object { Log "  ERROR: $_" }
    } else {
        Log "使用 dotnet build..."
        & dotnet build $mainProj -c Debug -p:Platform=x64 `
            /p:VcpkgEnabled=false /p:EnforceCodeStyleInBuild=false `
            /p:RunAnalyzers=false /p:TreatWarningsAsErrors=false `
            --nologo -v q 2>&1 |
            Where-Object { $_ -match "error CS" } |
            ForEach-Object { Log "  $_" }
    }

    if ($LASTEXITCODE -ne 0) {
        Log "FATAL: 构建失败 (exit $LASTEXITCODE)"
        exit 1
    }
    Log "构建完成"
}

# ── Loose Register ────────────────────────────────────────────

Log "=== 松散源注册 ==="

if (-not (Test-Path $ManifestPath)) {
    Log "FATAL: 找不到 AppxManifest.xml"
    Log "  预期路径: $ManifestPath"
    Log "  请先构建项目"
    exit 1
}

# 卸载旧版本（无论是 MSIX 包还是松散注册）
$old = Get-AppxPackage -Name $PackageFamilyName -ErrorAction SilentlyContinue
if ($old) {
    Log "移除旧注册: $($old.PackageFullName)"
    Remove-AppxPackage $old.PackageFullName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
}

# 清理 CmdPal 缓存
$cacheDir = "$env:LOCALAPPDATA\Microsoft\PowerToys\CmdPal"
if (Test-Path $cacheDir) {
    Remove-Item $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
}

# 复制 Assets 到 bin 目录（松散注册需要图标文件在 bin 下）
$assetsSrc = Join-Path $ProjectRoot "SysMonCmdPal\Assets"
$assetsDst = Join-Path $BinDir "Assets"
if ((Test-Path $assetsSrc) -and -not (Test-Path $assetsDst)) {
    Log "复制 Assets 到 bin 目录..."
    Copy-Item $assetsSrc $assetsDst -Recurse -Force
}

# 松散源注册 — 不需要 MSIX 打包，不需要证书
Log "注册: $ManifestPath"
Add-AppxPackage -Register $ManifestPath -ErrorAction Stop
Log "松散源注册成功"

# 验证
$pkg = Get-AppxPackage -Name $PackageFamilyName -ErrorAction SilentlyContinue
if ($pkg) {
    Log "已注册: $($pkg.PackageFullName)"
    Log "安装位置: $($pkg.InstallLocation)"
}

Log ""
Log "========================================"
Log " 开发部署完成!"
Log "========================================"
Log ""
Log "Win+Alt+T 打开 Command Palette, 搜索 'System Monitor'"
Log "重新部署: .\dev-deploy.ps1"
Log "卸载:     .\dev-deploy.ps1 -Uninstall"
Log ""
