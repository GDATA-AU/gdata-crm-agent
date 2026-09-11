# GDATA CRM Agent (.NET)

A lightweight extraction agent that runs as a Windows service, polls the GDATA Customer Portal for jobs, executes them locally (SQL queries or REST API calls), and writes gzip-compressed NDJSON results to Azure Blob Storage.

## Overview

```
crm-agent (extracts from on-prem DB or external API) → Azure Blob Storage → Portal (diffs, upserts to Postgres)
```

The agent is **intentionally dumb** — it has no knowledge of the citizen schema, deduplication logic, or business rules. It:

1. Polls the portal's REST API for a pending job
2. Executes the job (SQL query or REST API extraction) using streaming
3. Computes a SHA-256 `_rowHash` for each row (for change detection)
4. Writes gzip-compressed NDJSON to Azure Blob Storage
5. Reports completion (or failure) back to the portal

## Tech stack

- **.NET 10** Worker Service
- **Microsoft.Data.SqlClient** — SQL Server (Windows Integrated Security)
- **Azure.Storage.Blobs** — blob upload
- **Serilog** — structured JSON logging (one JSON object per line; the tray's log tailer parses it)
- **Microsoft.Extensions.Hosting.WindowsServices** — native Windows service support

## Project layout

| Project | Purpose |
|---|---|
| `CrmAgent/` | The worker service. `Program.cs` wires up DI; `AgentWorker.cs` is the poll loop; `Handlers/` holds one handler per job type; `Services/` holds blob, hashing, NDJSON, portal-client and redaction helpers. |
| `CrmAgent.Tray/` | WinForms system-tray companion (`net10.0-windows`). Writes agent credentials, shows status, tails logs, and drives updates. |
| `CrmAgent.Tests/` | xunit tests. Fakes live in `Fakes/` — tests never touch the network or a real database. |

## Prerequisites

- **Windows Server 2016+** (or Windows 10+ for development)
- Network access **outbound** to:
  - The portal URL (HTTPS, port 443)
  - Azure Blob Storage (HTTPS, port 443)
- Network access to local databases
- An Azure Blob Storage account with an `erp-imports` container

No inbound firewall rules are required. All communication is initiated by the agent.

Linux dev containers can build and test the whole solution — the tray project cross-compiles via `EnableWindowsTargeting` — but only the service runs on Linux, and the MSI builds on Windows only.

## Commands

| Task | Command |
|------|---------|
| Build | `dotnet build crm-agent.sln` |
| Run (dev) | `dotnet run --project dotnet/CrmAgent` |
| Test | `dotnet test dotnet/CrmAgent.Tests/CrmAgent.Tests.csproj` |
| Publish (self-contained) | `dotnet publish dotnet/CrmAgent -c Release -r win-x64 --self-contained -o publish` |
| Publish (framework-dependent) | `dotnet publish dotnet/CrmAgent -c Release -o publish` |

Self-contained output needs no .NET runtime on the target machine; framework-dependent output is smaller but requires one.

## Install as a Windows service

Publish first, edit `appsettings.json` in the publish folder, then run as Administrator:

```powershell
cd dotnet\CrmAgent
.\install-service.bat              # install and start
.\install-service.bat --uninstall  # stop and remove
```

The Windows service is registered as **`gdata-agent`** (the Linux systemd unit installed by `install-linux.sh` is named `crm-agent`):

```powershell
sc query gdata-agent       # Check status
sc stop gdata-agent        # Stop
sc start gdata-agent       # Start
```

## Configuration

Every setting can be supplied either in `appsettings.json` or as an environment variable. **The config file wins; environment variables are the fallback.**

Only the three credential settings treat an empty string as missing — that is what stops the blank placeholders shipped in `appsettings.json` from shadowing `PORTAL_URL`, `AGENT_API_KEY` and `AZURE_STORAGE_CONNECTION_STRING`. For every other setting an empty string is a value: it shadows the environment variable, fails to parse, and the default is used. **To configure an optional setting by environment variable, leave its key out of the file entirely rather than blanking it.**

On an installed agent the tray app writes credentials to `%ProgramData%\GDATA CRM Agent\appsettings.json`, which is layered over the copy in `Program Files` — so IT staff never need to edit files inside `Program Files`.

| Setting | Env var | Required | Default | Description |
|---|---|---|---|---|
| `Agent:PortalUrl` | `PORTAL_URL` | Yes | — | Base URL of the portal |
| `Agent:AgentApiKey` | `AGENT_API_KEY` | Yes | — | API key for authentication |
| `Agent:AzureStorageConnectionString` | `AZURE_STORAGE_CONNECTION_STRING` | Yes | — | Azure Blob Storage connection string |
| `Agent:PollIntervalMs` | `POLL_INTERVAL_MS` | | `5000` | Job poll interval |
| `Agent:HeartbeatIntervalMs` | `HEARTBEAT_INTERVAL_MS` | | `5000` | Heartbeat interval |
| `Agent:SqlTrustServerCertificate` | `SQL_TRUST_SERVER_CERTIFICATE` | | `true` | Accept **any** SQL Server certificate without validation. Defaults on because on-prem instances typically use self-signed certificates; set `false` where the server has a trusted certificate |
| `Agent:SqlCommandTimeoutSeconds` | `SQL_COMMAND_TIMEOUT_SECONDS` | | `300` | Per-command SQL timeout; `0` disables |
| `Agent:SqlConnectTimeoutSeconds` | `SQL_CONNECT_TIMEOUT_SECONDS` | | `15` | SQL connection-open timeout |
| `Agent:RestApiTimeoutSeconds` | `REST_API_TIMEOUT_SECONDS` | | `300` | Timeout per outbound REST API page request |
| `Agent:MaxRestApiPages` | `MAX_REST_API_PAGES` | | `100000` | Page cap guarding against a non-terminating cursor |
| `Agent:MaxJobDurationSeconds` | `MAX_JOB_DURATION_SECONDS` | | `1800` | Per-job wall-clock deadline; `0` disables |
| `Agent:WatchdogGraceSeconds` | `WATCHDOG_GRACE_SECONDS` | | `300` | Extra grace before the watchdog force-restarts a wedged process; `0` disables |

Disabling the timeouts or the watchdog is not recommended — they are what stop a stuck job from hanging the agent indefinitely.

### SQL connection strings

No local connection string configuration is needed for SQL Server. The portal sends `server` and `database` with each SQL job, and the agent builds the connection string locally using **Windows Integrated Security** (the service account). SQL User Id/Password credentials are never accepted.

The agent rejects any query whose first token is not `SELECT` or `WITH`. That is a guard rail against obvious mistakes, not a security boundary — a CTE of the form `WITH cte AS (...) DELETE ...` starts with `WITH` and would pass it.

**What actually keeps the agent read-only is the database grant.** Give the service account `db_datareader` on the target databases and nothing more; then a write cannot succeed regardless of what query the portal sends.
