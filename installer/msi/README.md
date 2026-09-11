# MSI packaging (WiX v4)

WiX v4 sources for the CRM agent installer. The `Build Windows MSI` workflow builds this on
every push to `main` that touches `dotnet/` or `installer/`, and publishes a GitHub Release
when a `v*` tag is pushed. The steps below are for reproducing that build locally on Windows.

## Prerequisites

- Windows build host
- .NET SDK (same major used by this repo)
- WiX v4 tooling

Install WiX v4 build tools:

```powershell
dotnet tool install --global wix
```

## Build flow

1. Publish service and tray binaries:

```powershell
dotnet publish dotnet/CrmAgent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
dotnet publish dotnet/CrmAgent.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-tray
```

2. Build MSI:

```powershell
dotnet build installer/msi/CrmAgent.Installer.wixproj -c Release
```

Expected output:

- `installer/msi/bin/Release/gdata-crm-agent-installer.msi`

## Current parity mapping

- Installs service binaries to `Program Files\GDATA CRM Agent`
- Installs tray binaries to `Program Files\GDATA CRM Agent\tray`
- Registers Windows service `gdata-agent` (auto start, removed on uninstall)
- Applies service recovery settings via `sc.exe failure ...`
- Creates HKLM Run entry for tray app (`GDATACrmAgent`)
- Launches tray app automatically after fresh interactive install
- Grants Modify access to `ProgramData\GDATA CRM Agent` for built-in Users group
- Kills tray process on uninstall
- Removes ProgramData folder on uninstall

## Notes

- The installer expects single-file publish outputs — use the `dotnet publish` flags above.
- Never change the `UpgradeCode` in `Product.wxs`; it is what makes in-place upgrades work
  for already-installed agents.
- Not yet done: code-signing for the MSI and binaries, and a "Launch tray app" checkbox on
  the exit dialog (needs `WixToolset.UI.wixext`).
