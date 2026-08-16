# DevMode 开发者指南(v2.5 管道时代)

DevMode 允许开发者在本机联调 broker 的命名管道 AUTH(跳过 `registered_hashes.txt` 白名单),
方便本地测试与自动化测试。**仅对 dev 构建生效,release 构建永远禁用。**

## 安全模型

| 构建方式 | DevRepoPath | 攻击者投放 marker | DevMode |
|---|---|---|---|
| `dotnet build`(release) | 无 (null) | 无关 | **永远禁用** |
| `dotnet build -p:Dev=true`(开发者) | 内嵌本机路径 | 路径不匹配 | 需 marker + 开关才激活 |
| 攻击者拿到 dev build | 内嵌开发者路径 | 不知道路径 | **无法激活** |

DevMode 激活需要**同时满足**:

1. 二进制是 `-p:Dev=true` 编译(含 `DevRepoPath` 元数据);
2. 编译时注入的路径下存在 `.devmode_marker` 文件(gitignore,不提交);
3. 运行时开关文件 `.devmode_on` 存在(`--devmode-on` 创建,`--devmode-off` 删除)。

激活后: 管道 AUTH 对任意格式合法的 SHA256 hash 放行(仅 dev 构建)。

## 启用步骤(一次性)

```powershell
cd sysmon-cmdpal\SysMonBroker

# 1. 编译 dev build(编译器自动注入当前项目目录路径)
dotnet build -p:Dev=true

# 2. 创建 marker 文件(gitignore,不提交)
New-Item .devmode_marker -ItemType File

# 3. 启动 dev broker 并打开运行时开关
SysMonBroker.exe --devmode-on
```

## 关闭 DevMode

```powershell
SysMonBroker.exe --devmode-off
# 或删除 marker / .devmode_on 文件
```

## 发布 release

**不要**在发布脚本里传 `-p:Dev=true`。默认构建不含 DevRepoPath,`IsDevModeActive()` 第一行即返回 false。
发布前可用工作区根 `verify-devmode.ps1` 断言二进制不含 DevRepoPath attribute:

```powershell
# 正确的 release 构建
dotnet publish -c Release   # 不带 -p:Dev

# 验证: 二进制中 DevRepoPath 只出现源码字符串(1 次), 不应出现盘符路径值
```

## 原理(与旧版 COM 时代一致的编译期门控)

- `-p:Dev=true` 触发 csproj 写入 `AssemblyMetadata("DevRepoPath", "$(MSBuildProjectDirectory)")`;
- `$(MSBuildProjectDirectory)` 是编译时 .csproj 所在目录的绝对路径(含用户名、盘符、目录结构);
- 运行时 `DevModeVerifier` 读取该元数据,检查 marker 文件是否在该路径下;
- 攻击者不知道开发者机器上的路径,无法创建正确位置的 marker;
- release 构建无此元数据,`IsDevModeActive()` 返回 false;
- 与旧版差异: **不再需要 SSH 签名 `.devmode` 文件**——新架构白名单为客户端自助注册(`btop4win.exe --register-broker`),DevMode 只承担本地联调/测试的放行,无"发布后激活"需求。

## 相关文件

- `SysMonBroker/IPC/DevModeVerifier.cs` — 门控实现(编译期路径 + marker + 运行时开关);
- `SysMonBroker/Program.cs` — `--devmode-on/--devmode-off` 命令入口;
- `SysMonBroker/SysMonBroker.csproj` — `-p:Dev` 条件注入;
- `SysMonBroker/IPC/BrokerAdminPipeServer.cs` — AUTH 处调用 `DevModeVerifier.IsDevModeActive()` 放行。
