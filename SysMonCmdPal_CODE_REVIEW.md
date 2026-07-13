# SysMonCmdPal Code Review Notes

Date: 2026-07-12
Scope: `sysmon-cmdpal`

Notes:
- This review did not rely on `CLAUDE.md`.
- `code-review-graph` was used first for architecture, hotspots, and coverage checks.
- Source changes were made after the initial review pass; see the fixed sections below.

## Findings

### High

1. Broker shared memory can be pre-created or spoofed.

   `SysMonBroker/IPC/BrokerSharedMemory.cs` uses fixed global names:
   `Global\SysMonBrokerShm` and `Global\SysMonBrokerEvent`.

   The map DACL is `D:(A;;GR;;;WD)(A;;GA;;;BA)`, and when an object already
   exists, the Broker accepts it after only checking magic/version. The plugin
   reader also trusts any mapping with the expected magic. A local process may
   be able to pre-create or tamper with the named object and feed false sensor
   data to the UI.

   References:
   - `SysMonBroker/IPC/BrokerSharedMemory.cs:26`
   - `SysMonBroker/IPC/BrokerSharedMemory.cs:62`
   - `SysMonBroker/IPC/BrokerSharedMemory.cs:126`
   - `SysMonCmdPal/Broker/SharedMemoryReader.cs:99`

### Medium

2. GPU power is summed per sensor, not per GPU/hardware.

   `SystemPowerReader.Read()` says each GPU should take the maximum power per
   hardware, but it actually adds every `GpuPower` sensor whose name contains
   `Core` or `Package`. If one GPU exposes multiple total-like power sensors,
   system power is overreported. This affects battery dual-power display.

   Reference:
   - `SysMonCmdPal/Services/SystemPowerReader.cs:40`

3. Detail pages subscribe to `DockBandRefreshCoordinator` without unsubscribe.

   The coordinator supports `Unsubscribe`, but detail pages only subscribe.
   Recreated or replaced pages can stay alive and continue receiving refresh
   callbacks every second, retaining `FormContent` and chart state.

   References:
   - `SysMonCmdPal/Commands/SysMonDockBands.cs:33`
   - `SysMonCmdPal/Pages/CpuDetailPage.cs:186`
   - `SysMonCmdPal/Pages/BatteryDetailPage.cs:278`
   - `SysMonCmdPal/Pages/GpuDetailPage.cs:287`
   - `SysMonCmdPal/Pages/DiskDetailPage.cs:326`

4. CPU/GPU dock title treats `N/A` as a valid temperature.

   `DockFormat.Temp(-1)` returns `N/A`, but CPU/GPU dock bands check
   `temp.Length > 0`, so unavailable temperature still uses the title format
   intended for real temperature data.

   References:
   - `SysMonCmdPal/Commands/SysMonDockBands.cs:127`
   - `SysMonCmdPal/Commands/SysMonDockBands.cs:176`
   - `SysMonCmdPal/Commands/SysMonDockBands.cs:377`

5. Version metadata is inconsistent, and certificate password is committed.

   Main assembly is `1.5.0.0`, MSIX identity is `1.3.0.8`, public CmdPal
   manifest is `1.1.0.0`, Broker logs `v2.3`, and Broker csproj says
   `2.2.0.0`. `PackageCertificatePassword` is also written directly in the
   project file.

   References:
   - `SysMonCmdPal/SysMonCmdPal.csproj:19`
   - `SysMonCmdPal/SysMonCmdPal.csproj:38`
   - `SysMonCmdPal/Package.appxmanifest:11`
   - `SysMonCmdPal/Public/manifest.json:7`
   - `SysMonBroker/Program.cs:31`
   - `SysMonBroker/SysMonBroker.csproj:8`

### Low / Maintenance

6. Shared-memory layout constants are duplicated in three places.

   The Broker writer, plugin reader, and `ShmLayout` each manually define map
   sizes and offsets. Current values appear aligned, but future changes can
   silently corrupt reads.

   References:
   - `SysMonBroker/IPC/BrokerSharedMemory.cs:34`
   - `SysMonCmdPal/Broker/SharedMemoryReader.cs:38`
   - `SysMonCmdPal/Broker/ShmLayout.cs:1`

7. `SharedMemoryReader` comments contradict the implementation.

   The file header says it uses P/Invoke `OpenFileMapping + MapViewOfFile`,
   but the implementation uses managed `MemoryMappedFile.OpenExisting` and
   persistent `MemoryMappedViewAccessor` handles.

   References:
   - `SysMonCmdPal/Broker/SharedMemoryReader.cs:6`
   - `SysMonCmdPal/Broker/SharedMemoryReader.cs:71`

8. `SensorChainConfig` appears to be runtime-dead code.

   The provider says precision-mode settings were removed, and search only
   found runtime references in tests. Either delete the dead config/tests or
   reconnect it to an actual setting surface.

   References:
   - `SysMonCmdPal/SysMonCommandsProvider.cs:26`
   - `SysMonCmdPal/Models/SensorChainConfig.cs:29`

9. Battery detail initial `isDual` type is inconsistent.

   The AdaptiveCard template compares `isDual` to string `"true"`, and update
   code writes `"true"` / `"false"`, but the constructor's initial JSON uses
   boolean `false`.

   References:
   - `SysMonCmdPal/Pages/BatteryDetailPage.cs:138`
   - `SysMonCmdPal/Pages/BatteryDetailPage.cs:275`
   - `SysMonCmdPal/Pages/BatteryDetailPage.cs:441`

## Test / Verification Notes

- `dotnet test SysMonCmdPal.Tests/SysMonCmdPal.Tests.csproj --no-restore`
  failed on Linux because the project targets Windows and requires
  `EnableWindowsTargeting=true`.
- Retrying with `-p:EnableWindowsTargeting=true` failed because the existing
  restore assets reference the Windows Visual Studio fallback folder
  `C:\Program Files (x86)\Microsoft Visual Studio\Shared\NuGetPackages`.
- `code-review-graph` reported no tests for these critical paths:
  `BrokerSharedMemory`, `SharedMemoryReader`, `SystemPowerReader`,
  and `SysMonDockBands`.

## Suggested Fix Order

1. Harden Broker shared-memory creation and reader trust boundary.
2. Fix GPU power aggregation by grouping per hardware/GPU and selecting one
   canonical or maximum value.
3. Add unsubscribe/disposal handling for all detail pages that subscribe to
   `DockBandRefreshCoordinator`.
4. Fix CPU/GPU dock temperature availability checks.
5. Normalize package/app/broker versions and move certificate password out of
   the project file.
6. Consolidate shared-memory layout constants or generate both sides from one
   source.

## Fixed In First Pass

Date: 2026-07-12

- Tightened the Broker shared-memory DACL and reinitialize existing mappings on
  startup, preserving hot restart when the plugin still holds a read handle.
- Fixed GPU power aggregation to group by hardware tag and take one maximum
  value per GPU class.
- Added safe unsubscribe/dispose paths for detail pages that subscribe to
  `DockBandRefreshCoordinator`.
- Fixed CPU/GPU dock temperature availability checks so `N/A` is not treated
  as a real temperature.
- Normalized main package/public manifest versions to `1.5.0.0` and Broker
  project version to `2.3.0.0`.
- Removed the hard-coded package certificate password from the project file.
- Fixed Battery detail initial `isDual` JSON type.
- Updated `SharedMemoryReader` comments to match the managed
  `MemoryMappedFile` implementation.
- Removed the redundant explicit `System.Text.Json` package reference.
- Made localization safe under testhost so ResourceLoader does not crash unit
  tests outside the packaged app context.

Verification:
- Host PowerShell 7.6.3 with VS 18 BuildTools MSBuild.
- `SysMonCmdPal` Debug/x64 build passed and produced
  `SysMonCmdPal_1.5.0.0_x64_Debug.msix`.
- `SysMonBroker` Debug/x64 build passed.
- `SysMonCmdPal.Tests` passed: 88/88.

## Fixed In Second Pass

Date: 2026-07-12

- Added `Generated Files/` to `.gitignore`; the VS-generated
  `SysMonCmdPal/Generated Files/CsWinRT` files are now ignored build output.
- Updated `deploy.ps1` so elevated broker operations relaunch through PowerShell
  7 (`pwsh.exe`) instead of hard-coded Windows PowerShell.
- Updated `deploy.ps1` build helpers to prefer VS 18/2026 amd64 MSBuild and use
  MSBuild `/t:Publish` for the Broker instead of `dotnet publish`.
- Updated `deploy.ps1` to unblock the Broker executable after copying it to the
  local `%ProgramFiles%\SysMonCmdPal\Broker` deployment directory. This avoids
  Mark-of-the-Web prompts if the file ever carries a `Zone.Identifier` stream.
- Added Appx MSBuild tools path discovery for VS 18/2026 and VS 2022 fallback.
- Packed same-type hardware instance index into Broker sensor `HardwareTag`
  (`type | (instance << 8)`), so multiple NVIDIA/AMD/Intel GPUs are not
  collapsed into one power bucket.
- Sorted Broker GPU snapshots by GPU index before producing UI results, avoiding
  refresh-to-refresh ordering jitter from `ConcurrentDictionary.Values`.
- Increased shared-memory `MaxSensors` from 128 to 250. Real hardware exposed
  more than 128 LHM sensors, so the previous limit truncated the Broker sensor
  list even though the 16KB mapping had room for more entries.

Residual notes:
- The shared-memory trust boundary is materially better with `Global\` names and
  a restricted DACL. A malicious process that already holds a write handle to a
  pre-created object is still not something Windows DACL changes can revoke
  retroactively; this should be treated as a local spoof/DoS residual risk.
- CRG still reports high impact and test gaps around `BrokerSharedMemory`,
  `SensorCollector`, `SystemPowerReader`, and hardware IO paths. Current unit
  tests do not exercise real LHM/D3DKMT/Broker sensor hardware behavior.

Verification:
- `deploy.ps1`, `dev-deploy.ps1`, and `verify.ps1` parse successfully under
  host PowerShell 7.
- Elevated smoke used `gsudo v2.6.1` from
  `C:\Program Files\gsudo\2.6.1\gsudo.exe`; the PowerShell module wrapper was
  present but `gsudo.exe` was not on PATH.
- Elevated `verify.ps1` ran successfully with `pwsh -ExecutionPolicy Bypass`:
  no package, broker install, scheduled task, or broker ACL currently exists on
  the host.
- Elevated Broker smoke:
  - LibreHardwareMonitor initialized successfully with PawnIO installed.
  - Detected `AMD Radeon(TM) Graphics` and `NVIDIA GeForce RTX 4060 Laptop GPU`.
  - Broker wrote `Global\SysMonBrokerShm`; a non-elevated reader opened the map
    read-only and observed magic `0x5342524B`, version `2`, GPU count `2`, and
    sensor count above the old 128 limit after the `MaxSensors` fix.
- Broker smoke should not launch directly from `\\wsl.localhost\...`; Windows
  can treat UNC-launched executables as untrusted network files. Copy the exe to
  a local NTFS staging directory or deploy to `%ProgramFiles%`, then unblock and
  start that local copy.
- Host PowerShell 7 with VS 18 BuildTools MSBuild:
  - `SysMonCmdPal` Release/x64 build passed and produced
    `SysMonCmdPal_1.5.0.0_x64.msix`.
  - `SysMonBroker` Release/x64 `/t:Publish` passed.
  - `SysMonCmdPal.Tests` Debug/x64 build passed.
  - `SysMonCmdPal.Tests` passed: 88/88.
