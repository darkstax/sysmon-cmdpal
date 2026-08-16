# sysmon-cmdpal 未提交改动分析报告

> 分析时间: 2026-08 | 对象: `git status` 全部未提交内容(截至本报告)
> 主题: **Microsoft Store 认证失败修复 + 1.5.0.0 → 1.6.0.0 版本提升**(与 broker/DevMode 无关, 纯主扩展商店提交工作)

## 1. 改动清单总览

```
 M SysMonCmdPal/Program.cs                    # +37: 无参数启动行为(商店认证修复核心)
 M SysMonCmdPal/SysMonCmdPal.csproj           # 版本 1.6.0.0 + Appx 打包属性 + Assets 通配
 M SysMonCmdPal/Package.appxmanifest          # 版本 1.6.0.0 + DefaultTile 补 Small/LargeTile
 M SysMonCmdPal/Public/manifest.json          # 版本 1.6.0.0
 M SysMonCmdPal/Assets/generate_icons.ps1     # +10: 生成 SmallTile/LargeTile
 M SysMonCmdPal.Tests/BrokerInstallerTests.cs # 测试 JSON 构建改 JsonNode DOM(AOT 安全)
 M docs/store-submission.md                   # +45: 认证失败记录 + 根因 + 修复清单
?? SysMonCmdPal/Assets/SmallTile.png          # 71×71, 4.3KB(脚本产物)
?? SysMonCmdPal/Assets/LargeTile.png          # 310×310, 30KB(脚本产物)
?? docs/broker-devmode-analysis.md            # 上一轮我生成的报告(不属于本次工作)
```

已构建产物佐证: `release/1.6.0.0/` 下已有 x64 + arm64 两个 MSIX,`release/1.5.0.0/` 有含 `_appeal.msix` 的旧包 —— 改动已进入构建验证阶段。

## 2. 逐文件分析

### 2.1 `SysMonCmdPal/Program.cs` — 认证修复核心

- **改动**: 无参数启动时不再立即退出,改为在 STA 线程弹**中英双语 MessageBox**("SysPulse 是 PowerToys 命令面板扩展,没有独立界面…"),进程存活到用户关闭对话框;
- **动机**(store-submission.md 记载): 商店认证(政策 10.1.2.10 Functionality)报"产品启动即崩溃"——扩展是纯 COM Server,无参数时退出码 0 立即退出,被认证 harness 误判为崩溃(WER 无记录,Error Message: N/A);
- **评审**: COM 路径(`-RegisterProcessAsComServer`)未动;MessageBox 用 P/Invoke `MessageBoxW` + 后台 STA 线程 + `Join()`,实现正确。风险: 认证环境无人点击对话框时进程会挂起而非退出——但"挂起等待用户"比"立即退出被判崩溃"更符合商店预期,属合理权衡(文档已记录)。

### 2.2 `SysMonCmdPal.csproj`

- 三处版本 `1.5.0.0 → 1.6.0.0`(AssemblyVersion/FileVersion/Version);
- 新增 `AppxPackageIdentityName` / `AppxPackagePublisher` / `AppxPackageVersion`(Partner Center 值, 按 publish-extension-store 文档);
- Assets Content 从 4 个显式 PNG 改为通配 `Assets\**\*.png`(SmallTile/LargeTile 自动进包)。

### 2.3 `Package.appxmanifest`

- `Identity Version="1.5.0.0" → "1.6.0.0"`(与 csproj 一致 ✅);
- `DefaultTile` 新增 `Square71x71Logo="Assets\SmallTile.png"` + `Square310x310Logo="Assets\LargeTile.png"`(商店验证要求的基础 tile)。

### 2.4 `Public/manifest.json`

- 扩展清单 `version: 1.5.0.0 → 1.6.0.0`,与 appxmanifest 对齐 ✅。

### 2.5 `generate_icons.ps1` + 两个新 PNG

- 新增 3.5 节: 从 `Square150x150Logo.scale-200.png` 缩放生成 `SmallTile.png`(71×71)/`LargeTile.png`(310×310);
- **产物尺寸已验证正确**(`file` 确认 71×71 与 310×310, RGBA 8-bit);
- 函数签名复用已有 `Save-ScaledCopy`,风格一致。

### 2.6 `BrokerInstallerTests.cs`

- `ReleaseBody()` 从匿名类型反射序列化(`JsonSerializer.SerializeToUtf8Bytes(new {...})`)改为 `JsonObject`/`JsonArray` DOM 构建 + `ToJsonString()`;
- **动机**: AOT/Trim 安全——与产品代码 `BrokerInstaller.ReleaseDownload.cs` 的 `JsonDocument.Parse`(DOM)策略对齐;裁剪模式下反射序列化被禁用;
- **评审**: 与产品代码一致,方向正确。注意 `JsonSerializer` 命名空间 using 仍在文件头部(`System.Text.Json`),不影响;产物 JSON 键序可能变化(JsonObject 保序, 与原匿名类型序一致),语义无变化。

### 2.7 `docs/store-submission.md`(+45 行)

记录认证失败 1 全链路: 失败详情(Surface Laptop 5, OS 26200.8655, 落后 5 个更新)→ 根因(无 UI COM 扩展启动即退出被误判)→ 5 项修复 → 待办:
1. Windows 侧构建 + `dotnet test`(CsWinRT 仅 Windows, Linux 无法验证);
2. x64 + ARM64 合成一个 `.msixbundle`;
3. 向 `reportapp@microsoft.com` 申诉(WER 证据 + 指出测试机系统版本落后);
4. 重建 MSIX 后重新提交草稿。

## 3. 发现的问题与风险

| # | 问题 | 说明 | 建议 |
|---|---|---|---|
| 1 | **文档待办过时** | store-submission.md 写"Rebuild the **1.5.0.0** Store-associated MSIX",但代码/清单已全升 1.6.0.0 且 `release/1.6.0.0/` 已有构建产物 | 待办文字应改为 1.6.0.0(或注明"用新 manifest 重建后重新提交"),避免下次提交时语义混乱 |
| 2 | **测试未在 Windows 验证** | BrokerInstallerTests 改动 + 全部改动尚未跑 300 用例(工具链限制) | 提交前必须 Windows 侧 MSBuild + vstest 全绿 |
| 3 | **PNG 未跟踪** | 两个 tile PNG 是构建必需资产, 目前 untracked | 提交时须 `git add` 一并入库, 否则换机后脚本不跑会缺文件 |
| 4 | Assets 通配符 | `Assets\**\*.png` 会包入未来所有 PNG(含可能的临时/调试图) | 低风险, 可接受; 如介意可改显式列表 + 新文件 |
| 5 | Publisher 双源定义 | csproj `AppxPackagePublisher` 与 appxmanifest `Publisher` 各定义一份(值一致) | 已一致, 仅提示: 改签名证书时两处须同步 |
| 6 | MessageBox 挂起语义 | 无人值守认证环境对话框无人点 → 进程挂起(非崩溃) | 已是文档化权衡; 如二次失败可考虑加超时自动退出 |

## 4. 结论

- 这批改动是**一个内聚的"商店认证失败修复 + 版本提升 1.6.0.0"工作**,逻辑自洽、方向正确,已构建出 x64/arm64 MSIX;
- 与 broker / DevMode / 命名管道工作**无交叉**,互不影响;
- 提交前必做: ① 修 store-submission.md 版本号待办笔误; ② Windows 侧跑全量测试; ③ `git add` 两个 PNG; ④ 中文提交信息(`fix(store): …` 前缀,按仓库约定)。

## 5. 与上一份 broker 报告的关联

两份报告互相独立,但提交时建议**分开提交**(商店提交工作一组, 文档报告另一组),避免混在一个 commit 里。
