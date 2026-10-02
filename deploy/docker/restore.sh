#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Brings this platform back on a NEW machine from one backup.sh bundle: same
# devices, same escrowed BitLocker keys, same administrators, same uploaded
# packages, same certificates. Enrolled agents keep working without a reinstall
# as long as the new machine answers on the same name (and, ideally, LAN IP).
#
# On a fresh Ubuntu machine:
#
#   sudo apt-get update && sudo apt-get install -y git
#   sudo git clone https://github.com/<owner>/<repo>.git /opt/endpoint-platform/src
#   cd /opt/endpoint-platform/src/deploy/docker
#   # copy the newest epp-backup-*.tar.gpg (and its .sha256) here
#   sudo ./restore.sh epp-backup-<stamp>.tar.gpg
#
# It asks for the backup passphrase (or reads it from --passphrase-file).
#
#   --force          restore over an existing .env / database on this machine.
#                    The existing database is REPLACED. Without it the script
#                    refuses, so it can never wipe a live server by accident.
#   --skip-packages  do not install Docker or other OS packages.
#
# Exit codes: 0 restored and verified, 1 anything else. Never prints a secret.
# ---------------------------------------------------------------------------
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"

die() { echo "restore.sh: $*" >&2; exit 1; }
step() { echo; echo "==> $*"; }

bundle=""
passphrase_file=""
force=0
install_packages=1
while [ $# -gt 0 ]; do
    case "$1" in
        --force) force=1 ;;
        --skip-packages) install_packages=0 ;;
        --passphrase-file) passphrase_file="${2:-}"; shift ;;
        -h|--help) sed -n '2,24p' "$0"; exit 0 ;;
        -*) die "unknown option $1" ;;
        *) bundle="$1" ;;
    esac
    shift
done

[ "$(id -u)" -eq 0 ] || die "run as root: sudo ./restore.sh <bundle>"
[ -n "$bundle" ] || die "usage: sudo ./restore.sh <epp-backup-...tar.gpg> [--force] [--passphrase-file <file>]"
[ -f "$bundle" ] || die "no such file: ${bundle}"
bundle="$(cd "$(dirname "$bundle")" && pwd)/$(basename "$bundle")"

# The account that runs deploy.sh and owns .env afterwards: whoever ran sudo.
owner="${SUDO_USER:-root}"

# --- 1. prerequisites -----------------------------------------------------------

step "prerequisites"
if [ "$install_packages" -eq 1 ]; then
    need=()
    command -v gpg >/dev/null || need+=(gnupg)
    command -v curl >/dev/null || need+=(curl)
    command -v git >/dev/null || need+=(git)
    command -v docker >/dev/null || need+=(docker.io)
    docker compose version >/dev/null 2>&1 || need+=(docker-compose-v2)
    docker buildx version >/dev/null 2>&1 || need+=(docker-buildx)
    if [ "${#need[@]}" -gt 0 ]; then
        echo "    installing: ${need[*]}"
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq
        apt-get install -y -qq "${need[@]}"
    fi
    systemctl enable --now docker >/dev/null 2>&1 || true
    if [ "$owner" != root ] && ! id -nG "$owner" | grep -qw docker; then
        usermod -aG docker "$owner"
        echo "    added ${owner} to the docker group (takes effect at next login)"
    fi
fi
command -v gpg >/dev/null || die "gpg is missing"
docker compose version >/dev/null 2>&1 || die "docker compose is missing"
echo "    $(docker --version), $(gpg --version | head -n1)"

# --- 2. refuse to clobber a live server ----------------------------------------

existing_db="$(docker volume ls -q --filter name='^endpoint-platform_pgdata$')"
if [ -f "${here}/.env" ] || [ -n "$existing_db" ]; then
    if [ "$force" -ne 1 ]; then
        die "this machine already has a deployment (.env or database volume). Re-run with --force to REPLACE it with the backup."
    fi
    echo "    --force: the existing deployment on this machine will be replaced"
fi

# --- 3. decrypt and check --------------------------------------------------------

step "decrypting the bundle"
work="$(mktemp -d /root/.epp-restore-XXXXXX)"
cleanup() { rm -rf "$work"; }
trap cleanup EXIT

if [ -f "${bundle}.sha256" ]; then
    (cd "$(dirname "$bundle")" && sha256sum -c --quiet "$(basename "$bundle").sha256") \
        || die "the bundle does not match its .sha256: it is damaged; use another one"
    echo "    checksum file matches"
fi

if [ -z "$passphrase_file" ]; then
    [ -t 0 ] || die "no terminal to ask for the passphrase; pass --passphrase-file"
    read -r -s -p "    backup passphrase: " pass; echo
    printf '%s' "$pass" > "${work}/.pass"
    unset pass
    passphrase_file="${work}/.pass"
fi
[ -s "$passphrase_file" ] || die "empty passphrase"

gpg --batch --quiet --pinentry-mode loopback --passphrase-file "$passphrase_file" -d "$bundle" \
    | tar -C "$work" -xf - \
    || die "could not decrypt the bundle: wrong passphrase, or the file is damaged"
rm -f "${work}/.pass"

for f in manifest.txt db.dump secrets.tar packages.tar; do
    [ -f "${work}/${f}" ] || die "the bundle has no ${f}; it is not a backup.sh bundle"
done
manifest() { grep -E "^$1=" "${work}/manifest.txt" | tail -n 1 | cut -d= -f2- || true; }
for f in db.dump secrets.tar packages.tar; do
    want="$(manifest "sha256_${f//./_}")"
    got="$(sha256sum "${work}/${f}" | cut -d' ' -f1)"
    [ "$want" = "$got" ] || die "${f} does not match the checksum recorded at backup time"
done
echo "    taken $(manifest created_utc) from commit $(manifest source_commit | cut -c1-12)"
backup_lan="$(manifest lan_origin)"
echo "    for $(manifest public_origin)${backup_lan:+ (LAN ${backup_lan})}"

# A database written by newer code may have tables this checkout does not know
# about. Restoring it under older code is how data quietly goes missing.
src_commit="$(manifest source_commit)"
if [ -n "$src_commit" ] && [ "$src_commit" != unknown ] && git -c safe.directory="*" -C "${here}/../.." rev-parse HEAD >/dev/null 2>&1; then
    if ! git -c safe.directory="*" -C "${here}/../.." merge-base --is-ancestor "$src_commit" HEAD 2>/dev/null; then
        if [ "$force" -ne 1 ]; then
            die "this checkout does not contain commit ${src_commit:0:12}, which the backup came from. Run 'git pull' (or check out that commit) first, or pass --force."
        fi
        echo "    WARNING: checkout does not contain the backup's commit; continuing because of --force"
    fi
fi

# --- 4. secrets and certificates ---------------------------------------------------

step "restoring .env, certificates and DNS credentials"
if [ -f "${here}/.env" ]; then
    mv "${here}/.env" "${here}/.env.replaced-$(date -u +%Y%m%dT%H%M%SZ)"
fi
rm -rf "${here}/letsencrypt" "${here}/tls"
tar --numeric-owner -C "$here" -xf "${work}/secrets.tar"
chown "$owner" "${here}/.env"
chmod 600 "${here}/.env"
if [ -f "${here}/cloudflare.ini" ]; then chmod 600 "${here}/cloudflare.ini"; fi
tar -tf "${work}/secrets.tar" | cut -d/ -f1 | sort -u | sed 's/^/    /'

env_value() { grep -E "^$1=" "${here}/.env" | tail -n 1 | cut -d= -f2- || true; }
db="$(env_value POSTGRES_DB)"; db="${db:-endpoint_platform}"
owner_role="$(env_value POSTGRES_SUPERUSER)"

compose() { docker compose "$@" </dev/null; }

# --- 5. database ---------------------------------------------------------------------

step "database"
compose down --remove-orphans >/dev/null 2>&1 || true
if [ -n "$existing_db" ]; then
    docker volume rm endpoint-platform_pgdata >/dev/null
    echo "    removed the old database volume"
fi
# A fresh volume makes the init script create the roles and an EMPTY database
# from the restored .env, so the passwords in .env and in the database agree.
compose up -d postgres
deadline=$(( $(date +%s) + 180 ))
until [ "$(docker inspect -f '{{.State.Health.Status}}' endpoint-platform-postgres-1 2>/dev/null)" = healthy ]; do
    [ "$(date +%s)" -lt "$deadline" ] || { compose logs --tail 40 postgres >&2; die "postgres did not become healthy"; }
    sleep 3
done
compose exec -T postgres psql -U postgres -d postgres -At \
    -c "select 1 from pg_database where datname = '${db}'" | grep -q 1 \
    || die "the init script did not create database ${db}; see: docker compose logs postgres"

docker compose exec -T postgres pg_restore -U postgres -d "$db" --exit-on-error \
    < "${work}/db.dump" \
    || die "pg_restore failed; the database is incomplete. Nothing else was started."
echo "    restored"

# --- 6. uploaded packages --------------------------------------------------------------

step "uploaded packages"
# Same labels compose would give it, or compose warns and treats it as foreign.
docker volume create \
    --label com.docker.compose.project=endpoint-platform \
    --label com.docker.compose.volume=packages \
    endpoint-platform_packages >/dev/null
docker run --rm -i -v endpoint-platform_packages:/dest postgres:17 \
    sh -c 'find /dest -mindepth 1 -delete && tar -C /dest -xf -' < "${work}/packages.tar"
echo "    $(tar -tf "${work}/packages.tar" | grep -vc '/$' || true) files"

# --- 7. build and start everything -------------------------------------------------------

step "building and starting the platform (deploy.sh)"
origin="$(env_value PUBLIC_ORIGIN)"
[ -n "$origin" ] || die ".env has no PUBLIC_ORIGIN"
bash ./deploy.sh "$origin"

# --- 8. prove it -------------------------------------------------------------------------

step "verifying against the backup's manifest"
mismatch=0
for table in devices platform_users bitlocker_recovery_escrows agent_releases; do
    want="$(manifest "count_${table}")"
    got="$(compose exec -T postgres psql -U postgres -d "$db" -At -c "select count(*) from endpoint_platform.${table}")"
    if [ "$want" = "$got" ]; then
        printf '    %-28s %s  ok\n' "$table" "$got"
    else
        printf '    %-28s %s  <-- backup had %s\n' "$table" "$got" "$want" >&2
        mismatch=1
    fi
done
[ "$mismatch" -eq 0 ] || die "row counts differ from the backup"

lan_origin="$(env_value LAN_ORIGIN)"
lan_ip="${lan_origin#https://}"; lan_ip="${lan_ip%%/*}"; lan_ip="${lan_ip%%:*}"
host="${origin#https://}"; host="${host%%/*}"; host="${host%%:*}"

echo
echo "restore.sh: the platform is back and its data matches the backup."
echo
echo "Before agents can reach it:"
if [ -n "$lan_ip" ] && ! ip -4 -o addr show | grep -qw "$lan_ip"; then
    echo "  * This machine does NOT have the old server's address ${lan_ip}."
    echo "    Easiest: give it ${lan_ip} (router DHCP reservation or netplan) and switch the old one off."
    echo "    Otherwise: set LAN_ORIGIN in .env to this machine's address and run"
    echo "    'sudo ./issue-certificate.sh' so ${host} points here in DNS."
else
    echo "  * This machine has the old server's address ${lan_ip}. Keep the old one switched off."
fi
echo "  * If 443 is port-forwarded on the router, it must point at this machine."
echo "  * Agents reconnect on their own within a few minutes. Watch Devices > Last seen."
