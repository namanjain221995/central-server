#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Restarts an application container that Docker reports as unhealthy.
#
#   sudo ./watchdog.sh                  # one check, now
#   sudo ./watchdog.sh --install-timer  # once: check every minute
#   journalctl -t epp-watchdog          # what it did, and why
#
# Why this exists. Docker runs the HEALTHCHECK, marks a container "unhealthy",
# and then does nothing: `restart: unless-stopped` only reacts to a process
# that EXITS. On 2026-10-14 the Agent API hung -- alive, 0% CPU, no log line,
# every request timing out -- and every device showed Offline until someone
# restarted it by hand. A crash restarts itself; a hang did not. Now it does,
# about four minutes after it starts (the image's healthcheck needs 10 failed
# probes 15 s apart before Docker calls it unhealthy).
#
# What it will NOT do:
#   * restart postgres or redis. They hold the data; an unhealthy database
#     needs a person, not a reflex. It is logged loudly instead.
#   * restart an API while postgres or redis is unhealthy. /health/ready
#     checks both, so the API is only reporting the real problem, and
#     restarting it would fix nothing.
#   * act while deploy.sh or restore.sh is running: they recreate containers
#     and wait for health themselves.
#   * take a memory dump of the hung process. The Admin API holds the escrow
#     and MFA keys in memory; a dump file would be a copy of them on disk.
#
# Every restart is logged with the container's last log lines first, so the
# reason for a hang is not lost with the process that had it.
#
# Exit codes: 0 nothing needed doing or every restart was issued, 1 otherwise.
# ---------------------------------------------------------------------------
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"

project=endpoint-platform
watched=(admin-api agent-api web)
data=(postgres redis)
tag=epp-watchdog

say() { echo "$*"; }

[ "$(id -u)" -eq 0 ] || { echo "watchdog.sh: run as root (sudo ./watchdog.sh): it drives the Docker daemon" >&2; exit 1; }

# --- install ---------------------------------------------------------------------

if [ "${1:-}" = "--install-timer" ]; then
    cat > /etc/systemd/system/endpoint-platform-watchdog.service <<UNIT
[Unit]
Description=Endpoint Platform - restart an unhealthy application container
Requires=docker.service
After=docker.service

[Service]
Type=oneshot
ExecStart=${here}/watchdog.sh
SyslogIdentifier=${tag}
UNIT
    cat > /etc/systemd/system/endpoint-platform-watchdog.timer <<UNIT
[Unit]
Description=Endpoint Platform - check container health every minute

[Timer]
# Not at boot exactly: let Docker bring the stack up and the health checks
# settle first, so the first pass does not judge containers still starting.
OnBootSec=5min
OnUnitActiveSec=1min
AccuracySec=10s

[Install]
WantedBy=timers.target
UNIT
    systemctl daemon-reload
    systemctl enable --now endpoint-platform-watchdog.timer
    systemctl list-timers endpoint-platform-watchdog.timer --no-pager
    exit 0
fi

# --- one check -------------------------------------------------------------------

if pgrep -f '(^|/| )(deploy|restore)\.sh( |$)' >/dev/null 2>&1; then
    exit 0
fi

health() {
    # "healthy", "unhealthy", "starting", or "missing" when the container is
    # gone. The name is fixed by the compose project name, so it is stable.
    docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}' \
        "${project}-$1-1" 2>/dev/null || echo missing
}

data_ok=1
for svc in "${data[@]}"; do
    state="$(health "$svc")"
    if [ "$state" != "healthy" ]; then
        say "${svc} is ${state}: not restarting anything that depends on it; this needs a person"
        data_ok=0
    fi
done

rc=0
for svc in "${watched[@]}"; do
    state="$(health "$svc")"
    [ "$state" = "unhealthy" ] || continue

    if [ "$data_ok" -eq 0 ] && [ "$svc" != "web" ]; then
        say "${svc} is unhealthy, but so is its database or cache; left alone"
        continue
    fi

    say "${svc} is unhealthy; its last log lines follow, then a restart"
    docker compose logs --no-color --tail 40 "$svc" 2>&1 | sed "s/^/  ${svc}| /" || true
    if docker compose restart "$svc" >/dev/null 2>&1; then
        say "${svc} restarted"
    else
        say "${svc} restart FAILED"
        rc=1
    fi
done

exit "$rc"
