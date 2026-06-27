# DevMode 开发者指南

DevMode 允许开发者跳过 btop.exe SHA256 认证，方便本地测试 broker 的所有 COM 接口。

## 安全模型

| 构建方式 | DevRepoPath | 攻击者投放 marker | DevMode |
|---|---|---|---|
| `dotnet build` (release) | 无 (null) | 无关 | **永远禁用** |
| `dotnet build -p:Dev=true` (开发者) | 内嵌本机路径 | 路径不匹配 | 需 marker 才激活 |
| 攻击者拿到 dev build | 内嵌开发者路径 | 不知道路径 | **无法激活** |

DevMode 激活需要**同时满足**：
1. 二进制是 `-p:Dev=true` 编译（含 `DevRepoPath` 元数据）
2. 编译时注入的路径下存在 `.devmode_marker` 文件
3. `%LOCALAPPDATA%\SysMonCmdPal\.devmode` 含有效的 SSH 签名

## 启用步骤（一次性）

```powershell
cd sysmon-cmdpal\SysMonBroker

# 1. 编译 dev build (编译器自动注入当前项目目录路径)
dotnet build -p:Dev=true

# 2. 创建 marker 文件 (gitignore, 不提交)
New-Item .devmode_marker -ItemType File

# 3. 签名 challenge
Set-Content -NoNewline $env:TEMP\challenge "SysMonBroker.DevMode.v2.2"
ssh-keygen -Y sign -n devmode -f $env:USERPROFILE\.ssh\id_ed25519 $env:TEMP\challenge

# 4. 部署签名文件
$devmodeDir = "$env:LOCALAPPDATA\SysMonCmdPal"
New-Item $devmodeDir -ItemType Directory -Force
Copy-Item "$env:TEMP\challenge.sig" "$devmodeDir\.devmode"
```

## 验证

```powershell
# 启动 dev build 的 broker, 然后:
# DevMode 应激活 (Authenticate 任意 hash 返回 0)
```

## 关闭 DevMode

删除 marker 或 `.devmode` 文件即可：

```powershell
Remove-Item .devmode_marker
# 或
Remove-Item "$env:LOCALAPPDATA\SysMonCmdPal\.devmode"
```

## 发布 release

**不要**在发布脚本里传 `-p:Dev=true`。默认构建不含 DevRepoPath，DevMode 代码路径被编译器短路为 `return false`。

```powershell
# 正确的 release 构建
dotnet publish -c Release  # 不带 -p:Dev

# 验证二进制不含 DevRepoPath:
# [System.Reflection.Assembly]::LoadFile("...\SysMonBroker.dll").GetCustomAttribute<AssemblyMetadataAttribute>()
# 应返回 null
```

## 原理

- `-p:Dev=true` 触发 csproj 写入 `AssemblyMetadata("DevRepoPath", "$(MSBuildProjectDirectory)")`
- `$(MSBuildProjectDirectory)` 是编译时 .csproj 所在目录的绝对路径（含用户名、盘符、目录结构）
- 运行时 `DevModeVerifier` 读取该元数据，检查 marker 文件是否在该路径下
- 攻击者不知道开发者机器上的路径，无法创建正确位置的 marker
- release 构建无此元数据，`IsDevModeActive()` 第一行即返回 false
