#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Shared constants and helpers for the infra/ubuntu scripts.
#
# Sourced, never executed. Every path, account and unit name the kit uses is
# defined here exactly once, so the scripts and the systemd units cannot drift
# apart. The unit files under infra/ubuntu/systemd/ repeat these literals
# because systemd cannot source a shell file; change both together.
# ---------------------------------------------------------------------------

# shellcheck disable=SC2034  # consumed by the scripts that source this file

# Configuration. Root-only: systemd reads the *.env files as PID 1 before it
# drops privileges, so no service account ever needs to open them.
EPP_ETC_DIR=/etc/endpoint-platform
EPP_SECRETS_FILE="${EPP_ETC_DIR}/secrets.env"
EPP_ENV_MIGRATIONS="${EPP_ETC_DIR}/migrations.env"
EPP_ENV_ADMIN_API="${EPP_ETC_DIR}/admin-api.env"
EPP_ENV_AGENT_API="${EPP_ETC_DIR}/agent-api.env"

# Releases. "current" is a symlink that deploys repoint and rollbacks restore.
EPP_OPT_DIR=/opt/endpoint-platform
EPP_RELEASES_DIR="${EPP_OPT_DIR}/releases"
EPP_CURRENT_LINK="${EPP_OPT_DIR}/current"

# State. Uploaded package content is the only thing the applications write.
EPP_DATA_DIR=/var/lib/endpoint-platform
EPP_PACKAGES_DIR="${EPP_DATA_DIR}/packages"

# One account per process. Two processes under one UID can read each other's
# environment through /proc, which would hand the Agent API the escrow keys the
# Admin API holds; separate UIDs are what keep that boundary real.
EPP_GROUP=endpoint-platform
EPP_USER_ADMIN_API=epp-admin-api
EPP_USER_AGENT_API=epp-agent-api
EPP_USER_MIGRATIONS=epp-migrations

EPP_UNIT_MIGRATIONS=endpoint-platform-migrations.service
EPP_UNIT_ADMIN_API=endpoint-platform-admin-api.service
EPP_UNIT_AGENT_API=endpoint-platform-agent-api.service

# Loopback only. nginx is the single public entry point.
EPP_ADMIN_API_PORT=5080
EPP_AGENT_API_PORT=5081

EPP_DOTNET_ROOT=/opt/dotnet
EPP_DOTNET=/usr/local/bin/dotnet
EPP_POSTGRES_MAJOR=17

require_root() {
    if [ "$(id -u)" -ne 0 ]; then
        echo "$(basename "$0") must run as root: sudo bash $0 $*" >&2
        exit 1
    fi
}

require_not_root() {
    if [ "$(id -u)" -eq 0 ]; then
        echo "$(basename "$0") builds the source tree and must NOT run as root." >&2
        echo "Run it as your normal login; it calls sudo itself for the install step." >&2
        exit 1
    fi
}

# Exports every KEY=VALUE (or KEY="VALUE") line of a file, verbatim.
#
# Deliberately not "source": sourcing would run the file through the shell, so
# a "$" or a backtick inside a hand-edited password would be expanded instead
# of being used as written. This reads the same subset systemd's
# EnvironmentFile= accepts for the files this kit generates.
load_env_file() {
    local file="$1" line key value
    if [ ! -r "$file" ]; then
        echo "cannot read ${file}" >&2
        return 1
    fi
    while IFS= read -r line || [ -n "$line" ]; do
        line="${line%$'\r'}"
        case "$line" in
            '' | '#'*) continue ;;
        esac
        key="${line%%=*}"
        value="${line#*=}"
        [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
        if [ "${#value}" -ge 2 ] && [ "${value:0:1}" = '"' ] && [ "${value: -1}" = '"' ]; then
            value="${value:1:${#value}-2}"
        fi
        export "${key}=${value}"
    done < "$file"
}

# The port of the PostgreSQL cluster this kit manages. Normally 5432, but when
# a different major version was already installed on the host, Debian's
# packaging gives the new cluster the next free port instead.
detect_pg_port() {
    local port=""
    if command -v pg_lsclusters >/dev/null 2>&1; then
        port="$(pg_lsclusters -h 2>/dev/null \
            | awk -v major="$EPP_POSTGRES_MAJOR" '$1 == major && $2 == "main" { print $3; exit }')"
    fi
    printf '%s' "${port:-5432}"
}
