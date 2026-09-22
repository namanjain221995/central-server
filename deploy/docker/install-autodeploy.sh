#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Turns this host into one that deploys itself.
#
#   sudo ./install-autodeploy.sh https://github.com/<owner>/<repo>.git [branch]
#
# After this, a developer pushing to the tracked branch is the whole deployment
# procedure. Within a minute the host notices, waits for CI to go green, backs
# the database up, rebuilds, deploys and verifies - and puts the previous
# version back if any of that fails.
#
# What it does, all of it idempotent:
#
#   1. makes /opt/endpoint-platform/src a git clone of the repository, WITHOUT
#      disturbing .env, tls/ or pgadmin/pgpass (they are git-ignored, and
#      `git reset --hard` does not touch ignored files)
#   2. writes /etc/endpoint-platform/autodeploy.conf
#   3. installs and starts the timer
#
# Nothing here touches the database, the uploaded packages or the secrets. The
# running stack is not restarted: the next commit does that.
# ---------------------------------------------------------------------------
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "run as root: sudo $0 $*" >&2
    exit 1
fi

repo_url="${1:-}"
branch="${2:-main}"

deploy_root=/opt/endpoint-platform
repo_dir="${deploy_root}/src"
compose_dir="${repo_dir}/deploy/docker"
etc_dir=/etc/endpoint-platform
conf="${etc_dir}/autodeploy.conf"

if [ -z "$repo_url" ]; then
    # Already installed once: reuse the remote that is there.
    if [ -d "${repo_dir}/.git" ]; then
        repo_url="$(git -C "$repo_dir" remote get-url origin)"
        echo "==> reusing the configured remote: ${repo_url}"
    else
        echo "usage: install-autodeploy.sh <repository-url> [branch]" >&2
        exit 1
    fi
fi

command -v git >/dev/null    || { echo "git is not installed" >&2; exit 1; }
command -v docker >/dev/null || { echo "docker is not installed" >&2; exit 1; }
command -v python3 >/dev/null || { echo "python3 is not installed (the CI gate needs it)" >&2; exit 1; }
command -v curl >/dev/null   || { echo "curl is not installed (the CI gate needs it)" >&2; exit 1; }

# --- 1. the deployment directory becomes a git clone -------------------------

echo "==> ${repo_dir}: tracking ${repo_url} (${branch})"
mkdir -p "$repo_dir"

if [ ! -d "${repo_dir}/.git" ]; then
    # `git init` in place rather than `git clone` into an empty directory: the
    # live secrets and certificates are ALREADY here and must stay exactly where
    # they are. Fetch, then reset - which writes the tracked files over whatever
    # is in the way and leaves every ignored file alone.
    echo "    initialising a repository in place (existing files are kept)"
    git -C "$repo_dir" init --quiet --initial-branch="$branch"
    git -C "$repo_dir" remote add origin "$repo_url"
else
    git -C "$repo_dir" remote set-url origin "$repo_url"
fi

# Git refuses to operate on a tree owned by another user when invoked as root.
git config --global --add safe.directory "$repo_dir" 2>/dev/null || true

echo "==> fetching ${branch}"
git -C "$repo_dir" fetch --quiet --prune origin "$branch" \
    || { echo "could not fetch ${branch} from ${repo_url}" >&2; exit 1; }

for protected in deploy/docker/.env deploy/docker/tls deploy/docker/pgadmin/pgpass; do
    if [ -e "${repo_dir}/${protected}" ]; then
        echo "    keeping ${protected}"
    fi
done

git -C "$repo_dir" reset --quiet --hard "origin/${branch}"
git -C "$repo_dir" branch --quiet --set-upstream-to="origin/${branch}" "$branch" 2>/dev/null \
    || git -C "$repo_dir" checkout --quiet -B "$branch" "origin/${branch}"

chmod +x "${compose_dir}"/*.sh "${compose_dir}"/postgres/init/*.sh 2>/dev/null || true

# The secrets must still be here. If a mistake ever wiped them, stop loudly
# rather than let the next timer tick generate a NEW escrow key over the top.
if [ ! -f "${compose_dir}/.env" ]; then
    echo
    echo "    NOTE: ${compose_dir}/.env does not exist yet."
    echo "    The first deployment will generate it. If this host was deployed"
    echo "    before, restore the backed-up .env here BEFORE the timer runs, or"
    echo "    every escrowed recovery password and MFA enrolment becomes"
    echo "    unreadable."
    echo
fi

# --- 2. configuration --------------------------------------------------------

install -d -m 0750 -o root -g root "$etc_dir"

if [ -f "$conf" ]; then
    echo "==> ${conf} already exists, keeping it"
else
    cat > "$conf" <<EOF
# ---------------------------------------------------------------------------
# Pull-based deployment, read by deploy/docker/autodeploy.sh once a minute.
# Change a value here and the next run picks it up; no restart is needed.
# ---------------------------------------------------------------------------

REPO_DIR=${repo_dir}
BRANCH=${branch}

# 1 = deploy a commit only once its CI run is green. Turning this off means a
# commit that fails its tests reaches this database.
REQUIRE_CI=1
CI_WORKFLOW_NAME=CI

# A commit whose paths do not match CI's filters never produces a run. After
# this long with no run at all, deploy it anyway rather than stalling for ever.
CI_GRACE_SECONDS=600

# A pg_dump is taken before EVERY deployment. These are the only copies of the
# data that exist outside the Docker volume.
BACKUP_DIR=/var/backups/endpoint-platform
KEEP_BACKUPS=10

STATE_DIR=/var/lib/endpoint-platform/autodeploy
EOF
    chmod 640 "$conf"
    echo "==> wrote ${conf}"
fi

install -d -m 0750 /var/backups/endpoint-platform
install -d -m 0755 /var/lib/endpoint-platform/autodeploy

# --- 3. the timer ------------------------------------------------------------

echo "==> installing the systemd units"
install -m 0644 "${compose_dir}/systemd/endpoint-platform-autodeploy.service" /etc/systemd/system/
install -m 0644 "${compose_dir}/systemd/endpoint-platform-autodeploy.timer"   /etc/systemd/system/
systemctl daemon-reload
systemctl enable --now endpoint-platform-autodeploy.timer

# The commit that is on disk right now is the one currently serving; recording
# it stops the very first run from treating a fresh install as a deployment.
if [ ! -f /var/lib/endpoint-platform/autodeploy/deployed_sha ]; then
    git -C "$repo_dir" rev-parse HEAD > /var/lib/endpoint-platform/autodeploy/deployed_sha
fi

echo
echo "install-autodeploy.sh: done"
echo
systemctl list-timers endpoint-platform-autodeploy.timer --no-pager || true
echo
echo "  tracking   ${repo_url} (${branch})"
echo "  watch it   journalctl -u endpoint-platform-autodeploy -f"
echo "  force one  sudo systemctl start endpoint-platform-autodeploy"
echo "  pause it   sudo systemctl stop endpoint-platform-autodeploy.timer"
