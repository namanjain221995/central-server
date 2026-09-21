#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Creates the first Super Administrator through the migration job's
# "bootstrap-admin" command.
#
#   printf '%s\n' "$password" | sudo bash infra/ubuntu/bootstrap-admin.sh <email>
#   sudo bash infra/ubuntu/bootstrap-admin.sh <email> --generate
#
# Arguments: the administrator e-mail, and optionally --generate to have this
# script create a 24-character random password and print it ONCE. Without
# --generate the password is read from STDIN (12+ characters); on a terminal it
# prompts, with the input hidden.
#
# The password is never an argv element, because argv is visible to every
# account on the host through ps. It reaches the job as an environment
# variable, which only root and the job's own account can read.
#
# The job runs as the migration account with the owner database credential from
# /etc/endpoint-platform/migrations.env, exactly as the systemd unit does.
#
# Exit codes: 0 created, or already bootstrapped (a Super Administrator exists,
# which is the expected outcome of a re-run); 1 anything else, with the job's
# output shown.
# ---------------------------------------------------------------------------
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=infra/ubuntu/common.sh
. "${script_dir}/common.sh"

require_root "$@"

email="${1:-}"
mode="${2:-}"
if [ -z "$email" ]; then
    echo "usage: bootstrap-admin.sh <email> [--generate]   (password on stdin unless --generate)" >&2
    exit 1
fi
if ! [[ "$email" =~ ^[^[:space:]@]+@[^[:space:]@]+$ ]]; then
    echo "not an e-mail address: ${email}" >&2
    exit 1
fi
if [ -n "$mode" ] && [ "$mode" != "--generate" ]; then
    echo "unknown option: ${mode}" >&2
    exit 1
fi

job_dir="${EPP_CURRENT_LINK}/migrations"
[ -f "${job_dir}/EndpointPlatform.Migrations.dll" ] || {
    echo "no release is installed at ${EPP_CURRENT_LINK}; run deploy.sh first" >&2; exit 1; }
[ -f "$EPP_ENV_MIGRATIONS" ] || { echo "${EPP_ENV_MIGRATIONS} is missing; run gen-env.sh first" >&2; exit 1; }

# --- password ------------------------------------------------------------------

password=""
if [ "$mode" = "--generate" ]; then
    # Alphanumeric only: it will be typed into a login form by a human.
    raw="$(openssl rand -base64 48 | tr -d '+/=\n')"
    password="${raw:0:24}"
    if [ "${#password}" -ne 24 ]; then
        echo "password generation failed" >&2
        exit 1
    fi
else
    if [ -t 0 ]; then
        printf 'Password for %s (12+ characters, input hidden): ' "$email" >&2
    fi
    IFS= read -rs password || true
    if [ -t 0 ]; then echo >&2; fi
    # A Windows caller ends the line with CRLF; drop the CR.
    password="${password%$'\r'}"
    if [ "${#password}" -lt 12 ]; then
        echo "the password must be at least 12 characters (got ${#password})" >&2
        exit 1
    fi
fi

# --- run the one-shot job --------------------------------------------------------
#
# runuser -u without -l keeps this shell's environment, which is how both the
# owner connection string and the two Bootstrap variables reach the job.

load_env_file "$EPP_ENV_MIGRATIONS"
export ENDPOINTPLATFORM_Bootstrap__AdminEmail="$email"
export ENDPOINTPLATFORM_Bootstrap__AdminPassword="$password"
export DOTNET_ROOT="$EPP_DOTNET_ROOT"
export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_EnableDiagnostics=0
export HOME=/nonexistent

echo "==> creating Super Administrator ${email}"
set +e
output="$(cd "$job_dir" && runuser -u "$EPP_USER_MIGRATIONS" -- \
    "$EPP_DOTNET" "${job_dir}/EndpointPlatform.Migrations.dll" bootstrap-admin 2>&1)"
code=$?
set -e

unset ENDPOINTPLATFORM_Bootstrap__AdminPassword ENDPOINTPLATFORM_Database__ConnectionString

if [ "$code" -eq 0 ]; then
    echo "==> Super Administrator ${email} created"
    if [ "$mode" = "--generate" ]; then
        echo
        echo "  ==================================================================="
        echo "  GENERATED ADMINISTRATOR PASSWORD (shown once, stored nowhere else):"
        echo
        echo "      ${password}"
        echo
        echo "  Sign in at the dashboard with ${email} and change it."
        echo "  ==================================================================="
        echo
    fi
    password=""
    exit 0
fi

password=""
if [ "$code" -eq 1 ] && printf '%s' "$output" | grep -qi 'Super Administrator already exists'; then
    echo "==> already bootstrapped: a Super Administrator exists, nothing changed."
    echo "    Create further administrators from the dashboard."
    exit 0
fi

echo "==> bootstrap-admin failed (exit ${code}). Job output:" >&2
printf '%s\n' "$output" >&2
exit 1
