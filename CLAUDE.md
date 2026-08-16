# sysmon-cmdpal — 快捷指南

> 完整技术文档见 `AGENTS.md`;本文件为精简版,与 AGENTS.md 保持同步。

## 速览

- PowerToys Command Palette 系统监控扩展(C#/.NET 10 + Windows App SDK 1.6,MSIX)。
- 可选 SysMonBroker(提升权限,独立分发)经 SHM v2 单向提供传感器数据。
- Broker 另监听命名管道 `\\.\pipe\SysMonBrokerAdmin`(btop4win 权限代理,协议见工作区根 `btop4win-broker-ipc.md` §2)。

## 常用命令

```powershell
# 构建 + 测试(Windows 工具链;纯 dotnet test 会缺 VS AppxPackage 任务)
& $msbuild SysMonCmdPal.Tests/SysMonCmdPal.Tests.csproj /t:Build /p:Configuration=Debug /p:Platform=x64 /restore
& $vstest SysMonCmdPal.Tests/bin/x64/Debug/net10.0-windows10.0.26100.0/SysMonCmdPal.Tests.dll

# Broker Dev 构建 + 运行时开关(仅本地联调, release 构建不带 Dev 元数据)
dotnet build SysMonBroker -p:Dev=true
SysMonBroker.exe --devmode-on   # 需项目目录下有 .devmode_marker
```

## 关键约束

- 传感器回退链不可破坏:Broker SHM → HWiNFO → D3DKMT → PDH → ThermalZone(全自动)。
- 统一 `DockBandRefreshCoordinator` 1s 刷新,禁止页面级独立定时器。
- SHM v2 布局 / 管道协议(btop4win-broker-ipc.md)/ resw 资源 / 测试须同步修改。
- 用户可见文案走 `Strings/{en-US,zh-CN}/Resources.resw`,禁止硬编码。
- Broker 涉及 UAC 操作优先 `gsudo`;SHM/管道进程控制面保持鉴权显式(AUTH 白名单热更新、DevMode 仅 dev 构建)。

## 开发约定

- 中文提交信息(feat/fix/chore/docs/test 前缀);提交前跑 300 个 xUnit 用例。
- 产物放 `release/sysmon-cmdpal/<target>/`,最多 3 个历史版本。
