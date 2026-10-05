#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Takes a complete, encrypted backup of this deployment: everything restore.sh
# needs to bring the platform back on a different machine, with every device,
# every escrowed BitLocker key and every administrator intact.
#
#   sudo ./backup.sh --init          # once: create the backup passphrase
#   sudo ./backup.sh                 # take a backup now
#   sudo ./backup.sh --install-timer # once: take one every night at 02:30
#
# One file per run, /var/backups/endpoint-platform/bundles/epp-backup-<UTC>.tar.gpg,
# with a .sha256 beside it. Inside, encrypted together:
#
#   db.dump        pg_dump -Fc of the platform database
#   secrets.tar    .env, tls/, letsencrypt/, cloudflare.ini (when present)
#   packages.tar   the uploaded agent releases and software packages
#   manifest.txt   when, from which commit, row counts, and a checksum per file
#
# Why one file, and why encrypted. The database alone is useless: every
# escrowed recovery password is sealed with keys that live only in .env. The
# two together open every BitLocker volume in the fleet, so they never travel
# unencrypted. The passphrase lives in /etc/endpoint-platform/backup.passphrase
# (root, 0600) and must ALSO be kept somewhere that does not die with this
# machine - a password manager. A backup nobody can decrypt is not a backup.
#
# The file is decrypted and listed again before the run reports success. On a
# machine whose memory is suspected of flipping bits, "the file was written" is
# not the same as "the file can be restored".
#
# Never prints a secret. Exit codes: 0 a verified backup exists, 1 anything else.
# ---------------------------------------------------------------------------
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"

root_dir=/var/backups/endpoint-platform
bundle_dir="${root_dir}/bundles"
passphrase_file=/etc/endpoint-platform/backup.passphrase
keep=14

die() { echo "backup.sh: $*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run as root (sudo ./backup.sh): the database volume and letsencrypt/ are root-owned"

# The deploy account owns .env; the finished bundle is handed to it so it can be
# copied off the machine over SSH without root. It is encrypted, so that is safe.
owner="$(stat -c %U "${here}/.env" 2>/dev/null || echo root)"

# --- one-time setup ------------------------------------------------------------

if [ "${1:-}" = "--init" ]; then
    install -d -m 700 -o root -g root "$(dirname "$passphrase_file")"
    if [ -s "$passphrase_file" ]; then
        echo "a backup passphrase already exists at ${passphrase_file}; keeping it"
    else
        # 32 random bytes as base64: strong, and safe to paste into a password
        # manager or a prompt. Written straight to the file, never to the screen.
        openssl rand -base64 32 | tr -d '\n' > "$passphrase_file"
        chmod 600 "$passphrase_file"
        echo "created ${passphrase_file} (root, 0600)"
    fi
    echo
    echo "Store this passphrase in your password manager NOW. Without it no backup"
    echo "can be restored, and it dies with this machine. To see it:"
    echo
    echo "    sudo cat ${passphrase_file}; echo"
    exit 0
fi

if [ "${1:-}" = "--install-timer" ]; then
    [ -s "$passphrase_file" ] || die "run 'sudo ./backup.sh --init' first"
    cat > /etc/systemd/system/endpoint-platform-backup.service <<UNIT
[Unit]
Description=Endpoint Platform - encrypted nightly backup
Requires=docker.service
After=docker.service network-online.target
# The off-site upload needs the network, not just Docker.
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=${here}/backup.sh
UNIT
    cat > /etc/systemd/system/endpoint-platform-backup.timer <<UNIT
[Unit]
Description=Endpoint Platform - encrypted nightly backup

[Timer]
OnCalendar=*-*-* 02:30:00
# A night the machine was off is made up at the next boot, not skipped.
Persistent=true
RandomizedDelaySec=10min

[Install]
WantedBy=timers.target
UNIT
    systemctl daemon-reload
    systemctl enable --now endpoint-platform-backup.timer
    systemctl list-timers endpoint-platform-backup.timer --no-pager
    exit 0
fi

# --- a backup run ----------------------------------------------------------------

passphrase_arg="${passphrase_file}"
if [ "${1:-}" = "--passphrase-file" ]; then
    passphrase_arg="${2:-}"
fi
[ -s "$passphrase_arg" ] || die "no backup passphrase at ${passphrase_arg}. Run 'sudo ./backup.sh --init' first."
[ -f "${here}/.env" ] || die "${here}/.env is missing; this is not a deployed stack"
command -v gpg >/dev/null || die "gpg is not installed (apt-get install gnupg)"
command -v docker >/dev/null || die "docker is not installed"

env_value() { grep -E "^$1=" "${here}/.env" | tail -n 1 | cut -d= -f2- || true; }
db="$(env_value POSTGRES_DB)"
db="${db:-endpoint_platform}"

stamp="$(date -u +%Y%m%dT%H%M%SZ)"
install -d -m 750 -o root -g "$(id -gn "$owner" 2>/dev/null || echo root)" "$root_dir" "$bundle_dir"
work="$(mktemp -d "${root_dir}/.work-XXXXXX")"
cleanup() { rm -rf "$work"; }
trap cleanup EXIT

# The PostgreSQL client tools run in a THROWAWAY container of a separately
# pulled image, connecting to the database over the compose network - not
# inside the long-running postgres container. A pull is checked against the
# registry's digests, so a client binary damaged on this disk (it happened:
# pg_dump in the running container's image segfaulted on every call) cannot
# silently break the backup. Same major version as the server.
pg_image="${BACKUP_PG_IMAGE:-postgres:17-alpine}"
echo "==> client tools (${pg_image})"
docker pull -q "$pg_image" >/dev/null || die "could not pull ${pg_image}"

# The superuser password reaches the container through a 0600 env file in the
# work directory, never through argv (visible to every account via ps).
admin_password="$(env_value POSTGRES_ADMIN_PASSWORD)"
[ -n "$admin_password" ] || die ".env has no POSTGRES_ADMIN_PASSWORD"
printf 'PGPASSWORD=%s\nPGHOST=postgres\nPGUSER=postgres\n' "$admin_password" > "${work}/pg.env"
unset admin_password
pg() { docker run --rm -i --network endpoint-platform_backend --env-file "${work}/pg.env" "$pg_image" "$@"; }

echo "==> database dump (${db})"
pg pg_dump -Fc "$db" </dev/null > "${work}/db.dump" \
    || die "pg_dump failed (exit $?); no backup was made"
listing="$(pg pg_restore --list < "${work}/db.dump")" \
    || die "the dump cannot be read back by pg_restore; no backup was made"
tables="$(grep -c 'TABLE DATA' <<<"$listing" || true)"
[ "${tables:-0}" -gt 0 ] || die "the dump has no table data; refusing to call it a backup"
echo "    ${tables} tables, $(du -h "${work}/db.dump" | cut -f1)"

# Row counts the restore compares against, so "it came back" is a fact rather
# than a feeling. Counts only: nothing here is sensitive.
count() {
    pg psql -d "$db" -At -c "select count(*) from endpoint_platform.$1" </dev/null 2>/dev/null || echo "?"
}

echo "==> uploaded packages"
packages_dir="$(docker volume inspect -f '{{.Mountpoint}}' endpoint-platform_packages 2>/dev/null || true)"
if [ -n "$packages_dir" ] && [ -d "$packages_dir" ]; then
    # Numeric owners: the files belong to the API's uid inside the container,
    # which has no name on the host.
    tar --numeric-owner -C "$packages_dir" -cf "${work}/packages.tar" .
else
    tar -cf "${work}/packages.tar" --files-from /dev/null
fi
echo "    $(du -h "${work}/packages.tar" | cut -f1)"

echo "==> secrets and certificates"
secret_items=(.env)
for item in tls letsencrypt cloudflare.ini; do
    [ -e "${here}/${item}" ] && secret_items+=("$item")
done
tar --numeric-owner -C "$here" -cf "${work}/secrets.tar" "${secret_items[@]}"
echo "    ${secret_items[*]}"

{
    echo "format=1"
    echo "created_utc=${stamp}"
    echo "source_commit=$(git -c safe.directory="*" -C "${here}/../.." rev-parse HEAD 2>/dev/null || echo unknown)"
    echo "public_origin=$(env_value PUBLIC_ORIGIN)"
    echo "lan_origin=$(env_value LAN_ORIGIN)"
    echo "postgres_db=${db}"
    echo "count_devices=$(count devices)"
    echo "count_platform_users=$(count platform_users)"
    echo "count_bitlocker_recovery_escrows=$(count bitlocker_recovery_escrows)"
    echo "count_audit_log_entries=$(count audit_log_entries)"
    echo "count_agent_releases=$(count agent_releases)"
    for f in db.dump secrets.tar packages.tar; do
        echo "sha256_${f//./_}=$(sha256sum "${work}/${f}" | cut -d' ' -f1)"
    done
} > "${work}/manifest.txt"

echo "==> encrypting"
bundle="${bundle_dir}/epp-backup-${stamp}.tar.gpg"
tar -C "$work" -cf - manifest.txt db.dump secrets.tar packages.tar \
    | gpg --batch --yes --quiet --pinentry-mode loopback --passphrase-file "$passphrase_arg" \
          --symmetric --cipher-algo AES256 --compress-algo none -o "${bundle}.partial"
mv "${bundle}.partial" "$bundle"

echo "==> verifying the bundle decrypts and is complete"
listed="$(gpg --batch --quiet --pinentry-mode loopback --passphrase-file "$passphrase_arg" -d "$bundle" | tar -tf - | sort | tr '\n' ' ')"
[ "$listed" = "db.dump manifest.txt packages.tar secrets.tar " ] \
    || { rm -f "$bundle"; die "the bundle did not read back intact (got: ${listed}); nothing kept"; }

(cd "$bundle_dir" && sha256sum "$(basename "$bundle")" > "$(basename "$bundle").sha256")
chown "$owner" "$bundle" "${bundle}.sha256"
chmod 640 "$bundle" "${bundle}.sha256"

echo "==> keeping the newest ${keep}"
ls -1t "${bundle_dir}"/epp-backup-*.tar.gpg 2>/dev/null | tail -n +"$((keep + 1))" | while read -r old; do
    rm -f "$old" "${old}.sha256"
done

echo
grep -E '^count_' "${work}/manifest.txt" | sed 's/^count_/    /; s/=/: /'
echo "backup.sh: done -> ${bundle} ($(du -h "$bundle" | cut -f1))"

# Off-site copy, when offsite-upload.sh --setup has been run. A failed upload
# fails the run (so the timer shows it), but the local bundle above stands.
if [ -s /etc/endpoint-platform/offsite.conf ]; then
    echo
    bash "${here}/offsite-upload.sh" "$bundle" \
        || die "the local backup is fine (${bundle}), but the OFF-SITE UPLOAD FAILED"
else
    echo "Copy it off this machine: a backup that dies with the server restores nothing."
    echo "(sudo ./offsite-upload.sh --setup uploads every backup to Google Drive automatically.)"
fi
