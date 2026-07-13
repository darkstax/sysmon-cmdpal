# SysMonCmdPal Small Fixes Parallel Plan

## 固定路径

- 项目根: `/home/starl/ai-code/sysmon-cmdpal`
- 主扩展: `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal`
- Broker: `/home/starl/ai-code/sysmon-cmdpal/SysMonBroker`
- 测试: `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal.Tests`
- 本计划: `/home/starl/ai-code/sysmon-cmdpal/docs/sysmon-cmdpal-small-fixes-plan.md`
- 项目笔记:
  - `/mnt/c/Users/StarL/Documents/coding-vault/1-Projects/sysmon-cmdpal/00-索引.md`
  - `/mnt/c/Users/StarL/Documents/coding-vault/1-Projects/sysmon-cmdpal/20-架构模型.md`
  - `/mnt/c/Users/StarL/Documents/coding-vault/1-Projects/sysmon-cmdpal/21-刷新模型.md`
  - `/mnt/c/Users/StarL/Documents/coding-vault/1-Projects/sysmon-cmdpal/23-页面与 Dock 模型.md`

## 通用约束

- 不做大功能，不改刷新总架构，不引入新后台服务。
- 保持现有边界:
  - `SystemInfoService` 负责 1 秒聚合刷新和 `SystemSnapshot`。
  - `Commands/` 只负责 DockBand 和命令。
  - `Pages/` 只负责 UI 内容拼装。
  - `Broker/` 只负责共享内存读取和快照状态。
  - `Services/` 只负责底层采集和回退链。
- 多 agent 并行时，每个 worker 只写自己分配的文件范围。
- 不回退别人已做的改动；遇到相邻改动时适配现状。
- 文案新增必须同时更新:
  - `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Strings/zh-CN/Resources.resw`
  - `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Strings/en-US/Resources.resw`
- JSON 序列化继续使用 source generator，不使用反射序列化。
- 当前 WSL/Linux 环境不能完整运行 Windows-targeting/CsWinRT 测试；最终验证以 Windows 环境为准。

## Worker A: 配置与文档收敛

### 目标

消除 `PrecisionMode` 设置已移除但配置、注释、测试仍描述手动切换的状态失真。

### 写入范围

- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Models/SensorChainConfig.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal.Tests/SensorChainConfigTests.cs`
- `/home/starl/ai-code/sysmon-cmdpal/CLAUDE.md`
- `/home/starl/ai-code/sysmon-cmdpal/README.md`

### 任务

1. 确认当前策略为自动回退链: Broker -> HWiNFO -> D3DKMT/PDH/ThermalZone。
2. 清理或降级 `SensorChainConfig` 里已无用户入口的 `PrecisionMode` 语义。
3. 测试不再断言不存在的 CmdPal 设置入口。
4. 修正文档里 `.sln` 项目包含关系的过时描述。
5. 保留向后兼容读取旧 settings.json 的能力，除非确认完全无人使用。

### 验收

- 搜索 `PrecisionMode`、`HighPrecision`、`settings page` 不再出现误导性描述。
- `SensorChainConfig` 注释不再声称和 CmdPal 设置页面同步。
- 文档与 `SysMonCmdPal.sln` 当前内容一致。

## Worker B: Broker 诊断状态

### 目标

Broker 不可用时能区分未连接、无新写入、数据过期、无传感器和读取异常。

### 写入范围

- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Broker/SharedMemoryReader.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Broker/BrokerPushReceiver.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Pages/SysMonMainPage.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Pages/SensorListPage.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Strings/zh-CN/Resources.resw`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Strings/en-US/Resources.resw`

### 任务

1. 在 `SharedMemoryReader` 增加线程安全的最近读取诊断状态:
   - 是否连接到 SHM
   - 最近读取 UTC 时间
   - 最近 counter
   - 最近 version
   - 最近 sensor count
   - 最近错误消息
2. `ReaderLoop` 的 `catch` 不再完全静默，记录最近错误；日志要限频，避免每秒刷盘。
3. 主页面传感器 subtitle 改为更明确状态:
   - Broker connected, N sensors
   - Broker alive but no sensor data
   - Broker shared memory unavailable
   - Broker data stale
4. 传感器页无数据状态显示更多诊断信息。
5. 新增文案全部本地化。

### 验收

- Broker 未启动时显示 SHM 不可用或未连接。
- Broker 停止写入后显示 stale，而不是泛化为无数据。
- Broker 有心跳但无传感器时显示 no sensor data。

## Worker C: 网络日志与接口过滤小修

### 目标

去掉 Release 默认网络调试日志，修复网络接口过滤大小写敏感问题。

### 写入范围

- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Services/NetworkMonitor.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Services/SensorLogger.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal.Tests/SystemInfoServiceTests.cs`

### 任务

1. `NetworkMonitor.GetPhysicalInterfaces()` 中虚拟、VPN、蓝牙、filter driver 过滤改成 `StringComparison.OrdinalIgnoreCase`。
2. `net_debug.log` 默认关闭:
   - 首选把 `NetLog` 改成 Debug-only。
   - 如果需要运行时打开，用环境变量或设置开关，但不要默认写文件。
3. 检查 `SensorLogger.ForceLog` 高频调用点；不在本 worker 范围内大改，只做明显的限频或 Debug-only 小修。
4. 补充大小写过滤相关测试，优先抽出可测试 predicate；不要让测试依赖真实机器网卡描述。

### 验收

- Release 默认不写 `net_debug.log`。
- 虚拟网卡过滤对大小写不敏感。
- 测试不依赖当前机器实际网卡列表。

## Worker D: 传感器分类小功能

### 目标

让“全部传感器”从单个长列表扩展为分类入口，仍保持只读 snapshot。

### 写入范围

- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Pages/SensorListPage.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Broker/ShmLayout.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Strings/zh-CN/Resources.resw`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Strings/en-US/Resources.resw`

### 任务

1. 在 `SensorListPage` 顶部增加分类入口:
   - 温度
   - 使用率/负载
   - 功耗
   - 风扇
   - 频率
   - 电压
   - 存储
2. 新增轻量 `SensorCategoryPage`，只从 `BrokerPushReceiver.Instance.Snapshot` 读取。
3. 复用现有 `FormatSensorValue` 和图标逻辑。
4. 保留原“全部传感器”列表语义，不破坏已有入口。

### 验收

- Broker 有数据时能进入分类页。
- 分类页不触发 `SystemInfoService.Refresh()`。
- Broker 无数据时仍走原无数据提示。

## Worker E: 复制指标与 btop 小设置

### 目标

增加低风险实用命令: 复制当前指标，以及 btop 自定义路径优先级。

### 写入范围

- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Commands/BtopLauncherCommand.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Pages/CpuDetailPage.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Pages/MemoryDetailPage.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Pages/DiskDetailPage.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Pages/NetworkDetailPage.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Pages/GpuDetailPage.cs`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Models/SensorChainConfig.cs` 或 Worker A 引入的新通用 settings 文件
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Strings/zh-CN/Resources.resw`
- `/home/starl/ai-code/sysmon-cmdpal/SysMonCmdPal/Strings/en-US/Resources.resw`

### 任务

1. 为核心详情页增加 `CopyTextCommand` 或等价 CmdPal 命令，复制一行当前指标。
2. 复制文本示例:
   - `CPU 18% · 56°C · 4200 MHz · Broker`
   - `Network ↓ 12.4 MB/s · ↑ 1.1 MB/s`
   - `GPU RTX 4070 · 42% · 68°C · 6.2/12.0 GB`
3. 复制命令只读 `SystemInfoService.Instance.Current`，不主动刷新。
4. `BtopLauncherCommand.FindBtopExe()` 搜索顺序:
   - 用户自定义路径
   - scoop 常见路径
   - PATH
   - Program Files 常见路径
5. 如果 settings 模型尚未稳定，只实现读取 settings.json 中可选字段，不接 CmdPal 设置 UI。

### 验收

- 复制文本不含 Markdown 粗体符号。
- N/A 值格式统一。
- 自定义 btop 路径存在时优先使用；不存在时回退旧搜索逻辑。

## 集成顺序

1. 先合并 Worker A、C。
2. 再合并 Worker B。
3. 再合并 Worker D。
4. 最后合并 Worker E，因为它可能依赖 Worker A 的 settings 模型结果。

## 验证命令

Windows 环境:

```powershell
cd C:\Users\StarL\Documents\AI_code\sysmon-cmdpal
dotnet test SysMonCmdPal.Tests\SysMonCmdPal.Tests.csproj
dotnet build SysMonCmdPal.sln -c Debug -p:Platform=x64
```

Broker:

```powershell
cd C:\Users\StarL\Documents\AI_code\sysmon-cmdpal
dotnet publish SysMonBroker\SysMonBroker.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
.\deploy.ps1 -BrokerOnly
.\verify.ps1
```

当前 WSL 环境可做的轻量检查:

```bash
cd /home/starl/ai-code/sysmon-cmdpal
dotnet test --no-restore
```

注意: WSL 中该命令目前可能因 Windows targeting 或 CsWinRT 失败，失败时记录原因，不把它当作功能回归。

## 手工验收

1. Broker 未启动: 主页面和传感器页显示明确断开/SHM 不可用状态。
2. Broker 启动: 显示 sensor count 和最近更新时间。
3. 停止 Broker: 5 到 10 秒后状态变 stale/disconnected。
4. Dock: CPU、内存、磁盘、网络、GPU、电池继续 1 秒刷新。
5. 传感器分类页: 打开不卡顿，不触发额外采集。
6. 复制命令: 每个核心详情页能复制一行纯文本状态。
7. btop: 自定义路径优先；找不到时 toast 提示明确。
