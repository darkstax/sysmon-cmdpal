# Technical Constraints

## Exploration

- Use `code-review-graph` first for architecture, impact, review, callers/callees, and tests.
- Fall back to `rg` or direct file reads only for exact text, generated assets, resources, or files not covered by the graph.
- Check `git status --short --branch` before edits; this repository often has active work in progress.

## Toolchain

- Target framework: `net10.0-windows10.0.26100.0`.
- Language/runtime: C# preview features on .NET 10.
- Main extension builds with Visual Studio/MSBuild, not Linux `dotnet build`, because MSIX/AppX packaging depends on Windows tooling.
- Required Windows stack: Windows 11, PowerToys installed, Developer Mode, Windows App SDK 1.6, VS Build Tools 2026, .NET SDK 10.0.300+.
- From WSL, convert repository paths with `wslpath -w` and invoke `pwsh.exe` or Windows MSBuild.

## Build And Test

```powershell
$msbuild = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild SysMonCmdPal.sln /p:Configuration=Debug /p:Platform=x64 /m /restore `
  /p:VcpkgEnabled=false /p:EnforceCodeStyleInBuild=false
dotnet test SysMonCmdPal.Tests\SysMonCmdPal.Tests.csproj
```

- Use `dotnet publish` for `SysMonBroker` only when producing the standalone broker.
- Keep Release trimming/AOT properties compatible with WinRT/COM and Windows App SDK.
- Put distributable artifacts under `release/sysmon-cmdpal/<target>/` and keep at most 3 historical builds.

## Architecture

- Main extension remains user-mode MSIX with `runFullTrust`.
- Optional `SysMonBroker` is elevated, independently distributed, and communicates one-way through Shared Memory v2 plus event notification.
- Preserve SHM v2 layout compatibility unless all producers/consumers and tests are updated together.
- Sensor freshness is time-bound; stale broker data must fall back automatically.
- Fallback chain must remain automatic: Broker SHM -> HWiNFO -> D3DKMT -> PDH -> ThermalZone where applicable.
- Do not add independent timers to pages or Dock Bands; use `DockBandRefreshCoordinator` and shared 1s refresh.
- `SystemInfoService.Refresh()` must remain concurrency guarded.
- Settings compatibility fields such as old `PrecisionMode` may be read/written back, but must not override the automatic sensor fallback chain.

## Resources And Localization

- Keep `Strings/en-US/Resources.resw` and `Strings/zh-CN/Resources.resw` synchronized for visible UI text.
- User-facing commands, diagnostics, and settings labels should use resource lookups rather than hard-coded strings.
- Do not leak raw exception details containing paths or environment data into user-facing UI.

## Privilege Boundaries

- Broker start/stop/deploy actions may require `gsudo`; avoid repeated UAC prompts when `gsudo` is available.
- COM, SHM, and process-control surfaces must keep authentication and ACL assumptions explicit.
- Process termination through broker paths must preserve authorization checks and Win32 error reporting.
