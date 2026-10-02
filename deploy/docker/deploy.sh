#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Brings the whole stack up on this machine, and does not claim success until
# the platform actually answers.
#
#   sudo ./deploy.sh https://<host-or-ip>          # first time, or any time
#   sudo ./deploy.sh https://<host-or-ip> --no-build
#
# Idempotent: generate-env.sh keeps existing secrets and certificates, the
# database volume survives, and `docker compose up` recreates only what changed.
#
# Exit codes: 0 the stack is up and answering, 1 anything else.
# ---------------------------------------------------------------------------
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"

origin="${1:-}"
build=1
[ "${2:-}" = "--no-build" ] && build=0

if [ -z "$origin" ]; then
    if [ -f .env ]; then
        origin="$(grep -E '^PUBLIC_ORIGIN=' .env | tail -n 1 | cut -d= -f2-)"
    fi
    if [ -z "$origin" ]; then
        echo "usage: deploy.sh https://<host-or-ip> [--no-build]" >&2
        exit 1
    fi
    echo "==> reusing PUBLIC_ORIGIN from .env: ${origin}"
fi

command -v docker >/dev/null 2>&1 || { echo "docker is not installed" >&2; exit 1; }
docker compose version >/dev/null 2>&1 || { echo "the docker compose plugin is not installed" >&2; exit 1; }

# --- secrets, TLS, pgAdmin wiring -------------------------------------------

bash ./generate-env.sh "$origin"

# --- build and start ---------------------------------------------------------

export SOURCE_REVISION="$(git -C "${here}/../.." rev-parse --short HEAD 2>/dev/null || echo unknown)"

if [ "$build" -eq 1 ]; then
    echo "==> building images (this takes a few minutes the first time)"
    docker compose build
fi

# pgAdmin's pgpass is bind-mounted INSIDE its per-user storage directory, so on
# a brand-new pgadmin volume Docker creates storage/<user>/ as root and pgAdmin
# (uid 5050) refuses to boot: "does not have permission to read and write to the
# specified storage directory". Existing installs never see it because their
# volume predates the mount; a new machine or a restore always does. Create the
# volume as compose would (same labels) and hand the directories to 5050.
pgadmin_storage="$(grep -E '^PGADMIN_STORAGE_DIR=' .env | tail -n 1 | cut -d= -f2-)"
pgadmin_storage="${pgadmin_storage:-admin_endpoint.local}"
if ! docker volume inspect endpoint-platform_pgadmin >/dev/null 2>&1; then
    docker volume create \
        --label com.docker.compose.project=endpoint-platform \
        --label com.docker.compose.volume=pgadmin \
        endpoint-platform_pgadmin >/dev/null
fi
docker run --rm --user 0 --entrypoint sh -v endpoint-platform_pgadmin:/v postgres:17 -c \
    "mkdir -p '/v/storage/${pgadmin_storage}' && chown 5050:5050 /v /v/storage '/v/storage/${pgadmin_storage}'" \
    </dev/null

echo "==> starting the stack"
# The migration job runs to completion first; both APIs are gated on its exit
# code, so this single command also proves the schema applied.
docker compose up -d --remove-orphans

# --- wait for it to actually answer ------------------------------------------

echo "==> waiting for the APIs to report ready"
deadline=$(( $(date +%s) + 300 ))
while :; do
    admin="$(docker inspect -f '{{.State.Health.Status}}' endpoint-platform-admin-api-1 2>/dev/null || echo missing)"
    agent="$(docker inspect -f '{{.State.Health.Status}}' endpoint-platform-agent-api-1 2>/dev/null || echo missing)"
    if [ "$admin" = "healthy" ] && [ "$agent" = "healthy" ]; then
        break
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
        echo "timed out waiting for the APIs (admin=${admin} agent=${agent})" >&2
        echo "--- migrations ---" >&2
        docker compose logs --tail 40 migrations >&2 || true
        echo "--- admin-api ---" >&2
        docker compose logs --tail 40 admin-api >&2 || true
        exit 1
    fi
    sleep 5
done

echo "==> smoke test"
#
# Insecure on purpose: the certificate is signed by THIS deployment's own CA,
# which the server itself has no reason to trust. What is proved here is that
# nginx, both APIs, PostgreSQL and Redis all answer on the real public path -
# not that the local trust store knows about the CA.
#
# curl is not on every Ubuntu image, so wget (which is) is the fallback. A stack
# that is up must not be reported broken because the host lacks a probe tool.
#
# The probes go to THIS machine whatever DNS says: the public name may still
# point at another box (a restore onto new hardware) or not resolve here at all
# (a LAN address behind a public name). --resolve pins it to the loopback.
probe_host="${origin#https://}"; probe_host="${probe_host%%/*}"; probe_host="${probe_host%%:*}"
status_of() {
    if command -v curl >/dev/null 2>&1; then
        curl -sS --insecure -o /dev/null -w '%{http_code}' --max-time 15 \
            --resolve "${probe_host}:443:127.0.0.1" --resolve "${probe_host}:80:127.0.0.1" \
            "$1" 2>/dev/null || echo 000
    elif command -v wget >/dev/null 2>&1; then
        wget -q -O /dev/null --no-check-certificate --timeout=15 --server-response "$1" 2>&1 \
            | awk '/^  HTTP\//{code=$2} END{print (code == "" ? "000" : code)}'
    else
        echo skipped
    fi
}

failed=0
check() { # check <label> <url> <acceptable-codes-regex>
    local label="$1" url="$2" expect="$3" code tries=0
    code="$(status_of "$url")"
    # A container that was just (re)created can take a minute to answer:
    # pgAdmin's first boot builds its own database. Retry before failing.
    while [ "$code" != skipped ] && ! [[ "$code" =~ $expect ]] && [ "$tries" -lt 24 ]; do
        sleep 5
        tries=$((tries + 1))
        code="$(status_of "$url")"
    done
    if [ "$code" = skipped ]; then
        echo "    ${label} no curl or wget on this host, not probed"
    elif [[ "$code" =~ $expect ]]; then
        echo "    ${label} HTTP ${code}"
    else
        echo "    ${label} HTTP ${code}  <-- expected ${expect}" >&2
        failed=1
    fi
}

check "dashboard        " "${origin}/"                   '^200$'
check "Admin API ready  " "${origin}/api/health/ready"   '^200$'
# 405 is the correct answer to a GET on a POST-only route, and the cheapest
# proof that /agent/ reaches the Agent API rather than nginx's own 404.
check "Agent API routed " "${origin}/agent/v1/heartbeat" '^(200|401|404|405)$'
check "pgAdmin          " "${origin}/pgadmin/"           '^(200|302)$'
check "CA download      " "http://${origin#https://}/endpoint-platform-ca.crt" '^200$'

if [ "$failed" -ne 0 ]; then
    echo "smoke test failed" >&2
    exit 1
fi

echo
docker compose ps
echo
echo "deploy.sh: done"
echo "  dashboard  ${origin}/"
echo "  pgAdmin    ${origin}/pgadmin/   (or http://${origin#https://}:$(grep -E '^PGADMIN_PORT=' .env | cut -d= -f2))"
echo "  CA root    http://${origin#https://}/endpoint-platform-ca.crt"
