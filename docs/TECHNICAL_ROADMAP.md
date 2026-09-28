# SysMonCmdPal — 技术路线与改进路线图

> 版本：v1.1  
> 日期：2026-08-16（初版）/ 2026-09-28（Phase 1+2 落地后同步）  
> 状态：**Phase 1 + Phase 2 已落地并过 QA/评审门禁（v1.7.0 里程碑）**；Phase 2.5 为新增待执行段，Phase 3–5 未启动  
> 关联文档：[AGENTS.md](../AGENTS.md)、[CLAUDE.md](../CLAUDE.md)

---

## 1. 项目定位与差异化

### 1.1 核心价值主张

PowerToys Command Palette 内置系统监控仅提供基础 CPU/内存/磁盘概览，**无法满足以下需求**：

| 能力 | 内置方案 | 本项目方案 |
|------|---------|-----------|
| GPU 精确利用率 | 无 / PDH 粗估 | D3DKMT 引擎级 RunningTime + PDH 回退 + Broker LHM 精确值 |
| GPU 温度 / 显存 | 无 | Broker(LHM) → HWiNFO SHM → D3DKMT（利用率 **+ 本地显存**，温度仍 N/A）→ PDH 四级回退；**用户态四层均零 COM/零 WMI**（见 §7「BuiltInCOM」行） |
| CPU 精确温度 | 无 | Broker(LHM) → HWiNFO → ThermalZone(ACPI) 三级回退 |
| 全量传感器浏览 | 无 | Broker 单向 SHM passthrough（最多 250 个传感器） |
| 实时趋势图 | 无 | SVG/PNG 火花线图，预渲染缓存 |
| 电池健康度 | 无 | 设计容量 / 满充容量 / 循环次数 / 30 天缓存 |
| 双 GPU 识别 | 无 | `GpuIdentityService` 单一身份来源：SetupDi（`setupapi.dll` 纯 P/Invoke）物理身份表 + HWiNFO iGPU/dGPU 角色标记 → `GpuKind`，主卡按 `Kind==Discrete` 竞选（显存读数降为未判定时的兼容兜底，杜绝集显凭 WDDM 分段抢主卡） |

### 1.2 护城河

**不是 UI，而是多源传感器融合 + 权限分离架构。**

- **用户态**（SysMonCmdPal）：MSIX 扩展，无需管理员，读取 SHM / 注册表 / PerformanceCounter。
- **提权态**（SysMonBroker）：独立进程，以交互用户 + Highest 运行（计划任务），LibreHardwareMonitor 采集 ring-0 传感器，经 SHM v2 单向推送。
- **AdminPipe**：命名管道权限代理（btop4win 等客户端可请求管理员操作），AUTH 白名单 + DevMode 门控。

微软若在未来版本将基础传感器读取内置到 CmdPal SDK，本项目的 Broker 层仍具备不可替代性（LHM 的 PawnIO 驱动需要管理员权限，商店应用无法内置）。

---

## 2. 当前技术栈基线

| 层级 | 技术选型 | 版本 / 备注 |
|------|---------|------------|
| 语言 | C# | `LangVersion=preview`（.NET 10 preview 特性） |
| 运行时 | .NET | `net10.0-windows10.0.26100.0`（Win11 24H2） |
| UI 框架 | Windows App SDK | 1.6，MSIX 打包，`runFullTrust` |
| CmdPal SDK | Microsoft.CommandPalette.Extensions | 未锁定版本（⚠️ 见 P2-14） |
| 传感器采集 | LibreHardwareMonitorLib | Broker 侧使用，PawnIO 可选 |
| 进程间通信 | 共享内存（SHM v2） | 单向，16KB，SMX1 提交序列 |
| 权限代理 | 命名管道（Win32） | `\\.\pipe\SysMonBrokerAdmin`，协议见 `btop4win-broker-ipc.md` |
| 测试 | xUnit | 34 个 `.cs` 测试文件（含 fixture/harness）；用例数以**实跑**为准，权威口径 = `vstest` 全量 0 failed / 0 skipped（Phase 1+2 落地后为 436，会随任务漂移，勿在文档固化后另行引用） |
| 构建 | MSBuild + vstest | 纯 `dotnet test` 不可用（缺 VS AppxPackage 任务） |
| 本地化 | .resw 资源文件 | en-US + zh-CN 同步维护 |

---

## 3. 架构分层与数据流

```
┌─────────────────────────────────────────────────────────────┐
│  PowerToys Command Palette (宿主进程)                        │
│  └── 通过 COM / WinRT 调用 SysMonCmdPal 扩展                 │
├─────────────────────────────────────────────────────────────┤
│  SysMonCmdPal (用户态 MSIX 扩展)                             │
│  ├── Pages/          — ListPage / ContentPage / FormContent │
│  ├── Commands/       — DockBand（CPU/内存/GPU/网络/磁盘/电池）│
│  ├── Services/       — SystemInfoService（单例协调器）        │
│  │   ├── CpuSensorReader    — Broker→HWiNFO→ThermalZone      │
│  │   ├── GpuSensorReader    — Broker→HWiNFO→D3DKMT→PDH       │
│  │   ├── GpuDxgkrnlAdapters — gdi32 D3DKMTEnumAdapters2 唯一  │
│  │   │                        GPU adapter 出口（零 COM/零 WMI）│
│  │   └── GpuIdentityService — SetupDi 身份 + GpuKind 单一来源 │
│  │   ├── NetworkMonitor     — 物理接口过滤 + EMA 平滑         │
│  │   ├── DiskMonitor        — DriveInfo + PerformanceCounter │
│  │   └── BatteryReportService — 30 天缓存健康报告            │
│  ├── Broker/         — SHM 读取端（Reader + Parser + Health） │
│  └── Localization/   — Loc.Get() 资源查找                    │
├─────────────────────────────────────────────────────────────┤
│  SysMonBroker (提权进程，独立分发)                            │
│  ├── Sensors/        — SensorCollector（LHM thin-shell）      │
│  ├── IPC/            — BrokerSharedMemory（写端）+ AdminPipe  │
│  └── Logging/        — BrokerLogger（跨进程安全）             │
├─────────────────────────────────────────────────────────────┤
│  外部数据源                                                   │
│  ├── HWiNFO 共享内存（Global\HWiNFO_SENS_SM2）— 用户态可读   │
│  ├── D3DKMT (gdi32.dll) — adapter 枚举 + 引擎 RunningTime    │
│  │     + SEGMENT 本地显存（T2-2；不依赖 DXGI/COM）            │
│  ├── PDH PerformanceCounter — GPU Engine 计数器              │
│  └── ThermalZone (WMI) — ACPI 热区温度                       │
└─────────────────────────────────────────────────────────────┘
```

**关键约束（不可破坏）：**

1. **回退链顺序固定（按传感器类型分两条链，文档链序为跨类型并集，不构成任一单链）**：
   - **GPU**：`Broker → HWiNFO → D3DKMT → PDH`。ThermalZone 对 GPU **有意排除**（ACPI 仅有 CPU 热区，把 CPU 温度标为 GPU 温度会误导用户，见 `GpuSensorReader.cs` 注释）——**禁止为对齐任何文档表述把 ThermalZone 重新加回 GPU 链**。
   - **CPU 温度**：`Broker → HWiNFO → ThermalZone`（D3DKMT/PDH 为 GPU 专属）。
   两条链均全自动、数据新鲜度超时自动降级，不允许用户配置覆盖。
2. **统一刷新**：所有页面/DockBand 共享 `DockBandRefreshCoordinator` 的 1s 刷新，禁止独立定时器。
3. **SHM v2 布局同步**：`ShmLayout.cs`（读端）与 `BrokerSharedMemory.cs`（写端）的偏移量、常量、结构体布局必须手动保持一致。
4. **本地化同步**：`Strings/en-US/Resources.resw` 与 `Strings/zh-CN/Resources.resw` 键值一一对应，禁止硬编码用户可见文案。
5. **权限边界**：AdminPipe 的 AUTH 必须验证客户端 PID 与可执行文件 SHA256 哈希；DevMode 仅限 dev 构建 + marker 文件。

---

## 4. 已知问题与技术债务

### 4.1 P0 — 架构债务（不改会越来越难维护）

**状态：三条均已于本轮（v1.7.0 / Phase 1）关闭。**

| 编号 | 问题 | 位置 | 影响 | 状态 |
|------|------|------|------|------|
| P0-1 | `SystemInfoService` 单例硬编码所有 monitor，无法测试替换/动态禁用 | `Services/SystemInfoService.cs` | 新加传感器源需改核心类；单元测试无法 mock | ✅ 已关闭（T1-1 + 预评审修复 R(t1)）：`ISystemInfoSource` 注册表驱动，`BuildDefaultSources` 为生产与守护测试**同一份数据**（影子注册表已消灭），`internal` 构造供 mock 注入 |
| P0-2 | `SysMonMainPage` 构造函数直接 `new` 7 个详情页，无惰性初始化 | `Pages/SysMonMainPage.cs` | 用户不打开电池页时，`BatteryReportService` 仍后台运行 | ✅ 已关闭（T1-2）：`DeferredListPage`/`DeferredContentPage` 轻量壳 + `Lazy<T>`，Dispose 仅释放 `IsValueCreated`；内层 ctor 零 Timer/零订阅 |
| P0-3 | `BrokerSharedMemory.Write()` 异常回滚依赖手动保存/恢复字段 | `SysMonBroker/IPC/BrokerSharedMemory.cs` | catch 块若再抛异常（如 OOM），SHM 状态 undefined | ✅ 已关闭（T1-3）：改为「状态标记 + 下一周期真 rebase 自愈」，删除手动回滚（消灭回滚自身二次抛出）；身份守卫拒绝 clobber 接管者，双写者 FATAL 防线未削弱；SHM 布局零触碰 |

**P0-1 落地时一并恢复的构造期不变式**（R(t1) F1）：`Refresh()` 发布的快照 `Disks`/`PhysicalDisks`/`Gpus` **恒非 null**（源头收口，消费端不做 null 补丁）；逐源异常隔离不得放开该不变式。

### 4.2 P1 — 传感器精度与稳定性（核心卖点，不能崩）

**状态：四条方案面均已落地（v1.7.0 / Phase 2）；P1-2 的「用户可见端到端效果」仍待真机冒烟，见下行标注与 §7。**

| 编号 | 问题 | 位置 | 影响 | 状态 |
|------|------|------|------|------|
| P1-1 | HWiNFO GPU 温度依赖**标签出现顺序**区分集显/独显，HWiNFO 版本更新后顺序可能变 | `Services/GpuSensorReader.cs` | 双 GPU 机器上温度/负载可能张冠李戴 | ✅ 已关闭（T2-1）：改以 **units 分区 + entry 偏移 4 的 `sensor_index`** 归属，精确整串标签取值；顺序约定载体（`ReadGpuTemp(index)`/`ReadGpuUsageAll`/WMI `EnsureGpuNames`/名称硬编码）全套删除。归属主依据是分区本身，**名称启发式全失败也不会再张冠李戴**。⚠️ 如实界定：达成的是 units 归属稳定化，**不是 PNPDeviceID 硬关联**（本机逐字节解码证实 HWiNFO 记录内无任何总线/序列号键，见 §8） |
| P1-2 | D3DKMT 回退只提供利用率，无温度/显存 | `Services/D3dkmtGpuReader.cs` | 无 Broker/HWiNFO 时 GPU 信息不完整 | 🟡 代码层已交付（T2-2）：显存取 gdi32 `QueryStatistics(SEGMENT)` 非 aperture 本地段（CommitLimit/BytesResident），**未采用原方案的 DXGI**（`[ComImport]` 在保留裁剪的出货配置下 BuiltInCOM 被禁，照字面实现会新加一条恒空 COM 链）。⚠️ **枚举可达 ≠ 端到端已验证**：现有证据为隔离探针级，产品进程用户可见效果仍 `unverifiable`（本机无头/vGPU），留 Phase 2.5 真机 dGPU 冒烟。温度仍 N/A（如实） |
| P1-3 | `GpuClassifier`（名称分类）与 `GpuSensorReader`（RAM 大小分类）逻辑可能不一致 | `GpuClassifier.cs` vs `Services/GpuSensorReader.cs` | UI 显示 GPU 数量与传感器读取数量不匹配 | ✅ 已关闭（T2-3）：`GpuClassifier.cs` 删除，收敛为 `internal GpuIdentityService` 单一来源（SetupDi 纯 P/Invoke，零 COM/零 WMI）；名称 marker 降为辅助判据；`GpuInfo/GpuResult` 新增 `Kind` 并贯穿主卡竞选（`Kind==Discrete` 优先，显存读数仅作未判定兜底） |
| P1-4 | `SharedMemoryReader` 确认链阈值（≈6s）远低于 Broker 合法静默上界（≈15s）；且 `StallDebounceThreshold` 当前**零测试覆盖**（无现有断言依赖它） | `Broker/SharedMemoryReader.Health.cs` | 瞬时慢周期导致提前断连/标记不可用（现代路径的可见降级另受 5s 新鲜度契约支配，见 §5 残余说明） | ✅ 已关闭（T2-4）：废弃计数去抖，改**时间基确认窗口 `ConfirmStallWindow=15s`**（有界；`timestamp<=0` 刚重启哨兵不确认）；publish 过期保留即时降级旁路；raw `IsStalled` 5s 观察阈值不变。原「零测试覆盖」已由 `SharedMemoryStallConfirmWindowTests` 5 例补齐（含 5 次喂观察的反回归设计）。⚠️ 诚实边界：**不改变用户可见降级时机**（由 5s 新鲜度网支配），只消除确认链提前动作 |

**本轮附带解决的存量缺陷（非本轮引入、非测试改造，属产品代码修复，记功）**

| 缺陷 | 位置（修复前） | 症状 | 修复 |
|------|----------------|------|------|
| `NodeOrdinal` 偏移越界 | `Services/D3dkmtGpuReader.cs` 结构体 `byte[800] + NodeId` | NodeId 实落偏移 **824**（越界），驱动恒读 **node0** ⇒ **GPU 利用率被单个引擎钉死并显示错值**（比缺值更严重：一直在显示错误数据） | T2-2 按 SDK `C_ASSERT(sizeof(D3DKMT_QUERYSTATISTICS)==0x328==808)` 精确重排至 `QueryElement@800`，并以 `Marshal.SizeOf/OffsetOf` 做确定性布局单测 |
| `D3DKMT_HANDLE` 类型错 | 同上，声明为 `IntPtr` | 该字段实为 `UINT`（4B），错 8B 导致**整条结构体后续字段全部布局错位** | T2-2 改为 `uint`，同上布局单测锁死 |
| PDH 空闲卡整卡消失 | `Services/PdhGpuReader.cs` `if (cooked > 0)` | 空闲 0% 的卡不进结果 ⇒ **整卡从 GPU 列表消失**（表现为缺值，非错值） | T2-2 删除该过滤：建立 delta 基线即产出、利用率如实为 0；逻辑抽为纯静态 `BuildResults` + 确定性用例（零 `PerformanceCounter`） |

### 4.3 P2 — 性能与资源管理

| 编号 | 问题 | 位置 | 影响 |
|------|------|------|------|
| P2-1 | `SparklineChart.ToPng()` 每秒分配大数组，GC 压力大 | `Services/SparklineChart.Png.cs` | 6 个图表 × 1Hz 刷新，长期运行内存碎片 |
| P2-2 | `NetworkMonitor.GetPhysicalInterfaces()` 每 10s 全量枚举 COM 接口 | `Services/NetworkMonitor.cs` | 不必要的 COM 调用开销 |
| P2-3 | `BatteryReportService` 首次访问后台生成，无取消机制 | `Services/BatteryReportService.cs` | 页面关闭后后台任务仍运行 |
| P2-4 | 测试大量使用反射调用私有方法，重构时易碎 | `SysMonCmdPal.Tests/BrokerSharedMemoryTests.cs` | 重命名私有方法 → 测试静默失败。**同类「影子测试债」已有一例在本轮消除**：R(t1) F2 把顺序守护从测试专用 helper 改为生产 ctor 实际使用的同一份注册表（`BuildDefaultSources`），改 ctor 顺序即会红；其余反射债仍归 T4-3 |

### 4.4 P2 — 可维护性与兼容性

| 编号 | 问题 | 位置 | 影响 |
|------|------|------|------|
| P2-5 | CmdPal SDK 未锁定版本范围，breaking change 会导致编译失败 | `SysMonCmdPal.csproj` | SDK 升级后扩展无法加载 |
| P2-6 | `Loc.GetFallback()` 只覆盖 5 个键，测试环境大量键返回原始键名 | `Localization/Loc.cs` | 测试输出难看，可能掩盖真实问题 |
| P2-7 | `unsafe` 代码分散在 `BrokerSharedMemory.cs` 各处，无统一封装 | `SysMonBroker/IPC/BrokerSharedMemory.cs` | .NET 10 preview → 正式版迁移时审查成本高 |
| P2-8 | **resw 双语键集一致性无自动守护**：`Strings/en-US` 与 `Strings/zh-CN` 的键集对齐纯靠人工。当前基线（HEAD `b7351fa`，本轮 v1.7.0 落地后复核仍一致）实测两侧各 **238** 个 `<data name=`、差集双零 | `SysMonCmdPal/Strings/{en-US,zh-CN}/Resources.resw` | 漏加一侧键 → 另一语言静默回落到原始键名（`Loc.Get` 返回 key），约束 4 无门禁保障 → 由 **T4-5** 补自动守护 |
| P2-9 | 存量硬编码中文用户可见文案（QA t7 移交观察 (i)，**HEAD 即有、非本轮引入**） | `SysMonCmdPal/Pages/GpuItemPage.cs`、`Pages/GpuDetailPage.cs` | 违反约束 4 字面（用户可见文案须走资源查找）；建议随 T4-5 的本地化普查一并清理 |

### 4.5 P3 — 安全加固（低概率但高影响）

| 编号 | 问题 | 位置 | 影响 |
|------|------|------|------|
| P3-1 | DevMode 门控依赖文件系统路径 + marker 文件，可被伪造 | `SysMonBroker/IPC/DevModeVerifier.cs` | 攻击者知道路径即可激活 DevMode |
| P3-2 | AdminPipe TERMINATE 命令无目标进程白名单/权限检查 | `SysMonBroker/IPC/BrokerAdminPipeServer.cs` | 被攻破后可 kill 任意进程（含系统进程） |

---

## 5. 改进路线图

### Phase 1：架构加固（1-2 周）

**目标**：解决 P0 债务，让核心服务可测试、可扩展。

| 任务 | 文件 | 方案 | 状态 |
|------|------|------|------|
| T1-1 | `Services/SystemInfoService.cs` | 引入 `ISystemInfoSource` 接口，各 monitor 自注册；`Refresh()` 改为遍历注册源 | ✅ 已交付（含预评审修复 R(t1)：F1 快照集合恒非 null 不变式、F2 消灭影子注册表） |
| T1-2 | `Pages/SysMonMainPage.cs` | 详情页改为 `Lazy<T>` + 工厂模式；`Dispose` 时统一释放 | ✅ 已交付（`DeferredListPage`/`DeferredContentPage` 壳 + 事件桥接守护） |
| T1-3 | `SysMonBroker/IPC/BrokerSharedMemory.cs` | `Write()` 的 catch 块改为**状态标记 + 下一周期自愈**，不再手动回滚字段 | ✅ 已交付（真 rebase 自愈 + 身份守卫拒 clobber；SHM 布局零触碰） |

**验收标准**（三条均已由守护用例进全量门禁）：
- `SystemInfoService` 可通过构造函数注入 mock monitor。
- `SysMonMainPage` 不预先创建任何详情页实例。
- `BrokerSharedMemory` 的 `Write()` 在异常后下一周期能自动恢复一致状态。

### Phase 2：传感器精度加固（2-3 周）

**目标**：解决 P1 问题，确保在各种硬件上稳定工作。

| 任务 | 文件 | 方案 | 状态 |
|------|------|------|------|
| T2-1 | `Services/GpuSensorReader.cs`、`HwinfoSharedMemoryReader.cs` | ~~用 WMI `Win32_VideoController.PNPDeviceID` 关联 HWiNFO 传感器~~ **原方案不可实现**：gpu-dev 逐字节解码本机 HWiNFO 记录证实 unit(392B)=`id\|instance\|名称×3`、entry(460B)=`type\|sensor_index\|entryId\|label×N\|unit\|value/min/max/avg`，**无任何硬件/总线/序列号键可关联**（§8 引用的论坛帖实为 Vega bug 帖，非接口规范）。定稿：**解析 units 数组（头 off 20/24/28，unit 名自带稳定分组）+ entry 偏移 4 的 `sensor_index`，以 unit 分区归属取代出现顺序**；unit 名→OS 设备身份为**启发式关联**（vendor token + HWiNFO 自带 iGPU/dGPU 角色标记；型号串不可靠——本机 `AMD Radeon 680M` 不出现在 OS 设备名 `AMD Radeon(TM) Graphics` 中）。**达成度界定**：归属主依据是 unit 分区本身，即使名称启发式失败也不再现温度张冠李戴。 | ✅ 已交付（units 归属 + fail-fast 布局校验 20 例 + `HwinfoTestData` 内存 fixture 进门禁；**未声称 PNPDeviceID 硬关联**） |
| T2-2 | `Services/D3dkmtGpuReader.cs` | ~~补充 DXGI `QueryVideoMemoryInfo` 显存查询~~ **按实交付记（偏离字面、达成目标，属必要修正非越界）**：显存取 **gdi32 `QueryStatistics(SEGMENT)`** 的非 aperture 本地段（`CommitLimit` 选型、`BytesResident` 作 used），**全仓不存在真实 `QueryVideoMemoryInfo` 调用**。理由：`[ComImport]` DXGI 在保留裁剪的出货配置下 BuiltInCOM 被禁 ⇒ 照原文字面实现会给 trim 宿主**新加一条恒空 COM 链**。adapter 枚举源统一收敛到新出口 `Services/GpuDxgkrnlAdapters.cs`（`D3DKMTEnumAdapters2` + SetupDi 精确 JOIN，零 COM/零 WMI），D3kmt 与 PDH **共用同一出口**（不建第二条 interop、不建第二套名称过滤）；显存**每周期重取**（否则首帧冻结）。顺带修掉 `D3DKMT_HANDLE`/`NodeOrdinal@824`/PDH `cooked>0` 三条存量缺陷（见 §4.2 附带解决表）。 | 🟡 代码层已交付；**端到端用户可见效果 unverifiable**（本机无头/vGPU，仅隔离探针 VERDICT=OPERATIONAL）→ 真机 dGPU 交互桌面冒烟留 Phase 2.5 |
| T2-3 | `GpuClassifier.cs` + `Services/GpuSensorReader.cs` | 统一为 `GpuIdentityService`，单一数据源 | ✅ 已交付（`GpuClassifier.cs` 删除；`Kind` 贯穿主卡竞选；名称 marker 降辅助） |
| T2-4 | `Broker/SharedMemoryReader.Health.cs` | ~~`StallDebounceThreshold` 2→3~~ **原建模有误，已改为时间基确认窗口**：原文把 `StallTimeout`(5s) 误当观察间隔，实际观察节奏由 `PollIntervalMilliseconds`=1000 决定 ⇒ 确认耗时 ≈ `StallTimeout + (threshold−1)×1s`，2→3 仅从 6s 变 **7s**，远低于 broker 声明的合法静默上界 ≈15s（`SysMonBroker/Program.cs:30-34`），达不到本条验收。定稿：确认条件由「连续 N 次观察」改为「自上次 counter 前进起持续静默 ≥ **15s**」（与 broker 合法静默上界对齐、低于 `CycleTimeout`=20s），必须有界；raw `IsStalled` 观察阈值(5s)不变；publish 过期保留即时降级。不动 SHM 布局（动态阈值方案否决：触 §3 约束 3 三处同步）。 | ✅ 已交付（计数去抖全删 + `ConfirmStallWindow=15s` 有界 + 即时降级旁路保留 + 5 例双向门禁） |

**验收标准**：
- 双 GPU（dGPU + iGPU）的 HWiNFO 回退归属正确，**由 `HwinfoTestData` 内存 fixture 进全量 vstest 门禁**（现状该函数零覆盖：全仓仅用 `DisableHwinfo()` 关掉该链，从未构造过 HWiNFO buffer）；标签乱序/交换、单卡、纯集显/纯独显、三卡、显存标签缺失等场景可证。真实机器上的活体归属若无法在不影响用户运行的前提下验证，诚实标 `unverifiable`，不得以单测绿替代。
- GPU 数据源标注不得跨链（见 §7 风险表「GPU 行 backend 标签」行）。
- 无 Broker 无 HWiNFO 时，D3DKMT 能提供利用率 + 显存。
- 高负载系统（单硬件更新 8s）上，Reader 不因**瞬时慢周期**提前断连/永久标记不可用（可测：12–15s 静默窗口内仍视为可用；超 15s 才确认）。
- **已知残余（诚实范围）**：现代（SMX1 extension）路径上用户可见的降级时机由 `AvailabilityTimeout`=5s（新鲜度契约，§3 约束 1 要求过期必回退）支配，而非确认链 ⇒ 慢周期下仍会有 5–10s 回退窗口。彻底消除需让新鲜度窗口适应 Broker 周期 → 触发 §3 约束 3 → 归 Phase 2.5/3 专项候选（见 §7 风险表）。

**Phase 1+2 落地状态（2026-09-28）**：T1-1/T1-2/T1-3/T2-1/T2-3/T2-4 已交付；T2-2 代码层交付但端到端待真机。QA 全量门禁 **0 failed / 0 skipped**，权威用例数以实跑为准（本里程碑收尾实测 **436**；口径 = `vstest` 展开后执行数、xUnit 按 display name 去重，`300 + Σadded − Σremoved = 436` 链式闭合）。§3 五约束逐条 PASS；代码评审 PASS（0 blocker / 0 high）。前置决策实验 tA（`BuiltInCOM` 豁免 A/B）裁决 = **A2**：豁免可行但代价是完全放弃裁剪（20MB → 105MB，5.25×），故本轮定策**保留裁剪 + GPU 用户态链 COM-free 化**，「以豁免换 COM」降为 Phase 2.5 备选。

### Phase 2.5：出货配置可达性与来源标注专项（1 周，**本轮裁决后新增，v1.7.0 未含**）

**缘起**：Phase 1+2 落地过程中，tA 决策实验与 QA/评审暴露出三类「本轮塞不进、但不能丢」的问题——(a) 出货 trim/self-contained 配置下的 COM/WMI 依赖面只完成了 GPU 侧的**代码层**改造；(b) GPU 数据来源标注与新鲜度窗口张力是既有设计债，需产品决策；(c) 单元测试结构性地触不到出货宿主。captain 裁决「不塞进本轮 DAG」，本段即其落档入口。

| 任务 | 内容 | 判据 / 边界 |
|------|------|-------------|
| T2.5-1 | **GPU 来源标注去跨链**：`GpuInfo` 增加 `Source` 字段，修正主页/DockBand 两处消费面（现 `snapshot.Backend` 只由 CPU Source 派生，GPU 行会显示 CPU 链的后端） | **Phase 2.5 首项**。需先由产品决策「GPU 无数据时该行显示什么」，故不塞进本轮。注意：这与「GPU 归属（哪块卡）」是两件事——归属已由 T2-1 处理，**来源标注仍未解决，不得混为一谈** |
| T2.5-2 | **真机 dGPU 交互式桌面冒烟门禁**：T2-2 显存/利用率的端到端用户可见效果验证（条件：物理 dGPU + 交互会话 + ≥2 周期带负载 + 产品进程而非测试宿主） | 现有证据仅隔离探针级；**枚举可达 ≠ 端到端已验证**，本段完成前不得在任何文档写「GPU trim 下已可用」 |
| T2.5-3 | **磁盘/电池 WMI 在已裁宿主的兜底/失效面普查** | 残余仍依赖 COM/WMI 的**非 GPU** 面。两者严重度不同，不得并列假定：DiskMonitor `Win32_DiskDrive` WMI 有 `powershell.exe` 子进程兜底 ⇒ 功能**退化**而非失效；BatteryQueryService `Win32_Battery` WMI 是否同有兜底**待实测定论**（快照层百分比/状态另有 `GetSystemPowerStatus` 纯 P/Invoke 主源，疑失效面集中在详情页功率/电压档） |
| T2.5-4 | **新鲜度窗口与 Broker 周期的张力**：让 5s `AvailabilityTimeout` 适应 Broker 合法静默周期（≤15s） | 需 SHM 携带 Broker 周期 → 触 §3 约束 3（读写端+测试三处同步），故独立成任务而非随手改 |
| T2.5-5 | **`HwinfoSharedMemoryReader.Reconnect()` 无人调用**（定义在 `Services/HwinfoSharedMemoryReader.cs:334`，全仓零调用点，实测确认）：HWiNFO 重启后的恢复目前只能靠 12h 重置窗口/新鲜度过期被动达成 | 决定是接进回退链主动自愈，还是删除以免留下「看似有、实未接」的死代码 |
| T2.5-6 | **出货配置门禁化**：把 tA 式「隔离 publish + 交互式实跑」固化为可重复检查（含 `GpuIdentityService.cs:120` 辅助 DXGI 信号在 trim 下每周期抛+吞的状态跃变节流评估） | 堵住 §7「测试宿主 runtimeconfig 无禁用位」结构性盲区；禁止以全量单测绿替代 |

### Phase 3：性能优化（1 周）

**目标**：降低 GC 压力，减少不必要的系统调用。

| 任务 | 文件 | 方案 |
|------|------|------|
| T3-1 | `Services/SparklineChart.*.cs` | 全面切换到 SVG data URI（代码已有 `ToSvgDataUri`），移除 PNG 渲染路径；或引入 `ArrayPool<byte>` |
| T3-2 | `Services/NetworkMonitor.cs` | 接口列表缓存改为事件驱动（`NetworkChange.NetworkAddressChanged`），而非 10s 定时刷新 |
| T3-3 | `Services/BatteryReportService.cs` | 后台任务改为 `Task` + `CancellationToken`，页面 `Dispose` 时取消 |

**验收标准**：
- 连续运行 24 小时，Gen 2 GC 次数不显著增加。
- 网络接口插拔时 1s 内感知，无网络变化时不枚举接口。

### Phase 4：可维护性与兼容性（1 周）

**目标**：锁定依赖，清理测试债务。

| 任务 | 文件 | 方案 |
|------|------|------|
| T4-1 | `SysMonCmdPal.csproj` | 锁定 CmdPal SDK 版本范围：`[0.9,0.13)` |
| T4-2 | `Localization/Loc.cs` | 为所有测试环境键添加 fallback（或改用 resw 文件直接读取） |
| T4-3 | `SysMonCmdPal.Tests/` | 提取 `BrokerSharedMemory` 的协议逻辑到纯托管静态工具类，测试不再反射私有方法 |
| T4-4 | `SysMonBroker/IPC/BrokerSharedMemory.cs` | 封装 `UnsafeShmAccessor`，所有 `unsafe` 操作集中管理 |
| T4-5 | `SysMonCmdPal.Tests/` + `Strings/{en-US,zh-CN}/Resources.resw` | **resw 双语键集守护测试**：枚举两侧 `<data name=` 键集并断言差集为空（当前基线两侧各 238、差集双零），同时守护「无新增硬编码用户可见文案」。关闭 P2-8（现为纯人工约束）。可顺带清 P2-9 存量硬编码中文文案 |

**验收标准**：
- `dotnet build` 在 SDK 0.9-0.12 范围内均能编译通过。
- 测试项目重命名 `BrokerSharedMemory` 私有方法后，测试仍通过。
- 故意只在 en-US 加一个键 ⇒ T4-5 守护用例必须变红（证明约束 4 自此有门禁）。

### Phase 5：安全加固（按需，低优先级）

| 任务 | 文件 | 方案 |
|------|------|------|
| T5-1 | `SysMonBroker/IPC/DevModeVerifier.cs` | DevMode 激活需同时验证编译时嵌入的随机 nonce（非仅路径） |
| T5-2 | `SysMonBroker/IPC/BrokerAdminPipeServer.cs` | TERMINATE 前验证目标进程：非 SYSTEM、非 Broker 自身、属于当前会话 |

---

## 6. 版本规划

| 版本 | 内容 | 目标日期 | 状态 |
|------|------|---------|------|
| v1.6.x | 当前稳定版，仅修关键 bug | 维护中 | csproj 版本线现置 **1.6.0.0**（`AssemblyVersion`/`Version`/`AppxPackageVersion` 三处一致） |
| v1.7.0 | Phase 1 + Phase 2（架构加固 + 传感器精度） | 2026-09 | **代码已落地并过 QA/评审门禁，尚未打版本/发布**（见下方偏差记录） |
| v1.7.x | **Phase 2.5**（出货配置可达性 + GPU 来源标注 + 真机冒烟）——本轮裁决后新插入，排在 Phase 3 之前 | 2026-09/10 | 待执行（§5 Phase 2.5） |
| v1.8.0 | Phase 3 + Phase 4（性能 + 可维护性） | 2026-10 | 未启动 |
| v1.9.0 | Phase 5（安全加固，可选） | 按需 | 未启动 |

**版本规划偏差记录（2026-09-28）**：
- 本文档初版把 Phase 1+2 排为 v1.7.0，但代码库版本号已在 v2.5（AdminPipe）/商店提审周期中推进到 **1.6.0.0**；本轮 Phase 1+2 变更**未随代码提升版本号**。里程碑语义对应 v1.7.0，实际发布号需由集成/下次发版统一裁定，避免文档与 csproj 长期不一致。
- 原规划中不存在 Phase 2.5；它是本轮 tA 决策实验（A2 裁决）+ QA/评审移交项的落档产物，已同时插入 §5 与本表。

---

## 7. 风险与缓解

| 风险 | 概率 | 影响 | 缓解 |
|------|------|------|------|
| CmdPal SDK breaking change | 中 | 扩展无法加载 | 锁定版本范围；订阅 PowerToys release notes |
| .NET 10 preview → 正式版迁移问题 | 低 | 编译错误 |  preview 特性使用最小化；准备回退到 C# 12 |
| HWiNFO 共享内存格式变更 | 低 | 回退链断裂 | 保持 HWiNFO 签名验证；格式变更时自动禁用 HWiNFO 回退 |
| LibreHardwareMonitor 停止维护 | 低 | Broker 无法采集 | 评估迁移到 `OpenLibSys` 或自研 ring-0 驱动（成本高） |
| **`PublishTrimmed`/self-contained 禁用 BuiltInCOM —— GPU 面：代码层已 COM-free 化，旧「GPU 整块不可用」现象已解除**（分界不是 Debug/Release！`bin\x64\Debug\...\win-x64` 同样被禁） | **高（历史已实证）** | 历史实证：`NotSupportedException` 17376 次；「GPU: 所有数据源不可用」旧 1789 次；`使用 D3DKMT`/`D3DKMT 异常`/`使用 PDH`/`HWiNFO` 均 0 次 = 静默空返回指纹。**现状（代码层）**：D3DKMT + PDH 的 adapter 枚举源均已改走 gdi32 `GpuDxgkrnlAdapters` + SetupDi（零 COM/零 WMI），旧 DXGI COM 枚举器全仓仅剩 `GpuIdentityService.cs:120` 一处**辅助**显存信号（失败即空、不影响身份链，主 VRAM 已由 D3DKMT 段提供）。|⚠️ **「枚举通路可达」≠「端到端已验证」**：现有证据为隔离探针级（`~/t5-probe` VERDICT=OPERATIONAL），产品进程的用户可见效果在本机仍 `unverifiable`（无头/vGPU）⇒ 本行**不判已修复/已关闭**，定性留 Phase 2.5 真机 dGPU 交互桌面冒烟（T2.5-2）。**结构性盲区仍在：`SysMonCmdPal.Tests` 的 runtimeconfig 无该禁用位且无任何 live COM/WMI/DXGI 断言，故全量单测全绿既不能证实也不能证伪此问题**|
| **磁盘/电池 WMI 在已裁宿主的兜底/失效面（需普查；磁盘有 FD 兜底、电池待定）** | 中（残余 COM 暴露面，**非 GPU**） | 仍依赖 built-in COM 的是 `DiskMonitor` 的 `Win32_DiskDrive` WMI（`DiskMonitor.cs:99/:184`）与 `BatteryQueryService` 的 WMI（`BatteryQueryService.cs:78`）。**两者严重度不同，不得并列**：磁盘有 `powershell.exe` 子进程兜底（`:162/:216/:264`，独立 framework-dependent 进程）⇒ 功能**退化**而非失效；电池是否同有兜底**须实测定论，不得先假定** | Phase 2.5 **T2.5-3** 普查。读码仅用于收窄普查面：快照层 `BatteryPercent`/`BatteryLifeSeconds` 取自 `GetSystemPowerStatus`（纯 P/Invoke）、状态分类有非 COM 位驱动 ⇒ 该层疑为退化；详情页功率/电压唯一来源是 WMI 且未见备源 ⇒ 疑为真失效档。**以上均为读码所得，非 trim 产物运行时实测，不作已结论** |
| 新鲜度窗口(5s，且 `StallTimeout ≡ AvailabilityTimeout`) 与 Broker 合法静默周期(≤15s) 的张力 | 中 | 慢周期下短暂回退到次级数据源（用户可见降级时机由新鲜度契约支配，T2-4 时间基窗口治不到） | 需 SHM 携带 Broker 周期 → 触 §3 约束 3；Phase 2.5 专项候选 |
| **GPU 行 backend 标签跨链消费 CPU 来源**（`SystemInfoService.Sensors.cs:24` 只由 CPU Source 派生，`GpuInfo` 无 Source 字段） | **高（今天即用户可见）** | **主页 GPU 行在 GPU 无数据时渲染「GPU — unavailable / 传感器后端: ACPI thermal zone」，语义自相矛盾并把排障引向错误子系统**。机理更正：原括注「真根因是 trim 禁 COM 导致 DXGI 枚举为空」**已过期**——t5 同进程对照定明本机为**两层独立成因**叠加：① DXGI 枚举在无头/vGPU 会话下首项即 `0x887A0001 INVALID_CALL`（实体 adapter 存在，gdi32 数得出 6 个）；② WMI 被裁。⚠️ **但 label bug 本体依旧在**：`Backend` 至今只由 CPU Source 派生（本轮未改，QA/评审复核确认）。它与「GPU 归属（哪块卡的数据）」是两件事——后者已由 T2-1 处理，**本条不得随之标关闭** | **Phase 2.5 首项（T2.5-1）**：`GpuInfo` 增加来源字段 + 修正两处消费面；需先产品决策「GPU 无数据时该行显示什么」，故不塞进本轮 |
| **门禁盲区：测试宿主与出货宿主 runtimeconfig 不同** | 高（结构性，**本轮未消除**） | 单测永远绿，真实 self-contained 用户环境行为无法验证。机理已 tA 运行时实测坐实：`SysMonCmdPal.Tests` 无 `RuntimeIdentifier`、其 runtimeconfig 无 `BuiltInComInterop` 禁用位 ⇒ 测试宿主内 COM/WMI 正常；且全仓无任何 live COM/WMI/DXGI 断言 ⇒ 同一测试集在 trim 宿主也不会翻红（「两宿主同绿」是平凡真，不证成产品路径健康） | 任何依赖 COM/WMI/DXGI 的改动必须经 tA 式隔离 publish + 交互式实跑验证，禁止以全量单测全绿交付 |
| ~~手写 `[ComImport]` 的 DXGI vtable 槽位错位~~ | — | **已三路否证 / 关闭**（本轮唯一由文档记录为「经确证无需修正」的疑点，而非「没改就当缺陷」）：t12 曾提出「repo 把 `EnumAdapters1` 放 slot12、疑应 @13」 | 三路独立否证：① 权威头 `Windows Kits\10\Include\10.0.26100.0\shared\dxgi.h` 的 `IDXGIFactory1Vtbl` 逐槽为 `10 CreateSwapChain / 11 CreateSoftwareAdapter（真实成员，非虚构）/ 12 EnumAdapters1 / 13 IsCurrent`，与 `GpuAdapterEnumerator.cs` 声明逐槽一致；② t4 A/B 探针（原声明 vs 官方补三槽布局）同进程同返回 `0x887A0001`；③ t12 作者自查 `dxgi.h` 后**正式撤回**该疑点（其 DESK 探针 slot12=`S_OK`、slot13=`E_FAIL` 恰为反证）。⇒ 声明未改是正确的；本机 0 adapter 属无头/vGPU 环境性质，非槽位问题 |
| Microsoft Store 政策变更 | 低 | 无法上架 | 保持 sideload 渠道可用；文档说明手动安装方法 |

---

## 8. 参考链接

- [PowerToys Command Palette 扩展模型](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/extensibility-overview)
- [Microsoft.CommandPalette.Extensions NuGet](https://www.nuget.org/packages/Microsoft.CommandPalette.Extensions)
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)
- ~~[HWiNFO 共享内存格式](https://www.hwinfo.com/forum/threads/shared-memory-interface.5886/)~~ — ⚠️ **本条原为错误引用**：reviewer 活体抓取证实该线程内容是 Vega 读数 bug 帖、**不是接口规范**，故本仓从来没有权威格式依据。本机实测布局（gpu-dev 逐字节解码，HWiNFO 7.33）：header `version=2/2`、units `off/size/count=48/392/17`、entries `6712/460/345`；unit 记录 = `id|instance|名称×3 副本`，entry 记录 = `type|sensor_index|entryId|label×N|unit@268|value@284|min/max/avg`——**记录内无任何硬件/总线/序列号键可关联**。⇒ 实现必须自带布局 fail-fast 校验（`entrySize>=292`、`unitSize>=136`、units 末尾 ≤ entries 起点）并打点 header version，且**不得声称以 PNPDeviceID 关联**。

---

*本文档与 `AGENTS.md`、`CLAUDE.md` 同步维护。架构变更时三处同时更新。*
