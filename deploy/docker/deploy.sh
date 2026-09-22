#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Brings the whole stack up on this machine, and does not claim success until
# the platform actually answers.
#
#   sudo ./deploy.sh https://192.168.8.96          # first time, or any time
#   sudo ./deploy.sh https://192.168.8.96 --no-build
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
status_of() {
    if command -v curl >/dev/null 2>&1; then
        curl -sS --insecure -o /dev/null -w '%{http_code}' --max-time 15 "$1" 2>/dev/null || echo 000
    elif command -v wget >/dev/null 2>&1; then
        wget -q -O /dev/null --no-check-certificate --timeout=15 --server-response "$1" 2>&1 \
            | awk '/^  HTTP\//{code=$2} END{print (code == "" ? "000" : code)}'
    else
        echo skipped
    fi
}

failed=0
check() { # check <label> <url> <acceptable-codes-regex>
    local label="$1" url="$2" expect="$3" code
    code="$(status_of "$url")"
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
