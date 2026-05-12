#!/usr/bin/env bash
# install-linux.sh
#
# Installs crm-agent as a systemd user service on Linux.
#
# Usage:
#   chmod +x install-linux.sh
#   ./install-linux.sh
#
# To uninstall:
#   ./install-linux.sh --uninstall
#
# Prerequisites:
#   - .NET 10 runtime in PATH, or use the self-contained publish output
#   - dotnet publish dotnet/CrmAgent -c Release -r linux-x64 --self-contained -o publish
#   - Environment variables set (see README)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICE_NAME="crm-agent"
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
PUBLISH_DIR="${SCRIPT_DIR}/publish"

if [[ "${1:-}" == "--uninstall" ]]; then
  echo "Stopping and disabling ${SERVICE_NAME}..."
  systemctl stop "${SERVICE_NAME}" || true
  systemctl disable "${SERVICE_NAME}" || true
  rm -f "${SERVICE_FILE}"
  systemctl daemon-reload
  echo "${SERVICE_NAME} service removed."
  exit 0
fi

echo "Installing ${SERVICE_NAME} as a systemd service..."

# Detect the user who will own the service.
SERVICE_USER="${SUDO_USER:-$(whoami)}"
SERVICE_GROUP="$(id -gn "${SERVICE_USER}")"

cat > "${SERVICE_FILE}" <<EOF
[Unit]
Description=GDATA CRM Agent — polls the customer portal for extraction jobs and processes them locally
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=${SERVICE_USER}
Group=${SERVICE_GROUP}
WorkingDirectory=${PUBLISH_DIR}
ExecStart=${PUBLISH_DIR}/CrmAgent
EnvironmentFile=-${SCRIPT_DIR}/.env
Restart=on-failure
RestartSec=10
StandardOutput=journal
StandardError=journal
SyslogIdentifier=${SERVICE_NAME}

# Harden the service
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "${SERVICE_NAME}"
systemctl start "${SERVICE_NAME}"

echo ""
echo "${SERVICE_NAME} installed and started."
echo ""
echo "Useful commands:"
echo "  journalctl -u ${SERVICE_NAME} -f    # follow logs"
echo "  systemctl status ${SERVICE_NAME}     # check status"
echo "  systemctl restart ${SERVICE_NAME}    # restart"
echo "  systemctl stop ${SERVICE_NAME}       # stop"
