# CLAUDE.md — sysmon-cmdpal

## 概述

PowerToys Command Palette 系统监控扩展（v1.6.0）。将 btop4win 的硬件指标采集移植到 C#/.NET，作为 Command Palette 的原生扩展运行。MSIX 主包商店安全版，可选的 SysMonBroker 提权进程通过共享内存 v2 推送全量 LHM 传感器数据，并通过 COM Local Server 为 btop4win 提供完整进程列表（管理员级 SE_DEBUG 采集）。

**开发者：** darkstax

## 技术栈

| 层 | 技术 |
|----|------|
| 扩展框架 | PowerToys Command Palette Extension SDK |
| 语言 | C# 12 (.NET 10) |
| UI | Command Palette 原生页面（List / Markdown） + Dock Band |
| 通信 | 共享内存 v2 (MemoryMappedFile 16KB) + EventWaitHandle |
| 打包 | MSIX (Windows App SDK) — 商店安全，无 ring-0 嵌入 |
| 数据采集 | Win32 P/Invoke + PerformanceCounter + HWiNFO SHM + LHM (via Broker) |
| 可选代理 | SysMonBroker v2 (WinExe, LHM thin-shell, 共享内存推送全量传感器) |
| 测试 | xUnit 2.9 + Moq 4.20 |

## 解决方案结构

```
sysmon-cmdpal/
├── SysMonCmdPal.sln
├── SysMonCmdPal/                         # 主扩展项目 (MSIX, 商店安全)
├── SysMonCmdPal.Tests/                   # 自动化测试项目 (xUnit)
├── SysMonBroker/                         # 可选提权代理进程 v2 (独立分发)
└── LhmTest/                              # LHM 独立测试工具
```

### SysMonCmdPal/ — 主扩展项目

```
SysMonCmdPal/
├── SysMonCmdPal.csproj           # .NET 10 + MSIX + AOT/Trim
├── Package.appxmanifest          # MSIX 包清单 + COM 注册 + CmdPal 扩展声明
├── Program.cs                    # COM Server 入口点
├── SysMonExtension.cs            # IExtension 实现 + SharedMemoryReader 生命周期
├── SysMonCommandsProvider.cs     # 顶级命令 + Dock Band 注册 + 设置
├── Broker/
│   ├── ISysMonBrokerPush.cs      # COM 接口定义（Broker 推送契约）
│   ├── BrokerPushReceiver.cs     # 接收器 + 不可变快照 + 全量传感器存储
│   ├── BrokerDetector.cs         # Broker 进程存在性检测
│   ├── SharedMemoryReader.cs     # v2 布局读取器（CPU/GPU/全量传感器）
│   └── ShmLayout.cs              # 共享内存布局常量 + 传感器分类标签
├── Commands/
│   ├── SysMonDockBands.cs        # DockFormat + DockBandRefreshCoordinator + 7 静态 Band
│   └── BtopLauncherCommand.cs    # btop4win 一键启动（scoop/PATH + Windows Terminal）
├── Pages/
│   ├── SysMonMainPage.cs         # 主页列表（CPU/内存/磁盘/网络/电池/GPU/传感器/btop）
│   ├── CpuDetailPage.cs          # CPU Markdown（温度 + 后端状态 + 12h 警告）
│   ├── MemoryDetailPage.cs       # 内存 Markdown
│   ├── DiskDetailPage.cs         # 磁盘 ListPage（IO 速度 + 卷标 + 使用率）
│   ├── NetworkDetailPage.cs      # 网络 Markdown + 活跃接口列表
│   ├── BatteryDetailPage.cs      # 电池 Markdown
│   ├── GpuDetailPage.cs          # GPU Markdown + VRAM + 后端状态
│   └── SensorListPage.cs         # 全量传感器浏览（Broker 共享内存数据，按类别分组）
├── Services/
│   ├── SystemInfoService.cs      # 系统数据聚合器（CPU%/内存/磁盘/网络/电池 + 传感器编排）
│   ├── CpuSensorReader.cs        # CPU 温度三层回退: Broker → HWiNFO → ThermalZone
│   ├── GpuSensorReader.cs        # GPU 数据三层回退: Broker → HWiNFO → ThermalZone
│   ├── HwinfoSharedMemoryReader.cs # HWiNFO Global\HWiNFO_SENS_SM2 共享内存读取
│   ├── ThermalZoneReader.cs      # Windows ACPI 热区 (PerformanceCounter)
│   ├── SensorLogger.cs           # 共享传感器日志工具
│   └── SparklineChart.cs         # 纯托管 PNG 火花线渲染器
├── Models/
│   └── SensorChainConfig.cs      # 精简版配置: PrecisionMode (Broker/None) + 版本
├── Assets/                       # MSIX 图标资源
└── Properties/
    └── PublishProfiles/          # win-x64 / win-arm64 发布配置
```

### SysMonBroker/ — 可选提权代理进程 v2

```
SysMonBroker/
├── SysMonBroker.csproj           # .NET 10 WinExe, 自包含/裁剪/单文件
├── Program.cs                    # LHM + SharedMemory + JSON 快照 + COM 服务器注册
├── IPC/
│   └── BrokerSharedMemory.cs     # v2 布局: 16KB SHM + CPU/GPU/全量传感器写入
├── COM/
│   ├── IBrokerInterfaces.cs      # COM 接口定义 (IBrokerService/Process/Sensor + GUIDs)
│   ├── BrokerComServer.cs        # COM Local Server 实现 + 硬编码 SHA256 认证
│   ├── DevModeVerifier.cs        # SSH RSA 签名验证 .devmode 调试模式
│   └── ProcessCollector.cs       # Win32 进程采集 (SE_DEBUG + CPU% delta + IO + 用户名)
└── Sensors/
    └── SensorCollector.cs        # LHM 全量传感器采集 (CPU/GPU/MB/Storage)
```

SysMonBroker v2 以管理员权限运行，通过 LHM 采集全量传感器（CPU/GPU/主板/存储的温度、负载、频率、功耗、风扇、电压），通过 16KB 共享内存推送给 Plugin。同时暴露 COM Local Server 接口，btop4win 通过 `CoCreateInstance` 获取完整进程列表（SE_DEBUG 权限，无需自身提权）和传感器数据。每 20 秒写入 JSON 快照作为备用通道。

## 关键架构

### 整体数据流 (v1.6)

```
[可选: SysMonBroker v2 (LHM 全量采集, 管理员)]
        ├── Shared Memory v2 (16KB) → Plugin 传感器数据
        ├── COM Local Server → btop4win 进程列表 + 传感器 (IBrokerProcessService/IBrokerSensorService)
        └── JSON Snapshot (20s) → btop4win 备用数据通道

Plugin 端:
[SharedMemoryReader (后台线程) → BrokerPushReceiver (不可变快照, volatile 交换)]
        ↓ 查询 IsFresh (<10s)
[CpuSensorReader / GpuSensorReader]
        ↓ Broker 不可用时走回退链
[HWiNFO SHM → ThermalZone]
        ↓
[SystemInfoService.Refresh() (1s 定时, SystemSnapshot)]
        ↓
[DockBand (实时 Widget) / Pages (按需详情页) / SensorListPage (全量浏览)]

btop4win 端:
[BrokerClient (COM CoCreateInstance)]
        ↓ 连接成功 → broker.getProcesses() (完整进程列表, 管理员级数据)
        ↓ 连接失败 → CreateToolhelp32Snapshot (本地采集, 非管理员受限)
```

### 传感器回退链 (v1.5 三层)

```
CPU 温度:
  1. Broker 共享内存推送 (if IsFresh && temp > 0)     ← 带外优先
  2. HWiNFO 共享内存 (Global\HWiNFO_SENS_SM2)        ← 用户态，不需管理员
  3. ThermalZone (Windows ACPI PerformanceCounter)    ← 精度差 5-15°C

GPU 数据:
  1. Broker 共享内存推送 (if IsFresh && gpus.Count > 0) ← 带外优先
  2. HWiNFO 共享内存 (GPU 温度标签匹配)              ← 用户态
  3. ThermalZone (ACPI)                               ← 精度差
```

### HWiNFO 共享内存读取

`HwinfoSharedMemoryReader` 从 HWiNFO 的 `Global\HWiNFO_SENS_SM2` 读取传感器数据：

- **签名**: `0x53695748` ("HWiS")
- **Header**: 12 × int32，其中 [8]=条目偏移, [9]=条目大小, [10]=条目数量
- **HWiNFOReading** (316 bytes, pack=1):
  - +0: int32 类型 (1=温度)
  - +12: char[128] 传感器标签
  - +284: double 当前值
- **CPU 标签**: "CPU Package", "Tctl/Tdie", "CPU Die", "CPU CCD", "CPU Tctl"
- **GPU 标签**: "GPU Core", "GPU Hot Spot", "GPU Junction", "GPU Temperature"
- **12 小时重置**: 连接超过 11 小时后在 UI 显示警告，提示用户重启 HWiNFO
- **自动重连**: 30 秒冷却后自动重试连接

### Broker 共享内存 v2 布局

```
Offset  Size  Field
0       4     Magic (0x5342524B "SBRK")
4       4     Version (=2)
8       4     Counter (递增，MemoryBarrier 保证可见性)
16      8     CpuTemp (double)
24      32    CpuSource (UTF-8, zero-padded)
56      4     GpuCount
60      288   GpuEntries (4 × 72 bytes: name[32] + temp + usage + memUsed + memTotal)
348     8     Timestamp (Ticks)
360     4     SensorCount (v2)
364     8192  SensorEntries (128 × 64 bytes: tag[4] + name[32] + value[8] + unit[16] + hwTag[4])
Total: 16384 bytes (16KB)
```

**共享内存 ACL (v2.2)**:
- SDDL: `D:(A;;GR;;;WD)(A;;GA;;;BA)` — Everyone 可读，仅 Administrators 可写
- 通过 `SetSecurityInfo` 在 Broker 启动时应用到内核对象
- 非管理员进程 `OpenFileMapping(FILE_MAP_WRITE)` 会被拒绝（error 5 = Access Denied）

**传感器分类标签 (tag)**:
0=CpuTemp, 1=CpuLoad, 2=CpuClock, 3=CpuPower, 4=CpuVoltage,
5=GpuTemp, 6=GpuLoad, 7=GpuClock, 8=GpuPower, 9=GpuMemory, 10=GpuFan, 11=GpuVoltage,
12=MbTemp, 13=MbFan, 14=MbVoltage, 15=StorageTemp, 16=StorageLoad

### COM Local Server 架构

Broker 作为 COM Local Server 运行，跨进程为 btop4win 提供服务：

```
[btop4win (C++, 非管理员)]
    ↓ CoCreateInstance(CLSID_BrokerService, CLSCTX_LOCAL_SERVER)
    ↓ COM 跨进程 marshaling (标准 marshaling, 无 type library)
[SysMonBroker (C#, 管理员)]
    ↓ ProcessCollector: CreateToolhelp32Snapshot + OpenProcess (SE_DEBUG)
    ↓ SensorCollector: LHM 全量传感器
    ↓ 2s 缓存 (避免多客户端频繁枚举)
```

**关键设计决策**:
- `InterfaceIsIUnknown` — 不用 IDispatch，C++ 端直接 vtable 调用
- BSTR 字符串字段 — 跨 COM 边界安全（不像 ByValTStr 固定长度）
- SAFEARRAY(VT_RECORD) — 结构体数组的标准 COM 传递方式
- 自注册 — Broker 启动时写入 `HKCR\CLSID\...\LocalServer32`（需管理员）
- 客户端计数 — `Interlocked` 追踪活跃客户端，60s 无客户端时 COM server 模式退出
- CPU% delta — ProcessCollector 内部维护 `_prevCpuTimes` 字典，两次快照差值 / 系统 CPU 差值

### COM 安全：客户端身份白名单

Broker 以管理员 + SE_DEBUG 运行，COM 接口暴露了 `KillProcess()` 和完整进程枚举——必须防止非授权客户端连接。

**机制**：硬编码 SHA256 文件哈希 + .devmode SSH 签名调试绕过。

```
认证流程 (v2.2):
btop.exe                                SysMonBroker (admin)
   ↓ connect()                              ↑
   ↓ computeSelfHash() — CNG SHA256(exe)    ↑
   ↓ Authenticate(pid, hash_hex)  ────→  检查 .devmode
   ↓                                      ↓ DevModeVerifier.IsDevModeActive()
   ↓                                      ↓   存在 → 验证 SSH RSA 签名 → 通过
   ↓                                      ↓   不存在 → 比对硬编码 BtopExeHash
   ↓ ← 0=成功 / 1=拒绝 / 3=其他错误
   ↓ (成功 → _authenticated=true)
   ↓ getProcesses() / killProcess() — 需 _authenticated
```

**硬编码 hash**:
- btop.exe SHA256 硬编码在 `BrokerComServer.cs` 的 `BtopExeHash` 常量中
- btop 更新时重新编译 Broker 即可（`Get-FileHash btop.exe -Algorithm SHA256`）

**.devmode 调试模式**:
- 文件: `%LOCALAPPDATA%\SysMonCmdPal\.devmode`（base64 编码的 SSH RSA 签名）
- 激活: `echo -n "SysMonBroker.DevMode.v2.2" | openssl dgst -sha256 -sign ~/.ssh/id_rsa | base64 -w0 > .devmode`
- 验证: Broker 读取 `~/.ssh/id_rsa.pub`，解析 OpenSSH RSA 公钥，验证签名
- 安全: 攻击者必须同时拥有 SSH 私钥才能激活 devmode

**权限门控**:
- `GetProcesses()` / `KillProcess()` → `RequireAuth()` 检查 `_authenticated`
- 未认证调用 → 抛出 `COMException(E_ACCESSDENIED)`
- `GetProcessCount()` / `Refresh()` / `IsAlive()` / `GetVersion()` → 无需认证（低敏感）

**实现文件**: `SysMonBroker/COM/BrokerComServer.cs`（硬编码 hash + 认证）、`SysMonBroker/COM/DevModeVerifier.cs`（SSH 签名验证）

### btop4win 集成

SysMonBroker 通过三种方式为 btop4win 提供数据：

**1. COM Local Server (主要通道)**

Broker 注册为 COM Local Server (`HKCR\CLSID\{7B3F8A1C-9D2E-4F50-B6C7-D8E9F0A1B2C3}\LocalServer32`)，btop4win 通过 `CoCreateInstance` 连接。

| 接口 | IID 末段 | 用途 |
|------|---------|------|
| `IBrokerService` | `...B2C4` | 入口：获取子服务 + `IsAlive()` + `GetVersion()` |
| `IBrokerProcessService` | `...B2C5` | `GetProcesses()` + CPU% delta + `KillProcess()` + `Authenticate(pid, hash)` |
| `IBrokerSensorService` | `...B2C6` | CPU/GPU 温度 + 全量传感器 |

**COM 结构体** (pack=8, BSTR 字符串):
- `BrokerProcessEntry` (96 bytes): Pid, ParentPid, Threads, Name, CommandLine, UserName, PrivateMemoryBytes, CpuPercent, CreationTime, KernelTime, UserTime, IoReadBytes, IoWriteBytes
- `BrokerSensorEntry` (32 bytes): CategoryTag, Name, Value, Unit

**btop4win 集成流程** (`broker_client.hpp`):
1. `Proc::collect()` 首先尝试 `BrokerClient::connect()`
2. 连接成功 → `authenticate()`（SHA256 白名单校验）
3. 认证成功 → `getProcesses()`（完整进程列表，管理员级 SE_DEBUG）
4. 认证失败 / 连接失败 → 回退到本地 `CreateToolhelp32Snapshot`（非管理员受限）
5. 进程结束：`btop_menu.cpp` 先尝试本地 `TerminateProcess`，失败后通过 `Proc::brokerKillPid()` 代理给 Broker（需已认证）
6. Broker 断连时自动检测并回退，每 30 个采集周期重试连接

**2. JSON 快照 (备用通道)**

每 20 秒写入: `%LOCALAPPDATA%\SysMonCmdPal\sensor_snapshot.json`

**3. 共享内存 (Plugin 端)**

16KB MemoryMappedFile 仅供 Plugin 读取（btop4win 通过 COM 获取数据）。

### 权限分层

| 层级 | 组件 | 需要管理员 | 说明 |
|------|------|-----------|------|
| Tier 0 | SysMonBroker v2 (LHM + SHM) | Broker 以管理员运行 | 全量传感器 + 最高精度 |
| Tier 1 | HwinfoSharedMemoryReader | 否 | HWiNFO 运行时可用，12h 重置 |
| Tier 2 | ThermalZoneReader (ACPI) | 否 | Windows 原生，精度差 |

### 刷新协调器

`DockBandRefreshCoordinator` — 共享 1s timer，所有 DockBand 订阅同一刷新事件。`Interlocked.Exchange` 防并发。引用计数管理。

### 配置持久化

| 文件 | 内容 |
|------|------|
| `settings.json` | PrecisionMode (Broker/None) |

存储在 `%LOCALAPPDATA%\SysMonCmdPal\`。

## 数据采集

| 指标 | 实现 |
|------|------|
| CPU 使用率 | `PerformanceCounter("% Processor Time", "_Total")` 单例复用 |
| 内存 | `GlobalMemoryStatusEx` P/Invoke，GC 回退 |
| 磁盘空间 | `DriveInfo` + `PerformanceCounter("LogicalDisk")` IO 读写字节 |
| 网络 | 仅物理接口 delta，EMA 平滑，排除虚拟网卡 |
| 电池 | `GetSystemPowerStatus` P/Invoke |
| CPU 温度 | Broker SHM → HWiNFO SHM → ThermalZone |
| GPU 温度 | Broker SHM → HWiNFO SHM → ThermalZone |
| 全量传感器 | Broker v2 SHM: LHM 全量 (CPU/GPU/MB/Storage 的 temp/load/clock/power/fan/voltage) |

## 用户可见命令结构

```
[根命令] "系统监控"
  |
  +-- [ListPage] 主面板
  |     |-- CPU → [ContentPage] CPU 详情 (+HWiNFO 12h 警告)
  |     |-- 内存 → [ContentPage] 内存详情
  |     |-- 磁盘 → [ListPage] 磁盘列表
  |     |-- 网络 → [ContentPage] 网络详情
  |     |-- 电池 → [ContentPage] 电池详情
  |     |-- GPU → [ContentPage] GPU 详情
  |     |-- 全部传感器 → [ListPage] 按类别分组浏览 (Broker 数据)
  |     +-- 启动 btop4win → [Invokable] Windows Terminal
  |
  +-- [Dock Bands] (7 个可固定的实时 Widget, 1s 刷新)
        |-- CPU / 内存 / 磁盘 / 下载 / 上传 / 电池 / GPU
```

## 开发模式

**管理员权限**：部署/停止 Broker 需要管理员权限。已配置 `gsudo`（全局缓存 5 分钟），开发期间反复需要提权时直接用 `sudo` 命令：

```powershell
sudo Get-Process SysMonBroker | Stop-Process -Force
sudo Copy-Item publish\SysMonBroker.exe "$env:LOCALAPPDATA\SysMonBroker\SysMonBroker.exe" -Force
sudo Start-Process "$env:LOCALAPPDATA\SysMonBroker\SysMonBroker.exe"
```

不要每次都弹 UAC 窗口，gsudo 缓存期内直接执行。

## 构建

```powershell
$msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"

# MSIX Release
& $msbuild SysMonCmdPal\SysMonCmdPal.csproj /p:Configuration=Release /p:Platform=x64 `
  /p:VcpkgEnabled=false /p:RunAnalyzers=false /p:TreatWarningsAsErrors=false `
  /t:Build /v:minimal

# 安装
& "$PWD\SysMonCmdPal\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\AppPackages\SysMonCmdPal_*_x64_Test\Add-AppDevPackage.ps1"

# 重载命令面板
Start-Process 'x-cmdpal://reload'

# 测试
dotnet test SysMonCmdPal.Tests\SysMonCmdPal.Tests.csproj

# SysMonBroker v2 独立构建
dotnet publish SysMonBroker\SysMonBroker.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## 日志路径

| 日志 | 路径 |
|------|------|
| 传感器后端 | `%LocalAppData%\SysMonCmdPal\sensor_backend.log` |
| 网络调试 | `%LocalAppData%\SysMonCmdPal\net_debug.log` |
| Broker | `%LocalAppData%\SysMonCmdPal\broker.log` |
| 传感器快照 | `%LocalAppData%\SysMonCmdPal\sensor_snapshot.json` (btop4win 集成) |

## 已知限制

- HWiNFO 共享内存每 ~12 小时需重启 HWiNFO（已有 11h 预警 UI）
- ThermalZone 精度差 5-15°C（仅 ACPI 报告）
- Broker v2 共享内存布局需两端版本匹配（Magic + Version 字段校验）
- COM 结构体布局需 C#/C++ 两端一致（pack=8, BSTR 字段顺序）
- Broker COM 自注册需管理员权限写入 HKCR
- COM 白名单已废弃 → 硬编码 btop.exe SHA256 + .devmode SSH 签名调试模式
- `GetDockBands()` 每次重建静态 Band 列表（可优化为 diff）

## 版本历史

| 版本 | 日期 | 变更 |
|------|------|------|
| 2.2.0 | 2026-06-19 | Broker 安全加固: 硬编码 SHA256 + .devmode SSH 签名 + 共享内存 SDDL ACL + COM Server CoInitializeEx 修复 |
| 1.6.1 | 2026-06-16 | COM 安全: SHA256 白名单认证 + KillProcess 代理结束进程 |
| 1.6.0 | 2026-06-16 | COM Local Server: Broker 为 btop4win 提供完整进程列表 (SE_DEBUG) + 传感器数据 |
| 1.5.0 | 2026-06-16 | HWiNFO SHM 回退 + Broker v2 全量传感器 + btop4win 快照集成 + 传感器列表页 |
| 1.3.0 | 2026-06-15 | 共享内存 IPC: MemoryMappedFile + EventWaitHandle 取代 COM 推送 |
| 1.1.0 | 2026-06-14 | 商店安全版: 移除 ring-0 + 测试覆盖 + 网络过滤 |
| 1.0.0 | 2026-06-09 | 初始版本 |
