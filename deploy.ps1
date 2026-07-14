# SysMonCmdPal 一键部署脚本
# 安装 MSIX 扩展 + 部署受保护的 Broker 计划任务
#
# 用法:
#   .\deploy.ps1                       # 完整安装（MSIX + Broker）
#   .\deploy.ps1 -SkipBuild            # 跳过构建，直接部署已有产物
#   .\deploy.ps1 -Action Uninstall     # 完全卸载
#   .\deploy.ps1 -BrokerOnly           # 只部署/更新 Broker

param(
    [ValidateSet("Install", "Uninstall")]
    [string]$Action = "Install",
    [switch]$SkipBuild,
    [switch]$BrokerOnly,
    [switch]$ElevatedBroker,
    [string]$TaskUserId = ""
)

$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$ProjectRoot = $PSScriptRoot
$PackageName = "darkstax.SysPulseforCommandPalette"

$BrokerTargetRoot = Join-Path $env:ProgramFiles "SysMonCmdPal"
$BrokerTargetDir = Join-Path $BrokerTargetRoot "Broker"
$BrokerExe = Join-Path $BrokerTargetDir "SysMonBroker.exe"
$BrokerDataRoot = Join-Path $env:ProgramData "SysMonCmdPal"
$BrokerLogDir = Join-Path $BrokerDataRoot "Logs"
$TaskName = "SysMonBroker"

$LogDir = Join-Path $env:TEMP "SysMonCmdPal"
$LogFile = Join-Path $LogDir "deploy.log"
$LegacyBrokerTargetDir = Join-Path $env:LOCALAPPDATA "SysMonBroker"

# ── Helpers ──────────────────────────────────────────────────

function Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $msg"
    try { Add-Content $LogFile $line -ErrorAction SilentlyContinue } catch {}
    Write-Host $line
}

function Test-IsAdmin {
    $current = [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    return $current.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-CurrentUserId {
    try { return [Security.Principal.WindowsIdentity]::GetCurrent().Name }
    catch { return "$env:USERDOMAIN\$env:USERNAME" }
}

function Invoke-ElevatedBroker {
    param(
        [ValidateSet("Install", "Uninstall")]
        [string]$BrokerAction
    )

    if (Test-IsAdmin) {
        if ($BrokerAction -eq "Uninstall") {
            Uninstall-Broker
        } else {
            Deploy-Broker
        }
        return
    }

    $targetUser = if ($TaskUserId) { $TaskUserId } else { Get-CurrentUserId }
    $argList = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-Action", $BrokerAction,
        "-SkipBuild",
        "-BrokerOnly",
        "-ElevatedBroker",
        "-TaskUserId", "`"$targetUser`""
    )

    Log "Broker 操作需要管理员权限，正在单独提升..."
    $shell = if ($PSVersionTable.PSEdition -eq "Core" -and $PSHOME) {
        Join-Path $PSHOME "pwsh.exe"
    } else {
        $cmd = Get-Command pwsh.exe -ErrorAction SilentlyContinue
        if ($cmd) { $cmd.Source } else { $null }
    }
    if (-not $shell) { $shell = "pwsh.exe" }
    $p = Start-Process $shell -ArgumentList $argList -Verb RunAs -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        throw "Broker elevated action failed with exit code $($p.ExitCode)"
    }
}

function Get-MSBuildPath {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
    )

    foreach ($p in $candidates) {
        if (Test-Path $p) { return $p }
    }

    throw "找不到 MSBuild。请安装 Visual Studio Build Tools，并确保包含 MSIX/AppX 构建工具链。"
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

function Set-ProtectedDirectoryAcl {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }

    $aclArgs = @(
        $Path,
        "/inheritance:r",
        "/grant:r",
        "*S-1-5-18:(OI)(CI)F",
        "*S-1-5-32-544:(OI)(CI)F",
        "*S-1-5-32-545:(OI)(CI)RX"
    )
    & icacls @aclArgs | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "设置目录 ACL 失败: $Path"
    }
}

function Unblock-LocalFile {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return
    }

    try {
        Unblock-File -LiteralPath $Path -ErrorAction Stop
        return
    } catch {
        try {
            Remove-Item -LiteralPath $Path -Stream Zone.Identifier -ErrorAction SilentlyContinue
        } catch {
            # Some file systems do not support alternate streams; safe to ignore.
        }
    }
}

# ── Build ────────────────────────────────────────────────────

function Build-Msix {
    Log "构建 SysMonCmdPal MSIX (Release/x64)..."

    $msbuild = Get-MSBuildPath
    $appxTools = Get-AppxMSBuildToolsPath
    $mainProj = Join-Path $ProjectRoot "SysMonCmdPal\SysMonCmdPal.csproj"
    $args = @(
        $mainProj,
        "/restore",
        "/p:Configuration=Release",
        "/p:Platform=x64",
        "/m",
        "/p:VcpkgEnabled=false",
        "/p:EnforceCodeStyleInBuild=false",
        "/p:RunAnalyzers=false",
        "/p:TreatWarningsAsErrors=false",
        "/v:minimal"
    )
    if ($appxTools) {
        $args += "/p:AppxMSBuildToolsPath=$appxTools"
    }

    & $msbuild @args

    if ($LASTEXITCODE -ne 0) {
        Log "FATAL: SysMonCmdPal MSIX 构建失败"
        exit 1
    }

    Log "SysMonCmdPal MSIX OK"
}

function Build-Broker {
    $msbuild = Get-MSBuildPath
    $brokerProj = Join-Path $ProjectRoot "SysMonBroker\SysMonBroker.csproj"
    if (-not (Test-Path $brokerProj)) {
        Log "WARNING: 找不到 SysMonBroker.csproj"
        return
    }

    Log "构建 SysMonBroker (self-contained)..."
    & $msbuild $brokerProj /restore /t:Publish `
        /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 `
        /p:SelfContained=true /p:PublishSingleFile=true `
        /p:VcpkgEnabled=false /p:EnforceCodeStyleInBuild=false `
        /p:RunAnalyzers=false /p:TreatWarningsAsErrors=false `
        /v:minimal

    if ($LASTEXITCODE -ne 0) {
        Log "FATAL: SysMonBroker 构建失败"
        exit 1
    }

    Log "SysMonBroker OK"
}

function Build-Projects {
    Log "=== 构建项目 ==="
    Build-Msix
    Build-Broker
}

# ── MSIX Install ─────────────────────────────────────────────

function Install-Msix {
    Log "=== 安装 MSIX ==="

    $msixRoot = Join-Path $ProjectRoot "SysMonCmdPal\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AppPackages"
    if (-not (Test-Path $msixRoot)) {
        Log "FATAL: 找不到 Release MSIX 输出，请先使用 MSBuild 构建 SysMonCmdPal.csproj 的 Release/x64 包"
        exit 1
    }

    $msix = Get-ChildItem $msixRoot -Filter "*.msix" -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $msix) {
        Log "FATAL: 找不到 .msix 文件"
        exit 1
    }

    $sizeMB = [math]::Round($msix.Length / 1MB, 1)
    Log "File: $($msix.Name) ($sizeMB MB)"

    $oldPkgs = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue)
    foreach ($old in $oldPkgs) {
        Log "移除旧版本: $($old.PackageFullName)"
        Remove-AppxPackage $old.PackageFullName -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }

    $cacheDir = "$env:LOCALAPPDATA\Microsoft\PowerToys\CmdPal"
    if (Test-Path $cacheDir) {
        Remove-Item $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
        Log "已清理 CmdPal 扩展缓存"
    }

    Add-AppxPackage -Path $msix.FullName -ErrorAction Stop
    Log "MSIX 安装成功"

    $pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
    if ($pkg) {
        Log "已安装: $($pkg.PackageFullName)"
    } else {
        Log "WARNING: 安装后未通过包名查询到 $PackageName"
    }
}

# ── Broker Deploy ────────────────────────────────────────────

function Deploy-Broker {
    if (-not (Test-IsAdmin)) {
        throw "Broker 部署必须在管理员权限下执行"
    }

    Log "=== 部署 Broker ==="

    $candidates = @(
        (Join-Path $ProjectRoot "SysMonBroker\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\publish\SysMonBroker.exe"),
        (Join-Path $ProjectRoot "SysMonBroker\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\SysMonBroker.exe"),
        (Join-Path $ProjectRoot "SysMonCmdPal\Broker\SysMonBroker.exe"),
        (Join-Path $ProjectRoot "SysMonBroker\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\SysMonBroker.exe"),
        (Join-Path $ProjectRoot "SysMonBroker\bin\Release\net10.0-windows10.0.26100.0\win-x64\SysMonBroker.exe")
    )

    $srcExe = $null
    foreach ($p in $candidates) {
        if (Test-Path $p) { $srcExe = $p; break }
    }

    if (-not $srcExe) {
        Log "WARNING: 找不到 SysMonBroker.exe"
        Log "  Broker 是可选的 — 扩展会使用 HWiNFO / D3DKMT / PDH / ThermalZone 回退"
        return
    }

    Set-ProtectedDirectoryAcl $BrokerTargetRoot
    Set-ProtectedDirectoryAcl $BrokerTargetDir
    Set-ProtectedDirectoryAcl $BrokerDataRoot
    Set-ProtectedDirectoryAcl $BrokerLogDir

    Get-Process SysMonBroker -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $oldTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($oldTask) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        Log "已移除旧 Broker 计划任务"
    }

    if (Test-Path $BrokerExe) {
        Remove-Item $BrokerExe -Force -ErrorAction SilentlyContinue
    }
    Copy-Item $srcExe $BrokerExe -Force
    Unblock-LocalFile $BrokerExe
    Log "已部署: $BrokerExe ($([math]::Round((Get-Item $BrokerExe).Length/1MB,1)) MB)"

    # 清理旧版用户可写 Broker 目录（同账户升级场景）
    if ((Test-Path $LegacyBrokerTargetDir) -and
        ($LegacyBrokerTargetDir -ne $BrokerTargetDir)) {
        Remove-Item $LegacyBrokerTargetDir -Recurse -Force -ErrorAction SilentlyContinue
        Log "已清理旧版 Broker 目录: $LegacyBrokerTargetDir"
    }

    $principalUser = if ($TaskUserId) { $TaskUserId } else { Get-CurrentUserId }
    $taskAction    = New-ScheduledTaskAction -Execute $BrokerExe -WorkingDirectory $BrokerTargetDir
    $taskTrigger   = New-ScheduledTaskTrigger -AtLogOn
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $principalUser -RunLevel Highest -LogonType Interactive
    $taskSettings  = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $taskAction -Trigger $taskTrigger `
        -Principal $taskPrincipal -Settings $taskSettings `
        -Description "SysMonCmdPal Broker — 高精度硬件温度读取 (LibreHardwareMonitor)" `
        -Force | Out-Null
    Log "计划任务已注册: 登录时自动启动 (最高权限, User=$principalUser)"

    Start-ScheduledTask -TaskName $TaskName

    $proc = $null
    for ($i = 0; $i -lt 15; $i++) {
        Start-Sleep -Seconds 1
        $proc = Get-Process SysMonBroker -ErrorAction SilentlyContinue
        if ($proc) { break }
    }

    if ($proc) {
        Log "Broker 运行中: PID $($proc.Id)"
    } else {
        Log "NOTE: Broker 进程未检测到 — 可能 PawnIO 驱动未安装或传感器初始化失败"
        try {
            $taskInfo = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction Stop
            Log "  LastTaskResult=$($taskInfo.LastTaskResult) LastRunTime=$($taskInfo.LastRunTime)"
        } catch {
            Log "  计划任务运行信息不可用: $($_.Exception.Message)"
        }
        Log "  Broker 缺席时扩展会自动使用用户态回退链"
    }
}

# ── Uninstall ────────────────────────────────────────────────

function Uninstall-Broker {
    if (-not (Test-IsAdmin)) {
        throw "Broker 卸载必须在管理员权限下执行"
    }

    Log "=== 卸载 Broker ==="

    Get-Process SysMonBroker -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        Log "已移除 Broker 计划任务"
    }

    if (Test-Path $BrokerTargetRoot) {
        Remove-Item $BrokerTargetRoot -Recurse -Force -ErrorAction SilentlyContinue
        Log "已移除 Broker 目录: $BrokerTargetRoot"
    }

    if (Test-Path $LegacyBrokerTargetDir) {
        Remove-Item $LegacyBrokerTargetDir -Recurse -Force -ErrorAction SilentlyContinue
        Log "已移除旧版 Broker 目录: $LegacyBrokerTargetDir"
    }
}

function Uninstall-Msix {
    Log "=== 卸载 MSIX ==="

    $pkgs = @(Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue)
    if ($pkgs.Count -gt 0) {
        foreach ($pkg in $pkgs) {
            Remove-AppxPackage $pkg.PackageFullName -ErrorAction SilentlyContinue
            Log "已移除 MSIX: $($pkg.PackageFullName)"
        }
    } else {
        Log "MSIX 未安装"
    }

    $cacheDir = "$env:LOCALAPPDATA\Microsoft\PowerToys\CmdPal"
    if (Test-Path $cacheDir) {
        Remove-Item $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
        Log "已清理 CmdPal 缓存"
    }
}

# ── Main ─────────────────────────────────────────────────────

try {
    if (-not (Test-Path (Split-Path $LogFile -Parent))) {
        New-Item -ItemType Directory -Path (Split-Path $LogFile -Parent) -Force | Out-Null
    }

    Log ""
    Log "========================================"
    Log " SysMonCmdPal Deploy"
    Log " Action=$Action  SkipBuild=$SkipBuild  BrokerOnly=$BrokerOnly  ElevatedBroker=$ElevatedBroker"
    Log "========================================"

    if ($ElevatedBroker) {
        if ($Action -eq "Uninstall") {
            Uninstall-Broker
        } else {
            Deploy-Broker
        }
        exit 0
    }

    if ($Action -eq "Uninstall") {
        Uninstall-Msix
        Invoke-ElevatedBroker -BrokerAction Uninstall
        Log "=== 卸载完成 ==="
        exit 0
    }

    if ($BrokerOnly) {
        if (-not $SkipBuild) {
            Build-Broker
        }
        Invoke-ElevatedBroker -BrokerAction Install
        exit 0
    }

    if (-not $SkipBuild) {
        Build-Projects
    }

    Install-Msix
    Invoke-ElevatedBroker -BrokerAction Install

    Log ""
    Log "========================================"
    Log " 部署完成!"
    Log "========================================"
    Log ""
    Log "使用: Win+Alt+T 打开 Command Palette, 搜索 'System Monitor'"
    Log "Broker: $BrokerExe"
    Log "卸载:   .\deploy.ps1 -Action Uninstall"
    Log ""

} catch {
    Log "ERROR: $($_.Exception.Message)"
    Log "STACK: $($_.ScriptStackTrace)"
    Write-Host "`n部署失败: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
