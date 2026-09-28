# sysmon-cmdpal — 快捷指南

> 完整技术文档见 `AGENTS.md`;本文件为精简版,与 AGENTS.md 保持同步。

## 速览

- PowerToys Command Palette 系统监控扩展(C#/.NET 10 + Windows App SDK 1.6,MSIX)。
- 可选 SysMonBroker(提升权限,独立分发)经 SHM v2 单向提供传感器数据。
- Broker 另监听命名管道 `\\.\pipe\SysMonBrokerAdmin`(btop4win 权限代理,协议见工作区根 `btop4win-broker-ipc.md` §2);计划任务以**交互用户 + Highest** 运行(非 SYSTEM,保证白名单 LOCALAPPDATA 与客户端一致)。

## 常用命令

```powershell
# 构建 + 测试(Windows 工具链;纯 dotnet test 会缺 VS AppxPackage 任务)
& $msbuild SysMonCmdPal.Tests/SysMonCmdPal.Tests.csproj /t:Build /p:Configuration=Debug /p:Platform=x64 /restore /p:GenerateAppxPackageOnBuild=false
& $vstest SysMonCmdPal.Tests/bin/x64/Debug/net10.0-windows10.0.26100.0/SysMonCmdPal.Tests.dll
# 只构建 Tests 项目且带 GenerateAppxPackageOnBuild=false;单独构建主项目会让 MSIX 打包消费 app dll,
# 致测试项目 MSB3030 + vstest 数百条假 FileNotFoundException。还原受限则加本地离线源(见 AGENTS.md §4)。

# Broker Dev 构建 + 运行时开关(仅本地联调, release 构建不带 Dev 元数据)
dotnet build SysMonBroker -p:Dev=true
SysMonBroker.exe --devmode-on   # 需项目目录下有 .devmode_marker
```

## 关键约束

- 传感器回退链不可破坏(分型): GPU=Broker SHM→HWiNFO→D3DKMT→PDH(ThermalZone 不得进入 GPU 链)；CPU温度=Broker SHM→HWiNFO→ThermalZone(全自动)。**GPU 用户态四层 adapter 统一取自唯一出口 `GpuDxgkrnlAdapters`(gdi32+SetupDi,零 COM/零 WMI)**。
- 出货裁剪与 COM:self-contained/RID 布局置 `BuiltInComInterop.IsSupported=false`(分界非 Debug/Release),禁 WMI/DXGI 等 built-in COM;**测试项目无 RID 故单测全绿证实/证伪不了该路径**(结构性盲区),涉 COM/WMI/DXGI 改动须隔离 publish + 交互实跑验证(详见 docs/TECHNICAL_ROADMAP.md §7)。
- 统一 `DockBandRefreshCoordinator` 1s 刷新,禁止页面级独立定时器。
- SHM v2 布局 / 管道协议(btop4win-broker-ipc.md)/ resw 资源 / 测试须同步修改。
- 用户可见文案走 `Strings/{en-US,zh-CN}/Resources.resw`,禁止硬编码。
- Broker 涉及 UAC 操作优先 `gsudo`;SHM/管道进程控制面保持鉴权显式(AUTH 白名单热更新、DevMode 仅 dev 构建)。

## 开发约定

- 中文提交信息(feat/fix/chore/docs/test 前缀);提交前跑全量 xUnit,合入口径 = vstest 实测 **0 failed / 0 skipped**(用例数随任务漂移;v1.7.0 里程碑实测 436,勿硬记数字)。
- 产物放 `release/sysmon-cmdpal/<target>/`,最多 3 个历史版本。
