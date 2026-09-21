#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Creates the platform database and its two roles in the local PostgreSQL.
#
#   sudo bash infra/ubuntu/setup-postgres.sh
#
# Reads the names and passwords from /etc/endpoint-platform/secrets.env and runs
# infra/postgres/setup-database.sql as the "postgres" operating-system user
# (peer authentication over the local socket: no superuser password exists or
# is needed).
#
# The passwords reach psql through its ENVIRONMENT and are read with \getenv,
# never as -v arguments: argv is visible to every account on the host through
# ps, the environment of another user's process is not.
#
# Idempotent. Re-running it re-applies the passwords from secrets.env, which is
# how a rotated database password is pushed into the server.
#
# PostgreSQL keeps its Debian defaults, which are already what the platform
# wants: listen_addresses = localhost (not reachable from the network) and
# scram-sha-256 for TCP logins from 127.0.0.1.
# ---------------------------------------------------------------------------
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_dir="$(cd "${script_dir}/../.." && pwd)"
# shellcheck source=infra/ubuntu/common.sh
. "${script_dir}/common.sh"

require_root "$@"

sql_file="${repo_dir}/infra/postgres/setup-database.sql"
[ -f "$sql_file" ] || { echo "missing ${sql_file}" >&2; exit 1; }
[ -f "$EPP_SECRETS_FILE" ] || { echo "${EPP_SECRETS_FILE} is missing; run gen-env.sh first" >&2; exit 1; }
command -v psql >/dev/null 2>&1 || { echo "psql is not installed; run host-prep.sh first" >&2; exit 1; }

load_env_file "$EPP_SECRETS_FILE"

# Only the five values psql needs are exported, and they are unset again below.
# The rest of secrets.env - RECOVERY_ESCROW_KEY above all - must not reach the
# environment of a process running as the unprivileged "postgres" account. That
# account otherwise holds only sealed ciphertext: it can read the escrowed
# BitLocker rows but not open them, and that separation is the whole point of
# keeping the escrow key in the Admin API alone.
export EPP_PG_OWNER="$POSTGRES_SUPERUSER"
export EPP_PG_OWNER_PASSWORD="$POSTGRES_SUPERUSER_PASSWORD"
export EPP_PG_APP="$POSTGRES_APP_USER"
export EPP_PG_APP_PASSWORD="$POSTGRES_APP_PASSWORD"
export EPP_PG_DB="$POSTGRES_DB"

unset RECOVERY_ESCROW_KEY RECOVERY_ESCROW_KEY_VERSION \
      RECOVERY_SEALING_PUBLIC_KEY RECOVERY_SEALING_PRIVATE_KEY \
      SECRET_PROTECTION_KEY REDIS_PASSWORD \
      AGENT_RELEASE_TRUST_MODE AGENT_RELEASE_SIGNER_SUBJECT PUBLIC_ORIGIN

pg_port="$(detect_pg_port)"
systemctl is-active --quiet postgresql || systemctl start postgresql

echo "==> creating roles and database on the local PostgreSQL ${EPP_POSTGRES_MAJOR} (port ${pg_port})"

# The SQL is piped rather than passed with -f: the repository normally sits in
# a home directory the "postgres" account cannot read. root reads it here.
# cd /: psql would otherwise warn that it cannot enter root's working directory.
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
# restricted role. A pg_hba.conf that an operator has tightened fails here, with
# PostgreSQL's own message, instead of as an API that never becomes ready.
echo "==> verifying a TCP login as ${POSTGRES_APP_USER}"
PGPASSWORD="$POSTGRES_APP_PASSWORD" psql -X -q -h 127.0.0.1 -p "$pg_port" \
    -U "$POSTGRES_APP_USER" -d "$POSTGRES_DB" -c 'SELECT 1' >/dev/null

echo
echo "setup-postgres.sh: done"
