#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Continuous deployment for a machine nothing can reach.
#
# The host sits on a private network: no GitHub-hosted runner can dial in, and
# opening it to the internet so that one could would be a far bigger change than
# a deployment pipeline. So the flow is inverted - the host PULLS:
#
#   developer pushes  ->  GitHub runs CI  ->  this timer notices the new commit,
#   waits for CI to go green, backs the database up, rebuilds, deploys, and
#   verifies. If anything fails it puts the previous version back.
#
# Run by endpoint-platform-autodeploy.timer, once a minute. Doing nothing is the
# normal outcome and is silent; the journal only grows when something happens:
#
#   journalctl -u endpoint-platform-autodeploy -f
#
# WHAT IT WILL NEVER TOUCH. Everything below outlives every deployment, because
# losing any of it is unrecoverable rather than inconvenient:
#
#   the pgdata volume      the database itself
#   the packages volume    uploaded installer content
#   the pgadmin volume     saved queries and console state
#   deploy/docker/.env     escrow key, MFA key, database passwords
#   deploy/docker/tls/     this deployment's CA and certificate
#   pgadmin/pgpass         the console's database password
#
# The first three are Docker volumes, which nothing here removes - `compose
# down -v` is never issued. The last three are git-ignored, and `git reset
# --hard` does not touch ignored files. On top of that, a pg_dump is taken
# BEFORE every single deployment, so even a bad migration is recoverable.
#
# Configuration: /etc/endpoint-platform/autodeploy.conf
# Exit codes: 0 nothing to do, or deployed successfully. 1 deployment failed
# (and the previous version was restored).
# ---------------------------------------------------------------------------
set -euo pipefail

CONF=/etc/endpoint-platform/autodeploy.conf

# Defaults; the conf file overrides any of them.
REPO_DIR=/opt/endpoint-platform/src
BRANCH=main
REQUIRE_CI=1
CI_WORKFLOW_NAME=CI
# A commit whose paths do not match CI's filters never produces a run at all.
# After this long with no run, the commit is deployed anyway rather than leaving
# the pipeline stuck for ever on a documentation change.
CI_GRACE_SECONDS=600
BACKUP_DIR=/var/backups/endpoint-platform
KEEP_BACKUPS=10
STATE_DIR=/var/lib/endpoint-platform/autodeploy

# shellcheck disable=SC1090
[ -f "$CONF" ] && . "$CONF"

COMPOSE_DIR="${REPO_DIR}/deploy/docker"
mkdir -p "$STATE_DIR" "$BACKUP_DIR"
chmod 750 "$BACKUP_DIR"

log() { printf '%s %s\n' "$(date -u +%H:%M:%SZ)" "$*"; }
die() { log "ERROR: $*"; exit 1; }

# --- 0. one at a time --------------------------------------------------------
#
# A build takes minutes and the timer fires every minute. Without this, a slow
# deployment would be racing the next one through the same working tree.

exec 9>/var/lock/endpoint-platform-autodeploy.lock
if ! flock -n 9; then
    exit 0
fi

[ -d "${REPO_DIR}/.git" ] || die "${REPO_DIR} is not a git clone; run install-autodeploy.sh"
[ -f "${COMPOSE_DIR}/deploy.sh" ] || die "${COMPOSE_DIR}/deploy.sh is missing"
command -v docker >/dev/null || die "docker is not installed"

cd "$REPO_DIR"

# --- 1. is there anything new? -----------------------------------------------

git fetch --quiet --prune origin "$BRANCH" || die "could not fetch origin/${BRANCH}"

current="$(git rev-parse HEAD)"
target="$(git rev-parse "origin/${BRANCH}")"

if [ "$current" = "$target" ]; then
    exit 0
fi

# A commit that already failed here is not retried every minute for ever. It is
# retried when something changes - a new commit, or an operator clearing the
# state file - which is the only thing that could make the outcome different.
if [ -f "${STATE_DIR}/failed_sha" ] && [ "$(cat "${STATE_DIR}/failed_sha")" = "$target" ]; then
    exit 0
fi

log "new commit on ${BRANCH}: ${current:0:7} -> ${target:0:7}"
git log --no-decorate --oneline "${current}..${target}" 2>/dev/null | head -10 | sed 's/^/         /' || true

# --- 2. did CI pass for it? --------------------------------------------------
#
# The one gate that matters: a commit that never passed CI is how a broken
# schema reaches a live database. The repository is public, so this needs no
# token; the API is only called when a NEW commit appears, which keeps it far
# below the unauthenticated rate limit.

ci_verdict() { # -> success | pending | failure | none
    local sha="$1" url owner_repo
    url="$(git remote get-url origin)"
    owner_repo="$(printf '%s' "$url" | sed -E 's#^.*github\.com[:/]##; s#\.git$##')"

    local json
    json="$(curl -sS --max-time 20 \
        -H 'Accept: application/vnd.github+json' \
        "https://api.github.com/repos/${owner_repo}/actions/runs?head_sha=${sha}&per_page=50" \
        2>/dev/null)" || { echo pending; return; }

    CI_WORKFLOW_NAME="$CI_WORKFLOW_NAME" python3 - "$json" <<'PY'
import json, os, sys
want = os.environ.get("CI_WORKFLOW_NAME", "CI")
try:
    runs = json.loads(sys.argv[1]).get("workflow_runs", [])
except Exception:
    print("pending"); raise SystemExit
runs = [r for r in runs if r.get("name") == want]
if not runs:
    print("none"); raise SystemExit
if any(r.get("status") != "completed" for r in runs):
    print("pending"); raise SystemExit
print("success" if all(r.get("conclusion") == "success" for r in runs) else "failure")
PY
}

if [ "$REQUIRE_CI" = "1" ]; then
    verdict="$(ci_verdict "$target")"
    commit_age=$(( $(date +%s) - $(git log -1 --format=%ct "$target") ))

    case "$verdict" in
        success)
            log "CI passed for ${target:0:7}" ;;
        pending)
            log "CI is still running for ${target:0:7}; waiting"
            exit 0 ;;
        failure)
            log "CI FAILED for ${target:0:7}; not deploying it"
            printf '%s' "$target" > "${STATE_DIR}/failed_sha"
            exit 0 ;;
        none)
            if [ "$commit_age" -lt "$CI_GRACE_SECONDS" ]; then
                log "no CI run for ${target:0:7} yet (commit is ${commit_age}s old); waiting"
                exit 0
            fi
            log "no CI run for ${target:0:7} after ${CI_GRACE_SECONDS}s - CI's path filters do not match it; deploying anyway" ;;
    esac
fi

# --- 3. back the database up FIRST -------------------------------------------
#
# Before the code changes, not after. A migration that corrupts data is exactly
# the case where the backup has to predate the deployment, and a custom-format
# dump restores into a running cluster with pg_restore.

if docker compose -f "${COMPOSE_DIR}/docker-compose.yml" --project-directory "$COMPOSE_DIR" \
       ps --status running --services 2>/dev/null | grep -qx postgres; then
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    dump="${BACKUP_DIR}/endpoint_platform-${stamp}-pre-${target:0:7}.dump"
    log "backing the database up to ${dump}"
    if docker compose -f "${COMPOSE_DIR}/docker-compose.yml" --project-directory "$COMPOSE_DIR" \
           exec -T postgres pg_dump -U postgres -Fc endpoint_platform > "$dump" 2>/dev/null \
       && [ -s "$dump" ]; then
        chmod 600 "$dump"
        # Keep the most recent KEEP_BACKUPS and no more: this partition also
        # holds the Docker volumes, and a full disk stops the database.
        ls -1t "${BACKUP_DIR}"/endpoint_platform-*.dump 2>/dev/null \
            | tail -n "+$((KEEP_BACKUPS + 1))" | xargs -r rm -f
    else
        rm -f "$dump"
        die "the pre-deployment backup failed; refusing to deploy without one"
    fi
else
    log "postgres is not running, so there is nothing to back up yet"
fi

# --- 4. keep the current images, so there is something to go back to ---------

for image in admin-api agent-api migrations web; do
    if docker image inspect "endpoint-platform/${image}:local" >/dev/null 2>&1; then
        docker tag "endpoint-platform/${image}:local" "endpoint-platform/${image}:previous"
    fi
done

# --- 5. deploy ---------------------------------------------------------------
#
# reset --hard, not pull: the deployed tree must be EXACTLY the commit, with no
# room for a local edit to survive and make the host disagree with the
# repository. Ignored files - .env, tls/, pgadmin/pgpass - are untouched by it.

git reset --quiet --hard "$target"
chmod +x "${COMPOSE_DIR}"/*.sh "${COMPOSE_DIR}"/postgres/init/*.sh 2>/dev/null || true

log "deploying ${target:0:7}"
if SOURCE_REVISION="${target:0:7}" bash "${COMPOSE_DIR}/deploy.sh"; then
    printf '%s' "$target" > "${STATE_DIR}/deployed_sha"
    rm -f "${STATE_DIR}/failed_sha"
    date -u +%Y-%m-%dT%H:%M:%SZ > "${STATE_DIR}/deployed_at"
    log "deployed ${target:0:7} successfully"
    exit 0
fi

# --- 6. roll back ------------------------------------------------------------
#
# deploy.sh only reports success when the dashboard, both APIs and pgAdmin all
# answer over the real public URL, so reaching here means the new version is
# genuinely not serving. The old images are still on the host; putting them back
# is faster and far more certain than rebuilding the previous commit.

log "deployment FAILED; rolling back to ${current:0:7}"
printf '%s' "$target" > "${STATE_DIR}/failed_sha"

git reset --quiet --hard "$current"

rolled_back=0
for image in admin-api agent-api migrations web; do
    if docker image inspect "endpoint-platform/${image}:previous" >/dev/null 2>&1; then
        docker tag "endpoint-platform/${image}:previous" "endpoint-platform/${image}:local"
        rolled_back=1
    fi
done

if [ "$rolled_back" = 1 ]; then
    # --no-build: the point of a rollback is to run the images that were known
    # to work, not to compile anything.
    if bash "${COMPOSE_DIR}/deploy.sh" '' --no-build; then
        log "rolled back to ${current:0:7}; the platform is serving the previous version"
    else
        log "ROLLBACK ALSO FAILED - the platform is down and needs a human"
    fi
else
    log "no previous images to roll back to - this was the first deployment"
fi

log "commit ${target:0:7} is marked failed and will not be retried; a new commit clears that"
exit 1
