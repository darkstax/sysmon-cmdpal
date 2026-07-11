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
                                   ├── SysMonDockBands         (DockFormat + 7 种 DockBand)
                                   ├── SystemInfoService       (单例: P/Invoke + 传感器回退)
                                   ├── Broker/                 (共享内存读取 + 快照管理)
                                   │   ├── SharedMemoryReader  (后台线程读 Broker SHM)
                                   │   ├── BrokerPushReceiver  (不可变快照存储)
                                   │   ├── BrokerDetector      (Broker 进程检测)
                                   │   └── ShmLayout           (SHM v2 布局常量)
                                   ├── Services/
                                   │   ├── CpuSensorReader     (CPU 温度三层回退)
                                   │   ├── GpuSensorReader     (GPU 数据三层回退)
                                   │   ├── HwinfoSharedMemoryReader (HWiNFO SHM 读取)
                                   │   ├── ThermalZoneReader   (ACPI 热区回退)
                                   │   ├── SensorLogger        (传感器日志)
                                   │   └── SparklineChart      (PNG 火花线渲染)
                                   ├── Pages/                   (8 个详情页)
                                   ├── Commands/                (Dock Band + btop 启动器)
                                   └── Models/                  (SensorChainConfig)
```

**可选 Broker**（管理员权限，独立分发）:

```
SysMonBroker.exe (.NET 10 WinExe, 管理员)
    ├── SensorCollector     (LHM 全量传感器采集)
    ├── BrokerSharedMemory  (16KB SHM v2 写入 + EventWaitHandle 通知)
    └── BrokerLogger        (缓冲 + 轮转日志)
         ↓
    SharedMemory v2 (16KB) → Plugin 传感器数据
```

## 传感器回退链

```
GPU 数据 (名称/利用率/温度/显存):
  Broker SHM v2 (LHM, 管理员) ──✓──> 全功能
      │ ✗ 或数据过期 (>10s)
      ├── HWiNFO SHM ──✓──> 名称+利用率+温度+显存 (用户态, 每 ~12h 需重启)
      │           │ ✗
      │           └── 不可用 → 下层
      ├── D3DKMT API ──✓──> 名称+利用率 (用户态, gdi32.dll, 无需管理员/第三方工具)
      │           │ ✗
      │           └── 不可用 → 下层
      └── PDH PerformanceCounter ──✓──> 名称+利用率 (用户态, GPU Engine 计数器, 无需管理员)
                  │ ✗
                  └── 不可用 → 无 GPU 数据

CPU 温度:
  Broker SHM v2 (LHM, 管理员) ──✓──> 最高精度
      │ ✗ 或数据过期 (>10s)
      ├── HWiNFO SHM ──✓──> CPU 温度 (用户态, 每 ~12h 需重启)
      │           │ ✗
      │           └── 不可用 → 下层
      └── ThermalZone (ACPI) ──✓──> 精度差 5-15°C, 聊胜于无
```

Broker 不可用时 GPU 依次降级到 HWiNFO → D3DKMT → PDH，CPU 温度降级到 HWiNFO → ThermalZone。
D3DKMT 和 PDH 完全不需要管理员权限或第三方工具。UI 实时显示当前数据源。

## 数据采集

| 指标 | 采集方式 |
|------|----------|
| CPU 使用率 | `PerformanceCounter("Processor", "% Processor Time", "_Total")` (异常自动重建) |
| CPU 频率 | `基础频率 × % Processor Performance / 100` (任务管理器算法, `_Total` 实例) |
| 内存 | `GlobalMemoryStatusEx` P/Invoke |
| 磁盘 IO | `PerformanceCounter("LogicalDisk")` Read/Write Bytes/sec (异常自动重建) |
| 磁盘空间 | `System.IO.DriveInfo` (含卷标) |
| 物理磁盘 | WMI `Win32_DiskDrive` (缓存 30s, 总线类型推断 NVMe/USB/Thunderbolt) |
| 网速 | `NetworkInterface.GetAllNetworkInterfaces()` delta (仅物理接口, EMA 平滑, 接口列表缓存 10s) |
| 电池 | `GetSystemPowerStatus` P/Invoke + WMI `BatteryStatus` 趋势检测 (缓存 3s) |
| 电池健康 | WinRT `Battery.GetReport()` (30天缓存) |
| CPU 温度 | **Tier 1**: Broker SHM → **Tier 2**: HWiNFO SHM → **Tier 3**: ThermalZone |
| GPU 利用率 | **Tier 1**: Broker SHM → **Tier 2**: HWiNFO SHM → **Tier 3**: D3DKMT API → **Tier 4**: PDH PerformanceCounter |
| GPU 温度 | **Tier 1**: Broker SHM → **Tier 2**: HWiNFO SHM |
| 传感器全量 | Broker v2 SHM: LHM 全量 (CPU/GPU/MB/Storage 的 temp/load/clock/power/fan/voltage) |
| GPU 详情 | Broker SHM: 名称、使用率、温度、显存 |
| GPU 名称 | Broker SHM / DXGI `IDXGIAdapter1` COM interop (LUID→名称映射, 缓存 30s) |

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

### SysMonBroker 独立构建（可选，独立分发）

```powershell
dotnet publish SysMonBroker\SysMonBroker.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## 项目结构

```
sysmon-cmdpal/
├── SysMonCmdPal.sln
├── SysMonCmdPal/                         # 主扩展项目 (MSIX, 商店安全)
│   ├── SysMonCmdPal.csproj               # .NET 10 + MSIX + AOT/Trim
│   ├── Package.appxmanifest              # MSIX 包清单 + CmdPal 扩展声明
│   ├── Program.cs                        # COM Server 入口点
│   ├── SysMonExtension.cs                # IExtension 实现 + SharedMemoryReader 生命周期
│   ├── SysMonCommandsProvider.cs         # 顶级命令 + Dock Band 注册 + 设置
│   ├── Broker/
│   │   ├── ISysMonBrokerPush.cs          # COM 接口定义（Broker 推送契约）
│   │   ├── BrokerPushReceiver.cs         # 接收器 + 不可变快照 + 全量传感器存储
│   │   ├── BrokerDetector.cs             # Broker 进程存在性检测
│   │   ├── SharedMemoryReader.cs         # v2 布局读取器（CPU/GPU/全量传感器）
│   │   └── ShmLayout.cs                  # 共享内存布局常量 + 传感器分类标签
│   ├── Commands/
│   │   ├── SysMonDockBands.cs            # DockFormat + DockBandRefreshCoordinator + 7 Band
│   │   └── BtopLauncherCommand.cs        # btop4win 一键启动
│   ├── Pages/
│   │   ├── SysMonMainPage.cs             # 主页列表
│   │   ├── CpuDetailPage.cs              # CPU Markdown
│   │   ├── MemoryDetailPage.cs           # 内存 Markdown
│   │   ├── DiskDetailPage.cs             # 磁盘 ListPage
│   │   ├── NetworkDetailPage.cs          # 网络 Markdown
│   │   ├── BatteryDetailPage.cs          # 电池 Markdown
│   │   ├── GpuDetailPage.cs              # GPU Markdown
│   │   └── SensorListPage.cs             # 全量传感器浏览
│   ├── Services/
│   │   ├── SystemInfoService.cs      # 系统数据聚合器 (1s Refresh, 加锁)
│   │   ├── CpuSensorReader.cs        # CPU 温度三层回退
│   │   ├── CpuFrequencyReader.cs     # CPU 频率 (任务管理器算法)
│   │   ├── GpuSensorReader.cs        # GPU 数据五层回退
│   │   ├── GpuAdapterEnumerator.cs   # DXGI COM interop GPU 枚举
│   │   ├── D3dkmtGpuReader.cs        # D3DKMT API GPU 利用率 (无需管理员)
│   │   ├── PdhGpuReader.cs           # PDH PerformanceCounter GPU 利用率
│   │   ├── HwinfoSharedMemoryReader.cs # HWiNFO SHM 读取
│   │   ├── ThermalZoneReader.cs      # ACPI 热区
│   │   ├── DiskMonitor.cs            # 磁盘 IO (WMI 缓存 30s)
│   │   ├── NetworkMonitor.cs         # 网络流量 (接口缓存 10s)
│   │   ├── BatteryQueryService.cs    # WMI 电池趋势检测 (缓存 3s)
│   │   ├── SensorLogger.cs           # 传感器日志
│   │   └── SparklineChart.cs         # PNG/SVG 火花线渲染
│   └── Models/
│       └── SensorChainConfig.cs          # 精简版配置
├── SysMonCmdPal.Tests/                   # 自动化测试 (xUnit)
├── SysMonBroker/                         # 可选提权代理 v2.3 (独立分发)
│   ├── SysMonBroker.csproj               # .NET 10 WinExe, 自包含/单文件
│   ├── Program.cs                        # LHM 采集 + SHM 写入
│   ├── IPC/
│   │   └── BrokerSharedMemory.cs         # v2 布局: 16KB SHM 写入
│   ├── Logging/
│   │   └── BrokerLogger.cs               # 缓冲 + 轮转日志
│   └── Sensors/
│       └── SensorCollector.cs            # LHM 全量传感器采集
└── LhmTest/                              # LHM 独立测试工具
```

## 关联项目

- [btop4win](https://github.com/aristocratos/btop4win) — C++ 系统监控（Win32 API 参考实现）
- [PowerToys](https://github.com/microsoft/PowerToys) — Command Palette 宿主
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) — 硬件传感器库

## License

MIT
