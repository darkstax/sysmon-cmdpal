# SysMonCmdPal Broker - 待办与上下文交接

## 项目概述

SysMonCmdPal 是一个 PowerToys Command Palette 系统监控扩展（MSIX 包），通过 Broker 架构在 AppContainer 沙箱中获取高精度 CPU 温度（Tctl/Tdie）。

- **MSIX 应用 (SysMonCmdPal)**: 运行在 AppContainer 沙箱，通过命名管道连接 Broker
- **Broker (SysMonBroker)**: 独立管理员进程，通过 PawnIO 驱动读 SMU/MSR，通过命名管道向 MSIX 提供数据
- **命名管道协议**: 管道名 `SysMonCmdPal`，请求 `[byte cmd]`，响应 `[int32 status][double value][int32 source]`

## 当前状态

**已修复并部署，等待重启 PowerToys 验证。** 最后一次部署时间：2026-06-13 ~12:50

已清理的环境：旧计划任务已删除、旧 broker_setup.log 已清除、旧 Broker 进程已停止。

### 本轮修复清单

| # | 修复 | 文件 |
|---|------|------|
| 1 | `Unregister-ScheduledTask` 移除 `-Force`（Install + Uninstall 两处） | setup-broker.ps1 L43, L76 |
| 2 | 管道安全描述符 `D:(A;;GA;;;WD)` 允许 AppContainer 连接 | SysMonBroker/Program.cs |
| 3 | 启动自检 `EnsureBrokerOnStartupAsync()` 自动触发安装 | SysMonCommandsProvider.cs |

### 上次诊断结果（部署前）

- 12:39 Broker 成功启动（PID 32540），AMD SMU 初始化正常
- 12:47 重新安装时 `Unregister-ScheduledTask -Force` 报错，杀掉了 Broker 但未重新注册
- MSIX 端温度来源仍为 Thermal Zone（降级），Broker 管道报 "Access to the path is denied"

## 下一步待办

- [ ] **重启 PowerToys 验证 Broker**：点 UAC 弹窗 → 检查 `C:\ProgramData\broker_setup.log` 是否有 "Install complete"
- [ ] **验证管道连通**：检查 `sensor_backend.log` 是否出现 Broker 温度源（`Broker_SMU` 而非 `ThermalZone`）
- [ ] **如果管道仍 Access Denied**：尝试 SDDL 改为 `D:(A;;GA;;;WD)(A;;GA;;;AC)` 加 AppContainer SID
- [ ] **如果 Broker 启动后退出**：检查 `broker.log`，可能需要在 Program.cs 加全局 try-catch 和崩溃日志
- [ ] **版本号递增**：当前 1.1.0.0，正式发布前需要递增
- [ ] **Store 描述更新**：提及 PawnIO 驱动前置条件

## 已完成的工作

1. **Broker 安装脚本 `setup-broker.ps1`** - 多次修复：
   - UTF-8 BOM（Windows PowerShell 5.x 必须有 BOM 才能正确解析）
   - `-RunLevel Highest`（不是 `HighestAvailable`，枚举值不存在）
   - `-ExecutionTimeLimit ([TimeSpan]::Zero)`（9999天超范围）
   - 移除 `-RestartCount` / `-RestartInterval`（PT30S 不合法，最小 PT1M）
   - Remove-Item 再 Copy-Item（跨安全上下文不能 Force 覆盖）
   - 自写日志到 `C:\ProgramData\broker_setup.log`

2. **管道安全描述符** - `SysMonBroker/Program.cs`:
   - 用 Win32 `CreateNamedPipe` + SDDL `D:(A;;GA;;;WD)` 创建管道
   - 允许 Everyone（包括 AppContainer）连接
   - 之前的 `new NamedPipeServerStream(...)` 默认只允许管理员访问

3. **启动自检逻辑** - `SysMonCommandsProvider.cs`:
   - 添加 `EnsureBrokerOnStartupAsync()`：如果高精度开关保存为 ON，启动 3 秒后探测管道
   - 管道不通则自动触发 `SetupBrokerAsync()`（弹 UAC 安装）
   - 解决了重启 PowerToys 后 SettingsChanged 不触发的问题

4. **Staging 模式**: MSIX 先把 Broker 文件复制到 `%LOCALAPPDATA%\SysMonCmdPal\broker-staging\`（绕过 WindowsApps ACL），再从 staging 启动提升权限脚本

5. **SysMonBroker 构建**: `dotnet publish` self-contained single-file，12.4 MB

## 关键文件位置

| 文件 | 路径 |
|------|------|
| MSIX 主项目 | `C:\Users\StarL\Documents\AI_code\sysmon-cmdpal\SysMonCmdPal\` |
| Broker 项目 | `C:\Users\StarL\Documents\AI_code\sysmon-cmdpal\SysMonBroker\` |
| 部署脚本 | `C:\Users\StarL\Documents\AI_code\sysmon-cmdpal\deploy.ps1` |
| 安装脚本 | `SysMonCmdPal\Broker\setup-broker.ps1`（必须有 UTF-8 BOM） |
| Broker 主程序 | `SysMonBroker\Program.cs`（含 CreateOpenPipe + PawnIOWrapper） |
| 命令提供者 | `SysMonCmdPal\SysMonCommandsProvider.cs`（含 SetupBrokerAsync + EnsureBrokerOnStartupAsync） |
| BrokerClient | `SysMonCmdPal\Services\BrokerClient.cs`（命名管道客户端，30s 重试） |
| 温度读取链 | `SysMonCmdPal\Services\CpuSensorReader.cs`（Phase 0: Broker → Phase 1-7 降级） |
| 日志 | `SysMonCmdPal\Services\SensorLogger.cs` → `%LOCALAPPDATA%\SysMonCmdPal\sensor_backend.log` |

## 诊断文件

| 文件 | 说明 |
|------|------|
| `C:\ProgramData\broker_setup.log` | 提升权限安装脚本的自写日志 |
| `%LOCALAPPDATA%\SysMonCmdPal\sensor_backend.log` | MSIX 端传感器日志（ForceLog） |
| `%LOCALAPPDATA%\SysMonCmdPal\broker.log` | Broker 进程自己的日志 |
| `%LOCALAPPDATA%\SysMonCmdPal\settings.json` | 高精度开关持久化状态 |

## 构建流程

```powershell
# 1. 构建 SysMonBroker（独立项目）
dotnet publish SysMonBroker\SysMonBroker.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# 2. 复制 exe 到 MSIX Broker 目录
Copy-Item SysMonBroker\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\SysMonBroker.exe SysMonCmdPal\Broker\SysMonBroker.exe -Force

# 3. 构建 MSIX（必须用 MSBuild，不能用 dotnet CLI）
& "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe" SysMonCmdPal\SysMonCmdPal.csproj /t:Build /p:Configuration=Release /p:Platform=x64 /p:TreatWarningsAsErrors=false /v:minimal

# 4. 部署
powershell -ExecutionPolicy Bypass -File deploy.ps1
```

## 已知陷阱

1. `setup-broker.ps1` **必须有 UTF-8 BOM**，否则 PowerShell 5.x 在中文 Windows 上按 GBK 解析会语法错误
2. MSBuild 必须用 VS Build Tools 路径，不能用 `dotnet build`（PowerToys SDK 有 C++ 组件依赖）
3. WindowsApps 目录 ACL 极严，即使管理员也读不了——必须 staging 中转
4. `Copy-Item -Force` 无法覆盖不同安全上下文创建的文件——必须先 `Remove-Item`
5. `NamedPipeServerStream` 默认权限只允许创建者访问——AppContainer 连不上，必须用 Win32 API 设 SDDL
6. `Unregister-ScheduledTask` **没有 `-Force` 参数**（`Register-ScheduledTask` 才有），重复安装时会报错
