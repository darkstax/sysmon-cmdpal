# CLAUDE.md — sysmon-cmdpal

## 概述

PowerToys Command Palette 系统监控扩展。将 btop4win 的硬件指标采集移植到 C#/.NET，作为 Command Palette 的原生扩展运行。

## 技术栈

| 层 | 技术 |
|----|------|
| 扩展框架 | PowerToys Command Palette Extension SDK |
| 语言 | C# 12 (.NET 10) — PowerToys SDK Toolkit 目标是 net10.0，主项目必须对齐 |
| UI | Command Palette 原生页面（List / Markdown） + Dock Band |
| 通信 | WinRT → 进程外 COM Server (Shmuelie.WinRTServer) |
| 打包 | MSIX (Windows App SDK) |
| 数据采集 | Win32 P/Invoke + PerformanceCounter + LHM (PawnIO) + AMD ADL + HWiNFO |

## 项目结构

```
SysMonCmdPal/
├── SysMonCmdPal.csproj           # .NET 10 + MSIX + LHM + AOT/Trim
├── Package.appxmanifest          # MSIX 包清单 + COM 注册 + CmdPal 扩展声明
├── app.manifest                  # DPI 感知 + Windows 10 兼容性
├── Program.cs                    # COM Server 入口点 (-RegisterProcessAsComServer)
├── SysMonExtension.cs            # IExtension 实现（COM CLSID）
├── SysMonCommandsProvider.cs     # 顶级命令 + Dock Band 注册
├── Commands/
│   ├── SysMonDockBands.cs        # DockFormat + CPU/Mem/Disk/Net↓/Net↑/Bat/GPU/Sensor DockBand
│   ├── BtopLauncherCommand.cs    # btop4win 一键启动
│   └── ToggleSensorCommand.cs    # 传感器 Dock 添加/移除
├── Pages/
│   ├── SysMonMainPage.cs         # 主页列表（后端状态 + 传感器列表入口）
│   ├── SensorListPage.cs         # 全量传感器浏览（17 类别分组）
│   ├── CpuDetailPage.cs          # CPU Markdown（温度 + 后端状态）
│   ├── MemoryDetailPage.cs       # 内存 Markdown
│   ├── DiskDetailPage.cs         # 磁盘 ListPage（IO 速度 + 卷标 + 使用率）
│   ├── NetworkDetailPage.cs      # 网络 Markdown + 活跃接口列表
│   ├── BatteryDetailPage.cs      # 电池 Markdown
│   └── GpuDetailPage.cs          # GPU Markdown + VRAM + 后端状态
├── Services/
│   ├── SystemInfoService.cs      # 系统数据采集 + 传感器回退链（LHM→ADL→HWiNFO→None）
│   ├── LhmSensorService.cs       # 全量 LHM 传感器采集 + 分类 + 健康追踪
│   └── AmdTempReader.cs          # AMD ADL PMLOG + HWiNFO 共享内存（免管理员回退）
├── Models/
│   ├── SensorReading.cs          # 传感器模型（17 类别枚举 + 配置持久化）
│   └── SensorCategoryMeta.cs     # 类别排序 + 中文名 + 图标缓存
├── Assets/                       # MSIX 图标资源
└── Properties/
    └── PublishProfiles/          # win-x64 / win-arm64 发布配置
```

## 关键架构

### 传感器回退链

```
LHM (PawnIO) → AMD ADL → HWiNFO → None
```

`SystemInfoService.TryReadSensors()` 按四级优先级尝试，实时反映到 `SystemSnapshot.Backend` / `BackendNote`。LHM 连续 3 次读取失败后自动标记不可用，支持 `TryReconnect()` 热恢复。

### 格式化统一

所有格式化和状态文本统一在 `DockFormat`（`Commands/SysMonDockBands.cs`）：
- `Speed()` / `CompactSpeed()` — 网络/磁盘速度
- `Temp()` / `TempMd()` — 温度 / Markdown 温度
- `Percent()` / `PercentMd()` — 百分比 / Markdown 百分比
- `BatteryStatusText()` — 电池状态中文映射

### 刷新协调器

`DockBandRefreshCoordinator`（`Commands/SysMonDockBands.cs`）— 共享 1s timer，所有 DockBand 订阅同一刷新事件，避免各自启动 timer。

### 配置持久化

用户传感器配置存储在 `%LocalAppData%\SysMonCmdPal\sensors.json`，`SensorConfig` 类管理。当前版本 `"1.0"`。

## 数据采集

| 指标 | 实现 |
|------|------|
| CPU 使用率 | `PerformanceCounter("% Processor Time", "_Total")` 单例复用 |
| 内存 | `GlobalMemoryStatusEx` P/Invoke |
| 磁盘空间 | `DriveInfo` + `PerformanceCounter("LogicalDisk")` IO 读写字节 |
| 网络 | `NetworkInterface.GetAllNetworkInterfaces()` delta |
| 电池 | `GetSystemPowerStatus` P/Invoke |
| CPU 温度 | LHM (PawnIO) → AMD ADL PMLOG sensor 504 → HWiNFO shared memory |
| GPU | LHM: 名称/使用率/温度/显存；回退 HWiNFO GPU Core |
| 传感器全量 | LHM: 17 类别枚举，递归遍历子硬件 |

## 构建

### 前置条件

- Windows 11 + PowerToys 已安装 + 开发者模式
- .NET 10 SDK（10.0.300+）
- VS Build Tools 2026：`C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\`

### 构建步骤

```powershell
$msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"

# Debug
& $msbuild SysMonCmdPal.sln /p:Configuration=Debug /p:Platform=x64 /m `
  /p:VcpkgEnabled=false /p:EnforceCodeStyleInBuild=false /p:TreatWarningsAsErrors=false `
  /p:RunCodeAnalysis=false /p:AnalysisLevel=none

# Release
& $msbuild SysMonCmdPal.sln /p:Configuration=Release /p:Platform=x64 /m `
  /p:VcpkgEnabled=false /p:EnforceCodeStyleInBuild=false /p:TreatWarningsAsErrors=false `
  /p:RunCodeAnalysis=false /p:AnalysisLevel=none
```

### Release 注意事项

- `PublishTrimmed=true` + `TrimmerRootAssembly Include="LibreHardwareMonitorLib"` 保护 LHM 反射依赖
- 产物在 `bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/AppPackages/`

## 已知限制

- GPU 温度在非 LHM 回退模式下仅从 HWiNFO 获取（ADL 不支持 GPU 温度）
- 传感器配置无版本迁移（当前 v1.0 schema）
- `GetDockBands()` 每次重建动态 SensorDockBand 列表（可优化为 diff）
