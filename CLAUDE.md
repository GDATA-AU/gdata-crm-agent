# CLAUDE.md

Windows service (`CrmAgent`) + WinForms tray app (`CrmAgent.Tray`) that polls the GDATA
portal for jobs, runs SQL/REST extractions, and uploads gzip NDJSON to Azure Blob Storage.
See `dotnet/README.md` for architecture and config reference.

## Commands

```bash
dotnet build crm-agent.sln                                   # builds everything, incl. tray, on Linux
dotnet test dotnet/CrmAgent.Tests/CrmAgent.Tests.csproj      # xunit
```

Dev containers are Linux; the tray targets `net10.0-windows` and builds (via
`EnableWindowsTargeting`) but cannot run there. The MSI only builds on Windows CI.

## Non-obvious behaviour

- **Config precedence:** `%ProgramData%\GDATA CRM Agent\appsettings.json` (written by the
  tray) is layered over the project's `appsettings.json` and **wins over env vars**; env
  vars are the fallback. Empty string counts as missing for the three credential settings
  only (`NonEmpty` in `Program.cs`) — blanking any other key shadows its env var and
  silently falls back to the default, so omit the key instead.
- **Service names differ by platform:** Windows SCM service is `gdata-agent`; the Linux
  systemd unit is `crm-agent`.
- **Log format is an API.** Serilog writes one JSON object per line and the tray's
  `LogTailer` parses it. Don't change the formatter or sink layout.
- The agent is deliberately dumb — no citizen schema, dedup, or business rules. That logic
  lives in the portal; don't add it here.

## Invariants — do not weaken

- SQL is read-only: queries must start with `SELECT`/`WITH`, and connections always use
  Windows Integrated Security. Never accept SQL `User Id`/`Password` from a job.
- Redact URLs (`Redaction.RedactUrl`) and never log API keys, connection strings, or row data.
- Every long-running path needs a timeout/cancellation token — see the defaults in
  `AgentConfig`. The watchdog `FailFast`s a wedged process; keep that path intact.
- Delete partial blobs when a job fails (see `SqlHandler`), so the portal never diffs a
  truncated upload.
- Never change the WiX `UpgradeCode` in `installer/msi/Product.wxs` — it breaks in-place
  upgrades for installed agents.

## Workflow

- Branch off `main`, PR into `main`. CI runs tests on every PR.
- Tests use fakes in `dotnet/CrmAgent.Tests/Fakes/` — no real network or SQL in tests.
- MSI version comes from CI (tag `v1.2.3`, else `1.0.<run_number>`); don't hand-edit
  `<Version>` in the csproj files to release.
