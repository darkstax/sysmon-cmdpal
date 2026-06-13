# CLAUDE.md — sysmon-cmdpal

## 概述

PowerToys Command Palette 系统监控扩展（v1.1.0）。将 btop4win 的硬件指标采集移植到 C#/.NET，作为 Command Palette 的原生扩展运行。

**开发者：** darkstax

## 技术栈

| 层 | 技术 |
|----|------|
| 扩展框架 | PowerToys Command Palette Extension SDK |
| 语言 | C# 12 (.NET 10) — PowerToys SDK Toolkit 目标是 net10.0，主项目必须对齐 |
| UI | Command Palette 原生页面（List / Markdown） + Dock Band |
| 通信 | WinRT → 进程外 COM Server (Shmuelie.WinRTServer) |
| 打包 | MSIX (Windows App SDK) |
| 数据采集 | Win32 P/Invoke + PerformanceCounter + PawnIO (Intel MSR / AMD SMU) + NVAPI + ADL + IGCL + LHM + HWiNFO |

## 项目结构

```
SysMonCmdPal/
├── SysMonCmdPal.csproj           # .NET 10 + MSIX + LHM + AOT/Trim + 嵌入 .bin
├── Package.appxmanifest          # MSIX 包清单 + COM 注册 + CmdPal 扩展声明
├── app.manifest                  # DPI 感知 + Windows 10 兼容性
├── Program.cs                    # COM Server 入口点 (-RegisterProcessAsComServer)
├── SysMonExtension.cs            # IExtension 实现（COM CLSID）
├── SysMonCommandsProvider.cs     # 顶级命令 + Dock Band 注册
├── Pawn/
│   ├── PawnIOWrapper.cs          # PawnIO 设备通信封装（打开、加载模块、执行函数）
│   ├── IntelMSR.bin              # Intel MSR ring0 模块（嵌入资源）
│   └── RyzenSMU.bin              # AMD SMU ring0 模块（嵌入资源）
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
│   ├── SystemInfoService.cs      # 系统数据采集 + 传感器编排
│   ├── CpuSensorReader.cs        # CPU 温度回退链（自包含）
│   ├── GpuSensorReader.cs        # GPU 数据回退链（自包含）
│   ├── IntelMsrReader.cs         # PawnIO → Intel MSR 温度
│   ├── AmdSmuReader.cs           # PawnIO → AMD SMU 邮箱协议
│   ├── NvapiReader.cs            # NVAPI → NVIDIA GPU 数据
│   ├── AdlGpuReader.cs           # ADL PMLOG → AMD GPU 温度
│   ├── IgclReader.cs             # IGCL → Intel GPU 数据
│   ├── LhmSensorService.cs       # 全量 LHM 传感器采集 + 分类 + 健康追踪
│   ├── AmdTempReader.cs          # AMD ADL PMLOG + HWiNFO 共享内存（CPU+GPU回退）
│   └── SensorLogger.cs           # 共享传感器日志工具
├── Models/
│   ├── SensorReading.cs          # 传感器模型（17 类别枚举 + 配置持久化）
│   ├── SensorCategoryMeta.cs     # 类别排序 + 中文名 + 图标缓存
│   └── SensorChainConfig.cs      # 用户可配置传感器链（版本 v2，JSON 持久化）
├── Assets/                       # MSIX 图标资源
└── Properties/
    └── PublishProfiles/          # win-x64 / win-arm64 发布配置
```

## 关键架构

### 传感器回退链

CPU 和 GPU 采用**用户可配置回退链**，通过 CmdPal 设置页面自定义数据源优先级。

用户可用的数据源:
- **Broker** — 高精度子链（命名管道 → PawnIO MSR/SMU → LHM HTTP → ADL → LHM）
- **ThermalZone** — Windows ACPI 热区（PerformanceCounter，无需管理员）
- **HWiNFO** — HWiNFO 共享内存（最后兜底，每 12h 需重置）

配置存储: `SensorChainConfig`（`Models/SensorChainConfig.cs`），序列化到 `%LOCALAPPDATA%\SysMonCmdPal\settings.json`
版本 v2，向后兼容 v1（仅含 `highPrecision` 键）。

```
CPU 链默认: [Broker, ThermalZone, HWiNFO]
GPU 链默认: [Broker, ThermalZone, HWiNFO]

Broker 子链内部顺序:
  CPU: Broker → PawnIO MSR → PawnIO SMU → LHM HTTP → ADL → LHM
  GPU: Broker (ReadAllGpus cmd=4)
```

### GpuMode

通过 CmdPal 设置页面可选的 GPU 筛选模式:
- **Auto**（默认）— 多卡时，部分卡有 3D 活动则只显示活动的
- **DedicatedOnly** — 仅显示独立显卡（过滤集显关键词）
- **All** — 显示所有检测到的 GPU

### PawnIO ring0 模块

PawnIO 驱动安装后，SDDL 包含 `IU`（Interactive User）权限，MSIX `runFullTrust` 进程可正常访问。

两个 ring0 模块作为嵌入资源编译进 exe：
- `Pawn/IntelMSR.bin` — Intel CPU: 读 MSR 0x19C (IA32_THERM_STATUS) 获取 DTS 温度
- `Pawn/RyzenSMU.bin` — AMD CPU: 通过 SMU 邮箱协议读取 PM Table 中的 TctlTemp

### 格式化统一

所有格式化和状态文本统一在 `DockFormat`（`Commands/SysMonDockBands.cs`）：
- `Speed()` / `CompactSpeed()` — 网络/磁盘速度
- `Temp()` / `TempMd()` — 温度 / Markdown 温度
- `Percent()` / `PercentMd()` — 百分比 / Markdown 百分比
- `BatteryStatusText()` — 电池状态中文映射

### 刷新协调器

`DockBandRefreshCoordinator`（`Commands/SysMonDockBands.cs`）— 共享 1s timer，所有 DockBand 订阅同一刷新事件，避免各自启动 timer。使用 `Interlocked.Exchange` 防并发。

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
| CPU 温度 | PawnIO MSR(Intel) / PawnIO SMU(AMD) → ADL PMLOG sensor 32 → LHM → HWiNFO |
| GPU 温度 | NVAPI(NVIDIA) / ADL PMLOG(AMD) / IGCL(Intel) → LHM → HWiNFO |
| 传感器全量 | LHM NuGet: 17 类别枚举，递归遍历子硬件 |

## 构建

### 前置条件

- Windows 11 + PowerToys 已安装 + 开发者模式
- .NET 10 SDK（10.0.300+）
- VS Build Tools 2026：`C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\`

### 构建步骤

```powershell
$msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"

# Release（推荐，带 AOT/Trim）
& $msbuild SysMonCmdPal\SysMonCmdPal.csproj /p:Configuration=Release /p:Platform=x64 `
  /p:VcpkgEnabled=false /p:RunAnalyzers=false /p:TreatWarningsAsErrors=false `
  /t:Build /v:minimal

# 安装
& "$PWD\SysMonCmdPal\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AppPackages\SysMonCmdPal_*_x64_Test\Add-AppDevPackage.ps1"

# 重载命令面板
Start-Process 'x-cmdpal://reload'
```

### Release 注意事项

- `PublishTrimmed=true` + `TrimmerRootAssembly Include="LibreHardwareMonitorLib"` 保护 LHM 反射依赖
- 产物在 `bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/AppPackages/`
- PawnIO ring0 模块（`IntelMSR.bin` / `RyzenSMU.bin`）作为 `EmbeddedResource` 编译进 DLL

## AMD ADL PMLOG 实现细节

### 结构体布局（官方 SDK，非 btop4win 的错误版本）

```c
// adl_defines.h
#define ADL_PMLOG_MAX_SENSORS  256

// adl_structures.h
typedef struct ADLSingleSensorData {
    int supported;   // 非零 = 传感器可用
    int value;       // 传感器值（温度单位直接是 °C，不是毫度）
} ADLSingleSensorData;

typedef struct ADLPMLogDataOutput {
    int size;                                    // 4 bytes (offset 0)
    ADLSingleSensorData sensors[256];            // 256 × 8 = 2048 bytes (offset 4)
} ADLPMLogDataOutput;                            // total = 2052 bytes
```

**C# 中的偏移量计算：**
- `sensors[i].supported` → offset = 4 + i * 8
- `sensors[i].value` → offset = 4 + i * 8 + 4

### 正确的传感器 ID（来自 ADL_PMLOG_SENSORS 枚举）

| 传感器 | 官方 ID | 含义 |
|--------|---------|------|
| TEMPERATURE_EDGE | **8** | GPU 边缘温度 |
| TEMPERATURE_MEM | **9** | 显存温度 |
| TEMPERATURE_VRVDDC | **10** | VR VDDC 温度 |
| TEMPERATURE_HOTSPOT | **27** | GPU 热点温度 |
| TEMPERATURE_GFX | **28** | GFX 温度 |
| TEMPERATURE_SOC | **29** | SoC 温度 |
| **TEMPERATURE_CPU** | **32** | **CPU 温度** |

### 值单位

ADL PMLOG 温度值**直接是摄氏度**（per SDK docs: "value in C"）。不需要除以 1000。

### btop4win 的 bug

btop4win (`src/amd_temp.cpp`) 使用了错误的传感器 ID (500-504) 和错误的结构体布局 (`supported[256]` + `values[256]` 两个独立数组)。能"碰巧工作"是因为：
1. 索引 504 越界读取 256 元素数组，C++ heap 分配器给了更大内存块
2. 越界读恰好落到了 ADL 驱动写入的有效数据区域
3. `v / 1000` 对于毫度级值恰好给出了正确摄氏度

**已修复**（`src/amd_temp.cpp`），使用官方 SDK 的正确结构体和传感器 ID。

## MSIX AppContainer 环境限制

SysMonCmdPal 运行在 MSIX AppContainer 沙箱中（即使有 `runFullTrust` capability）。

### 已知被阻止的操作

| 操作 | 原因 | 影响 |
|------|------|------|
| WMI `Win32_VideoController` 查询 | AppContainer 下 WMI 查询系统类可能返回空 | 不能作为 GPU 存在性判断依据 |

### 不受影响的操作

| 操作 | 原因 |
|------|------|
| **PawnIO `DeviceIoControl`** | PawnIO 驱动 SDDL 包含 `IU`（Interactive User）权限，`runFullTrust` 进程可正常访问 |
| ADL `atiadlxx.dll` 加载 | 用户态 DLL，用户模式 IOCTL |
| ADL PMLOG 读取 | 用户态调用 |
| HWiNFO 共享内存读取 | `runFullTrust` 允许访问 `Global\` 命名内核对象 |
| NVAPI (nvapi64.dll) | 纯用户态 API，不涉及内核驱动 |
| IGCL (igcl.dll) | 纯用户态 API |
| `PerformanceCounter` | .NET 内置，不需要特殊权限 |

### 教训

**不要在 ADL 初始化前加 WMI 前置检查。** WMI 在 MSIX 下不可靠，应直接尝试加载 DLL 并依靠已有的错误处理链（DLL 加载失败 → adapter 枚举失败 → PMLOG 探测失败）来优雅降级。

## 构建工具链

### 安装脚本

```powershell
# 卸载旧版 + 安装新版
Get-AppxPackage -Name "SysMonCmdPal" | Remove-AppxPackage
& "path\to\Add-AppDevPackage.ps1" -Force
```

### 命令面板重载协议

```powershell
Start-Process 'x-cmdpal://reload'
```

触发 PowerToys Command Palette 重新装载所有扩展，无需重启 PowerToys。

### 日志路径

| 日志 | 路径 |
|------|------|
| 传感器后端 | `%LocalAppData%\SysMonCmdPal\sensor_backend.log` |
| ADL 调试 | `%LocalAppData%\SysMonCmdPal\adl_debug.log` |
| LHM 初始化 | `%LocalAppData%\SysMonCmdPal\lhm_init.log` |

## 已知限制

- PawnIO ring0 模块需 PawnIO 驱动已安装（`https://pawnio.eu`，安装后无需管理员）
- AMD SMU PM Table 版本依赖 CPU 代际（Rembrandt/Phoenix/StrixPoint 已验证）
- Intel IGCL 支持程度取决于驱动版本和 GPU 型号
- HWiNFO 回退模式每 12 小时需重置（共享内存重启）
- 传感器配置无版本迁移（当前 v1.0 schema）
- `GetDockBands()` 每次重建动态 SensorDockBand 列表（可优化为 diff）

## btop4win 集成

### 启动器

`BtopLauncherCommand.cs` 按以下优先级查找 btop：
1. 硬编码路径：`scoop\apps\btop-lhm\current\btop.exe`、`scoop\shims\btop.exe`、`scoop\apps\btop4win\current\btop4win.exe`
2. PATH 环境变量搜索 `btop.exe` 和 `btop4win.exe`

优先通过 Windows Terminal 启动（`wt.exe new-tab --cmdline btop.exe`），wt 不可用时 fallback 到 `Process.Start(UseShellExecute=true)`。

### btop4win 构建依赖

完整编译 btop4win 需要 Visual Studio BuildTools 的 **C++ ATL for latest v143 build tools (x86 & x64)** 组件（提供 `atlstr.h`）。Release|x64 配置已添加 `/utf-8` 编译选项解决中文字符串编码问题。

## 版本历史

| 版本 | 日期 | 变更 |
|------|------|------|
| 1.1.0 | 2026-06-12 | 官方 API 优先回退链：PawnIO MSR/SMU + NVAPI + ADL GPU + IGCL |
| 1.0.0 | 2026-06-09 | 初始版本 |
