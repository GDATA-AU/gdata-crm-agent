# GDATA MyCity CRM Agent
GDATA's Agent for MyCity CRM.

A lightweight Windows service that securely extracts data from your council's on-premises databases and sends it to MyCity CRM.

## What it does

The GDATA CRM Agent runs as a background Windows service on your network. It periodically receives extraction jobs from the GDATA platform, executes read-only queries against your local SQL Server databases, and transmits the results securely to GDATA's cloud infrastructure. No manual intervention is required after initial setup.

## Prerequisites

- **Windows Server 2016+** (or Windows 10+)
- **Outbound HTTPS** (port 443) to GDATA cloud services — no inbound firewall rules are required
- Network access from the agent host to your local SQL Server databases
- The following details from GDATA (provided during onboarding):
  - Portal URL
  - Agent API key
  - Storage connection string

## Installation

1. Download the latest installer (`CrmAgentSetup.msi`) from [GitHub Releases](https://github.com/GDATA-AU/gdata-crm-agent/releases)
2. Run the installer — it will install the agent service and the system tray application
3. The tray application launches automatically and opens the setup wizard on first run
4. Enter the Portal URL, API key, and storage connection string provided by GDATA
5. Click **Test Connection** to verify connectivity, then **Save & Start Service**

The agent is now running. You can check its status at any time via the GDATA CRM Agent icon in the system tray.

## Database access

The agent connects to your SQL Server databases using **Windows Integrated Security** — no database passwords are stored or transmitted. It authenticates as the Windows service account and requires **read-only** access only.

Grant the service account `db_datareader` access on each target database. By default, the service runs as `LocalSystem`. If your organisation uses a dedicated service account, substitute accordingly.

```sql
USE [YourDatabase];
CREATE USER [DOMAIN\ServiceAccount] FOR LOGIN [DOMAIN\ServiceAccount];
EXEC sp_addrolemember 'db_datareader', 'DOMAIN\ServiceAccount';
```

The agent will never write to your databases.

## Updates

The agent checks for updates automatically. When a new version is available, the system tray application will notify you and offer to install it.

## Verifying the agent is working

- **System tray** — right-click the GDATA CRM Agent icon and select **Status** to see the current service state and recent activity
- **GDATA portal** — the portal shows the last heartbeat time and job status for your agent

## Troubleshooting

### Agent can't connect to GDATA services

- Verify that outbound HTTPS (port 443) is open on the host firewall and any network firewalls
- Confirm the agent host can resolve the portal's DNS name
- Check the status window in the tray application for specific error messages

### Agent can't connect to the database

- Verify the service account has `db_datareader` access to the target database
- Check that TCP/IP is enabled in SQL Server Configuration Manager
- Confirm the SQL Server hostname is reachable from the agent host
- Ensure the database server allows connections from the agent host's IP

For all other issues, contact GDATA support at **support@gdata.com.au**.

## Security

- All communication is **outbound-only** over HTTPS — no inbound firewall rules are required
- Database connections use **Windows Integrated Security** — no database passwords are stored or transmitted
- The agent performs **read-only** database access only
- No extracted data is stored locally — it is transmitted directly to GDATA's secure cloud infrastructure
- The agent runs as a Windows service under a controlled service account
