# SysMonCmdPal — System Monitor for Command Palette

PowerToys Command Palette 系统监控扩展。把 btop4win 的核心指标带进 Command Palette Dock 栏。

## 功能

| 模块 | 功能 |
|------|------|
| 🖥 CPU | 使用率、核心数、温度 |
| 🧠 内存 | 已用/总量 GB、百分比 |
| 💾 磁盘 | 各分区使用率、剩余空间、IO 读写速度 + 卷标 |
| 🌐 网络 | 上下行分开显示（两个独立 Dock Band） |
| 🔋 电池 | 电量、充电/放电/满电状态 |
| 🎮 GPU | 名称、使用率、温度、显存 |
| 📡 传感器列表 | 全量 LHM 传感器按类别浏览，点击添加到 Dock |
| 📌 Dock Band | CPU / 内存 / 磁盘 / 网络↓ / 网络↑ / 电池 / GPU + 动态传感器（1s 刷新） |
| 🚀 btop4win | 一键启动完整系统监控 |

## 架构

```
Command Palette ← WinRT/COM → SysMonCmdPal.exe (.NET 10)
                                   ├── SysMonCommandsProvider  (顶级命令 + Dock Band 注册)
                                   ├── SysMonDockBands         (DockFormat + 8 种 DockBand)
                                   ├── SystemInfoService       (单例: P/Invoke + 传感器回退)
                                   ├── LhmSensorService        (全量 LHM 传感器分类)
                                   ├── AmdTempReader           (ADL + HWiNFO 回退)
                                   ├── Models/
                                   │   ├── SensorReading       (17 类传感器模型)
                                   │   └── SensorCategoryMeta  (类别排序 + 中文名 + 图标缓存)
                                   ├── Pages/
                                   │   ├── SysMonMainPage      (列表主页 + 后端状态)
                                   │   ├── SensorListPage      (全量传感器浏览)
                                   │   ├── CpuDetailPage       (CPU Markdown)
                                   │   ├── MemoryDetailPage    (内存 Markdown)
                                   │   ├── DiskDetailPage      (磁盘列表 + IO 速度)
                                   │   ├── NetworkDetailPage   (网络 Markdown + 接口)
                                   │   ├── BatteryDetailPage   (电池 Markdown)
                                   │   └── GpuDetailPage       (GPU + 温度 Markdown)
                                   └── Commands/
                                       ├── BtopLauncherCommand (btop4win 启动器)
                                       └── ToggleSensorCommand  (传感器 Dock 开关)
```

## 传感器回退链

```
LHM (PawnIO 驱动) ──✓──> 全功能 (CPU + GPU + 所有传感器)
        │ ✗
        ├── AMD ADL (atiadlxx.dll) ──✓──> 仅 CPU 温度 (用户态)
        │         │ ✗
        │         └── HWiNFO 共享内存 ──✓──> CPU + GPU 温度
        │                       │ ✗
        │                       └── 不可用 → UI 显示后端状态
```

LHM 崩溃时自动降级，UI 实时显示当前数据源。

## 数据采集

| 指标 | 采集方式 |
|------|----------|
| CPU 使用率 | `PerformanceCounter("Processor", "% Processor Time", "_Total")` |
| 内存 | `GlobalMemoryStatusEx` P/Invoke |
| 磁盘 IO | `PerformanceCounter("LogicalDisk")` Read/Write Bytes/sec |
| 磁盘空间 | `System.IO.DriveInfo` (含卷标) |
| 网速 | `NetworkInterface.GetAllNetworkInterfaces()` delta |
| 电池 | `GetSystemPowerStatus` P/Invoke |
| CPU/GPU 温度 | **Tier 1**: LHM (PawnIO) → **Tier 2**: AMD ADL → **Tier 3**: HWiNFO |
| 传感器全量 | LibreHardwareMonitorLib sensor tree (17 类别) |
| GPU 详情 | LHM: 名称、使用率、温度、显存 |

## 构建

### 前置条件

- Windows 11 + PowerToys 已安装 + 开发者模式
- .NET 10 SDK（10.0.300+）
- VS Build Tools 2026（或 VS 2022+ IDE）

### 一键构建

```powershell
$msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild SysMonCmdPal.sln /p:Configuration=Debug /p:Platform=x64 /m `
  /p:VcpkgEnabled=false /p:EnforceCodeStyleInBuild=false
# MSIX 包位于 bin/x64/Debug/.../AppPackages/
```

### 发布

```powershell
& $msbuild SysMonCmdPal.sln /p:Configuration=Release /p:Platform=x64 /m `
  /p:VcpkgEnabled=false /p:EnforceCodeStyleInBuild=false
# 安装: 双击 .msix 证书 + .msixbundle
```

## 项目结构

```
SysMonCmdPal/
├── SysMonCmdPal.csproj           # .NET 10 + MSIX + LHM + AOT/Trim
├── Package.appxmanifest          # MSIX 包清单 + COM 注册 + 扩展声明
├── app.manifest                  # DPI 感知 + Windows 10 兼容性
├── Program.cs                    # COM Server 入口 (-RegisterProcessAsComServer)
├── SysMonExtension.cs            # IExtension 实现 (COM CLSID)
├── SysMonCommandsProvider.cs     # 顶级命令 + Dock Band 注册
├── Commands/
│   ├── SysMonDockBands.cs        # DockFormat + CPU/内存/磁盘/网络/电池/GPU/Sensor DockBand
│   ├── BtopLauncherCommand.cs    # btop4win 一键启动
│   └── ToggleSensorCommand.cs    # 传感器 Dock 添加/移除
├── Pages/
│   ├── SysMonMainPage.cs         # 主页（列表 + 后端状态）
│   ├── SensorListPage.cs         # 全量传感器浏览（17 类别）
│   ├── CpuDetailPage.cs          # CPU 详情（Markdown）
│   ├── MemoryDetailPage.cs       # 内存详情（Markdown）
│   ├── DiskDetailPage.cs         # 磁盘详情（列表 + IO）
│   ├── NetworkDetailPage.cs      # 网络详情（Markdown + 接口列表）
│   ├── BatteryDetailPage.cs      # 电池详情（Markdown）
│   └── GpuDetailPage.cs          # GPU 详情（Markdown）
├── Services/
│   ├── SystemInfoService.cs      # 系统数据采集（P/Invoke） + 传感器回退链
│   ├── LhmSensorService.cs       # 全量 LHM 传感器分类 + 健康追踪
│   └── AmdTempReader.cs          # AMD ADL + HWiNFO 共享内存回退
├── Models/
│   ├── SensorReading.cs          # 传感器模型（17 类别枚举 + 配置）
│   └── SensorCategoryMeta.cs     # 类别元数据 + 图标缓存
└── Assets/                       # MSIX 图标资源
```

## 关联项目

- [btop4win](https://github.com/aristocratos/btop4win) — C++ 系统监控（Win32 API 参考实现）
- [PowerToys](https://github.com/microsoft/PowerToys) — Command Palette 宿主
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) — 硬件传感器库
- [PawnIO](https://pawnio.eu) — 免管理员硬件传感器驱动

## License

MIT
