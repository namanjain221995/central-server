#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Installs a built release and makes it the running one. Called by deploy.sh.
#
#   sudo bash infra/ubuntu/install-release.sh <artifact-dir> <label>
#
#   1. copy the artifacts to /opt/endpoint-platform/releases/<label>, owned by
#      root and read-only to the service accounts: a compromised API process
#      cannot rewrite the binaries it will be restarted from
#   2. (re)install the systemd units from infra/ubuntu/systemd/
#   3. stop both APIs, repoint /opt/endpoint-platform/current at the new release
#   4. start both APIs. Each Requires= the migration job, so systemd runs it
#      first, once, and a failed migration stops the APIs from starting
#   5. wait up to 180 s for /health/ready on both APIs
#   6. on success prune old releases; on failure show the journals, repoint
#      "current" at the previous release, start that, and exit 1
#
# The APIs are stopped before "current" moves rather than restarted after: a
# running .NET process loads assemblies lazily from its application directory,
# and repointing the symlink under it would let an old process pick up a new
# assembly. The cost is a few seconds of 502 from /api/ and /agent/, which
# agents ride out on their own (they retry with backoff).
#
# Migrations are forward-only and additive by design. A rollback restores the
# previous BINARIES; it does not reverse the schema, and normally does not need
# to, because older code runs against a newer additive schema.
#
# It never touches PostgreSQL's data directory, the package store or
# /etc/endpoint-platform.
# ---------------------------------------------------------------------------
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=infra/ubuntu/common.sh
. "${script_dir}/common.sh"

require_root "$@"

src="${1:-}"
label="${2:-}"
if [ -z "$src" ] || [ -z "$label" ]; then
    echo "usage: install-release.sh <artifact-dir> <label>" >&2
    exit 1
fi
if ! [[ "$label" =~ ^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$ ]]; then
    echo "release label must match [A-Za-z0-9][A-Za-z0-9._-]*; got: ${label}" >&2
    exit 1
fi

HEALTH_TIMEOUT_SECONDS=180
KEEP_RELEASES=3

# --- preconditions -------------------------------------------------------------

for required in \
    "${src}/migrations/EndpointPlatform.Migrations.dll" \
    "${src}/admin-api/EndpointPlatform.Api.dll" \
    "${src}/agent-api/EndpointPlatform.AgentApi.dll" \
    "${src}/dashboard/index.html"; do
    [ -f "$required" ] || { echo "artifact is incomplete, missing: ${required}" >&2; exit 1; }
done
for env_file in "$EPP_ENV_MIGRATIONS" "$EPP_ENV_ADMIN_API" "$EPP_ENV_AGENT_API"; do
    [ -f "$env_file" ] || { echo "${env_file} is missing; run gen-env.sh first" >&2; exit 1; }
done
[ -d "$EPP_RELEASES_DIR" ] || { echo "${EPP_RELEASES_DIR} is missing; run host-prep.sh first" >&2; exit 1; }
[ -d "$EPP_PACKAGES_DIR" ] || { echo "${EPP_PACKAGES_DIR} is missing; run host-prep.sh first" >&2; exit 1; }

dest="${EPP_RELEASES_DIR}/${label}"
[ ! -e "$dest" ] || { echo "release ${label} already exists at ${dest}" >&2; exit 1; }

# --- 1. copy -------------------------------------------------------------------

echo "==> installing release ${label}"
incoming="${dest}.incoming"
rm -rf "$incoming"
cp -a "$src" "$incoming"
chown -R root:root "$incoming"
chmod -R u=rwX,go=rX "$incoming"
mv "$incoming" "$dest"

# --- 2. units --------------------------------------------------------------------

for unit in "$EPP_UNIT_MIGRATIONS" "$EPP_UNIT_ADMIN_API" "$EPP_UNIT_AGENT_API"; do
    [ -f "${script_dir}/systemd/${unit}" ] || { echo "missing ${script_dir}/systemd/${unit}" >&2; exit 1; }
    install -m 0644 -o root -g root "${script_dir}/systemd/${unit}" "/etc/systemd/system/${unit}"
done
systemctl daemon-reload
systemctl enable --quiet "$EPP_UNIT_ADMIN_API" "$EPP_UNIT_AGENT_API"

# --- helpers -----------------------------------------------------------------------

previous=""
if [ -L "$EPP_CURRENT_LINK" ]; then
    previous="$(readlink -f "$EPP_CURRENT_LINK" || true)"
fi

# Atomic: a new symlink is renamed over the old one, so "current" never dangles.
point_current_at() {
    ln -sfn "$1" "${EPP_CURRENT_LINK}.next"
    mv -T "${EPP_CURRENT_LINK}.next" "$EPP_CURRENT_LINK"
}

stop_stack() {
    systemctl stop "$EPP_UNIT_ADMIN_API" "$EPP_UNIT_AGENT_API" 2>/dev/null || true
}

# One systemctl call, one transaction: the migration job both units require
# runs exactly once for the pair.
start_stack() {
    systemctl start "$EPP_UNIT_ADMIN_API" "$EPP_UNIT_AGENT_API"
}

# X-Forwarded-Proto: the hosts redirect plain http to https outside Development
# whenever they know an https port. Saying "https" here, as nginx does, makes
# the probe independent of that.
probe() {
    curl -fsS -o /dev/null --max-time 5 -H 'X-Forwarded-Proto: https' "http://127.0.0.1:$1/health/ready"
}

wait_healthy() {
    local timeout="$1" deadline admin agent
    deadline=$((SECONDS + timeout))
    while :; do
        admin=down; agent=down
        if probe "$EPP_ADMIN_API_PORT"; then admin=ready; fi
        if probe "$EPP_AGENT_API_PORT"; then agent=ready; fi
        echo "    admin-api=${admin} agent-api=${agent}"
        if [ "$admin" = ready ] && [ "$agent" = ready ]; then
            return 0
        fi
        if [ "$SECONDS" -ge "$deadline" ]; then
            return 1
        fi
        sleep 5
    done
}

show_failure_context() {
    local unit
    for unit in "$EPP_UNIT_MIGRATIONS" "$EPP_UNIT_ADMIN_API" "$EPP_UNIT_AGENT_API"; do
        echo
        echo "==> last 60 journal lines of ${unit}"
        journalctl -u "$unit" -n 60 --no-pager 2>&1 || true
    done
    echo
    systemctl --no-pager --lines=0 status "$EPP_UNIT_MIGRATIONS" "$EPP_UNIT_ADMIN_API" "$EPP_UNIT_AGENT_API" 2>&1 || true
}

rollback() {
    echo
    stop_stack
    # Set aside so it is neither mistaken for a good release nor counted by the
    # pruning below; the next successful deploy removes it.
    mv "$dest" "${dest}.failed" 2>/dev/null || true

    if [ -z "$previous" ] || [ ! -d "$previous" ]; then
        echo "==> no previous release on this host, nothing to roll back to"
        rm -f "$EPP_CURRENT_LINK"
        return 0
    fi

    echo "==> rolling back to $(basename "$previous")"
    point_current_at "$previous"
    if start_stack && wait_healthy 120; then
        echo "==> ROLLED BACK. The previous release is serving; the new one was not deployed."
    else
        echo "==> ROLLED BACK, but the previous release is not healthy either. See the journals above." >&2
    fi
}

# --- 3-5. switch -----------------------------------------------------------------

echo "==> stopping the APIs"
stop_stack

echo "==> current -> ${label}"
point_current_at "$dest"

echo "==> starting the APIs (the migration job runs first)"
if ! start_stack; then
    echo "==> the stack failed to start; a failed migration job is the usual cause" >&2
    show_failure_context
    rollback
    exit 1
fi

echo "==> waiting up to ${HEALTH_TIMEOUT_SECONDS}s for /health/ready on both APIs"
if ! wait_healthy "$HEALTH_TIMEOUT_SECONDS"; then
    echo "==> the stack did not become healthy within ${HEALTH_TIMEOUT_SECONDS}s" >&2
    show_failure_context
    rollback
    exit 1
fi

# --- 6. success ------------------------------------------------------------------

# nginx serves the dashboard from "current" and needs no reload: the symlink is
# resolved per request. The check below goes through it, end to end. It only
# warns, because on a first install the certificate may not exist yet.
if [ -f "$EPP_SECRETS_FILE" ]; then
    public_origin="$(grep -E '^PUBLIC_ORIGIN=' "$EPP_SECRETS_FILE" | tail -n 1 | cut -d= -f2- || true)"
    public_host="${public_origin#https://}"
    public_host="${public_host%%:*}"
    if [ -n "$public_host" ]; then
        if curl -fsS -o /dev/null --max-time 10 --resolve "${public_host}:443:127.0.0.1" \
                "https://${public_host}/api/health/ready"; then
            echo "==> https://${public_host}/api/health/ready answers through nginx"
        else
            echo "==> WARNING: https://${public_host}/api/health/ready did not answer through nginx."
            echo "    If setup-nginx.sh has not run yet this is expected; otherwise: sudo nginx -t"
        fi
    fi
fi

echo "==> pruning old releases (keeping ${KEEP_RELEASES})"
find "$EPP_RELEASES_DIR" -mindepth 1 -maxdepth 1 -type d \( -name '*.failed' -o -name '*.incoming' \) \
    -exec rm -rf {} + 2>/dev/null || true
kept=0
while IFS= read -r release; do
    [ -n "$release" ] || continue
    kept=$((kept + 1))
    if [ "$kept" -le "$KEEP_RELEASES" ]; then
        continue
    fi
    if [ "$release" = "$dest" ] || [ "$release" = "$previous" ]; then
        continue
    fi
    echo "    removing $(basename "$release")"
    rm -rf "$release"
done < <(find "$EPP_RELEASES_DIR" -mindepth 1 -maxdepth 1 -type d -printf '%T@ %p\n' | sort -rn | cut -d' ' -f2-)

echo
echo "==> running release: ${label}"
systemctl --no-pager --lines=0 status "$EPP_UNIT_ADMIN_API" "$EPP_UNIT_AGENT_API" 2>&1 | grep -E '●|Active:' || true
echo
echo "install-release.sh: done"
exit 0
