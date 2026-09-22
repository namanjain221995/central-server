#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Deploys this working tree to the Ubuntu host over SSH, from a workstation.
#
#   ./remote-deploy.sh paras-thind@192.168.8.96 https://192.168.8.96
#   ./remote-deploy.sh paras-thind@192.168.8.96                  # reuse the deployed origin
#   ./remote-deploy.sh paras-thind@192.168.8.96 '' --no-build    # restart, do not rebuild
#
# This is the manual path. The pipeline in .github/workflows/deploy.yml does the
# same thing from a runner installed on the host itself; use this one when there
# is no runner yet, or to try a change that is not committed.
#
# It ships the files git knows about (tracked, plus untracked files that are not
# ignored), so a stray build artefact or a local .env cannot travel with it. On
# the host it NEVER overwrites .env, tls/ or pgadmin/pgpass: those are that
# deployment's identity, and regenerating them would invalidate every escrowed
# recovery password and every authenticator enrolment at once.
#
# Works from Git Bash on Windows as well as from Linux or macOS: rsync is used
# when both ends have it, and a tar stream over ssh otherwise.
# ---------------------------------------------------------------------------
set -euo pipefail

target="${1:-}"
origin="${2:-}"
build_flag="${3:-}"

if [ -z "$target" ]; then
    echo "usage: remote-deploy.sh <user@host> [https://<origin>] [--no-build]" >&2
    exit 1
fi
if [ -n "$build_flag" ] && [ "$build_flag" != "--no-build" ]; then
    echo "unknown option: ${build_flag}" >&2
    exit 1
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${here}/../.." && pwd)"
remote_dir=/opt/endpoint-platform/src

cd "$repo_root"
git rev-parse --is-inside-work-tree >/dev/null 2>&1 || {
    echo "not a git repository: ${repo_root}" >&2; exit 1; }

revision="$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
if ! git diff --quiet || ! git diff --cached --quiet; then
    echo "note: the working tree has uncommitted changes; they WILL be deployed"
fi

echo "==> ${target}:${remote_dir}  (revision ${revision})"

ssh "$target" "mkdir -p ${remote_dir}"

# The exclusions matter more than the transport: everything else on the host is
# replaceable, and these three are not.
excludes=(
    '--exclude=.git'
    '--exclude=deploy/docker/.env'
    '--exclude=deploy/docker/tls'
    '--exclude=deploy/docker/pgadmin/pgpass'
    '--exclude=deploy/docker/pgadmin/servers.json'
)

if command -v rsync >/dev/null 2>&1 && ssh "$target" 'command -v rsync >/dev/null 2>&1'; then
    echo "==> rsync"
    git ls-files -co --exclude-standard -z \
        | rsync -a --delete-missing-args --from0 --files-from=- "${excludes[@]}" \
                ./ "${target}:${remote_dir}/"
else
    echo "==> tar over ssh (rsync is not available on both ends)"
    # No --delete equivalent, so a file removed from git stays on the host until
    # someone clears the directory. Said out loud rather than papered over.
    git ls-files -co --exclude-standard \
        | grep -v -E '^deploy/docker/(\.env|tls/|pgadmin/(pgpass|servers\.json))' \
        | tar -czf - -T - \
        | ssh "$target" "tar -xzf - -C ${remote_dir}"
fi

echo "==> running deploy.sh on the host"
# The password prompt, if sudo asks for one, is forwarded to this terminal:
# deploy.sh needs root only to hand pgadmin/pgpass to uid 5050.
ssh -t "$target" "cd ${remote_dir}/deploy/docker \
    && chmod +x ./*.sh ./postgres/init/*.sh \
    && sudo SOURCE_REVISION=${revision} bash ./deploy.sh ${origin} ${build_flag}"
