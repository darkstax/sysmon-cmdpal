# Microsoft Store Submission Draft

Product ID: `9N40M6G9MWRV`

Draft submission: `1152921505701316701`

## Package

- Product name: `SysPulse for Command Palette`
- Current repository version: `1.5.0.0`
- Current Store draft package: `1.3.0.0` (replace before certification)
- Architecture: x64
- Category: Utilities & tools
- Pricing: Free
- Required host: Microsoft PowerToys Command Palette

Do not upload the loose-registration output. Generate a Store-associated `1.5.0.0`
MSIX with the Partner Center identity and validate it before replacing the draft
package.

## URLs

- Privacy policy: https://github.com/darkstax/sysmon-cmdpal/blob/master/PRIVACY.md
- Support: https://github.com/darkstax/sysmon-cmdpal/issues
- Website: https://github.com/darkstax/sysmon-cmdpal

## Properties

- This product does not access, collect, transmit, sell, or share personal data.
- Hardware metrics and sensor readings are processed locally.
- The optional Broker contacts GitHub only after the user explicitly confirms
  install or update.
- No advertising, purchases, user-generated content, account system, location
  access, or background network telemetry.
- `runFullTrust` is required to host the out-of-process COM server used by the
  Command Palette extension.

## Age Ratings

Suggested IARC answers for the current build:

- App category: non-game utility.
- Violence, fear, sexuality, language, controlled substances, gambling: none.
- User-generated content or user-to-user communication: none.
- Location sharing: none.
- Digital purchases: none.
- Unrestricted internet browsing: no.
- Online interaction: the optional Broker action only retrieves fixed release
  metadata and a selected asset from the official GitHub repository.

Review every answer against the live questionnaire before saving it.

## English Listing

### Short description

Real-time CPU, memory, disk, network, battery, GPU, and sensor data in Command Palette.

### Description

SysPulse brings local system monitoring into Microsoft PowerToys Command Palette.
Open a compact dashboard for CPU, memory, disks, network traffic, battery, GPUs,
and hardware sensors, or pin live metrics to the Command Palette Dock.

The extension reads system metrics locally and refreshes shared views once per
second. Integrated and discrete GPUs are shown separately, disk usage is grouped
by partition, and detailed pages provide current values, history charts, and
copyable diagnostics.

Advanced hardware readings can use the optional SysMon Broker. The extension
continues to work without it through automatic user-mode fallback providers.
Broker installation is always explicit and validates the selected GitHub release
asset before requesting administrator approval.

Microsoft PowerToys Command Palette is required.

### Features

- CPU usage, temperature, frequency, and core information
- Memory usage and capacity
- Disk partitions, capacity, and read/write throughput
- Network download/upload rates and active interfaces
- Battery level, power state, and health details
- Integrated and discrete GPU usage, temperature, and VRAM
- Categorized hardware sensors with custom Dock bands
- Optional Broker diagnostics and explicit install/update/uninstall controls
- Local processing with no analytics or telemetry

### Search terms

`system monitor`, `command palette`, `PowerToys`, `CPU`, `GPU`,
`hardware sensors`, `performance`

## Chinese Listing

### 简短说明

在 PowerToys 命令面板中查看 CPU、内存、磁盘、网络、电池、GPU 和硬件传感器。

### 说明

SysPulse 将本地系统监控集成到 Microsoft PowerToys 命令面板。你可以快速查看
CPU、内存、磁盘、网络流量、电池、GPU 和硬件传感器，也可以把常用指标固定到
命令面板 Dock 栏。

扩展在本机读取系统指标，并通过共享刷新模型每秒更新页面与 Dock。集成显卡和
独立显卡会分别显示，磁盘占用按分区汇总，详情页提供当前数值、历史图表和可复制
的诊断信息。

高级硬件读数可以使用可选的 SysMon Broker。未安装 Broker 时，扩展仍会自动使用
用户态数据源继续工作。Broker 安装只会在用户明确确认后开始，并在请求管理员批准
前校验来自项目官方 GitHub Release 的对应架构文件。

需要安装 Microsoft PowerToys 命令面板。

### 功能

- CPU 使用率、温度、频率和核心信息
- 内存使用率与容量
- 磁盘分区、容量和读写速度
- 网络下载/上传速度与活动接口
- 电池电量、电源状态和健康信息
- 集显/独显使用率、温度和显存
- 分类硬件传感器与自定义 Dock Band
- 可选 Broker 诊断及安装、更新、卸载控制
- 本地处理，不包含分析或遥测

### 搜索词

`系统监控`, `命令面板`, `PowerToys`, `CPU`, `GPU`, `硬件传感器`,
`性能监控`

## Certification Notes

SysPulse is an extension for Microsoft PowerToys Command Palette and does not
provide a standalone application window. Install or enable PowerToys Command
Palette, open it, and search for "System Monitor" or "系统监控".

The optional Broker is not bundled in the Store package. Its settings action is
explicit, requires confirmation, selects an architecture-specific asset from
the official GitHub repository, validates the GitHub-provided SHA-256 digest and
PE architecture, and then requests one administrator approval. The main
extension remains functional when the Broker is absent.

No test account or external hardware is required for basic certification.

## Submission Options

Recommended draft values:

- Publish after certification: manual or scheduled only after final package review.
- Gradual rollout: off for the first public release.
- Mandatory update: off.
- Certification notes: use the text above.

Do not submit for certification until package version, listing assets, Broker
release/signing decision, and final user approval are complete.

## Screenshot Set

Prepare clean 16:9 PNG screenshots at native desktop resolution:

1. Search result showing the SysPulse entry and app icon.
2. Main dashboard showing the data-category icons and explicit Back command.
3. GPU list showing integrated and discrete GPU icons.
4. Sensor categories or a detailed metrics page.
5. Extension settings showing btop and optional Broker controls.

Avoid private windows in the background. The current cropped 982x591 validation
screenshots are evidence for development review, not final Store assets.
