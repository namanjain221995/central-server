#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Refuses to let a machine be used for an agent upgrade/removal pilot if that
# machine has ever been enrolled in production.
#
# Run it ON THE PRODUCTION HOST, before installing a pilot agent anywhere:
#
#   sudo bash infra/ubuntu/assert-pilot-machine-is-safe.sh \
#       --machine-id 5A4839FE-996A-4748-9411-7EA29DC6978A --hostname DESKTOP-PJCC143
#
# or from the candidate Windows machine, over SSH:
#
#   ssh <login>@<production-host> sudo bash app/infra/ubuntu/assert-pilot-machine-is-safe.sh `
#       --machine-id (Get-CimInstance Win32_ComputerSystemProduct).UUID --hostname $env:COMPUTERNAME
#
# An agent pilot repoints a machine's agent at an isolated pilot server. If that
# machine already has a production enrollment, the pilot install overwrites the
# device credential and the production device silently stops reporting. The
# production database is never written to, so every pilot-side check still says
# PASS while a production endpoint goes dark. That is exactly what happened on
# 2026-09-11; see docs/runbooks/agent-pilot-safety.md.
#
# CHECKING ONLY THE PILOT DATABASE IS NOT SUFFICIENT. The pilot database cannot
# know that a machine belongs to production; only production can answer that.
#
# Options:
#   --machine-id <id>              the candidate's identifier as the agent reports it
#   --hostname <name>              the candidate's hostname
#   --approve-hostname-collision   permits a hostname that matches an ACTIVE
#                                  production device, for two genuinely different
#                                  machines sharing a name. Requires --reason.
#                                  It never permits a machine-identifier match:
#                                  a shared identifier means it is the same machine.
#   --reason "<why>"               recorded in the output beside an approved collision
#
# WHAT THIS TOUCHES: production PostgreSQL, four SELECT statements, in a session
# the server itself holds read-only (default_transaction_read_only=on). No
# INSERT, UPDATE, DELETE, task, token, session or audit entry. There is no
# switch that makes it write, and deliberately never will be.
#
# Exit codes: 0 PASS, 1 FAIL. Anything unexpected is a FAIL: it never assumes a
# pass.
# ---------------------------------------------------------------------------
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=infra/ubuntu/common.sh
. "${script_dir}/common.sh"

require_root "$@"

machine_id=""
host_name=""
approve_collision=0
reason=""
while [ "$#" -gt 0 ]; do
    case "$1" in
        --machine-id) machine_id="${2:-}"; shift 2 ;;
        --hostname) host_name="${2:-}"; shift 2 ;;
        --approve-hostname-collision) approve_collision=1; shift ;;
        --reason) reason="${2:-}"; shift 2 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

if [ -z "$machine_id" ] || [ -z "$host_name" ]; then
    echo "usage: assert-pilot-machine-is-safe.sh --machine-id <id> --hostname <name> [--approve-hostname-collision --reason \"<why>\"]" >&2
    exit 1
fi
if [ "$approve_collision" -eq 1 ] && [ -z "${reason// /}" ]; then
    echo "--approve-hostname-collision requires --reason." >&2
    exit 1
fi

# Validating is safer than escaping: these two shapes cover every real value, so
# nothing legitimate is turned away.
if ! [[ "$machine_id" =~ ^[A-Za-z0-9-]{1,64}$ ]]; then
    echo "machine identifier '${machine_id}' is not a plain identifier; refusing to query with it." >&2
    exit 1
fi
if ! [[ "$host_name" =~ ^[A-Za-z0-9._-]{1,255}$ ]]; then
    echo "hostname '${host_name}' is not a plain hostname; refusing to query with it." >&2
    exit 1
fi

database=endpoint_platform
if [ -f "$EPP_SECRETS_FILE" ]; then
    configured="$(grep -E '^POSTGRES_DB=' "$EPP_SECRETS_FILE" | tail -n 1 | cut -d= -f2- || true)"
    database="${configured:-$database}"
fi
pg_port="$(detect_pg_port)"

# psql does NOT interpolate -v variables inside -c, so each statement goes in on
# stdin, where :'mid' and :'h' are substituted and properly quoted.
query() {
    printf '%s\n' "$1" | (cd / && PGOPTIONS='-c default_transaction_read_only=on' \
        runuser -u postgres -- psql -X -q -t -A -v ON_ERROR_STOP=1 \
            -p "$pg_port" -d "$database" -v mid="$machine_id" -v h="$host_name")
}

fail_blind() {
    echo "PILOT PREFLIGHT: FAIL" >&2
    echo "  - production could not be asked ($1). Do NOT proceed blind." >&2
    exit 1
}

echo "Asking production (read-only) about ${host_name} / ${machine_id} ..."

machine_any="$(query "SELECT count(*) FROM endpoint_platform.devices WHERE machine_identifier = :'mid';")" \
    || fail_blind "query failed"
machine_active="$(query "SELECT count(*) FROM endpoint_platform.devices WHERE machine_identifier = :'mid' AND status = 'Active';")" \
    || fail_blind "query failed"
host_active="$(query "SELECT count(*) FROM endpoint_platform.devices WHERE hostname = :'h' AND status = 'Active';")" \
    || fail_blind "query failed"
detail="$(query "SELECT id || ' | ' || hostname || ' | ' || agent_version || ' | ' || status || ' | last_seen ' || coalesce(last_seen_at::text, 'never') FROM endpoint_platform.devices WHERE machine_identifier = :'mid' OR hostname = :'h' ORDER BY enrolled_at;")" \
    || fail_blind "query failed"

for count in "$machine_any" "$machine_active" "$host_active"; do
    [[ "$count" =~ ^[0-9]+$ ]] || fail_blind "unexpected answer '${count}'"
done

echo
echo "Production says:"
echo "  rows with this machine identifier ... ${machine_any} (${machine_active} Active)"
echo "  Active devices with this hostname .. ${host_active}"
if [ -n "$detail" ]; then
    echo
    echo "Matching production rows:"
    printf '%s\n' "$detail" | sed 's/^/  /'
fi
echo

failures=()

# Fatal and unconditional. A shared machine identifier is the same machine, so
# installing a pilot agent here would overwrite a production device credential.
if [ "$machine_any" -gt 0 ]; then
    failures+=("This machine identifier is already enrolled in production (${machine_any} row(s), ${machine_active} Active). A pilot agent install would overwrite the production device credential and take that endpoint offline. Use a machine that has never been enrolled in production.")
fi

if [ "$host_active" -gt 0 ] && [ "$machine_any" -eq 0 ]; then
    if [ "$approve_collision" -eq 1 ]; then
        echo "WARNING: hostname '${host_name}' matches an Active production device, approved: ${reason}"
    else
        failures+=("Hostname '${host_name}' is an Active production device, though the machine identifier differs. Confirm these are genuinely different machines, then re-run with --approve-hostname-collision --reason \"<why>\".")
    fi
fi

if [ "${#failures[@]}" -gt 0 ]; then
    echo "PILOT PREFLIGHT: FAIL"
    for failure in "${failures[@]}"; do
        echo "  - ${failure}"
    done
    echo
    echo "Do not install a pilot agent on this machine."
    exit 1
fi

echo "PILOT PREFLIGHT: PASS"
echo "This machine has no production enrollment. Safe to use for an agent pilot."
exit 0
