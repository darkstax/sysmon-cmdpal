# Changelog

## [Unreleased] — 2026-06-09

### Added
- **全量 LHM 传感器集成**：17 类传感器枚举（CPU/GPU/主板/存储的温度、负载、频率、功耗、风扇、电压、显存）
- **传感器列表页** (`SensorListPage`): 按类别浏览所有 LHM 传感器，点击添加/移除 Dock
- **动态传感器 Dock Band** (`SensorDockBand`): 用户可选任意传感器固定到 Dock 栏
- **传感器回退链路**: LHM (PawnIO) → AMD ADL → HWiNFO 共享内存 → 不可用，四级自动降级
- **传感器后端状态** (`SensorBackend` 枚举): 主页/详情页实时显示当前数据源及降级原因
- **LHM 健康追踪**: `LhmSensorService.LastError` + 连续失败计数 + 自动标记不可用 + `TryReconnect()` 热恢复
- **HWiNFO GPU 温度读取**: `AmdTempReader.ReadGpuTempViaHwInfo()` 支持 GPU 温度回退
- **`SensorCategoryMeta.GetIcon()`** 图标缓存，消除每秒 IconInfo 分配
- **`DockFormat.BatteryStatusText()`** 统一电池状态中文映射
- **`DockFormat.TempMd()` / `PercentMd()`** Markdown 格式化辅助
- **`TrimmerRootAssembly`** 保护 LibreHardwareMonitorLib 不受 Release 裁剪破坏

### Changed
- **磁盘 Dock Band**: 标题改为 `↓速度 ↑速度` 紧凑格式，副标题显示各分区使用率
- **网络 Dock Band**: 拆分为独立的下载/上传两个 Band
- **温度显示**: `DockFormat.Temp()` 从 `> 0` 放宽为 `>= 0`
- **统一格式化**: 所有 Page/DockBand 的 `FormatSpeed`/`FormatTemp`/`FormatPercent`/`StatusText` 统一到 `DockFormat`
- **文件拆分**: `BtopLauncherCommand` → `Commands/`; `SensorCategoryMeta` → `Models/`; `ToggleSensorCommand` → `Commands/`
- **CPU 详情页**: 移除架构/CLR 信息，中文标签，显示后端状态
- **GPU 详情页**: 修复过时注释（"需管理员权限"），显示后端来源

### Fixed
- **LHM 崩溃静默**: `Refresh()` 不再吞异常，`LastError` + 连续失败追踪
- **配置丢失风险**: 构造函数中 LHM 失败时保留已加载的 `SensorConfig`
- **子硬件遍历**: `CollectSubHardware` 改为递归，AMD CCD#0→Core#0 等多层传感器不再丢失
- **`SysMonExtension.Dispose`**: 补充 `_provider.Dispose()` 调用
- **`SensorDockBand.OnRefresh`**: 权限从 `public` 改为 `internal`
- **`ToggleSensorCommand._onChanged`**: 回调现在实际调用
- **`SensorReading` NRT**: 所有 string 字段标注 `string?`，消除 null 哨兵与 NRT 矛盾
- **死代码清理**: 删除旧版 `Commands/SysMonDockBand.cs`

### Removed
- 旧版单体 `SysMonDockBand` 类（已由 `SysMonDockBands.cs` 中的分体 Band 替代）

---

## [0.1.0] — 2026-06-08

### Added
- 初始版本：PowerToys Command Palette 系统监控扩展
- CPU 使用率、内存、磁盘空间、网络速度、电池状态
- LHM 集成：CPU/GPU 温度 + GPU 使用率/显存
- 磁盘 IO 读写速度 + 卷标显示
- btop4win 一键启动
- Dock Band 常驻显示（1s 刷新共享协调器）
- MSIX 打包，支持 x64 和 ARM64
- 全部页面中文本地化
