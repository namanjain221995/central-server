#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Creates the platform database and its two roles, on FIRST initialisation of
# the pgdata volume only (that is when the official entrypoint runs this
# directory).
#
# It feeds the repository's own infra/postgres/setup-database.sql to psql -
# the same file infra/ubuntu/setup-postgres.sh uses - so the container and the
# native host cannot drift apart. No password is ever a command-line argument:
# the values arrive as environment variables and psql reads them as variables.
#
# After a password change in .env, re-run the SQL by hand (the volume is
# already initialised, so this script will not fire again):
#
#   docker compose exec -T postgres bash -c \
#     'psql -v ON_ERROR_STOP=1 -U postgres -d postgres \
#        -v owner="$EPP_OWNER"  -v ownerpw="$EPP_OWNER_PASSWORD" \
#        -v app="$EPP_APP"      -v apppw="$EPP_APP_PASSWORD" \
#        -v db="$EPP_DB" -f /opt/endpoint-platform/setup-database.sql'
# ---------------------------------------------------------------------------
set -euo pipefail

for required in EPP_DB EPP_OWNER EPP_OWNER_PASSWORD EPP_APP EPP_APP_PASSWORD; do
    if [ -z "${!required:-}" ]; then
        echo "postgres init: ${required} is not set; refusing to create roles without it." >&2
        exit 1
    fi
done

echo "endpoint-platform: creating database and roles..."

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
     -v owner="$EPP_OWNER" \
     -v ownerpw="$EPP_OWNER_PASSWORD" \
     -v app="$EPP_APP" \
     -v apppw="$EPP_APP_PASSWORD" \
     -v db="$EPP_DB" \
     -f /opt/endpoint-platform/setup-database.sql
