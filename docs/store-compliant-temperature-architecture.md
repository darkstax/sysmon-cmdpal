## SysMonCmdPal 精准温度读取 — Microsoft Store 合规方案分析

### 问题根因

PawnIO 驱动 SDDL 只授予 SYSTEM 和 Administrators `GENERIC_READ|GENERIC_WRITE`。标准用户 token 无法打开设备。G-Helper 通过计划任务 `HighestAvailable` 在登录时静默获取完整管理员 token，用户感知不到 UAC 弹窗。

### Microsoft Store 硬性约束

| 能力 | 是否可行 | 原因 |
|------|----------|------|
| `allowElevation` | 几乎不可能过审 | Microsoft 明确表示此类应用"very unlikely to be approved" |
| MSIX 内置内核驱动 | 不支持 | MSIX 规范不支持打包 kernel driver |
| `FullTrustProcessLauncher` | 可用但不提权 | 只是脱离 AppContainer，token 权限不变 |
| MSIX 内置 Windows Service | 支持 | 但 Service 在 MSIX 容器内运行，token 为标准用户 |
| 计划任务 `HighestAvailable` | Store 不允许 | 自动提权技术，违反 Store 策略 |

### 方案一：Companion Installer + Broker Service（推荐）

**架构**：

```
[Microsoft Store]                   [GitHub / 官网下载]
┌─────────────────────┐             ┌─────────────────────────────┐
│  SysMonCmdPal MSIX  │             │  SysMonBroker Setup.exe     │
│                     │  Named Pipe │                             │
│  CpuSensorReader    │◄────────────│  1. 安装 PawnIO 驱动         │
│    └─ BrokerClient  │  \\.\pipe\  │  2. 注册 Broker Windows 服务 │
│                     │  SysMonPipe │  3. 服务以 SYSTEM 运行       │
└─────────────────────┘             │  4. 服务做 PawnIO 调用       │
                                    │  5. 结果写入命名管道         │
                                    └─────────────────────────────┘
```

**通信协议**：命名管道，二进制请求/响应

```
请求: [byte cmd] [reserved x3]
  cmd=1 → 读 CPU Tctl/Tdie
  cmd=2 → 读 CPU 功率
  cmd=3 → 读 GPU 温度
  
响应: [int32 status] [double value] [byte unit]
  status=0 → OK, value 有效
  status=1 → 驱动不可用
  status=2 → 读取超时
```

**Store 合规性**：
- MSIX 本身不含驱动、不含提权代码 → 正常过审
- Companion Installer 独立分发，不经过 Store 审核
- 用户体验：Store 安装主应用 → 首次运行提示"安装硬件支持组件" → 跳转下载页

**代价**：
- 需要 EV 代码签名证书（PawnIO 驱动需要签名）
- 需要维护两个安装包（MSIX + Companion）
- 卸载时需要同时清理两部分

### 方案二：LHM 作为 Companion 组件

**架构**：

```
[Microsoft Store]              [Companion Installer]
┌─────────────────────┐        ┌──────────────────────────┐
│  SysMonCmdPal MSIX  │        │  SysMonLHM Setup.exe     │
│                     │ HTTP   │                          │
│  CpuSensorReader    │◄───────│  1. 安装 LHM (含驱动)     │
│    └─ LhmHttpReader │ :8085  │  2. 配置 LHM 开机自启     │
│                     │        │  3. 启用 LHM Web 服务器   │
└─────────────────────┘        └──────────────────────────┘
```

**优点**：不需要自己维护驱动和 SMU 协议代码，LHM 已封装好  
**缺点**：依赖第三方 LHM、用户需额外安装、LHM 的 InpOut 驱动也需要签名、仍需 Loopback 豁免

### 方案三：ADL 动态校准模型（无额外安装）

不追求绝对精准，而是用 CPU 功率动态补偿 ADL 的偏移：

```
Tctl_estimated = ADL_temp + k × (CPU_Power / TDP_max) × ΔT_max

其中：
  ADL_temp     = ADL PMLOG sensor 32 (SoC 域温度)
  CPU_Power    = ADL PMLOG sensor 33 (CPU 功率)
  TDP_max      = CPU 额定 TDP (可查 CPUID)
  ΔT_max       = 经验值，桌面 ~10°C，笔记本 ~15°C
  k            = 校准系数，默认 1.0
```

**优点**：零额外安装、不需要驱动、完全 Store 合规  
**缺点**：仍是估算，精度 ±2~3°C，不同 CPU 型号差异大

### 方案四：利用用户已安装的 PawnIO（最简路径）

你的系统已经有 PawnIO 驱动（G-Helper 安装的）。我们可以创建一个极轻量的 Broker：

```
[Microsoft Store]                [一键脚本/小工具]
┌─────────────────────┐          ┌──────────────────────────────┐
│  SysMonCmdPal MSIX  │  Pipe    │  SysMonBroker.exe            │
│                     │◄─────────│  (通过计划任务 HighestAvail.  │
│  BrokerClient       │          │   静默获取管理员 token)       │
└─────────────────────┘          └──────────────────────────────┘
```

用户运行一次 `install-broker.bat`（需 UAC 确认），注册计划任务。
之后 Broker 随登录自动启动，MSIX 应用直接读取管道。

**Store 合规性**：
- MSIX 本身干净 → 过审
- Broker 注册脚本独立分发
- 缺点：不算"开箱即用"，用户需额外操作一步

### 对比总结

| 维度 | 方案一 Broker | 方案二 LHM | 方案三 ADL校准 | 方案四 轻量Broker |
|------|:---:|:---:|:---:|:---:|
| 精度 | 精准 Tctl | 精准 Tctl | ±2~3°C | 精准 Tctl |
| Store 过审 | 通过 | 通过 | 通过 | 通过 |
| 额外安装 | 需要 | 需要 | 不需要 | 需要(一键) |
| 开发量 | 中 | 小 | 小 | 小 |
| 驱动签名 | 需要 | 依赖LHM | 不需要 | 依赖已有 |
| 用户体验 | 好 | 中 | 最好 | 较好 |

### 推荐路径

**短期（v1.x）**：方案三 ADL 动态校准 — 快速上线 Store，无需额外安装
**中期（v2.x）**：方案四 轻量 Broker — 用户安装一次脚本，之后自动精准
