#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Builds a release from the source tree this file sits in, and installs it.
#
#   bash infra/ubuntu/deploy.sh
#
# Runs as your normal login, NOT as root: the build (dotnet publish, npm ci)
# executes code from the package feeds and has no business doing that with
# root's privileges. Only the last step, install-release.sh, runs under sudo.
#
#   1. dotnet publish  migration job, Admin API, Agent API  (Release,
#      framework-dependent: the host's /opt/dotnet provides the runtime)
#   2. npm ci + npm run build for the dashboard. tsc -b runs first (see
#      dashboard/package.json), so a type error fails the deploy rather than
#      shipping a broken bundle.
#   3. sudo install-release.sh: copy into /opt/endpoint-platform/releases,
#      repoint "current", run migrations, restart the APIs, wait for health,
#      and roll back to the previous release if it does not become healthy.
#
# Build output goes to artifacts/publish/ under the repository, which is
# git-ignored and replaced on every run.
#
# Exit codes: 0 deployed, 1 build or install failed (the running release is
# untouched by a failed build, and restored by a failed install).
# ---------------------------------------------------------------------------
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_dir="$(cd "${script_dir}/../.." && pwd)"
# shellcheck source=infra/ubuntu/common.sh
. "${script_dir}/common.sh"

require_not_root

cd "$repo_dir"

[ -x "$EPP_DOTNET" ] || { echo "${EPP_DOTNET} is missing; run: sudo bash infra/ubuntu/host-prep.sh" >&2; exit 1; }
command -v npm >/dev/null 2>&1 || { echo "npm is missing; run: sudo bash infra/ubuntu/host-prep.sh" >&2; exit 1; }
[ -f EndpointPlatform.slnx ] || { echo "repository root not recognised at ${repo_dir}" >&2; exit 1; }

# --- release label -------------------------------------------------------------
#
# <UTC timestamp>-<commit>. The timestamp makes every label unique and sortable;
# the commit says what is running. Deploy-Ubuntu.ps1 writes .deployed-commit
# because the tree it uploads is a git archive without a .git directory.

commit=""
if [ -f .deployed-commit ]; then
    commit="$(head -n 1 .deployed-commit)"
elif command -v git >/dev/null 2>&1 && git rev-parse --short HEAD >/dev/null 2>&1; then
    commit="$(git rev-parse --short HEAD)"
    if [ -n "$(git status --porcelain 2>/dev/null)" ]; then
        commit="${commit}-dirty"
    fi
fi
commit="$(printf '%s' "${commit:-unversioned}" | tr -c 'A-Za-z0-9._-' '-' | cut -c1-40)"
label="$(date -u +%Y%m%d-%H%M%S)-${commit}"

out_root="${repo_dir}/artifacts/publish"
out="${out_root}/${label}"
rm -rf "$out_root"
mkdir -p "$out"

echo "==> building release ${label}"

export DOTNET_ROOT="$EPP_DOTNET_ROOT"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Sequential on purpose: the three projects share Domain, Infrastructure and
# Contracts, and parallel publishes race on those projects' obj directories.
publish() {
    local project="$1" target="$2"
    echo "==> dotnet publish ${project}"
    "$EPP_DOTNET" publish "$project" -c Release -o "$target" --nologo -v minimal
}

publish server/Migrations/EndpointPlatform.Migrations.csproj "${out}/migrations"
publish server/Api/EndpointPlatform.Api.csproj               "${out}/admin-api"
publish server/AgentApi/EndpointPlatform.AgentApi.csproj     "${out}/agent-api"

echo "==> dashboard: npm ci"
(cd dashboard && npm ci --no-audit --no-fund)
echo "==> dashboard: npm run build"
(cd dashboard && npm run build)
cp -r dashboard/dist "${out}/dashboard"

printf '%s\n' "$label" > "${out}/RELEASE"

echo "==> installing (sudo)"
sudo bash "${script_dir}/install-release.sh" "$out" "$label"

echo
echo "deploy.sh: done"
