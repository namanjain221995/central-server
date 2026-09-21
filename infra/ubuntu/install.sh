#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# One command, on the Ubuntu host, from a fresh checkout to a running platform.
#
# On a private network, with the domain's DNS hosted by Cloudflare (the usual
# case here - no inbound internet is needed, and nothing is installed on any
# managed PC):
#
#   bash infra/ubuntu/install.sh --host epp.example.com --cloudflare \
#        --admin-email admin@example.com --generate-admin-password
#
# On a host that IS reachable from the internet on port 80:
#
#   bash infra/ubuntu/install.sh --host epp.example.com --email ops@example.com \
#        --admin-email admin@example.com --generate-admin-password
#
# Runs as your normal login (it must be able to sudo) and drives the other
# scripts in this directory, in order:
#
#   host-prep.sh        PostgreSQL, Redis, .NET SDK, Node, nginx, certbot   (sudo)
#   gen-env.sh          secrets once, per-service environment files         (sudo)
#   setup-postgres.sh   database and its two roles                          (sudo)
#   setup-redis.sh      loopback-only, password-protected Redis             (sudo)
#   setup-nginx.sh      reverse proxy + Let's Encrypt certificate           (sudo)
#   deploy.sh           build, install, migrate, start, health-check        (you)
#   bootstrap-admin.sh  first Super Administrator                           (sudo)
#
# Every step is idempotent, so running this again on a prepared host is safe,
# and "bash infra/ubuntu/deploy.sh" on its own is all a redeploy needs.
#
# Options:
#   --host <name>                 public DNS name; becomes https://<name>
#   --email <address>             Let's Encrypt contact (unless --skip-cert)
#   --admin-email <address>       create the first Super Administrator
#   --generate-admin-password     have the host generate and print the password
#                                 once, instead of prompting for one
#   --cloudflare                  get a real Let's Encrypt certificate by DNS-01
#                                 through Cloudflare. Works with NO inbound
#                                 internet, so it is the right choice for a
#                                 machine on a private network whose domain is on
#                                 Cloudflare. Needs a token in
#                                 /etc/endpoint-platform/cloudflare.ini.
#   --self-signed                 issue TLS from a local CA instead. Fallback for
#                                 a private network with no suitable domain; costs
#                                 a CA root installed on every managed PC.
#   --skip-host-prep              packages and accounts are already in place
#   --skip-cert                   do not touch nginx or the certificate
# ---------------------------------------------------------------------------
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=infra/ubuntu/common.sh
. "${script_dir}/common.sh"

require_not_root

public_host=""
certbot_email=""
admin_email=""
generate_password=0
cloudflare=0
self_signed=0
skip_host_prep=0
skip_cert=0
while [ "$#" -gt 0 ]; do
    case "$1" in
        --host) public_host="${2:-}"; shift 2 ;;
        --email) certbot_email="${2:-}"; shift 2 ;;
        --admin-email) admin_email="${2:-}"; shift 2 ;;
        --generate-admin-password) generate_password=1; shift ;;
        --cloudflare) cloudflare=1; shift ;;
        --self-signed) self_signed=1; shift ;;
        --skip-host-prep) skip_host_prep=1; shift ;;
        --skip-cert) skip_cert=1; shift ;;
        -h | --help) sed -n '2,47p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown option: $1" >&2; exit 1 ;;
    esac
done

if [ -z "$public_host" ]; then
    echo "--host <public-dns-name> is required" >&2
    exit 1
fi
if [ "$skip_cert" -eq 0 ] && [ "$self_signed" -eq 0 ] && [ "$cloudflare" -eq 0 ] && [ -z "$certbot_email" ]; then
    echo "--email is required unless --skip-cert, --cloudflare or --self-signed is given" >&2
    echo "(HTTP-01 needs a contact address; DNS-01 and a local CA do not.)" >&2
    exit 1
fi

step_number=0
banner() {
    step_number=$((step_number + 1))
    echo
    echo "=============================================================================="
    echo "  [${step_number}] $1"
    echo "=============================================================================="
}

banner "Host preparation"
if [ "$skip_host_prep" -eq 1 ]; then
    echo "  skipped (--skip-host-prep)"
else
    sudo bash "${script_dir}/host-prep.sh"
fi

banner "Secrets and per-service environment files"
sudo bash "${script_dir}/gen-env.sh" "https://${public_host}"

banner "PostgreSQL database and roles"
sudo bash "${script_dir}/setup-postgres.sh"

banner "Redis"
sudo bash "${script_dir}/setup-redis.sh"

banner "nginx and the TLS certificate"
if [ "$skip_cert" -eq 1 ]; then
    echo "  skipped (--skip-cert)"
    echo
    echo "  WARNING: without a certificate the platform is unusable. The session cookie"
    echo "  is __Host-/Secure and browsers discard it over plain HTTP on any origin other"
    echo "  than localhost, so NOBODY CAN SIGN IN at http://${public_host}. Release agents"
    echo "  also reject an untrusted certificate."
else
    if [ "$cloudflare" -eq 1 ]; then
        sudo bash "${script_dir}/setup-nginx.sh" "$public_host" --cloudflare
    elif [ "$self_signed" -eq 1 ]; then
        sudo bash "${script_dir}/setup-nginx.sh" "$public_host" --self-signed
    else
        sudo bash "${script_dir}/setup-nginx.sh" "$public_host" "$certbot_email"
    fi
fi

banner "Build and deploy"
bash "${script_dir}/deploy.sh"

banner "First administrator"
if [ -z "$admin_email" ]; then
    echo "  skipped (no --admin-email). Create one later with:"
    echo "    sudo bash infra/ubuntu/bootstrap-admin.sh <email> --generate"
elif [ "$generate_password" -eq 1 ]; then
    sudo bash "${script_dir}/bootstrap-admin.sh" "$admin_email" --generate
else
    sudo bash "${script_dir}/bootstrap-admin.sh" "$admin_email"
fi

banner "Done"
echo "  Dashboard   https://${public_host}"
echo "  Health      https://${public_host}/api/health/ready"
echo
echo "  Windows agent install (elevated, on each managed endpoint):"
echo "    msiexec /i EndpointPlatformAgent-<version>-x64.msi SERVERBASEURL=https://${public_host}"
echo "    then approve the device in the dashboard under Enrollments."
echo
echo "  Redeploy after a code change:   bash infra/ubuntu/deploy.sh"
echo "  Logs:                           journalctl -u 'endpoint-platform-*' -f"
echo "  Back up:                        ${EPP_SECRETS_FILE}, the database, ${EPP_PACKAGES_DIR}"
