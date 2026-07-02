# CLAUDE.md — sysmon-cmdpal

## 1. 软件定位 / 技术栈 / 技术蓝图

**这是什么**：PowerToys Command Palette 的系统监控扩展（SysMonCmdPal）。把 btop4win 的硬件指标采集移植到 C#/.NET，作为 Command Palette 的原生扩展运行，提供 CPU/内存/磁盘/网络/电池/GPU/全量传感器实时数据 + 7 个可固定的 Dock Band Widget + 8 个详情页 + btop4win 一键启动。开发者 darkstax。

**技术栈**：

| 层 | 技术 |
|----|------|
| 扩展框架 | PowerToys Command Palette Extension SDK（本地 `PowerToys-sdk/` 目录引用） |
| 语言/运行时 | C# (LangVersion=preview) / .NET 10 (`net10.0-windows10.0.26100.0`) |
| 宿主/打包 | Windows App SDK 1.6 + MSIX（`GenerateAppxPackageOnBuild=true`，商店安全，无 ring-0） |
| COM Server | `Shmuelie.WinRTServer`（让 .NET WinExe 作为 WinRT/COM 扩展被 Command Palette 加载） |
| Win32 互操作 | `Microsoft.Windows.CsWin32`（P/Invoke 源生成）+ `System.Diagnostics.PerformanceCounter` + `System.Management` |
| IPC | `MemoryMappedFile` 16KB + `EventWaitHandle`（Broker→Plugin 单向推送） |
| 硬件采集 | 主端：HWiNFO 共享内存 (`Global\HWiNFO_SENS_SM2`) + ACPI ThermalZone + D3DKMT (GPU 利用率) + PDH PerformanceCounter (GPU Engine)；Broker 端：`LibreHardwareMonitorLib 0.9.6` |
| i18n | .resw 资源（en-US + zh-CN） |
| 测试 | xUnit |
| AOT/Trim | Release 配置开启 `PublishTrimmed` + `IsAotCompatible` + `CsWinRTAotOptimizerEnabled` |

**技术蓝图（数据流）**：

```
[可选] SysMonBroker.exe (WinExe, 管理员, 自包含单文件)
    │  LHM 全量采集 (CPU/GPU/MB/Storage 的 temp/load/clock/power/fan/voltage)
    └──> Shared Memory v2 (16KB, SDDL ACL: Everyone 读 / Admin 写)
            │  + EventWaitHandle 通知
            ▼
SysMonCmdPal.exe (MSIX 扩展, runFullTrust, 用户态)
    ├── SharedMemoryReader (后台线程) → BrokerPushReceiver (lock 不可变快照, <10s 视为新鲜)
    ├── 传感器五层回退链:
    │     Tier 0: Broker SHM (LHM, 全量, 最高精度, 需管理员)
    │     Tier 1: HWiNFO SHM (用户态, CPU/GPU 温度+利用率+显存, ~12h 需重启)
    │     Tier 2: D3DKMT API (用户态, GPU 利用率, gdi32.dll P/Invoke, 无需管理员)
    │     Tier 3: PDH PerformanceCounter (用户态, GPU Engine 计数器, 无需管理员)
    │     CPU 温度: ThermalZone (Windows ACPI PerformanceCounter, 精度差 5-15°C)
    ├── SystemInfoService.Refresh() (1s 定时 → SystemSnapshot, Interlocked 防并发)
    │     + CPU% (PerformanceCounter, 异常自动重建) / CPU 频率 (基础频率 × 性能百分比 / 100)
    │       内存 (GlobalMemoryStatusEx) / 磁盘 (DriveInfo + LogicalDisk, 物理磁盘缓存 30s)
    │       网络 (物理接口 delta + EMA, 接口列表缓存 10s) / 电池 (GetSystemPowerStatus + WMI 趋势检测, 缓存 3s)
    ├── DockBandRefreshCoordinator (共享 1s timer, Interlocked.Exchange 防并发, 引用计数)
    │     → 7 个静态 Dock Band (CPU/内存/磁盘/下载/上传/电池/GPU) + 动态传感器 Band
    │     所有详情页通过 Subscribe(Update) 共享此 timer (不再有独立 timer)
    └── Pages (8 个详情页, 按需 FormContent/ListPage 渲染)
```

**权限分层**：主扩展为 `runFullTrust`（Package.appxmanifest 声明），始终用户态运行。仅可选 Broker 以管理员独立运行（不在 MSIX 内，独立分发），通过 SHM 把高精度数据"喂"回主扩展。Broker 缺席时主扩展自动降级：HWiNFO → D3DKMT → PDH → ThermalZone。D3DKMT 和 PDH 完全不需要管理员权限或第三方工具。

## 2. 代码仓库文件结构

```
sysmon-cmdpal/
├── SysMonCmdPal.sln              # 解决方案（只含主扩展 + 2 个 PowerToys SDK 项目；Broker/Tests/LhmTest 不在 sln，各自独立构建）
├── global.json                   # .NET SDK 10.0.300, rollForward=latestPatch
├── CLAUDE.md / README.md
├── setup.ps1 / deploy.ps1 / dev-deploy.ps1 / verify.ps1   # 构建/部署脚本
├── SysPulse_TemporaryKey.cer     # MSIX 签名证书
├── PowerToys-sdk/                # 符号链接 → 本地 PowerToys 仓库的 extensionsdk（提供 CmdPal SDK 源项目）
│
├── SysMonCmdPal/                 # 主扩展项目（MSIX, 商店安全）
│   ├── SysMonCmdPal.csproj       # .NET 10 + MSIX + AOT/Trim (Release)
│   ├── Package.appxmanifest      # MSIX 清单 + COM 注册 + CmdPal 扩展声明
│   ├── app.manifest              # 应用清单 (DPI/管理员/UAC)
│   ├── Program.cs                # COM Server 入口点
│   ├── SysMonExtension.cs        # IExtension 实现 + SharedMemoryReader 生命周期
│   ├── SysMonCommandsProvider.cs # 顶级命令 + Dock Band 注册 + 设置
│   ├── JsonHelper.cs             # JSON 辅助
│   ├── Public/manifest.json      # CmdPal AppExtension 元数据
│   ├── Broker/                   # 与 Broker 通信的接收侧
│   │   ├── ISysMonBrokerPush.cs      # COM 接口定义（推送契约）
│   │   ├── BrokerPushReceiver.cs     # 接收器 + 不可变快照 + 全量传感器存储
│   │   ├── BrokerDetector.cs         # Broker 进程存在性检测
│   │   ├── SharedMemoryReader.cs     # v2 SHM 布局读取器（CPU/GPU/全量传感器）
│   │   └── ShmLayout.cs              # SHM 布局常量 + 传感器分类标签
│   ├── Commands/
│   │   ├── SysMonDockBands.cs        # DockFormat + DockBandRefreshCoordinator + 7 静态 Band
│   │   └── BtopLauncherCommand.cs    # btop4win 一键启动 (scoop/PATH + Windows Terminal)
│   ├── Pages/                    # 8 个详情页
│   │   ├── SysMonMainPage.cs         # 主面板列表
│   │   ├── CpuDetailPage.cs          # CPU Markdown (+HWiNFO 12h 警告)
│   │   ├── MemoryDetailPage.cs       # 内存 Markdown
│   │   ├── DiskDetailPage.cs         # 磁盘 ListPage (IO 速度 + 卷标 + 使用率)
│   │   ├── NetworkDetailPage.cs      # 网络 Markdown + 活跃接口
│   │   ├── BatteryDetailPage.cs      # 电池 Markdown
│   │   ├── GpuDetailPage.cs          # GPU Markdown + VRAM + 后端状态
│   │   └── SensorListPage.cs         # 全量传感器浏览（按类别分组）
│   ├── Services/                 # 数据采集 + 回退链
│   │   ├── SystemInfoService.cs      # 系统数据聚合器（1s Refresh → SystemSnapshot, 加锁）
│   │   ├── CpuSensorReader.cs        # CPU 温度三层回退
│   │   ├── CpuFrequencyReader.cs     # CPU 频率 (任务管理器算法: base×perf%/100)
│   │   ├── GpuSensorReader.cs        # GPU 数据五层回退
│   │   ├── GpuAdapterEnumerator.cs   # DXGI COM interop 枚举 GPU adapter (LUID→名称, 缓存 30s)
│   │   ├── D3dkmtGpuReader.cs        # D3DKMT API GPU 利用率 (gdi32.dll, per-engine RunningTime delta)
│   │   ├── PdhGpuReader.cs           # PDH PerformanceCounter GPU 利用率 (GPU Engine 计数器)
│   │   ├── HwinfoSharedMemoryReader.cs  # HWiNFO Global\HWiNFO_SENS_SM2 读取
│   │   ├── ThermalZoneReader.cs      # Windows ACPI 热区 (PerformanceCounter, 首值重试)
│   │   ├── DiskMonitor.cs            # 磁盘 IO 监控 (物理磁盘 WMI 缓存 30s, 计数器异常重建)
│   │   ├── NetworkMonitor.cs         # 网络流量监控 (接口列表缓存 10s, EMA 平滑)
│   │   ├── BatteryQueryService.cs    # WMI 电池趋势检测 (缓存 3s, 双重供电判断)
│   │   ├── BatteryReportService.cs   # WinRT 电池健康报告 (30天缓存)
│   │   ├── SystemPowerReader.cs      # 系统功耗信息
│   │   ├── PdChargerDetector.cs      # USB-C/PD 充电检测 (SetupAPI)
│   │   ├── SensorLogger.cs           # 共享传感器日志
│   │   └── SparklineChart.cs         # 纯托管 PNG/SVG 火花线渲染
│   ├── Models/SensorChainConfig.cs   # 精简配置: 版本号 (PrecisionMode 设置已移除)
│   ├── Localization/Loc.cs           # i18n 辅助
│   ├── Strings/en-US/Resources.resw  # 英文资源
│   ├── Strings/zh-CN/Resources.resw  # 中文资源
│   ├── Assets/                       # MSIX 图标 + generate_icons.ps1
│   └── Properties/
│       ├── launchSettings.json
│       └── PublishProfiles/          # win-x64.pubxml / win-arm64.pubxml
│
├── SysMonBroker/                 # 可选提权代理 v2（独立分发，不在 sln）
│   ├── SysMonBroker.csproj       # .NET 10 WinExe, 自包含/单文件, win-x64 only
│   ├── Program.cs                # LHM 采集 + SHM 写入 (standalone)
│   ├── .devmode_marker           # 开发模式标记
│   ├── IPC/BrokerSharedMemory.cs # v2 布局: 16KB SHM 写入 + EventWaitHandle
│   ├── Logging/BrokerLogger.cs   # 缓冲 + 大小轮转日志
│   └── Sensors/SensorCollector.cs # LHM 全量传感器采集 (CPU/GPU/MB/Storage)
│
├── SysMonCmdPal.Tests/           # xUnit 测试（不在 sln，dotnet test 运行）
│   ├── SysMonCmdPal.Tests.csproj
│   ├── xunit.runner.json
│   ├── DockFormatTests.cs
│   ├── SensorChainConfigTests.cs
│   ├── SensorLoggerTests.cs
│   ├── SparklineChartTests.cs
│   └── SystemInfoServiceTests.cs
│
└── LhmTest/                      # LHM 独立测试工具（不在 sln）
    ├── LhmTest.csproj
    └── Program.cs
```

## 3. 编译工具

**SDK**：.NET 10.0.300+（`global.json` 锁定，`rollForward=latestPatch`）。需要 Windows 11 + PowerToys 已安装 + 开发者模式。

**MSBuild（主扩展 MSIX 打包）**：VS Build Tools 2026 自带的 MSBuild，路径固定为 `${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe`。主扩展必须用 MSBuild 而非 `dotnet build`，因为 MSIX 打包 (`GenerateAppxPackageOnBuild`) 依赖 MSBuild 的 AppX 工具链。

```powershell
$msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"

# Debug/Release 构建（生成 MSIX 包到 bin\<Platform>\<Config>\...\AppPackages\）
& $msbuild SysMonCmdPal.sln /p:Configuration=Release /p:Platform=x64 /m `
  /p:VcpkgEnabled=false /p:EnforceCodeStyleInBuild=false

# 安装 MSIX（开发者模式）
& ".\SysMonCmdPal\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AppPackages\SysMonCmdPal_*_x64_Test\Add-AppDevPackage.ps1"
Start-Process 'x-cmdpal://reload'   # 重载命令面板
```

**dotnet CLI（Broker / Tests / LhmTest，均不在 .sln 内）**：

```powershell
# SysMonBroker：自包含单文件发布（独立分发，需管理员运行）
dotnet publish SysMonBroker\SysMonBroker.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# 测试
dotnet test SysMonCmdPal.Tests\SysMonCmdPal.Tests.csproj

# LhmTest（调试用）
dotnet run --project LhmTest\LhmTest.csproj
```

**平台**：主扩展 `x64` + `ARM64`；Broker 仅 `win-x64`（自包含）。PowerToys SDK 通过 `PowerToys-sdk/` 符号链接解析，或设置 `POWERTOYS_REPO` 环境变量指向完整 PowerToys 仓库。

**部署期管理员操作**（部署/停止 Broker）：已配置 `gsudo`（缓存 5 分钟），用 `sudo` 而非反复弹 UAC：
```powershell
sudo Get-Process SysMonBroker | Stop-Process -Force
sudo Copy-Item publish\SysMonBroker.exe "$env:LOCALAPPDATA\SysMonBroker\SysMonBroker.exe" -Force
sudo Start-Process "$env:LOCALAPPDATA\SysMonBroker\SysMonBroker.exe"
```
