# SysMonCmdPal 一键部署脚本
# 自动安装 MSIX 扩展 + 部署 Broker (计划任务自启动)
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
    [switch]$BrokerOnly
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$BrokerTargetDir = Join-Path $env:LOCALAPPDATA "SysMonBroker"
$BrokerExe = Join-Path $BrokerTargetDir "SysMonBroker.exe"
$TaskName = "SysMonBroker"
$LogFile = Join-Path $BrokerTargetDir "deploy.log"

# ── Helpers ──────────────────────────────────────────────────

function Log($msg) {
    $line = "[$(Get-Date -Format 'HH:mm:ss')] $msg"
    try { Add-Content $LogFile $line -ErrorAction SilentlyContinue } catch {}
    Write-Host $line
}

function Ensure-Admin {
    $current = [Security.Principal.WindowsPrincipal]::new(
        [Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $current.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Write-Host "需要管理员权限，正在提升..." -ForegroundColor Yellow
        $argList = "-ExecutionPolicy Bypass -NoProfile -File `"$PSCommandPath`" -Action $Action"
        if ($SkipBuild)   { $argList += " -SkipBuild" }
        if ($BrokerOnly)  { $argList += " -BrokerOnly" }
        Start-Process pwsh -ArgumentList $argList -Verb RunAs -Wait
        exit
    }
}

# ── Build ────────────────────────────────────────────────────

function Build-Projects {
    Log "=== 构建项目 ==="

    $mainProj = Join-Path $ProjectRoot "SysMonCmdPal\SysMonCmdPal.csproj"
    Log "构建 SysMonCmdPal (Release/x64)..."
    & dotnet build $mainProj -c Release -p:Platform=x64 --nologo -v q 2>&1 |
        Where-Object { $_ -match "error CS|warning CS" } |
        ForEach-Object { Log "  $_" }
    if ($LASTEXITCODE -ne 0) {
        Log "FATAL: SysMonCmdPal 构建失败"
        exit 1
    }
    Log "SysMonCmdPal OK"

    $brokerProj = Join-Path $ProjectRoot "SysMonBroker\SysMonBroker.csproj"
    if (Test-Path $brokerProj) {
        Log "构建 SysMonBroker (self-contained)..."
        & dotnet publish $brokerProj -c Release -r win-x64 --self-contained `
            -p:PublishSingleFile=true --nologo -v q 2>&1 |
            Where-Object { $_ -match "error CS|warning CS" } |
            ForEach-Object { Log "  $_" }
        if ($LASTEXITCODE -ne 0) {
            Log "WARNING: SysMonBroker 构建失败，将使用已有 exe"
        } else {
            Log "SysMonBroker OK"
        }
    }
}

# ── MSIX Install ─────────────────────────────────────────────

function Install-Msix {
    Log "=== 安装 MSIX ==="

    $msixRoot = Join-Path $ProjectRoot "SysMonCmdPal\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AppPackages"
    if (-not (Test-Path $msixRoot)) {
        Log "FATAL: 找不到 Release 构建输出，请先构建项目"
        Log "  dotnet build SysMonCmdPal\SysMonCmdPal.csproj -c Release -p:Platform=x64"
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

    # Remove old version first
    $old = Get-AppxPackage -Name "SysMonCmdPal" -ErrorAction SilentlyContinue
    if ($old) {
        Log "移除旧版本: $($old.PackageFullName)"
        Remove-AppxPackage $old.PackageFullName -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }

    # Clean CmdPal extension cache
    $cacheDir = "$env:LOCALAPPDATA\Microsoft\PowerToys\CmdPal"
    if (Test-Path $cacheDir) {
        Remove-Item $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
        Log "已清理 CmdPal 扩展缓存"
    }

    Add-AppxPackage -Path $msix.FullName -ErrorAction Stop
    Log "MSIX 安装成功"

    $pkg = Get-AppxPackage -Name "SysMonCmdPal" -ErrorAction SilentlyContinue
    if ($pkg) {
        Log "已安装: $($pkg.PackageFullName)"
    }
}

# ── Broker Deploy ────────────────────────────────────────────

function Deploy-Broker {
    Log "=== 部署 Broker ==="

    # Find Broker exe (search order matters)
    $candidates = @(
        # dotnet publish output
        (Join-Path $ProjectRoot "SysMonBroker\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\SysMonBroker.exe"),
        # Pre-built artifact
        (Join-Path $ProjectRoot "SysMonCmdPal\Broker\SysMonBroker.exe"),
        # Non-publish build output
        (Join-Path $ProjectRoot "SysMonBroker\bin\Release\net10.0-windows10.0.26100.0\win-x64\SysMonBroker.exe")
    )

    $srcExe = $null
    foreach ($p in $candidates) {
        if (Test-Path $p) { $srcExe = $p; break }
    }

    if (-not $srcExe) {
        Log "WARNING: 找不到 SysMonBroker.exe"
        Log "  Broker 是可选的 — 扩展会使用 HWiNFO / ADL / ThermalZone 作为温度源"
        return
    }

    # Prepare directory
    if (-not (Test-Path $BrokerTargetDir)) {
        New-Item -ItemType Directory -Path $BrokerTargetDir -Force | Out-Null
    }

    # Stop existing
    Get-Process SysMonBroker -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    # Remove old task
    $oldTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($oldTask) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    }

    # Copy
    if (Test-Path $BrokerExe) { Remove-Item $BrokerExe -Force -ErrorAction SilentlyContinue }
    Copy-Item $srcExe $BrokerExe -Force
    Log "已部署: SysMonBroker.exe ($([math]::Round((Get-Item $BrokerExe).Length/1MB,1)) MB)"

    # Scheduled task: auto-start on login, highest privileges, auto-restart
    $taskAction    = New-ScheduledTaskAction -Execute $BrokerExe -WorkingDirectory $BrokerTargetDir
    $taskTrigger   = New-ScheduledTaskTrigger -AtLogOn
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -RunLevel Highest -LogonType Interactive
    $taskSettings  = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew `
        -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

    Register-ScheduledTask `
        -TaskName $TaskName `
        -Action $taskAction -Trigger $taskTrigger `
        -Principal $taskPrincipal -Settings $taskSettings `
        -Description "SysMonCmdPal Broker — 高精度硬件温度读取 (PawnIO)" `
        -Force | Out-Null
    Log "计划任务已注册: 登录时自动启动 (最高权限)"

    # Start now
    Start-ScheduledTask -TaskName $TaskName
    Start-Sleep -Seconds 3

    $proc = Get-Process SysMonBroker -ErrorAction SilentlyContinue
    if ($proc) {
        Log "Broker 运行中: PID $($proc.Id)"
    } else {
        Log "NOTE: Broker 进程未检测到 — 可能 PawnIO 驱动未安装"
        Log "  没有 PawnIO 时 Broker 会正常退出，不影响扩展使用"
        Log "  安装 PawnIO 驱动后 Broker 即可提供精准温度"
    }
}

# ── Uninstall ────────────────────────────────────────────────

function Uninstall-All {
    Log "=== 卸载 SysMonCmdPal ==="

    # Broker
    Get-Process SysMonBroker -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        Log "已移除 Broker 计划任务"
    }
    if (Test-Path $BrokerTargetDir) {
        Remove-Item $BrokerTargetDir -Recurse -Force -ErrorAction SilentlyContinue
        Log "已移除 Broker 目录"
    }

    # MSIX
    $pkg = Get-AppxPackage -Name "SysMonCmdPal" -ErrorAction SilentlyContinue
    if ($pkg) {
        Remove-AppxPackage $pkg.PackageFullName -ErrorAction SilentlyContinue
        Log "已移除 MSIX: $($pkg.PackageFullName)"
    } else {
        Log "MSIX 未安装"
    }

    # Cache
    $cacheDir = "$env:LOCALAPPDATA\Microsoft\PowerToys\CmdPal"
    if (Test-Path $cacheDir) {
        Remove-Item $cacheDir -Recurse -Force -ErrorAction SilentlyContinue
        Log "已清理 CmdPal 缓存"
    }

    Log "=== 卸载完成 ==="
}

# ── Main ─────────────────────────────────────────────────────

try {
    Ensure-Admin

    if (-not (Test-Path (Split-Path $LogFile -Parent))) {
        New-Item -ItemType Directory -Path (Split-Path $LogFile -Parent) -Force | Out-Null
    }

    Log ""
    Log "========================================"
    Log " SysMonCmdPal Deploy"
    Log " Action=$Action  SkipBuild=$SkipBuild  BrokerOnly=$BrokerOnly"
    Log "========================================"

    if ($Action -eq "Uninstall") {
        Uninstall-All
        exit 0
    }

    # ── Install ──
    if (-not $SkipBuild -and -not $BrokerOnly) {
        Build-Projects
    }

    if (-not $BrokerOnly) {
        Install-Msix
    }

    Deploy-Broker

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
