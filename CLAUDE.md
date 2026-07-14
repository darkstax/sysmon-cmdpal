# Technical Constraints

- Canonical rules live in `AGENTS.md`; keep this file synchronized with it.
- Build the MSIX extension with Windows MSBuild, not Linux `dotnet build`.
- Invoke Windows-only tools from WSL through `pwsh.exe` with paths converted by `wslpath -w`.
- Preserve the sensor fallback chain: Broker SHM -> HWiNFO -> D3DKMT -> PDH -> ThermalZone.
- Keep SHM v2 layout, COM contracts, resource files, and tests synchronized when changing data models.
- Use the shared refresh coordinator; do not introduce per-page or per-band independent timers.
- Keep broker/elevated operations isolated from the user-mode MSIX extension.
