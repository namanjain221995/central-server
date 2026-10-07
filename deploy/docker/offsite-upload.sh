#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Copies backup.sh bundles to off-site storage (Google Drive, or anything else
# rclone speaks), verifies the copy, and keeps the newest N there.
#
#   sudo ./offsite-upload.sh --setup     # once: connect a Google Drive account
#   sudo ./offsite-upload.sh             # upload the newest bundle now
#   sudo ./offsite-upload.sh <bundle>    # upload one particular bundle
#
# Once set up, backup.sh calls this after every nightly backup on its own.
#
# What leaves the machine is the ENCRYPTED bundle and its checksum, nothing
# else. The passphrase never goes off-site: keep it in a password manager,
# never in the same Drive. Bundle plus passphrase opens every BitLocker key.
#
# The Drive login is an OAuth token in /etc/endpoint-platform/rclone.conf (root,
# 0600). Set it up with the "drive.file" scope: rclone then sees only the files
# it created itself, not the rest of that Drive, so a stolen token cannot read
# anything else in the account.
#
# Settings live in /etc/endpoint-platform/offsite.conf:
#   OFFSITE_REMOTE=gdrive:EndpointPlatformBackups   rclone remote and folder
#   OFFSITE_KEEP=30                                 bundles kept off-site
#
# Exit codes: 0 every requested bundle is off-site and verified, 1 otherwise.
# ---------------------------------------------------------------------------
set -euo pipefail
umask 077

etc_dir=/etc/endpoint-platform
rclone_conf="${OFFSITE_RCLONE_CONFIG:-${etc_dir}/rclone.conf}"
offsite_conf="${OFFSITE_CONFIG:-${etc_dir}/offsite.conf}"
bundle_dir="${OFFSITE_BUNDLE_DIR:-/var/backups/endpoint-platform/bundles}"

die() { echo "offsite-upload.sh: $*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run as root (sudo): the Drive token is readable by root only"

# --- one-time setup ------------------------------------------------------------

if [ "${1:-}" = "--setup" ]; then
    [ -t 0 ] || die "--setup is interactive; run it from a terminal"
    if ! command -v rclone >/dev/null; then
        echo "==> installing rclone"
        DEBIAN_FRONTEND=noninteractive apt-get install -y -qq rclone
    fi
    install -d -m 700 "$etc_dir"
    cat <<'TXT'

==> connecting Google Drive

rclone now asks a series of questions. Answer:

  n                       new remote
  name>                   gdrive
  Storage>                drive            (Google Drive)
  client_id> client_secret>   press Enter for both
  scope>                  drive.file       (only files rclone creates - IMPORTANT)
  service_account_file>   press Enter
  Edit advanced config?   n
  Use web browser?        n                (this server has no browser)

It then prints a command beginning with   rclone authorize "drive" ...
Run that command on a PC that has a browser and rclone installed
(Windows:  winget install Rclone.Rclone ), sign in there with the Google
account that should hold the backups, and paste the token it prints back
here. Then:   Configure as Shared Drive?  n    Keep this remote?  y    q to quit.

TXT
    rclone config --config "$rclone_conf"
    chmod 600 "$rclone_conf"
    remote_name="$(rclone listremotes --config "$rclone_conf" | head -n 1 | tr -d ':')"
    [ -n "$remote_name" ] || die "no remote was configured"

    read -r -p "Drive folder for the backups [EndpointPlatformBackups]: " folder
    folder="${folder:-EndpointPlatformBackups}"
    printf 'OFFSITE_REMOTE=%s:%s\nOFFSITE_KEEP=30\n' "$remote_name" "$folder" > "$offsite_conf"
    chmod 600 "$offsite_conf"

    echo "==> testing"
    rclone mkdir --config "$rclone_conf" "${remote_name}:${folder}"
    rclone about --config "$rclone_conf" "${remote_name}:" 2>/dev/null | head -n 4 || true
    echo
    echo "offsite-upload.sh: connected. backup.sh will upload every new bundle to ${remote_name}:${folder}."
    echo "Upload the newest bundle now with:  sudo ./offsite-upload.sh"
    exit 0
fi

# --- upload ---------------------------------------------------------------------

[ -s "$offsite_conf" ] || die "not set up; run 'sudo ./offsite-upload.sh --setup' first"
[ -s "$rclone_conf" ] || die "${rclone_conf} is missing; run --setup again"
command -v rclone >/dev/null || die "rclone is not installed; run --setup again"

conf_value() { grep -E "^$1=" "$offsite_conf" | tail -n 1 | cut -d= -f2- || true; }
remote="$(conf_value OFFSITE_REMOTE)"
keep="$(conf_value OFFSITE_KEEP)"; keep="${keep:-30}"
[ -n "$remote" ] || die "OFFSITE_REMOTE is not set in ${offsite_conf}"
[[ "$keep" =~ ^[1-9][0-9]*$ ]] || die "OFFSITE_KEEP must be a positive number"

# --tpslimit: Drive counts queries per minute against the shared rclone
# project; a burst at the start of an upload has tripped a 403 before. A few
# calls a second is plenty for one file a night and keeps under the limit.
rc() { rclone --config "$rclone_conf" --retries 5 --low-level-retries 10 --tpslimit 4 --tpslimit-burst 4 "$@"; }

bundles=("$@")
if [ "${#bundles[@]}" -eq 0 ]; then
    newest="$(ls -1 "${bundle_dir}"/epp-backup-*.tar.gpg 2>/dev/null | sort | tail -n 1 || true)"
    [ -n "$newest" ] || die "no bundles in ${bundle_dir}"
    bundles=("$newest")
fi

for bundle in "${bundles[@]}"; do
    [ -f "$bundle" ] || die "no such bundle: ${bundle}"
    name="$(basename "$bundle")"
    dir="$(cd "$(dirname "$bundle")" && pwd)"
    [ -f "${dir}/${name}.sha256" ] || die "${name} has no .sha256 beside it"

    echo "==> uploading ${name} to ${remote}"
    rc copy "$dir" "$remote" --include "/${name}" --include "/${name}.sha256"

    # Compares the uploaded bytes with the local file by hash (Drive keeps an
    # MD5 of every file). "Uploaded" is not the same as "arrived intact".
    rc check "$dir" "$remote" --one-way --include "/${name}" --include "/${name}.sha256" 2>/dev/null \
        || die "${name} on ${remote} does not match the local file"
    echo "    verified"
done

# Keep the newest OFFSITE_KEEP by name (names sort by time). Counted, never aged:
# if backups stopped for a month, an age rule would delete every copy there is.
mapfile -t remote_bundles < <(rc lsf --files-only --include 'epp-backup-*.tar.gpg' "$remote" | sort)
excess=$(( ${#remote_bundles[@]} - keep ))
if [ "$excess" -gt 0 ]; then
    for old in "${remote_bundles[@]:0:excess}"; do
        rc deletefile "${remote}/${old}" || echo "    could not delete ${old}" >&2
        rc deletefile "${remote}/${old}.sha256" >/dev/null 2>&1 || true
        echo "    pruned ${old}"
    done
fi
kept=$(( ${#remote_bundles[@]} < keep ? ${#remote_bundles[@]} : keep ))
echo "offsite-upload.sh: done (${kept} bundles off-site, newest ${keep} kept)"
