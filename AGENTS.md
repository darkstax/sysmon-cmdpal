# sysmon-cmdpal — 项目技术文档

> PowerToys Command Palette 系统监控扩展:在命令面板中实时展示 CPU/内存/GPU/温度等传感器数据;
> 可选提升权限的 SysMonBroker 进程采集硬件数据,经共享内存单向送达。

## 1. 技术栈

| 类别 | 内容 |
|------|------|
| 语言/运行时 | C#(.NET 10 preview 特性),目标框架 `net10.0-windows10.0.26100.0` |
| UI 框架 | Windows App SDK 1.6,PowerToys Command Palette 扩展(MSIX + runFullTrust) |
| 通信 | Shared Memory v2(单向)+ 事件通知 |
| 测试 | xUnit,`SysMonCmdPal.Tests` 项目;合入口径 = 全量 `vstest` 实测 0 failed / 0 skipped(数字随任务漂移,勿硬记;v1.7.0 里程碑实测 436) |
| 构建 | Visual Studio / MSBuild(Build Tools 2026),非 Linux `dotnet build` |

## 2. 架构概览

```
SysMonCmdPal(用户态 MSIX 扩展)
   │  SHM v2 单向读 + 事件通知
   ▼
SysMonBroker(可选,提升权限,独立分发)
   │  传感器采集                   命名管道权限代理(btop4win 等客户端)
   ▼                               ▲
Broker SHM → HWiNFO → (GPU 温度/负载/显存: D3DKMT → PDH ／ CPU 温度: ThermalZone)(自动回退链)
```

- **主扩展**:用户态 MSIX(runFullTrust),负责 UI 与命令面板集成;不持有独立定时器,统一走 `DockBandRefreshCoordinator` 共享 1s 刷新。
- **SysMonBroker**:独立分发的提升进程(计划任务以**交互用户 + Highest** 运行,非 SYSTEM——保证白名单 `%LOCALAPPDATA%` 与客户端同路径),传感器采集后单向写入共享内存;`SystemInfoService.Refresh()` 必须并发守卫。
- **AdminPipe(btop4win 权限代理)**:broker 以管理员运行,监听 `\\.\pipe\SysMonBrokerAdmin`(协议见工作区根 `btop4win-broker-ipc.md` §2):AUTH(白名单 `%LOCALAPPDATA%\SysMonCmdPal\registered_hashes.txt` 热更新,客户端自助注册)/ PING / TERMINATE(代理终止管理员进程)/ SERVICE_CONTROL(保留);DevMode(dev 构建 + marker + 运行时开关)仅供本地联调。
- **数据回退**:传感器数据有新鲜度时限,过期自动回退下一级来源,链路全自动,不允许配置项覆盖(如旧 `PrecisionMode` 可读写兼容但不得越过回退链)。
- **兼容性**:SHM v2 布局、管道协议、资源文件与测试须同步修改,保持读写端一致。

## 3. 目录结构

```
sysmon-cmdpal/
├── SysMonCmdPal/            # 主扩展(命令面板、页面、DockBand、设置)
│   ├── Services/            # SystemInfoService、传感器回退链、GpuAdapterEnumerator 等
│   └── Strings/{en-US,zh-CN}/Resources.resw   # 本地化资源(须同步)
├── SysMonBroker/            # 提升权限 Broker(独立分发,dotnet publish 产物)
├── SysMonCmdPal.Tests/      # xUnit 测试(共享内存协议、读端状态机等)
├── PowerToys-sdk/           # PowerToys SDK 依赖
├── release/                 # MSIX/broker 发布产物(每目标最多 3 个历史版本)
└── AGENTS.md / CLAUDE.md    # 本技术文档
```

## 4. 构建与测试

Windows 工具链必需(MSIX/AppX 打包依赖 Windows 侧 MSBuild 任务)。从 WSL 调用时用 `wslpath -w` 转路径 + `pwsh.exe`:

```powershell
$msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
# 只构建测试项目(Tests-only),切勿单独构建主项目:见下方注意事项
& $msbuild SysMonCmdPal.Tests/SysMonCmdPal.Tests.csproj /t:Build /p:Configuration=Debug /p:Platform=x64 /restore `
  /p:GenerateAppxPackageOnBuild=false
& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\Common7\IDE\CommonExtensions\Microsoft\TestWindow\vstest.console.exe" `
  "SysMonCmdPal.Tests/bin/x64/Debug/net10.0-windows10.0.26100.0/SysMonCmdPal.Tests.dll"
```

- 纯 `dotnet test` 会因缺 VS 的 AppxPackage PRI 任务失败(MSB4062),属环境限制,须用上述 MSBuild + vstest 组合。
- **勿单独构建主项目 `SysMonCmdPal.csproj`(不带 `GenerateAppxPackageOnBuild=false`)**:MSIX 打包阶段会把 `SysMonCmdPal.dll` 消费进 `win-x64/AppPackages` 而不落到 RID 根目录,导致测试项目复制依赖报 **MSB3030**,并在 vstest 里表现为**数百条假 `FileNotFoundException`**。正确做法是**只构建 Tests 项目**(它 ProjectReference 主项目)且带 `/p:GenerateAppxPackageOnBuild=false`。
- **离线包源**:代理会在 ~38MB 截断 HTTPS 下载,而 WindowsAppSDK 1.6 达 76MB,Windows 侧 NuGet 无断点续传 ⇒ 干净机器还原常失败。已备本地离线源,restore 追加 `/p:RestoreAdditionalProjectSources=<你的离线包源目录>`(含 WindowsAppSDK 1.6.250205002、CommandPalette 0.9.260303001、CsWin32、WebView2 1.0.2651.64、SDK.BuildTools 10.0.22621.756 等;由 `curl -C -` 断点续传预建)。
- Release 的 trimming/AOT 属性须与 WinRT/COM、Windows App SDK 兼容(**具体后果见 docs/TECHNICAL_ROADMAP.md §7「BuiltInCOM」行**:self-contained/RID 布局会置 `BuiltInComInterop.IsSupported=false`,禁用 WMI/DXGI 等 built-in COM;分界是 RID/裁剪而非 Debug/Release;**本轮定策 = 保留裁剪 + GPU 用户态链 COM-free 化**)。
- 环境要求:Windows 11、PowerToys、开发者模式、Windows App SDK 1.6、VS Build Tools 2026、.NET SDK 10.0.300+。

## 5. 运行与部署

- 扩展本体:MSIX 安装(仓库 `release/` 下产物;csproj 版本线现为 **1.6.0.0**,`release/` 含 1.5.x→1.6.x 历史包;未签名,接受 SmartScreen 警告)。
- Broker:`SysMonBroker` 独立发布,可经 `gsudo` 部署;Broker 启停涉及 UAC,有 `gsudo` 时避免重复弹窗。
- 测试验证:全量 `vstest` **0 failed / 0 skipped** 为合入基准(用例数随任务漂移;v1.7.0 里程碑实测 436)。账目口径见 docs/TECHNICAL_ROADMAP.md §5。

## 6. 关键约束

- **回退链不可破坏**(按传感器分型,见 docs/TECHNICAL_ROADMAP.md §3 约束1): GPU=Broker SHM → HWiNFO → D3DKMT → PDH;CPU温度=Broker SHM → HWiNFO → ThermalZone。ThermalZone 不得进入 GPU 链(ACPI 仅有 CPU 热区,冒充 GPU 温度会误导用户);顺序与自动语义不可破坏,传感器数据时间受限,过期必须回退。**GPU 用户态四层现统一从唯一出口 `GpuDxgkrnlAdapters`(gdi32 `D3DKMTEnumAdapters2` + SetupDi,零 COM/零 WMI)取 adapter**,D3kmt 与 PDH 共用之(不建第二条 interop)。注意 `Backend` 字段目前仍只由 CPU 来源派生、跨链标注 GPU 行是既有债(Phase 2.5 首项),不要据此判断 GPU 数据来源。
- **出货裁剪 vs COM**:`PublishTrimmed`/self-contained 布局禁用 built-in COM ⇒ WMI/DXGI 类 `[ComImport]` 在 trim 产物中不可用;**测试项目无 RID、其 runtimeconfig 无该禁用位 ⇒ 单测全绿不能证实亦不能证伪此路径**(结构性门禁盲区)。任何依赖 COM/WMI/DXGI 的改动须隔离 publish + 交互式实跑验证。
- **共享刷新**:不得给页面/Dock Band 加独立定时器,统一用 `DockBandRefreshCoordinator` 的 1s 刷新。
- **本地化**:`Strings/en-US` 与 `Strings/zh-CN` 的 `Resources.resw` 同步;用户可见文案走资源查找,禁止硬编码;不向 UI 泄漏含路径/环境信息的原始异常。
- **权限边界**:COM/SHM/进程控制面必须保持认证与 ACL 假设显式;经 broker 终止进程须保留鉴权与 Win32 错误上报。
- **SHM 兼容**:SHM v2 布局变更必须生产者/消费者/测试同步更新。
- **探索**:优先 code-review-graph,回退 `rg`/读文件;编辑前查 `git status`(本仓库常有进行中的工作)。

## 7. 开发约定

- 提交信息用中文,带 `feat/fix/chore/docs/test` 前缀,描述实际变更。
- 提交/推送前跑测试(Windows 工具链);构建产物放 `release/sysmon-cmdpal/<target>/`,最多 3 个历史版本。
- 架构/结构变化时同步更新本文件与 `CLAUDE.md`。
