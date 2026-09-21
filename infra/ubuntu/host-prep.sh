#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Prepares an Ubuntu 22.04 / 24.04 host to run the endpoint platform natively.
#
#   sudo bash infra/ubuntu/host-prep.sh
#
# Nothing here runs in a container. The platform is three .NET processes under
# systemd, with PostgreSQL, Redis and nginx installed as ordinary packages:
#
#   PostgreSQL 17   apt.postgresql.org. Ubuntu's own archive carries 14 (22.04)
#                   or 16 (24.04); the platform is developed against 17.
#   Redis 8         packages.redis.io. NOT Ubuntu's package: the platform redeems
#                   one-time secrets with GETDEL, which needs Redis 6.2 or later,
#                   and 22.04 ships 6.0.
#   .NET 10 SDK     dotnet-install.sh into /opt/dotnet. global.json pins the
#                   10.0.4xx feature band, which distribution feeds do not
#                   reliably carry. The SDK is needed because releases are
#                   built on this host; it also provides the ASP.NET runtime.
#   Node.js 24      official tarball, SHA-256 checked, into /opt/nodejs. Build
#                   time only (the dashboard); nothing runs on Node.
#   nginx, certbot  the public HTTPS entry point and its certificate.
#
# Also: the three service accounts, the directory layout, a 2 GB swapfile on
# small VMs (dotnet publish was seen to OOM in 1 GB), vm.overcommit_memory=1
# for Redis, and ufw rules ONLY if ufw is already active.
#
# Runs as root and is safe to run again: every step checks its own end state.
# Re-running is also how the host picks up a newer .NET 10 patch release.
#
# It never enables ufw. Turning a firewall on from a script that may be running
# over the very SSH session it would cut is how operators lock themselves out.
# ---------------------------------------------------------------------------
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=infra/ubuntu/common.sh
. "${script_dir}/common.sh"

require_root "$@"

export DEBIAN_FRONTEND=noninteractive

# shellcheck disable=SC1091
. /etc/os-release
if [ "${ID:-}" != "ubuntu" ]; then
    echo "This script targets Ubuntu; /etc/os-release says ID=${ID:-unknown}." >&2
    exit 1
fi
codename="${VERSION_CODENAME:-}"
if [ -z "$codename" ]; then
    echo "VERSION_CODENAME missing from /etc/os-release; cannot pick the apt repositories." >&2
    exit 1
fi
deb_arch="$(dpkg --print-architecture)"
echo "==> Ubuntu ${VERSION_ID:-?} (${codename}), ${deb_arch}"

# Writes a sources entry only when it differs, and records that it changed.
sources_changed=0
ensure_sources_entry() {
    local file="$1" entry="$2"
    if [ ! -f "$file" ] || [ "$(cat "$file")" != "$entry" ]; then
        printf '%s\n' "$entry" > "$file"
        sources_changed=1
    fi
}

# --- base packages ----------------------------------------------------------

echo "==> apt-get update"
apt-get update -q
echo "==> base packages"
apt-get install -y -q ca-certificates curl gnupg openssl xz-utils

install -m 0755 -d /etc/apt/keyrings

# --- PostgreSQL 17 from apt.postgresql.org ------------------------------------

pg_keyring=/etc/apt/keyrings/postgresql.asc
if [ ! -s "$pg_keyring" ]; then
    echo "==> PostgreSQL apt keyring"
    curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc -o "$pg_keyring"
    chmod a+r "$pg_keyring"
fi
ensure_sources_entry /etc/apt/sources.list.d/pgdg.list \
    "deb [arch=${deb_arch} signed-by=${pg_keyring}] https://apt.postgresql.org/pub/repos/apt ${codename}-pgdg main"

# --- Redis from packages.redis.io ----------------------------------------------

redis_keyring=/etc/apt/keyrings/redis.gpg
if [ ! -s "$redis_keyring" ]; then
    echo "==> Redis apt keyring"
    curl -fsSL https://packages.redis.io/gpg | gpg --dearmor --yes -o "$redis_keyring"
    chmod a+r "$redis_keyring"
fi
ensure_sources_entry /etc/apt/sources.list.d/redis.list \
    "deb [arch=${deb_arch} signed-by=${redis_keyring}] https://packages.redis.io/deb ${codename} main"

if [ "$sources_changed" -eq 1 ]; then
    echo "==> apt-get update (new repositories)"
    apt-get update -q
fi

echo "==> PostgreSQL ${EPP_POSTGRES_MAJOR}, Redis, nginx, certbot"
apt-get install -y -q "postgresql-${EPP_POSTGRES_MAJOR}" redis nginx certbot python3-certbot-nginx
systemctl enable --now postgresql
systemctl enable --now redis-server
systemctl enable --now nginx

pg_port="$(detect_pg_port)"
if [ "$pg_port" != "5432" ]; then
    echo "==> NOTE: PostgreSQL ${EPP_POSTGRES_MAJOR} listens on port ${pg_port}, not 5432 (another cluster"
    echo "    already held 5432). gen-env.sh picks the port up automatically."
fi

# --- .NET 10 SDK -------------------------------------------------------------
#
# dotnet-install.sh is idempotent: when the newest 10.0 SDK is already in
# /opt/dotnet it says so and changes nothing, otherwise it adds the newer one
# side by side. The native dependencies below are what Microsoft documents for
# Ubuntu; only the libicu package name varies between releases.

icu_pkg="$(apt-cache pkgnames libicu | grep -E '^libicu[0-9]+$' | sort -V | tail -n 1 || true)"
if [ -z "$icu_pkg" ]; then
    echo "could not find a libicuNN package for this Ubuntu release" >&2
    exit 1
fi
echo "==> .NET native dependencies (${icu_pkg})"
apt-get install -y -q libc6 libgcc-s1 libstdc++6 libssl3 zlib1g "$icu_pkg"

echo "==> .NET 10 SDK into ${EPP_DOTNET_ROOT}"
dotnet_installer="$(mktemp)"
curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$dotnet_installer"
bash "$dotnet_installer" --channel 10.0 --install-dir "$EPP_DOTNET_ROOT" --no-path
rm -f "$dotnet_installer"
ln -sfn "${EPP_DOTNET_ROOT}/dotnet" "$EPP_DOTNET"

# global.json pins 10.0.400 with rollForward=latestFeature: any 10.0 SDK in the
# 4xx band or later satisfies it, and nothing earlier does.
if ! "$EPP_DOTNET" --list-sdks | grep -Eq '^10\.0\.[4-9][0-9]{2}'; then
    echo "no .NET SDK >= 10.0.400 is installed; global.json would reject the build:" >&2
    "$EPP_DOTNET" --list-sdks >&2 || true
    exit 1
fi

# --- Node.js 24 (dashboard build only) -------------------------------------------

case "$deb_arch" in
    amd64) node_arch=x64 ;;
    arm64) node_arch=arm64 ;;
    *) echo "no official Node.js build for architecture ${deb_arch}" >&2; exit 1 ;;
esac

node_major=""
if [ -x /usr/local/bin/node ]; then
    node_major="$(/usr/local/bin/node --version 2>/dev/null | sed -E 's/^v([0-9]+)\..*/\1/' || true)"
fi
if [ "$node_major" = "24" ]; then
    echo "==> Node.js $(/usr/local/bin/node --version) already installed"
else
    echo "==> Node.js 24 into /opt/nodejs"
    node_base=https://nodejs.org/dist/latest-v24.x
    node_sums="$(curl -fsSL "${node_base}/SHASUMS256.txt")"
    node_line="$(printf '%s\n' "$node_sums" \
        | grep -E "  node-v24\.[0-9]+\.[0-9]+-linux-${node_arch}\.tar\.xz\$" | head -n 1 || true)"
    if [ -z "$node_line" ]; then
        echo "could not find a linux-${node_arch} tarball in ${node_base}/SHASUMS256.txt" >&2
        exit 1
    fi
    node_sha="${node_line%% *}"
    node_file="${node_line##* }"
    node_tmp="$(mktemp -d)"
    curl -fsSL "${node_base}/${node_file}" -o "${node_tmp}/${node_file}"
    printf '%s  %s\n' "$node_sha" "${node_tmp}/${node_file}" | sha256sum -c - >/dev/null
    rm -rf /opt/nodejs
    mkdir -p /opt/nodejs
    tar -xJf "${node_tmp}/${node_file}" -C /opt/nodejs --strip-components=1
    rm -rf "$node_tmp"
    for tool in node npm npx; do
        ln -sfn "/opt/nodejs/bin/${tool}" "/usr/local/bin/${tool}"
    done
fi

# --- service accounts and directories ---------------------------------------------

echo "==> service accounts and directories"
getent group "$EPP_GROUP" >/dev/null || groupadd --system "$EPP_GROUP"

# Both APIs share the group so the Agent API can READ package content the Admin
# API wrote. The migration job touches no files and gets a group of its own.
for account in "$EPP_USER_ADMIN_API" "$EPP_USER_AGENT_API"; do
    id -u "$account" >/dev/null 2>&1 || useradd --system --gid "$EPP_GROUP" \
        --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin "$account"
done
id -u "$EPP_USER_MIGRATIONS" >/dev/null 2>&1 || useradd --system --user-group \
    --home-dir /nonexistent --no-create-home --shell /usr/sbin/nologin "$EPP_USER_MIGRATIONS"

install -d -m 0750 -o root -g root "$EPP_ETC_DIR"
install -d -m 0755 -o root -g root "$EPP_OPT_DIR" "$EPP_RELEASES_DIR"
install -d -m 0750 -o root -g "$EPP_GROUP" "$EPP_DATA_DIR"
# setgid so every uploaded file inherits the shared group; with the Admin API's
# UMask=0027 that makes content group-readable (the Agent API) and nothing more.
install -d -m 2750 -o "$EPP_USER_ADMIN_API" -g "$EPP_GROUP" "$EPP_PACKAGES_DIR"

# --- swap on small VMs -------------------------------------------------------
#
# Only when RAM is under 3 GB and no swap is active at all: a host that already
# has swap configured by its operator is left alone.

mem_kb="$(awk '/^MemTotal:/ {print $2}' /proc/meminfo)"
swap_kb="$(awk '/^SwapTotal:/ {print $2}' /proc/meminfo)"
if [ "$mem_kb" -lt 3145728 ] && [ "$swap_kb" -eq 0 ]; then
    echo "==> RAM is $((mem_kb / 1024)) MB and no swap is active: creating a 2 GB /swapfile"
    if [ ! -f /swapfile ]; then
        fallocate -l 2G /swapfile
        chmod 600 /swapfile
        mkswap /swapfile
    fi
    # A file left behind by an interrupted earlier run may lack the swap header.
    if ! swapon /swapfile 2>/dev/null; then
        chmod 600 /swapfile
        mkswap /swapfile
        swapon /swapfile
    fi
    if ! grep -qE '^/swapfile\s' /etc/fstab; then
        printf '%s\n' '/swapfile none swap sw 0 0' >> /etc/fstab
    fi
else
    echo "==> swap: RAM $((mem_kb / 1024)) MB, swap $((swap_kb / 1024)) MB; nothing to do"
fi

# --- kernel setting Redis asks for ------------------------------------------

sysctl_file=/etc/sysctl.d/60-endpoint-platform-redis.conf
if [ ! -f "$sysctl_file" ] || ! grep -q 'vm.overcommit_memory = 1' "$sysctl_file"; then
    echo "==> vm.overcommit_memory=1 (persistent)"
    printf '%s\n' 'vm.overcommit_memory = 1' > "$sysctl_file"
fi
sysctl -q -w vm.overcommit_memory=1

# --- firewall: open ports only if a firewall is already on ------------------

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active'; then
    echo "==> ufw is active: allowing OpenSSH and 'Nginx Full' (80/443)"
    ufw allow OpenSSH >/dev/null
    ufw allow 'Nginx Full' >/dev/null
else
    echo "==> ufw is not active; leaving it that way (never enabled from here, lockout risk)"
fi

# --- report ------------------------------------------------------------------

echo
echo "==> installed versions"
psql --version
redis-server --version
"$EPP_DOTNET" --version
/usr/local/bin/node --version
nginx -v 2>&1
certbot --version 2>&1
echo
echo "host-prep.sh: done"
