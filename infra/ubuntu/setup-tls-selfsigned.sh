#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Issues TLS for a deployment that cannot use Let's Encrypt.
#
#   sudo bash infra/ubuntu/setup-tls-selfsigned.sh <hostname> [extra-name-or-ip ...]
#
# A machine on a private network has no publicly resolvable name and no inbound
# port 80 from the internet, so the HTTP-01 challenge cannot succeed. HTTPS is
# still mandatory here, not optional: the session cookie is issued with the
# __Host- prefix and Secure, and browsers exempt only localhost, so over plain
# HTTP nobody can sign in at all.
#
# WHAT THIS CREATES
#
#   /etc/endpoint-platform/ca/ca.crt   a small certificate authority, created
#                                      once and then reused
#   /etc/endpoint-platform/ca/ca.key   its private key, root-only, never leaves
#                                      this machine
#   /etc/endpoint-platform/tls/        the server certificate and key nginx uses
#
# A local CA rather than a bare self-signed certificate, deliberately. The CA
# root is ONE file you install on each managed Windows PC; after that every
# certificate this script issues - including a replacement when this one expires,
# or one for a second host - is trusted automatically. A bare self-signed
# certificate would have to be re-distributed every single time.
#
# THIS MATTERS FOR AGENTS, NOT JUST BROWSERS. A Release build of the Windows
# agent validates the server certificate against the machine's trusted roots and
# has no "accept any certificate" switch - that escape hatch is gated on a Debug
# build on purpose. Until the CA root is installed in Local Machine\Trusted Root
# Certification Authorities, agents on that PC will refuse to connect. The script
# prints the exact command at the end.
#
# Safe to run again: an existing CA is reused, and the server certificate is
# reissued only when it is missing, expiring within 30 days, or no longer covers
# the names asked for.
# ---------------------------------------------------------------------------
set -euo pipefail
umask 077

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=infra/ubuntu/common.sh
. "${script_dir}/common.sh"

require_root "$@"

hostname="${1:-}"
if [ -z "$hostname" ]; then
    echo "usage: setup-tls-selfsigned.sh <hostname> [extra-name-or-ip ...]" >&2
    exit 1
fi
shift || true
extra_names=("$@")

if ! [[ "$hostname" =~ ^[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]*[A-Za-z0-9])?)*$ ]]; then
    echo "not a plain DNS host name: ${hostname}" >&2
    exit 1
fi

ca_dir="${EPP_ETC_DIR}/ca"
tls_dir="${EPP_ETC_DIR}/tls"
ca_key="${ca_dir}/ca.key"
ca_crt="${ca_dir}/ca.crt"
server_key="${tls_dir}/server.key"
server_crt="${tls_dir}/server.crt"

install -d -m 0700 -o root -g root "$ca_dir"
install -d -m 0700 -o root -g root "$tls_dir"

# --- the certificate authority, created once ---------------------------------

if [ -s "$ca_key" ] && [ -s "$ca_crt" ]; then
    echo "==> reusing the existing certificate authority"
else
    echo "==> creating a certificate authority (10 years)"
    openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
        -keyout "$ca_key" -out "$ca_crt" \
        -subj "/CN=Endpoint Platform Local CA/O=Endpoint Platform" \
        -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
        -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null
    chmod 600 "$ca_key"
    # The root is public material and must be readable to be distributed.
    chmod 644 "$ca_crt"
fi

# --- what the server certificate must cover ----------------------------------
#
# Modern clients ignore the Common Name entirely and read subjectAltName, so every
# name and address anyone might type has to appear there. The host's own primary
# IPv4 is added automatically: on a LAN, browsing by address is common, and a
# certificate that omits it produces a warning that looks like a broken deployment.

alt_names=("DNS:${hostname}")
for name in ${extra_names+"${extra_names[@]}"}; do
    if [[ "$name" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
        alt_names+=("IP:${name}")
    else
        alt_names+=("DNS:${name}")
    fi
done

primary_ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
if [ -n "$primary_ip" ] && ! printf '%s\n' "${alt_names[@]}" | grep -qx "IP:${primary_ip}"; then
    alt_names+=("IP:${primary_ip}")
fi
# localhost, so a check run on the box itself does not have to disable verification.
printf '%s\n' "${alt_names[@]}" | grep -qx 'DNS:localhost' || alt_names+=("DNS:localhost")
printf '%s\n' "${alt_names[@]}" | grep -qx 'IP:127.0.0.1' || alt_names+=("IP:127.0.0.1")

san="$(IFS=,; echo "${alt_names[*]}")"
echo "==> certificate will cover: ${san}"

# --- reissue only when needed ------------------------------------------------

needs_issue=1
if [ -s "$server_crt" ] && [ -s "$server_key" ]; then
    if ! openssl x509 -in "$server_crt" -noout -checkend $((30 * 24 * 3600)) >/dev/null 2>&1; then
        echo "==> the existing certificate expires within 30 days; reissuing"
    elif ! openssl x509 -in "$server_crt" -noout -ext subjectAltName 2>/dev/null \
            | tr -d ' ' | grep -qF "$(echo "$san" | tr -d ' ')"; then
        echo "==> the existing certificate does not cover those names; reissuing"
    else
        echo "==> the existing certificate is current and covers every name"
        needs_issue=0
    fi
fi

if [ "$needs_issue" -eq 1 ]; then
    echo "==> issuing a server certificate (2 years)"
    tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' EXIT

    openssl req -newkey rsa:2048 -sha256 -nodes \
        -keyout "$tmp/server.key" -out "$tmp/server.csr" \
        -subj "/CN=${hostname}/O=Endpoint Platform" 2>/dev/null

    # A leaf certificate, explicitly not a CA, restricted to server authentication.
    cat > "$tmp/ext.cnf" <<EOF
basicConstraints = critical,CA:FALSE
keyUsage = critical,digitalSignature,keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = ${san}
EOF

    openssl x509 -req -in "$tmp/server.csr" -CA "$ca_crt" -CAkey "$ca_key" \
        -CAcreateserial -out "$tmp/server.crt" -days 730 -sha256 \
        -extfile "$tmp/ext.cnf" 2>/dev/null

    install -m 0600 -o root -g root "$tmp/server.key" "$server_key"
    install -m 0644 -o root -g root "$tmp/server.crt" "$server_crt"
    rm -rf "$tmp"
    trap - EXIT
fi

# --- a copy of the root where it can be fetched ------------------------------
#
# Served over plain HTTP from the ACME location nginx already exposes, so an
# administrator can download it onto a Windows PC before that PC trusts anything.
# It is a public certificate; publishing it grants nobody anything.

acme_root=/var/www/html
install -d -m 0755 "$acme_root"
install -m 0644 "$ca_crt" "${acme_root}/endpoint-platform-ca.crt"

echo
echo "==> certificate"
openssl x509 -in "$server_crt" -noout -subject -enddate -ext subjectAltName | sed 's/^/    /'
echo
cat <<EOF
================================================================================
  TLS is ready, but nothing trusts it yet.

  Install the CA root on EVERY machine that will use this platform - both to
  browse the dashboard without a warning and, more importantly, because a
  Release build of the Windows agent REFUSES an untrusted certificate and has
  no override.

  On each Windows PC, in an ELEVATED PowerShell:

      curl.exe -o \$env:TEMP\\epp-ca.crt http://${hostname}/endpoint-platform-ca.crt
      Import-Certificate -FilePath \$env:TEMP\\epp-ca.crt \`
          -CertStoreLocation Cert:\\LocalMachine\\Root

  (Cert:\\LocalMachine\\Root, not CurrentUser: the agent runs as LocalSystem and
  reads the machine store, not any user's.)

  Across a domain, push it once by Group Policy instead:
  Computer Configuration > Policies > Windows Settings > Security Settings >
  Public Key Policies > Trusted Root Certification Authorities.

  The root is also on this machine at:
      ${ca_crt}
================================================================================
EOF
