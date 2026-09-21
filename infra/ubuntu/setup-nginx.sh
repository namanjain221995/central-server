#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Host nginx as the single public HTTPS entry point, with a Let's Encrypt
# certificate.
#
#   sudo bash infra/ubuntu/setup-nginx.sh <hostname> <certbot-email>   (Let's Encrypt, HTTP-01)
#   sudo bash infra/ubuntu/setup-nginx.sh <hostname> --cloudflare      (Let's Encrypt, DNS-01)
#   sudo bash infra/ubuntu/setup-nginx.sh <hostname> --self-signed     (local CA, last resort)
#
# --cloudflare is the right answer for a machine on a private network whose DNS
# is hosted by Cloudflare: DNS-01 proves control of the NAME, so no inbound
# connectivity is needed and the certificate is publicly trusted - which means
# nothing has to be installed on any managed endpoint. --self-signed is the
# fallback when there is no such domain, and costs a CA root on every PC.
#
# Plain HTTP-01 (an e-mail address as the second argument) needs a publicly
# resolvable name AND inbound port 80 from the internet, so a machine on a
# private LAN cannot use it at all. The other two modes work without any
# inbound connectivity, and both render their 443 server block from the one
# render_https_site template below, so the two cannot drift apart.
#
# Writes /etc/nginx/sites-available/endpoint-platform. One server block that:
#
#   /            serves the dashboard's static build straight from
#                /opt/endpoint-platform/current/dashboard
#   /api/<path>  proxies to the Admin API on 127.0.0.1:5080 as /<path>. The
#                prefix is stripped, mirroring the Vite dev proxy, which is why
#                one frontend build works in both places.
#   /agent/...   proxies to the Agent API on 127.0.0.1:5081 unchanged. That API
#                already serves /agent/v1 (AgentProtocol.RoutePrefix).
#
# Serving all three from ONE origin is a requirement, not a convenience: the
# session cookie carries the __Host- prefix with Secure and SameSite=Strict, so
# the browser pins it to exactly one host. A dashboard on a different host name
# than the API cannot sign in. It also means 5080/5081 are never published and
# a managed endpoint only ever dials the public HTTPS origin, outbound.
#
# In HTTP-01 mode only, runs certbot with the nginx plugin, which REWRITES the
# site file: it
# adds the 443 listener and certificate to this server block and a port 80
# redirect. That is expected, and it is why this script leaves the site file
# alone while its own template is unchanged (the rendered template is kept in
# /etc/nginx/endpoint-platform.template to detect changes). Renewal is handled
# by the systemd timer the certbot package installs; nothing here needs to run
# again for that.
#
# Requirements, HTTP-01 only: DNS for <public-hostname> already points at this
# host and port 80 is reachable from the internet. --cloudflare needs neither,
# only an API token; --self-signed needs nothing.
# Safe to run again: unchanged config is left alone, certbot keeps a
# certificate that is not yet due for renewal.
# ---------------------------------------------------------------------------
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=infra/ubuntu/common.sh
. "${script_dir}/common.sh"

require_root "$@"

hostname="${1:-}"
mode_or_email="${2:-}"
if [ -z "$hostname" ] || [ -z "$mode_or_email" ]; then
    echo "usage: setup-nginx.sh <hostname> <certbot-email>|--cloudflare|--self-signed" >&2
    exit 1
fi

self_signed=0
cloudflare=0
email=""
case "$mode_or_email" in
    --self-signed) self_signed=1 ;;
    --cloudflare)  cloudflare=1 ;;
    *)             email="$mode_or_email" ;;
esac
if ! [[ "$hostname" =~ ^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$ ]]; then
    echo "not a plain DNS host name: ${hostname}" >&2
    exit 1
fi
if [ -n "$email" ] && ! [[ "$email" =~ ^[^[:space:]@]+@[^[:space:]@]+$ ]]; then
    echo "not an e-mail address: ${email}" >&2
    exit 1
fi

site_available=/etc/nginx/sites-available/endpoint-platform
site_enabled=/etc/nginx/sites-enabled/endpoint-platform
template_copy=/etc/nginx/endpoint-platform.template
acme_root=/var/www/html
dashboard_root="${EPP_CURRENT_LINK}/dashboard"

command -v nginx >/dev/null 2>&1 || { echo "nginx is not installed; run host-prep.sh first" >&2; exit 1; }
if [ "$self_signed" -eq 0 ]; then
    command -v certbot >/dev/null 2>&1 || { echo "certbot is not installed; run host-prep.sh first" >&2; exit 1; }
fi

render_https_site() {
    local cert="$1" key="$2"

    cat > "$site_available" <<EOF
# endpoint platform: public entry point.
# Managed by infra/ubuntu/setup-nginx.sh. Re-run that to change it.
server {
    listen 80;
    listen [::]:80;
    server_name ${hostname};
    server_tokens off;

    # Served over plain HTTP deliberately: a machine that does not trust a local
    # CA yet has to be able to fetch it. Harmless with a public certificate, and
    # absent then anyway.
    location = /endpoint-platform-ca.crt {
        root ${acme_root};
        default_type application/x-x509-ca-cert;
    }

    location ^~ /.well-known/acme-challenge/ {
        root ${acme_root};
        default_type "text/plain";
    }

    location / {
        return 301 https://\$host\$request_uri;
    }
}

server {
    listen 443 ssl;
    listen [::]:443 ssl;
    http2 on;
    server_name ${hostname};
    server_tokens off;

    ssl_certificate     ${cert};
    ssl_certificate_key ${key};
    ssl_protocols       TLSv1.2 TLSv1.3;
    ssl_prefer_server_ciphers off;
    ssl_session_cache   shared:SSL:10m;

    root ${dashboard_root};
    index index.html;

    client_max_body_size 512m;

    # Admin API. The dashboard calls /api/<path>; the API serves /<path>, and the
    # trailing slash on proxy_pass is what strips the prefix.
    location /api/ {
        proxy_pass http://127.0.0.1:${EPP_ADMIN_API_PORT}/;

        proxy_http_version 1.1;
        proxy_set_header Host              \$host;
        proxy_set_header X-Real-IP         \$remote_addr;
        proxy_set_header X-Forwarded-For   \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Forwarded-Host  \$host;

        proxy_request_buffering off;
        proxy_buffering         off;
        proxy_connect_timeout   10s;
        proxy_send_timeout      300s;
        proxy_read_timeout      300s;
    }

    # Agent API. No prefix rewrite: it already serves these paths at /agent/v1.
    location /agent/ {
        proxy_pass http://127.0.0.1:${EPP_AGENT_API_PORT};

        proxy_http_version 1.1;
        proxy_set_header Host              \$host;
        proxy_set_header X-Real-IP         \$remote_addr;
        proxy_set_header X-Forwarded-For   \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Forwarded-Host  \$host;

        proxy_request_buffering off;
        proxy_buffering         off;
        proxy_connect_timeout   10s;
        proxy_send_timeout      300s;
        proxy_read_timeout      300s;
    }

    location /assets/ {
        try_files \$uri =404;
        expires 1y;
        add_header Cache-Control "public, immutable";
        add_header X-Content-Type-Options nosniff always;
        add_header X-Frame-Options DENY always;
        add_header Referrer-Policy same-origin always;
    }

    # Single-page app: unknown paths are client-side routes, not 404s.
    location / {
        try_files \$uri \$uri/ /index.html;
        add_header Cache-Control "no-store, must-revalidate";
        add_header X-Content-Type-Options nosniff always;
        add_header X-Frame-Options DENY always;
        add_header Referrer-Policy same-origin always;
    }
}
EOF

    install -m 0644 "$site_available" "$template_copy"

    if [ ! -L "$site_enabled" ] || [ "$(readlink "$site_enabled")" != "$site_available" ]; then
        ln -sfn "$site_available" "$site_enabled"
    fi
    rm -f /etc/nginx/sites-enabled/default

    nginx -t
    systemctl reload nginx
}


mkdir -p "$acme_root"

# --- certificate -------------------------------------------------------------
#
# Two of the three modes never need nginx to be serving first: DNS-01 proves
# control of the NAME through Cloudflare's API, and a local CA signs offline.
# Both are handled here, BEFORE the HTTP-only bootstrap below, so that re-running
# this script never drops a working site back to plain HTTP - on which nobody can
# sign in, because the session cookie is __Host-/Secure and the browser discards
# it. Only HTTP-01 needs that bootstrap, because certbot --nginx edits an
# existing server block rather than writing one.

if [ "$cloudflare" -eq 1 ]; then
    bash "${script_dir}/setup-tls-cloudflare.sh" "$hostname"

    echo "==> writing the HTTPS server block"
    live_dir="/etc/letsencrypt/live/${hostname}"
    render_https_site "${live_dir}/fullchain.pem" "${live_dir}/privkey.pem"

    echo
    echo "setup-nginx.sh: done, https://${hostname}/ is served with a publicly"
    echo "                trusted certificate. Nothing to install on any endpoint."
    exit 0
fi

if [ "$self_signed" -eq 1 ]; then
    # A local CA issues the certificate; certbot is not involved at all. The 443
    # server is written here rather than by a plugin, so this file stays the
    # single description of the site.
    bash "${script_dir}/setup-tls-selfsigned.sh" "$hostname"

    echo "==> writing the HTTPS server block"
    tls_dir="${EPP_ETC_DIR}/tls"
    render_https_site "${tls_dir}/server.crt" "${tls_dir}/server.key"

    echo
    echo "setup-nginx.sh: done, https://${hostname}/ is served with a local-CA certificate."
    echo "                Install the CA root on every machine - see the notice above."
    exit 0
fi

# --- HTTP-01 -----------------------------------------------------------------
#
# Only this mode reaches here.

# --- render the site ---------------------------------------------------------

rendered="$(mktemp)"
trap 'rm -f "$rendered"' EXIT
cat > "$rendered" <<EOF
# endpoint platform: public entry point. Managed by infra/ubuntu/setup-nginx.sh;
# certbot adds the 443 listener, the certificate and the https redirect.
server {
    listen 80;
    listen [::]:80;
    server_name ${hostname};

    server_tokens off;

    root ${dashboard_root};
    index index.html;

    # Package and agent-MSI uploads pass through this proxy.
    client_max_body_size 512m;

    # Let's Encrypt HTTP-01.
    location ^~ /.well-known/acme-challenge/ {
        root ${acme_root};
        default_type "text/plain";
    }

    # Admin API. The dashboard calls /api/<path>; the API serves /<path>. The
    # trailing slash on proxy_pass is what strips the prefix.
    location /api/ {
        proxy_pass http://127.0.0.1:${EPP_ADMIN_API_PORT}/;

        proxy_http_version 1.1;
        proxy_set_header Host              \$host;
        proxy_set_header X-Real-IP         \$remote_addr;
        proxy_set_header X-Forwarded-For   \$proxy_add_x_forwarded_for;
        # The API issues Secure cookies and decides about https redirection from
        # this. It must say "https" for requests that arrived over TLS.
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Forwarded-Host  \$host;

        # Uploads are streamed through; buffering them to disk first would
        # double the time and the disk use of every upload.
        proxy_request_buffering off;
        proxy_buffering         off;
        proxy_connect_timeout   10s;
        proxy_send_timeout      300s;
        proxy_read_timeout      300s;
    }

    # Agent API (ADR-0001 keeps it a separate process). No prefix rewrite: it
    # already serves these paths at /agent/v1.
    location /agent/ {
        proxy_pass http://127.0.0.1:${EPP_AGENT_API_PORT};

        proxy_http_version 1.1;
        proxy_set_header Host              \$host;
        proxy_set_header X-Real-IP         \$remote_addr;
        proxy_set_header X-Forwarded-For   \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header X-Forwarded-Host  \$host;

        # Inventory uploads and package downloads are the large ones here.
        proxy_request_buffering off;
        proxy_buffering         off;
        proxy_connect_timeout   10s;
        proxy_send_timeout      300s;
        proxy_read_timeout      300s;
    }

    # nginx does not inherit add_header into a location that sets its own, so
    # the static-file security headers are repeated in both locations below.
    # The APIs set their own headers on everything they serve.

    # Hashed asset file names, so these are safe to cache hard.
    location /assets/ {
        try_files \$uri =404;
        expires 1y;
        add_header Cache-Control "public, immutable";
        add_header X-Content-Type-Options nosniff always;
        add_header X-Frame-Options DENY always;
        add_header Referrer-Policy same-origin always;
    }

    # Single-page app: unknown paths are client-side routes, not 404s. index.html
    # itself must never be cached, or a deploy leaves browsers on the old bundle.
    location / {
        try_files \$uri \$uri/ /index.html;
        add_header Cache-Control "no-store, must-revalidate";
        add_header X-Content-Type-Options nosniff always;
        add_header X-Frame-Options DENY always;
        add_header Referrer-Policy same-origin always;
    }
}
EOF

if [ -f "$site_available" ] && [ -f "$template_copy" ] && cmp -s "$rendered" "$template_copy"; then
    echo "==> ${site_available}: template unchanged, leaving certbot's version in place"
else
    echo "==> writing ${site_available}"
    install -m 0644 "$rendered" "$site_available"
    install -m 0644 "$rendered" "$template_copy"
fi

# The stock default site is a catch-all on port 80 and would shadow ours for
# any request that arrives by IP or during the ACME challenge.
if [ -e /etc/nginx/sites-enabled/default ]; then
    echo "==> removing sites-enabled/default"
    rm -f /etc/nginx/sites-enabled/default
fi
if [ ! -L "$site_enabled" ] || [ "$(readlink "$site_enabled")" != "$site_available" ]; then
    ln -sfn "$site_available" "$site_enabled"
fi

nginx -t
systemctl reload nginx


# --keep-until-expiring: an existing, still-valid certificate is reused (and
# re-installed into the config, which is what restores the 443 listener after
# the site file is rewritten). --redirect makes port 80 a 301 to https.

echo "==> certbot for ${hostname}"
certbot --nginx \
    -d "$hostname" \
    --non-interactive \
    --agree-tos \
    -m "$email" \
    --no-eff-email \
    --redirect \
    --keep-until-expiring

nginx -t
systemctl reload nginx

echo
echo "==> certificate"
certbot certificates -d "$hostname" 2>/dev/null | grep -E 'Certificate Name|Domains|Expiry Date' || certbot certificates
echo
echo "==> renewal timer"
systemctl list-timers 'certbot*' 'snap.certbot*' --no-pager 2>/dev/null || echo "(no certbot timer listed; check: systemctl list-timers)"
echo
echo "setup-nginx.sh: done, https://${hostname}/ is served by nginx"
