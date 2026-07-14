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
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$ProjectRoot = $PSScriptRoot
$PackageName = "darkstax.SysPulseforCommandPalette"
$BuildOutputDir = Join-Path $ProjectRoot "SysMonCmdPal\bin\x64\Debug\net10.0-windows10.0.26100.0\win-x64"
$LooseRoot = Join-Path $env:LOCALAPPDATA "SysMonCmdPal\Loose"
$BinDir = Join-Path $LooseRoot "win-x64"
$ManifestPath = Join-Path $BinDir "AppxManifest.xml"

function Log($msg) {
    Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $msg" -ForegroundColor Cyan
}

function Get-MSBuildPath {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )
    foreach ($p in $candidates) {
        if (Test-Path $p) { return $p }
    }
    throw "找不到 MSBuild"
}

function Get-AppxMSBuildToolsPath {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Microsoft\VisualStudio\v18.0\AppxPackage\",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\"
    )
    foreach ($p in $candidates) {
        if (Test-Path $p) { return $p }
    }
    return $null
}

# ── Uninstall ─────────────────────────────────────────────────

if ($Uninstall) {
    Log "=== 卸载松散源扩展 ==="
    $pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
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

    $msbuild = Get-MSBuildPath
    $appxTools = Get-AppxMSBuildToolsPath
    $mainProj = Join-Path $ProjectRoot "SysMonCmdPal\SysMonCmdPal.csproj"
    $args = @(
        $mainProj,
        "/restore",
        "/t:Build",
        "/p:Configuration=Debug",
        "/p:Platform=x64",
        "/p:VcpkgEnabled=false",
        "/p:EnforceCodeStyleInBuild=false",
        "/p:RunAnalyzers=false",
        "/p:TreatWarningsAsErrors=false",
        "/v:minimal"
    )
    if ($appxTools) {
        $args += "/p:AppxMSBuildToolsPath=$appxTools"
    }

    Log "使用 MSBuild: $msbuild"
    & $msbuild @args

    if ($LASTEXITCODE -ne 0) {
        Log "FATAL: 构建失败 (exit $LASTEXITCODE)"
        exit 1
    }
    Log "构建完成"
}

# ── Loose Register ────────────────────────────────────────────

Log "=== 松散源注册 ==="

if (-not (Test-Path (Join-Path $BuildOutputDir "AppxManifest.xml"))) {
    Log "FATAL: 找不到 AppxManifest.xml"
    Log "  预期路径: $(Join-Path $BuildOutputDir "AppxManifest.xml")"
    Log "  请先构建项目"
    exit 1
}

# 卸载旧版本（无论是 MSIX 包还是松散注册）
$old = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
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

# 复制到本地 NTFS 目录后注册。Windows 不支持从 WSL 9P/\\wsl.localhost loose register。
if (Test-Path $BinDir) {
    Remove-Item $BinDir -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $BinDir -Force | Out-Null
Log "复制 loose registration 文件到本地: $BinDir"
Copy-Item (Join-Path $BuildOutputDir "*") $BinDir -Recurse -Force

$assetsSrc = Join-Path $ProjectRoot "SysMonCmdPal\Assets"
$assetsDst = Join-Path $BinDir "Assets"
if (Test-Path $assetsSrc) {
    if (Test-Path $assetsDst) {
        Remove-Item $assetsDst -Recurse -Force -ErrorAction SilentlyContinue
    }
    Copy-Item $assetsSrc $assetsDst -Recurse -Force
}

# 确保本地 staging 文件不带 Mark-of-the-Web。
Get-ChildItem $BinDir -Recurse -File -ErrorAction SilentlyContinue |
    ForEach-Object { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue }

# 松散源注册 — 不需要 MSIX 打包，不需要证书
$appPackageRoot = Join-Path $BuildOutputDir "AppPackages"
$dependency = Get-ChildItem $appPackageRoot -Filter "Microsoft.WindowsAppRuntime.1.6.msix" -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match "\\Dependencies\\x64\\" } |
    Select-Object -First 1
if ($dependency) {
    $depInstalled = Get-AppxPackage -Name "Microsoft.WindowsAppRuntime.1.6" -ErrorAction SilentlyContinue
    if (-not $depInstalled) {
        Log "安装依赖: $($dependency.FullName)"
        Add-AppxPackage -Path $dependency.FullName -ErrorAction Stop
    }
}

Log "注册: $ManifestPath"
Add-AppxPackage -Register $ManifestPath -ErrorAction Stop
Log "松散源注册成功"

# 验证
$pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
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
