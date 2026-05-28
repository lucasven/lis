#!/usr/bin/env bash
# Installs the lis.service systemd unit and starts it.
# Run with: sudo bash /home/agenticcompany/lis/install-service.sh
set -euo pipefail

install -m 0644 /home/agenticcompany/lis/lis.service /etc/systemd/system/lis.service
systemctl daemon-reload
systemctl enable lis.service
systemctl restart lis.service
sleep 2
systemctl --no-pager --full status lis.service || true
echo
echo "Logs: journalctl -u lis -f"
echo "Health: curl -i http://localhost:3010/health"
