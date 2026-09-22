#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Creates the first Super Administrator, through the migration job's
# "bootstrap-admin" command - the container equivalent of
# infra/ubuntu/bootstrap-admin.sh.
#
#   printf '%s\n' "$password" | ./bootstrap-admin.sh <email>
#   ./bootstrap-admin.sh <email> --generate
#
# The password is never an argv element, because argv is visible to every
# account on the host through ps. It reaches the job as an environment
# variable of a throwaway container.
#
# The job runs with the OWNER database credential from .env, exactly as the
# migrations service does.
#
# Exit codes: 0 created, or already bootstrapped (a Super Administrator exists,
# which is the expected outcome of a re-run); 1 anything else.
# ---------------------------------------------------------------------------
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"

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
[ -f .env ] || { echo ".env is missing; run generate-env.sh first" >&2; exit 1; }

password=""
if [ "$mode" = "--generate" ]; then
    # Alphanumeric only: it will be typed into a login form by a human.
    raw="$(openssl rand -base64 48 | tr -d '+/=\n')"
    password="${raw:0:24}"
else
    if [ -t 0 ]; then
        read -r -s -p "Password for ${email}: " password
        echo
    else
        read -r password
    fi
fi

if [ "${#password}" -lt 12 ]; then
    echo "the password must be at least 12 characters" >&2
    exit 1
fi

# --rm and no published port: the container exists only for this command.
if ENDPOINTPLATFORM_Bootstrap__AdminEmail="$email" \
   ENDPOINTPLATFORM_Bootstrap__AdminPassword="$password" \
   docker compose run --rm --no-deps \
       -e ENDPOINTPLATFORM_Bootstrap__AdminEmail \
       -e ENDPOINTPLATFORM_Bootstrap__AdminPassword \
       migrations bootstrap-admin
then
    echo
    echo "bootstrap-admin.sh: done"
    if [ "$mode" = "--generate" ]; then
        echo
        echo "  e-mail:   ${email}"
        echo "  password: ${password}"
        echo
        echo "  Shown ONCE. Sign in, change it, and enrol an authenticator app -"
        echo "  the platform requires both before it will let this account do anything."
    fi
else
    status=$?
    echo "bootstrap-admin.sh: the job failed (exit ${status})" >&2
    exit 1
fi
