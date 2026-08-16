# SysMonBroker DevMode 现状分析 & 按 btop4win-broker-ipc.md 的修改方案

> 生成日期: 2026-08
> 依据: 工作区根 `btop4win-broker-ipc.md`(v1.0, 权威协议定义) + sysmon-cmdpal 仓库 git 历史
> 结论先行: **当前 SysMonBroker(v2.4)只实现了协议第 1 章的 SHM 写端;协议第 2 章要求的命名管道服务端(权限代理)在 sysmon-cmdpal 中完全缺失,DevMode 机制也已在 v2.3 移除。btop4win 侧客户端已按协议完整实现,正在等待服务端。**

---

## 1. 根文档要求回顾(btop4win-broker-ipc.md)

两种 IPC 通道,均由 broker 管理端(sysmon-cmdpal 侧)提供服务:

| 通道 | 对象 | 现状 |
|---|---|---|
| 共享内存(只读) | `Global\SysMonBrokerShm`, 16384B, v2 + SMX1 | ✅ broker v2.4 已实现写端 |
| 命名管道(双向, 权限代理) | `\\.\pipe\SysMonBrokerAdmin` | ❌ **服务端缺失** |

管道协议要点(§2):

- 服务端以**管理员**运行,建议 ≥4 个实例,byte 模式,双工;
- 消息帧: 12B 头 `[Magic=0x534B5042 (u32)][Cmd (u32)][载荷长度 (u32)]`,载荷上限 4096;响应头 `[Magic][status=Win32 错误码][len]` + 可选 UTF-8 错误文本;
- 命令表: AUTH=1 `[pid u32][sha256 hex 64B]`(必须是首条) / PING=2 / TERMINATE=3 `[pid u32]` / SERVICE_CONTROL=4(保留);
- 白名单: `%LOCALAPPDATA%\SysMonCmdPal\registered_hashes.txt`,每行一个小写 hex SHA256,**每次 AUTH 重新读文件**(热更新);文件不存在视为白名单为空;
- 客户端注册: `btop4win.exe --register-broker`(只写文件,不再连接 COM);
- 错误语义: 未 AUTH/不在白名单 → `ERROR_ACCESS_DENIED(5)` 并断开;管道不存在 → `ERROR_FILE_NOT_FOUND(2)`;中断 → `ERROR_BROKEN_PIPE(109)`。

## 2. sysmon-cmdpal 中 broker 的 DevMode 内容(读代码 + git 历史)

### 2.1 现状(v2.4):DevMode 已彻底移除

`SysMonBroker/Program.cs` 头注释明确记载(v2.3 变更):

```
// v2.3: Removed COM Local Server (btop4win no longer reads it).
//       Removed JSON snapshot (only btop4win consumed it).
//       Removed DevMode verifier + hash registration.
```

当前 broker 是**纯 SHM 写端**:

- 主循环: 2s 周期采集 LHM → `shm.Write()`;带写者租约互斥(`Global\SysMonBrokerWriter`)防多写者;看门狗兜底重启;
- csproj 仅引用 `LibreHardwareMonitorLib` + `System.Management`;
- **全仓库无 `NamedPipeServerStream` / `SysMonBrokerAdmin` / `registered_hashes` 的任何服务端代码**(grep 验证);
- 部署: 计划任务 `SysMonBroker`, `RunLevel=Highest`(管理员), 装到 `%ProgramFiles%\SysMonCmdPal\Broker` —— 满足管道服务端的运行前提。

### 2.2 DevMode 历史沿革(已删除的代码)

| 版本 | 提交 | 内容 |
|---|---|---|
| v2.2 | `95841cb` | 安全加固: 硬编码 btop.exe SHA256 替换白名单(修空白名单漏洞);新增 `SysMonBroker/COM/DevModeVerifier.cs` — **SSH 签名验证 `.devmode` 文件**(SSHSIG 格式, RSA + Ed25519, challenge `"SysMonBroker.DevMode.v2.2"`, 文件在 `%LOCALAPPDATA%\SysMonCmdPal\.devmode`),激活后跳过硬编码 hash 认证;删除 `--register-hash/--list-hashes` |
| v2.3 前 | `a96fe5b` | DevMode 升级: **编译期路径门控**(`-p:Dev=true` → csproj 写 `AssemblyMetadata("DevRepoPath", 项目目录)`, release 无此元数据 → 短路返回 false)+ `.devmode_marker` 文件门 + `.devmode_on` 运行时开关文件(`--devmode-on/--devmode-off` 命令);BrokerComServer 三级认证(DevMode → 已注册 hash 文件 → 硬编码 hash);新增 `docs/DevMode.md` |
| v2.3 | `3aba406` | **整体删除**: COM Local Server、JSON 快照、DevMode verifier + hash 注册全部移除,broker 改为纯 SHM |

配套物: 工作区根 `verify-devmode.ps1`(校验 release/dev 构建的 DevRepoPath 元数据)、`build-attack-broker.ps1`(渗透测试用 AttackBroker 构建, 目录已不存在)。

**为什么 DevMode 存在**: COM 时代服务端硬编码了 btop.exe 的 SHA256,开发者本地重编 btop.exe 后 hash 变化无法认证 → 需要签名激活的"后门"绕过。**为什么被移除**: v2.3 起 btop4win 不再走 COM,broker 只单向写 SHM,无认证面,DevMode 失去载体。

### 2.3 btop4win 客户端已就绪(等待服务端)

- `src/broker_pipe.cpp/hpp`: 完整实现管道客户端 — 连接(ERROR_PIPE_BUSY 时 WaitNamedPipe 重试)、首条 AUTH(自身 SHA256 + PID)、PING/TERMINATE 调用、错误映射;
- `src/btop.cpp --register-broker`: 计算自身 exe SHA256 并追加写入 `%LOCALAPPDATA%\SysMonCmdPal\registered_hashes.txt`(幂等去重);
- `src/btop_menu.cpp`: 终止进程时本地 `TerminateProcess` 失败且 `ERROR_ACCESS_DENIED` → `pipe.terminate(pid)` 走管道;
- ⚠️ 遗留文案: `btop.cpp` 帮助文本仍写 `--register-broker ... (requires broker DevMode)`,与现状不符(现在是自助写白名单,不需要 DevMode),应更新。

## 3. 差距分析:协议 §2 vs 现有代码

| 协议要求 | 现状 | 差距 |
|---|---|---|
| 管道服务端 `SysMonBrokerAdmin`, ≥4 实例, 管理员运行 | 无任何服务端代码 | **需新建** |
| 管道 ACL SDDL `D:P(A;;GRGW;;;BU)(A;;GA;;;BA)(A;;GA;;;SY)` | 无 | **需新建** |
| 帧格式 Magic/命令/长度 + 响应 status/载荷 | 无 | **需新建** |
| AUTH: 白名单文件校验, 热更新, 首条强制, 失败断开 | 无(文件只被 btop4win 写入) | **需新建** |
| PING / TERMINATE(管理员 TerminateProcess) | 无 | **需新建** |
| SERVICE_CONTROL(保留命令) | 无 | 可选实现 |
| DevMode(开发者绕过认证) | v2.3 已删, 无载体 | 需重新设计(见 §4.2) |

## 4. 修改方案

### 4.1 阶段一(核心): 在 SysMonBroker 实现命名管道服务端

**新增文件**(建议 `SysMonBroker/IPC/` 下, 与现有 `BrokerSharedMemory.cs` 并列):

1. `BrokerAdminPipe.cs` — 常量与消息定义:
   - `PipeName = @"\\.\pipe\SysMonBrokerAdmin"`, `Magic = 0x534B5042`, 命令枚举 AUTH=1/PING=2/TERMINATE=3/SERVICE_CONTROL=4, 载荷上限 4096;
2. `BrokerAdminPipeServer.cs` — 服务端主体:
   - `NamedPipeServerStream(PipeName, PipeDirection.InOut, 4, Byte, Async)` + `PipeSecurity`(SDDL 按协议 §2.1);
   - 异步 Accept 循环(独立后台线程, 与 2s 传感器主循环并行, **不得阻塞传感器周期**);
   - 每连接: 读 12B 头 → 校验 Magic/长度 → 首条必须是 AUTH → 分发执行 → 写响应 → 断开;
   - TERMINATE: `Process.GetProcessById(pid).Kill(entireProcessTree: false)`(服务端以管理员运行, 天然有 SeDebugPrivilege 的进程终止权限);异常映射为 Win32 错误码(如进程不存在 → 1168),按协议 §2.7 原样返回;
   - 安全细节: 单连接串行处理;非法 Magic 直接断开;载荷超限断开;`GetNamedPipeClientProcessId`(P/Invoke)校验 AUTH 载荷中的 pid 与真实客户端一致(协议未强制, 建议的加固项);
3. `BrokerHashWhitelist.cs` — 白名单读取:
   - 路径 `%LOCALAPPDATA%\SysMonCmdPal\registered_hashes.txt`;
   - 每次 AUTH 重新读文件(热更新);逐行 `OrdinalIgnoreCase` 比对 64 字符 hex;文件缺失/空 → 拒绝;
4. `BrokerAdminPipeServer` 挂接 `Program.cs`:
   - 与 SHM/采集器并行启动;日志走现有 `BrokerLogger`;看门狗语义不变;
   - SERVICE_CONTROL: 按协议 §2.6 实现或先返回 `ERROR_NOT_SUPPORTED` 占位(btop4win 未接线, 建议先占位并记录日志)。

### 4.2 阶段二: DevMode 重新设计(针对管道认证面)

**新架构下 DevMode 的定位变化**: 白名单改为自助注册(`--register-broker` 直接写文件),开发者重编 btop.exe 后重跑一次注册即可,不再需要"跳过硬编码 hash"的后门。DevMode 的剩余用途是**开发/测试便利**: 自动化测试、CI、或不想动白名单文件的本地联调。

**推荐方案 — 复用 v2.3 已验证的门控模式, 重新挂到管道 AUTH 上**:

1. 恢复 `-p:Dev=true` 编译期注入: csproj 在 Dev 构建时写 `AssemblyMetadata("DevRepoPath", 项目目录)`(代码可从 git 历史 `a96fe5b` 取回, 逻辑基本可原样复用);
2. 恢复 `.devmode_marker`(仓库内文件门)+ `.devmode_on` 运行时开关文件(`--devmode-on/--devmode-off` 命令, 跨进程共享状态) —— **去掉 SSH 签名 `.devmode` 环节**(它是 COM 时代为"发布后激活"设计的, 新架构无发布后激活需求, 反而增加复杂度; 保留编译期门控 + marker 已足够防止 release 泄露);
3. 语义: DevMode 激活时 AUTH 对任意格式合法的 hash 返回成功(仅限 dev 构建 + marker + 开关三者同时满足, 与旧模型一致);release 构建无 `DevRepoPath` 元数据, 第一行短路返回 false, 编译期即不可达;
4. 更新 `docs/DevMode.md` 与 `verify-devmode.ps1`(验证逻辑不变, 构建产物路径更新);
5. 同步修正 btop4win 侧 `btop.cpp` 帮助文案("requires broker DevMode" → 描述自助注册流程)。

**备选(更轻)**: 不加 DevMode,dev 联调直接 `--register-broker`。缺点: 自动化测试无法确定性构造"白名单拒绝"场景, 且会污染真实白名单文件。不推荐作为唯一方案。

### 4.3 阶段三: 测试与验证

- **xUnit 单测**(可纯逻辑测, 不进真管道): 帧编解码、AUTH 载荷解析(68B)、白名单热更新、大小写不敏感比对、TERMINATE 错误映射、DevMode 门控逻辑(DevRepoPath 有无 / marker / 开关四象限);
- **集成验证**(Windows 工具链): 管理员起 broker → 用 btop4win(`--register-broker` 后)执行终止管理员进程 → 观察 AUTH/PING/TERMINATE 往返;用未注册 exe 验证 `ERROR_ACCESS_DENIED` 断开;
- 回归: 仓库 300 个 xUnit 用例全绿;`verify-devmode.ps1` 对 dev/release 两种构建断言。

### 4.4 阶段四: 文档与仓库同步

- `btop4win-broker-ipc.md` §2.8"服务端实现要点(C# 参考)"升级为实际实现说明;
- 本仓库 `AGENTS.md`/`CLAUDE.md` 架构段更新(新增管道服务端模块);
- 按工作区规则同步 coding-vault 笔记(`1-Projects/sysmon-cmdpal/20-架构模型.md`、`24-迭代候选.md`)。

## 5. 风险与注意事项

| 风险 | 说明 | 缓解 |
|---|---|---|
| 管道是提权面 | 任何被白名单放行的用户态程序可终止管理员进程(TERMINATE);且白名单文件同用户可自助写入(协议设计如此) | 严格首条 AUTH 强制;pid 与客户端一致性校验;ACL 按协议;文档明示威胁模型 |
| 会话隔离 | 命名管道默认机器级可见, `BU` 可读写 → 其他会话进程也可连接 | 可选加固: PipeSecurity 加会话 SID 限制;先按协议 SDDL 实现 |
| 与传感器主循环互相干扰 | 管道阻塞可能影响 2s 采集周期 | 独立异步 Accept 线程;单连接串行;读写超时;看门狗保持兜底 |
| 载荷/帧校验不严 | 畸形帧可导致内存拷贝越界 | 严格校验 Magic + 长度上限 4096 + 异常捕获断开 |
| DevMode 回归泄露 | release 构建误带 DevMode | 编译期门控(无元数据即短路)+ `verify-devmode.ps1` 发布前断言 |
| 部署 | 新 exe 需走现有计划任务安装器 | 现有 `BrokerInstallElevation.Script.cs` 流水线直接可用, 无新部署面 |

## 6. 变更清单(摘要)

1. 新增 `SysMonBroker/IPC/BrokerAdminPipe.cs`(协议常量/命令)
2. 新增 `SysMonBroker/IPC/BrokerAdminPipeServer.cs`(Accept 循环 + AUTH/PING/TERMINATE/SERVICE_CONTROL 处理)
3. 新增 `SysMonBroker/IPC/BrokerHashWhitelist.cs`(白名单热更新读取)
4. `Program.cs`: 挂接管道服务端 + 恢复 `--devmode-on/--devmode-off` 参数
5. `SysMonBroker.csproj`: 恢复 `-p:Dev` 条件注入 `AssemblyMetadata("DevRepoPath")`
6. 恢复(精简版)`DevModeVerifier`: 编译期门控 + marker + 运行时开关, 挂到 AUTH
7. 更新 `docs/DevMode.md`、`verify-devmode.ps1`、根协议文档 §2.8、btop4win 帮助文案
8. 新增 xUnit 用例(帧/AUTH/白名单/DevMode 门控)
