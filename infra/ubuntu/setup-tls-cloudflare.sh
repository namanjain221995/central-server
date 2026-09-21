#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# A genuinely trusted Let's Encrypt certificate for a machine that has NO
# inbound connectivity from the internet.
#
#   sudo bash infra/ubuntu/setup-tls-cloudflare.sh <hostname> [ip-address]
#
# WHY THIS EXISTS. The usual HTTP-01 challenge requires Let's Encrypt to connect
# to port 80 on the host being certified, which a machine on a private LAN cannot
# offer. DNS-01 proves control of the NAME instead of the ADDRESS: certbot writes
# a TXT record, Let's Encrypt reads it over public DNS, and nothing ever connects
# to this machine. So a box on 192.168.x.x gets a certificate every browser and
# every Windows machine already trusts.
#
# That matters more for AGENTS than for browsers. A Release build of the Windows
# agent validates the server certificate against the machine's trusted roots and
# has no "accept any certificate" switch. With a real certificate there is
# nothing to install on any endpoint; with a self-signed one, every single PC
# needs the CA root deployed first.
#
# WHAT IT NEEDS: a Cloudflare API token with Zone:DNS:Edit on the zone, placed at
#
#     /etc/endpoint-platform/cloudflare.ini      (root:root, 0600)
#     dns_cloudflare_api_token = <token>
#
# Create that file yourself; this script never asks for the token, never prints
# it, and never puts it on a command line. A token scoped to one zone's DNS is
# the least authority that can do this job - do not use a Global API Key, which
# can do anything to the whole account.
#
# It also upserts the A record for <hostname>, DNS ONLY (never proxied).
# Cloudflare's proxy cannot reach a private address, so an orange-cloud record
# would resolve to Cloudflare and time out.
#
# Renewal is automatic: certbot stores the DNS-01 configuration with the
# certificate and its systemd timer renews without this script running again.
# ---------------------------------------------------------------------------
set -euo pipefail
umask 077

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=infra/ubuntu/common.sh
. "${script_dir}/common.sh"

require_root "$@"

hostname="${1:-}"
target_ip="${2:-}"

if [ -z "$hostname" ]; then
    echo "usage: setup-tls-cloudflare.sh <hostname> [ip-address]" >&2
    exit 1
fi
if ! [[ "$hostname" =~ ^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)+$ ]]; then
    echo "not a fully-qualified DNS name: ${hostname}" >&2
    exit 1
fi

credentials="${EPP_ETC_DIR}/cloudflare.ini"
if [ ! -s "$credentials" ]; then
    cat >&2 <<EOF
${credentials} is missing.

Create it with a Cloudflare API token that has Zone:DNS:Edit on the zone for
${hostname}, then run this again:

    sudo install -d -m 0750 ${EPP_ETC_DIR}
    sudo tee ${credentials} >/dev/null <<'INI'
dns_cloudflare_api_token = YOUR_TOKEN_HERE
INI
    sudo chmod 600 ${credentials}
    sudo chown root:root ${credentials}

Make the token at https://dash.cloudflare.com/profile/api-tokens using the
"Edit zone DNS" template, scoped to this one zone. Do NOT use a Global API Key.
EOF
    exit 1
fi

# certbot reads it as the invoking user; it warns loudly about loose permissions
# and a world-readable token is a real leak, so this is enforced rather than
# suggested.
chown root:root "$credentials"
chmod 600 "$credentials"

api_token="$(awk -F= '/dns_cloudflare_api_token/ {gsub(/[[:space:]]/, "", $2); print $2}' "$credentials")"
if [ -z "$api_token" ]; then
    echo "${credentials} does not contain dns_cloudflare_api_token." >&2
    exit 1
fi

if [ -z "$target_ip" ]; then
    target_ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
fi
if ! [[ "$target_ip" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
    echo "could not determine an IPv4 address to publish; pass one explicitly." >&2
    exit 1
fi

export DEBIAN_FRONTEND=noninteractive
if ! dpkg -s python3-certbot-dns-cloudflare >/dev/null 2>&1; then
    echo "==> installing the certbot Cloudflare DNS plugin"
    apt-get update -q
    apt-get install -y -q python3-certbot-dns-cloudflare
fi
command -v curl >/dev/null 2>&1 || apt-get install -y -q curl

# --- the DNS record ----------------------------------------------------------
#
# The token is passed through the environment, never as an argument: a command
# line is readable by every account on this machine through /proc.

cf() {
    local method="$1" path="$2" data="${3:-}"
    if [ -n "$data" ]; then
        curl -sS -X "$method" "https://api.cloudflare.com/client/v4${path}" \
            -H "Authorization: Bearer ${api_token}" \
            -H "Content-Type: application/json" \
            --data "$data"
    else
        curl -sS -X "$method" "https://api.cloudflare.com/client/v4${path}" \
            -H "Authorization: Bearer ${api_token}"
    fi
}

# The zone is the registrable domain: strip labels until Cloudflare recognises one.
zone_id=""
zone_name="$hostname"
while [ -z "$zone_id" ] && [[ "$zone_name" == *.* ]]; do
    response="$(cf GET "/zones?name=${zone_name}&status=active")"
    if printf '%s' "$response" | grep -q '"success":true'; then
        zone_id="$(printf '%s' "$response" | python3 -c 'import json,sys; z=json.load(sys.stdin)["result"]; print(z[0]["id"] if z else "")')"
    fi
    [ -n "$zone_id" ] && break
    zone_name="${zone_name#*.}"
done

if [ -z "$zone_id" ]; then
    echo "No active Cloudflare zone found for ${hostname}." >&2
    echo "Check that the token has Zone:DNS:Edit AND Zone:Zone:Read on that zone." >&2
    exit 1
fi
echo "==> Cloudflare zone ${zone_name}"

existing="$(cf GET "/zones/${zone_id}/dns_records?type=A&name=${hostname}" \
    | python3 -c 'import json,sys; r=json.load(sys.stdin).get("result") or []; print(r[0]["id"] if r else "")')"

# proxied MUST be false. Cloudflare's proxy terminates connections at their edge
# and then connects to the origin - which it cannot do for a private address, so
# an orange-cloud record produces a name that resolves but never answers.
record="{\"type\":\"A\",\"name\":\"${hostname}\",\"content\":\"${target_ip}\",\"ttl\":120,\"proxied\":false}"

if [ -n "$existing" ]; then
    echo "==> updating the A record ${hostname} -> ${target_ip} (DNS only)"
    result="$(cf PUT "/zones/${zone_id}/dns_records/${existing}" "$record")"
else
    echo "==> creating the A record ${hostname} -> ${target_ip} (DNS only)"
    result="$(cf POST "/zones/${zone_id}/dns_records" "$record")"
fi

if ! printf '%s' "$result" | grep -q '"success":true'; then
    echo "Cloudflare refused the DNS change:" >&2
    printf '%s\n' "$result" | python3 -c 'import json,sys; print(json.dumps(json.load(sys.stdin).get("errors"), indent=2))' >&2 || true
    exit 1
fi

# --- the certificate ---------------------------------------------------------
#
# certonly, not --nginx: this script writes the server block itself, so the site
# file stays one description rather than something a plugin edits.

echo "==> requesting a certificate for ${hostname} (DNS-01)"
certbot certonly \
    --dns-cloudflare \
    --dns-cloudflare-credentials "$credentials" \
    --dns-cloudflare-propagation-seconds 30 \
    -d "$hostname" \
    --non-interactive \
    --agree-tos \
    --register-unsafely-without-email \
    --keep-until-expiring

live="/etc/letsencrypt/live/${hostname}"
if [ ! -s "${live}/fullchain.pem" ]; then
    echo "certbot reported success but ${live}/fullchain.pem is missing." >&2
    exit 1
fi

echo
echo "==> certificate"
openssl x509 -in "${live}/fullchain.pem" -noout -subject -issuer -enddate | sed 's/^/    /'
echo
echo "==> renewal"
systemctl list-timers 'certbot*' 'snap.certbot*' --no-pager 2>/dev/null | head -3 \
    || echo "    (no timer listed; check: systemctl list-timers)"
echo
echo "setup-tls-cloudflare.sh: done. The certificate is publicly trusted, so no"
echo "                         root has to be installed on any managed PC."
