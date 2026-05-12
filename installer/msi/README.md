# MSI packaging (WiX v4)

This folder contains an initial MSI scaffold for the CRM agent.

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

- This is a practical scaffold intended to be iterated with validation on a Windows VM.
- This installer currently expects single-file publish outputs.
- If your code-signing pipeline is ready, add signing in CI for both MSI and binaries.
- If you want an "Launch tray app" checkbox on final dialog, add `WixToolset.UI.wixext` and an exit dialog action.