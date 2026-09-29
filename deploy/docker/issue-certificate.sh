#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Gives the public name a publicly trusted certificate, and points the name at
# this host - with Cloudflare as DNS ONLY.
#
#   ./issue-certificate.sh        # as the account that owns .env; it needs docker, not root
#
# Reads everything from .env (SERVER_NAME, LAN_ORIGIN) and the Cloudflare API
# token from cloudflare.ini next to this script:
#
#   dns_cloudflare_api_token = <token>
#
# 0600, git-ignored. The token needs Zone:DNS:Edit on the one zone and nothing
# more - Cloudflare's "Edit zone DNS" template. It is NOT a tunnel token: this
# deployment never uses a Cloudflare Tunnel (see CLAUDE.md).
#
# What it does, every step idempotent:
#
#   1. creates or corrects <SERVER_NAME>  A  <LAN address>, proxied:FALSE. An
#      existing record that is proxied is switched to DNS-only and said so -
#      a proxied record would route admin sessions and revealed recovery keys
#      through Cloudflare's edge in cleartext.
#   2. proves the whole DNS-01 flow against Let's Encrypt STAGING first
#      (--dry-run), so a wrong token or zone costs nothing against the real
#      rate limits.
#   3. issues the real certificate, as lineage "public".
#   4. records TLS_CERT, TLS_KEY and COMPOSE_PROFILES in .env and recreates
#      `web` and `certbot`. Nothing else is restarted.
#   5. verifies the name is served with the new certificate, and the LAN address
#      is still served with the old one.
#
# The LAN address keeps its local-CA certificate throughout: agents enrolled
# against it trust that CA and nothing else.
#
# DNS-01 needs no inbound connectivity, which is why it works on a private LAN.
# The A record points at a private address, so the name only resolves to
# something reachable from inside the office network (or over a VPN) - that is
# the intended outcome, not a limitation to work around.
# ---------------------------------------------------------------------------
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$here"

env_file="${here}/.env"
cf_ini="${here}/cloudflare.ini"

die() { echo "issue-certificate.sh: $*" >&2; exit 1; }

[ -f "$env_file" ] || die ".env is missing; run generate-env.sh first"
[ -f "$cf_ini" ]   || die "cloudflare.ini is missing. Create it (0600) containing:
    dns_cloudflare_api_token = <Zone:DNS:Edit token for this zone>"
chmod 600 "$cf_ini"
command -v curl >/dev/null    || die "curl is not installed"
command -v python3 >/dev/null || die "python3 is not installed"

env_value() { grep -E "^$1=" "$env_file" | tail -n 1 | cut -d= -f2- || true; }

# Replace or append KEY=VALUE in .env, keeping it 0600 and never printing VALUE.
set_env() {
    local key="$1" value="$2" tmp="${env_file}.tmp.$$"
    grep -vE "^${key}=" "$env_file" > "$tmp" || true
    printf '%s=%s\n' "$key" "$value" >> "$tmp"
    chmod 600 "$tmp"
    # Keep the ORIGINAL owner. Written by root (sudo), the replacement would be
    # root:root 0600, and every later `docker compose` run as the operator
    # account would fail to read .env.
    chown --reference="$env_file" "$tmp" 2>/dev/null || true
    mv "$tmp" "$env_file"
}

name="$(env_value SERVER_NAME)"
lan_origin="$(env_value LAN_ORIGIN)"
lan_host="${lan_origin#https://}"; lan_host="${lan_host%%:*}"

[ -n "$name" ] || die "SERVER_NAME is not set in .env"
if [[ "$name" =~ ^[0-9.]+$ ]] || [ "$name" = "_" ]; then
    die "SERVER_NAME is '${name}', not a DNS name. Run: ./generate-env.sh https://<public-name>"
fi
[[ "$lan_host" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]] \
    || die "LAN_ORIGIN must be https://<LAN IPv4 address>; got '${lan_origin:-<unset>}'"

token="$(sed -nE 's/^[[:space:]]*dns_cloudflare_api_token[[:space:]]*=[[:space:]]*//p' "$cf_ini" | tr -d '[:space:]')"
[ -n "$token" ] || die "cloudflare.ini has no dns_cloudflare_api_token line"

# --- 1. DNS: <name> A <lan address>, DNS-only --------------------------------

cf() { # cf <METHOD> <path> [json-body]
    # An array rather than ${3:+--data "$3"}: the body contains spaces, and the
    # array is the form nobody has to think twice about.
    local args=(-sS --max-time 30 -X "$1" "https://api.cloudflare.com/client/v4$2"
                -H "Authorization: Bearer ${token}" -H "Content-Type: application/json")
    [ $# -ge 3 ] && args+=(--data "$3")
    curl "${args[@]}"
}

echo "==> DNS: ${name} -> ${lan_host} (DNS only, never proxied)"

verify="$(cf GET /user/tokens/verify)"
python3 -c 'import json,sys; d=json.loads(sys.argv[1]); sys.exit(0 if d.get("success") and d["result"]["status"]=="active" else 1)' "$verify" \
    || die "Cloudflare rejected the API token (inactive, expired or mistyped)"

# Walk up the labels until Cloudflare recognises a zone, rather than assuming
# the zone is the last two labels - which is wrong for names under co.uk.
zone_id=""; candidate="$name"
while [[ "$candidate" == *.* ]]; do
    zone_id="$(python3 -c 'import json,sys; r=json.loads(sys.argv[1]).get("result") or []; print(r[0]["id"] if r else "")' \
        "$(cf GET "/zones?name=${candidate}")")"
    [ -n "$zone_id" ] && break
    candidate="${candidate#*.}"
done
[ -n "$zone_id" ] || die "no Cloudflare zone this token can see contains ${name}"
echo "    zone: ${candidate}"

existing="$(cf GET "/zones/${zone_id}/dns_records?name=${name}")"
read -r rec_id rec_type rec_content rec_proxied < <(python3 -c '
import json,sys
r=json.loads(sys.argv[1]).get("result") or []
r=[x for x in r if x["type"] in ("A","AAAA","CNAME")]
print(*( (r[0]["id"], r[0]["type"], r[0]["content"], str(r[0]["proxied"]).lower()) if r else ("-","-","-","-") ))
' "$existing")

body="$(printf '{"type":"A","name":"%s","content":"%s","ttl":300,"proxied":false,"comment":"endpoint platform: DNS only by design, see CLAUDE.md"}' "$name" "$lan_host")"

if [ "$rec_id" = "-" ]; then
    result="$(cf POST "/zones/${zone_id}/dns_records" "$body")"
    echo "    created A ${lan_host}, proxied:false"
elif [ "$rec_type" = "A" ] && [ "$rec_content" = "$lan_host" ] && [ "$rec_proxied" = "false" ]; then
    result='{"success":true}'
    echo "    already correct"
else
    [ "$rec_proxied" = "true" ] && echo "    WARNING: the existing record was PROXIED - switching it to DNS only"
    result="$(cf PUT "/zones/${zone_id}/dns_records/${rec_id}" "$body")"
    echo "    updated ${rec_type} ${rec_content} -> A ${lan_host}, proxied:false"
fi
python3 -c 'import json,sys; sys.exit(0 if json.loads(sys.argv[1]).get("success") else 1)' "$result" \
    || die "Cloudflare refused the DNS change: ${result}"

# --- 2 + 3. the certificate --------------------------------------------------

mkdir -p "${here}/letsencrypt"

certbot() { # certbot <args...> - one-off container, same volumes as the service
    docker compose run --rm --no-deps --entrypoint certbot certbot "$@"
}

common=(certonly
    --dns-cloudflare
    --dns-cloudflare-credentials /run/secrets/cloudflare.ini
    # Cloudflare usually serves a new TXT record within seconds; 30 leaves room
    # for the slow case without making every renewal crawl.
    --dns-cloudflare-propagation-seconds 30
    --cert-name public
    -d "$name"
    --key-type ecdsa
    --agree-tos
    # Let's Encrypt stopped sending expiry e-mail in 2025, so an address buys
    # nothing here, and renewal is automatic.
    --register-unsafely-without-email
    --non-interactive)

echo "==> Let's Encrypt STAGING dry run (proves the token, zone and DNS-01 flow)"
certbot "${common[@]}" --dry-run

echo "==> issuing the real certificate for ${name}"
certbot "${common[@]}" --keep-until-expiring

[ -f "${here}/letsencrypt/live/public/fullchain.pem" ] \
    || die "certbot reported success but letsencrypt/live/public/fullchain.pem is missing"

# --- 4. switch the public name over ------------------------------------------

set_env TLS_CERT /etc/letsencrypt/live/public/fullchain.pem
set_env TLS_KEY  /etc/letsencrypt/live/public/privkey.pem
profiles="$(env_value COMPOSE_PROFILES)"
case ",${profiles}," in
    *,letsencrypt,*) ;;
    *) set_env COMPOSE_PROFILES "${profiles:+${profiles},}letsencrypt" ;;
esac

echo "==> recreating web and certbot (nothing else is touched)"
docker compose up -d --no-deps web certbot

# --- 5. verify ---------------------------------------------------------------

echo "==> waiting for web to report healthy"
for _ in $(seq 1 30); do
    [ "$(docker inspect -f '{{.State.Health.Status}}' endpoint-platform-web-1 2>/dev/null)" = healthy ] && break
    sleep 2
done

issuer_of() { # issuer_of <sni-or-empty>
    local sni=()
    [ -n "$1" ] && sni=(-servername "$1")
    echo | openssl s_client -connect 127.0.0.1:443 "${sni[@]}" 2>/dev/null \
        | openssl x509 -noout -issuer 2>/dev/null | sed 's/^issuer=//'
}

echo "    ${name}: $(issuer_of "$name")"
echo "    LAN address (no SNI): $(issuer_of '')"

# Against the real name, with normal verification: the whole point.
if curl -fsS --max-time 15 --resolve "${name}:443:127.0.0.1" -o /dev/null "https://${name}/api/health/ready"; then
    echo "    https://${name}/api/health/ready verifies with the system trust store"
else
    die "https://${name}/ does not verify against the system trust store"
fi

echo
echo "issue-certificate.sh: done"
echo "  https://${name}/  publicly trusted, DNS only, renewed automatically by the certbot service"
