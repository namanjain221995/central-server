#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Points a locally installed PostgreSQL and Redis at what infra/.env says, for
# DEVELOPMENT ONLY.
#
#   sudo bash scripts/setup-local-services.sh
#
# Run it on the machine where those two services actually run. On Windows that
# normally means WSL, with the repository reached through /mnt/c:
#
#   wsl sudo bash /mnt/c/Projects/endpoint-platform/scripts/setup-local-services.sh
#
# It does two things, both idempotent:
#
#   1. Runs infra/postgres/setup-database.sql - the SAME file the deployed
#      Ubuntu host runs - to create the database and its two roles. Connects as
#      the "postgres" operating-system user over the local socket (peer
#      authentication), so no superuser password is needed or stored anywhere.
#   2. Sets Redis's `requirepass` to REDIS_PASSWORD and persists it with
#      CONFIG REWRITE, so it survives a restart.
#
# Re-running it re-applies the passwords from infra/.env, which is how a
# rotated local password reaches the servers.
#
# This is the development counterpart of infra/ubuntu/setup-postgres.sh and
# setup-redis.sh. It is deliberately NOT part of the deployment kit: a deployed
# host generates its own secrets into /etc/endpoint-platform/secrets.env and
# never reads infra/.env.
# ---------------------------------------------------------------------------
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "setup-local-services.sh must run as root: sudo bash $0" >&2
    exit 1
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_dir="$(cd "${script_dir}/.." && pwd)"
env_file="${repo_dir}/infra/.env"
sql_file="${repo_dir}/infra/postgres/setup-database.sql"

[ -f "$env_file" ] || { echo "${env_file} is missing. Copy infra/.env.example and fill it in." >&2; exit 1; }
[ -f "$sql_file" ] || { echo "missing ${sql_file}" >&2; exit 1; }

# Read KEY=VALUE without sourcing: a "$" or a backtick in a generated password
# must not be expanded by the shell. Strips the CR a file written on Windows has.
while IFS= read -r line || [ -n "$line" ]; do
    line="${line%$'\r'}"
    case "$line" in '' | '#'*) continue ;; esac
    key="${line%%=*}"
    value="${line#*=}"
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    export "${key}=${value}"
done < "$env_file"

for required in POSTGRES_DB POSTGRES_SUPERUSER POSTGRES_SUPERUSER_PASSWORD \
                POSTGRES_APP_USER POSTGRES_APP_PASSWORD REDIS_PASSWORD; do
    if [ -z "${!required:-}" ]; then
        echo "${env_file} does not set ${required}." >&2
        exit 1
    fi
done

# --- PostgreSQL ----------------------------------------------------------------

command -v psql >/dev/null 2>&1 || { echo "psql is not installed. sudo apt install postgresql" >&2; exit 1; }

pg_port="${POSTGRES_PORT:-5432}"
echo "==> creating database '${POSTGRES_DB}' and its two roles on port ${pg_port}"

# The values reach psql through its ENVIRONMENT and are read with \getenv, never
# as arguments: argv is visible to every account on the machine through ps.
export EPP_PG_OWNER="$POSTGRES_SUPERUSER"
export EPP_PG_OWNER_PASSWORD="$POSTGRES_SUPERUSER_PASSWORD"
export EPP_PG_APP="$POSTGRES_APP_USER"
export EPP_PG_APP_PASSWORD="$POSTGRES_APP_PASSWORD"
export EPP_PG_DB="$POSTGRES_DB"

# The SQL is piped rather than passed with -f: under /mnt/c the "postgres"
# account often cannot read the repository. root reads it here.
# cd /: psql would otherwise warn it cannot enter root's working directory.
{
    printf '%s\n' \
        '\getenv owner EPP_PG_OWNER' \
        '\getenv ownerpw EPP_PG_OWNER_PASSWORD' \
        '\getenv app EPP_PG_APP' \
        '\getenv apppw EPP_PG_APP_PASSWORD' \
        '\getenv db EPP_PG_DB'
    cat "$sql_file"
} | (cd / && runuser -u postgres -- psql -X -q -v ON_ERROR_STOP=1 -p "$pg_port" -d postgres)

# Prove the thing the APIs will actually do: a password login over TCP as the
# restricted role.
echo "==> verifying a TCP login as ${POSTGRES_APP_USER}"
PGPASSWORD="$POSTGRES_APP_PASSWORD" psql -X -q -w -h 127.0.0.1 -p "$pg_port" \
    -U "$POSTGRES_APP_USER" -d "$POSTGRES_DB" -c 'SELECT 1' >/dev/null

# --- Redis -----------------------------------------------------------------------

command -v redis-cli >/dev/null 2>&1 || { echo "redis-cli is not installed. sudo apt install redis" >&2; exit 1; }

redis_port="${REDIS_PORT:-6379}"
echo "==> setting the Redis password on port ${redis_port}"

# Whichever of these works depends on whether a password is already set. Both
# use REDISCLI_AUTH so nothing reaches the process list.
if redis-cli -p "$redis_port" ping >/dev/null 2>&1; then
    redis-cli -p "$redis_port" CONFIG SET requirepass "$REDIS_PASSWORD" >/dev/null
else
    REDISCLI_AUTH="$REDIS_PASSWORD" redis-cli -p "$redis_port" CONFIG SET requirepass "$REDIS_PASSWORD" >/dev/null
fi

# Persist it, so a restart does not silently drop the password.
if ! REDISCLI_AUTH="$REDIS_PASSWORD" redis-cli -p "$redis_port" CONFIG REWRITE >/dev/null 2>&1; then
    echo "    NOTE: CONFIG REWRITE failed (no config file to rewrite?). The password"
    echo "          is set for this session but will not survive a Redis restart."
fi

echo "==> verifying an authenticated PING"
pong="$(REDISCLI_AUTH="$REDIS_PASSWORD" redis-cli -p "$redis_port" ping 2>/dev/null || true)"
[ "$pong" = "PONG" ] || { echo "Redis did not answer an authenticated PING." >&2; exit 1; }

# And that the password is actually enforced.
if [ "$(redis-cli -p "$redis_port" ping 2>/dev/null || true)" = "PONG" ]; then
    echo "Redis answered PING without a password; requirepass did not take effect." >&2
    exit 1
fi

echo
echo "setup-local-services.sh: done. Now run .\\scripts\\run-local.ps1 on Windows."
