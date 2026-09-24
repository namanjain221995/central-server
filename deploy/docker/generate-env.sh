#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Generates this deployment's secrets ONCE, plus the TLS material and the
# pgAdmin connection file that depend on them.
#
#   ./generate-env.sh https://192.168.8.96
#   ./generate-env.sh https://epp.example.com
#
# Writes, all git-ignored, all next to this script:
#
#   .env                    every secret; compose reads it automatically
#   tls/server.{crt,key}    the certificate `web` serves
#   tls/ca.{crt,key}        the local CA that signed it
#   pgadmin/servers.json    pre-registered connection to the platform database
#   pgadmin/pgpass          that connection's password, 0600, uid 5050
#
# An existing .env is NEVER regenerated. RECOVERY_ESCROW_KEY seals every escrowed
# BitLocker recovery password at rest, RECOVERY_SEALING_PRIVATE_KEY opens every
# automatically escrowed one, and MFA_TOTP_KEY seals every authenticator
# enrolment. New values would make all of them unreadable at once, and nobody
# would notice until a machine refused to boot or every administrator was
# locked out. PUBLIC_ORIGIN is the one value this script will update in place;
# a value that is missing altogether (an .env written by an older version of
# this script) is added, never replaced.
#
# BACKUP-CRITICAL: .env is the complete secret set for this deployment. Back it
# up together with every database dump.
#
# Nothing is ever printed except variable NAMES.
# ---------------------------------------------------------------------------
set -euo pipefail
umask 077

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
env_file="${here}/.env"
tls_dir="${here}/tls"
pgadmin_dir="${here}/pgadmin"

origin="${1:-}"
if [ -z "$origin" ]; then
    echo "usage: generate-env.sh https://<host-or-ip>" >&2
    exit 1
fi
# https:// only, no path, no trailing slash: the value is compared against the
# browser's Origin header verbatim, and the session cookie is __Host-/Secure, so
# an http:// origin would produce a platform nobody can sign in to.
if ! [[ "$origin" =~ ^https://[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?(:[0-9]{1,5})?$ ]]; then
    echo "PUBLIC_ORIGIN must be https://<host> with no path and no trailing slash; got: ${origin}" >&2
    exit 1
fi

host_with_port="${origin#https://}"
host="${host_with_port%%:*}"

# Passwords: [A-Za-z0-9] only. They travel through an Npgsql connection string,
# a Redis connection string ("host:6379,password=VALUE"), a compose .env file
# and a pgpass line, and each of those has its own opinion about punctuation.
gen_password() {
    local raw
    raw="$(openssl rand -base64 48 | tr -d '+/=\n')"
    if [ "${#raw}" -lt 32 ]; then
        echo "password generation produced too few characters" >&2
        return 1
    fi
    printf '%s' "${raw:0:32}"
}

# Keys: the services require base64 of EXACTLY 32 bytes; anything else is
# rejected at startup. The padding is part of the value, so nothing is stripped.
gen_key() {
    local key
    key="$(openssl rand -base64 32)"
    if [ "${#key}" -ne 44 ]; then
        echo "key generation produced an unexpected length" >&2
        return 1
    fi
    printf '%s' "$key"
}

# The automatic BitLocker escrow sealing pair: RSA-3072, private half as PKCS#8
# DER, public half as SPKI DER, both base64 - exactly what the Admin API imports.
# Written to FILES in a private temp dir and never into a shell variable, per
# docs/runbooks/escrow-sealing-key.md: the two real provisioning failures were a
# PKCS#1 key (genpkey -outform DER is not reliably PKCS#8, hence the explicit
# pkcs8 step) and a base64 value corrupted by an unquoted printf format.
#
# Prints the key size and the public fingerprint. Never the keys.
gen_sealing_pair() { # gen_sealing_pair <dir>
    local d="$1"
    openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -outform DER -out "$d/raw.der" 2>/dev/null
    openssl pkcs8 -topk8 -nocrypt -inform DER -outform DER -in "$d/raw.der" -out "$d/private.der"
    # Derived from the private half, so the two cannot disagree.
    openssl pkey -inform DER -in "$d/private.der" -pubout -outform DER -out "$d/public.der"
    base64 -w0 "$d/private.der" > "$d/private.b64"
    base64 -w0 "$d/public.der"  > "$d/public.b64"
    rm -f "$d/raw.der" "$d/private.der" "$d/public.der"
    verify_sealing_pair "$d/private.b64" "$d/public.b64"
}

# The same checks the Admin API makes at startup, run BEFORE anything is written
# to .env, so a bad pair is caught here rather than as a crash loop.
verify_sealing_pair() { # verify_sealing_pair <private.b64> <public.b64>
    local priv="$1" pub="$2" f bits a b
    for f in "$priv" "$pub"; do
        # base64 length is a multiple of 4; anything else is corrupt however
        # plausible it looks. This is the check that catches a stray character.
        if [ $(( $(wc -c < "$f") % 4 )) -ne 0 ]; then
            echo "sealing key in ${f##*/} is not valid base64" >&2
            return 1
        fi
    done
    # Must parse as PKCS#8 - this is the check that catches a PKCS#1 key.
    base64 -d "$priv" | openssl pkey -inform DER -noout 2>/dev/null \
        || { echo "the sealing private key is not PKCS#8 DER" >&2; return 1; }
    bits="$(base64 -d "$priv" | openssl pkey -inform DER -noout -text 2>/dev/null | sed -n '1s/.*(\([0-9]*\) bit.*/\1/p')"
    if [ "${bits:-0}" -lt 3072 ]; then
        echo "the sealing private key is ${bits:-?} bits; 3072 are required" >&2
        return 1
    fi
    # The halves must be the same key. A mismatch means every escrow succeeds and
    # none can ever be opened - found on the day a disk will not boot.
    a="$(base64 -d "$priv" | openssl pkey -inform DER -pubout -outform DER 2>/dev/null | openssl dgst -sha256 -hex | awk '{print $NF}')"
    b="$(base64 -d "$pub" | openssl dgst -sha256 -hex | awk '{print $NF}')"
    if [ -z "$a" ] || [ "$a" != "$b" ]; then
        echo "the sealing key halves do not match" >&2
        return 1
    fi
    echo "    sealing pair verified: RSA-${bits}, SPKI SHA-256 ${a}"
}

# --- 1. secrets, once --------------------------------------------------------

if [ -f "$env_file" ]; then
    echo "==> ${env_file} already exists, keeping every secret in it"

    current_origin="$(grep -E '^PUBLIC_ORIGIN=' "$env_file" | tail -n 1 | cut -d= -f2- || true)"
    if [ "$current_origin" != "$origin" ]; then
        echo "==> PUBLIC_ORIGIN changes: ${current_origin:-<unset>} -> ${origin}"
        tmp="${env_file}.tmp.$$"
        grep -vE '^(PUBLIC_ORIGIN|SERVER_NAME)=' "$env_file" > "$tmp" || true
        printf 'PUBLIC_ORIGIN=%s\nSERVER_NAME=%s\n' "$origin" "$host" >> "$tmp"
        chmod 600 "$tmp"
        mv "$tmp" "$env_file"
    fi
else
    tmp="${env_file}.tmp.$$"
    trap 'rm -f "$tmp"' EXIT

    cat > "$tmp" <<EOF
# ---------------------------------------------------------------------------
# endpoint platform: deployment secrets for the container stack.
#
# Generated by deploy/docker/generate-env.sh on $(date -u +%Y-%m-%dT%H:%M:%SZ).
# 0600, never printed, never committed.
#
# BACKUP-CRITICAL. This is the complete secret set for this deployment:
#   - the PostgreSQL passwords the database roles were created with
#   - REDIS_PASSWORD
#   - SECRET_PROTECTION_KEY  (in-flight account secrets; rotation is safe)
#   - RECOVERY_ESCROW_KEY    (escrowed BitLocker passwords; LOSS IS PERMANENT)
#   - RECOVERY_SEALING_PRIVATE_KEY (automatic escrow; LOSS IS PERMANENT)
#   - MFA_TOTP_KEY           (authenticator secrets; LOSS LOCKS EVERY ADMIN OUT)
# Back it up alongside every database dump. generate-env.sh never regenerates it.
# ---------------------------------------------------------------------------

# --- PostgreSQL (no published port; compose network only) --------------------
POSTGRES_DB=endpoint_platform
# Cluster superuser inside the container. Creates the two roles below on first
# initialisation; the application never connects as this role.
POSTGRES_ADMIN_PASSWORD=$(gen_password)
# Owner role: owns the schema and holds DDL rights. Used ONLY by the migration
# job. Despite the historical variable name it is NOT a cluster superuser.
POSTGRES_SUPERUSER=endpoint_owner
POSTGRES_SUPERUSER_PASSWORD=$(gen_password)
# Restricted runtime role used by both APIs: no DDL, INSERT/SELECT only on the
# audit table.
POSTGRES_APP_USER=endpoint_app
POSTGRES_APP_PASSWORD=$(gen_password)

# --- Redis (no published port; compose network only) -------------------------
REDIS_PASSWORD=$(gen_password)

# --- Secret protection (both APIs, identical value) --------------------------
# base64 of 32 random bytes. Rotating only invalidates in-flight secrets.
SECRET_PROTECTION_KEY=$(gen_key)

# --- BitLocker recovery-password escrow (Admin API only) ---------------------
# base64 of 32 random bytes. Rotation does NOT re-seal existing rows: bump the
# version and re-seal before retiring the old key.
RECOVERY_ESCROW_KEY=$(gen_key)
RECOVERY_ESCROW_KEY_VERSION=1

# --- Multi-factor authentication (Admin API only) ----------------------------
# base64 of 32 random bytes, sealing the TOTP secret behind each administrator's
# authenticator app. LOSS IS NOT RECOVERABLE BY RE-ENROLLING ONE PERSON: every
# enrolment becomes unreadable at once. Never given to the Agent API.
MFA_TOTP_KEY=$(gen_key)

# --- Agent release trust mode ------------------------------------------------
# Internal: integrity is the server-computed SHA-256 over HTTPS, no
# Authenticode signature required.
AGENT_RELEASE_TRUST_MODE=Internal

# --- pgAdmin -----------------------------------------------------------------
PGADMIN_EMAIL=admin@endpoint.local
PGADMIN_PASSWORD=$(gen_password)
PGADMIN_PORT=5050
# pgAdmin's per-user storage directory: the login address with '@' as '_'. The
# pass file has to live in there, because that is the only place pgAdmin looks.
PGADMIN_STORAGE_DIR=admin_endpoint.local

# --- Published ports ---------------------------------------------------------
HTTP_PORT=80
HTTPS_PORT=443

# --- Image tagging -----------------------------------------------------------
IMAGE_TAG=local
IMAGE_VERSION=0.1.0

# --- Public origin -----------------------------------------------------------
# The exact https:// origin browsers and agents use. CORS allow-list; the Admin
# API refuses to start without it. SERVER_NAME is the same host for nginx.
PUBLIC_ORIGIN=${origin}
SERVER_NAME=${host}
EOF

    chmod 600 "$tmp"
    mv "$tmp" "$env_file"
    trap - EXIT

    echo "==> wrote ${env_file} (0600) with:"
    grep -oE '^[A-Z_]+=' "$env_file" | tr -d '=' | sed 's/^/      /'
    echo "    back this file up: it holds the escrow keys, the MFA key and the database passwords"
fi

chmod 600 "$env_file"

# Upgrade path, for an .env written before the pass file was put in pgAdmin's
# storage directory. Deriving it is safe and destroys nothing; an existing value
# is never touched.
if ! grep -qE '^PGADMIN_STORAGE_DIR=' "$env_file"; then
    pgadmin_email="$(grep -E '^PGADMIN_EMAIL=' "$env_file" | tail -n 1 | cut -d= -f2-)"
    {
        echo ""
        echo "# pgAdmin's per-user storage directory: the login address with '@' as '_'."
        echo "PGADMIN_STORAGE_DIR=${pgadmin_email//@/_}"
    } >> "$env_file"
    echo "==> added PGADMIN_STORAGE_DIR to ${env_file}"
fi

# --- Automatic BitLocker escrow sealing pair ---------------------------------
#
# Same rule as PGADMIN_STORAGE_DIR above: an .env that lacks the pair gets one,
# an .env that has it is never touched. Without the pair automatic escrow is
# simply off and every device reads "automatic escrow unavailable -
# re-enrollment required" in the console, which is how the gap was found in
# production. Devices enrolled BEFORE the pair existed carry no pinned
# fingerprint and must re-enrol once it does: docs/runbooks/escrow-sealing-key.md.
#
# Empty placeholders (a hand-edited file may carry them) count as absent and are
# removed first, so the append below never produces a second definition.
sed -i '/^RECOVERY_SEALING_PUBLIC_KEY=$/d;/^RECOVERY_SEALING_PRIVATE_KEY=$/d' "$env_file"

has_sealing_public=0;  grep -qE '^RECOVERY_SEALING_PUBLIC_KEY=.+'  "$env_file" && has_sealing_public=1
has_sealing_private=0; grep -qE '^RECOVERY_SEALING_PRIVATE_KEY=.+' "$env_file" && has_sealing_private=1

if [ "$has_sealing_public" -eq 1 ] && [ "$has_sealing_private" -eq 1 ]; then
    : # provisioned; the Admin API verifies the pair itself at startup
elif [ "$has_sealing_public" -eq 1 ]; then
    # Public without private: endpoints would seal what nobody can open, and the
    # Admin API refuses to start in this state anyway. Nothing can be derived -
    # the private half is either in a backup or gone.
    echo "RECOVERY_SEALING_PUBLIC_KEY is set but RECOVERY_SEALING_PRIVATE_KEY is not, in ${env_file}." >&2
    echo "Restore the private half from backup, or remove the public one to leave automatic escrow off." >&2
    exit 1
else
    sealing_tmp="$(mktemp -d)"
    chmod 700 "$sealing_tmp"
    scrub_sealing_tmp() {
        [ -d "$sealing_tmp" ] || return 0
        shred -u "$sealing_tmp"/* 2>/dev/null || rm -f "$sealing_tmp"/*
        rmdir "$sealing_tmp"
    }
    trap scrub_sealing_tmp EXIT

    if [ "$has_sealing_private" -eq 1 ]; then
        # Private without public: the public half is derivable, so derive it
        # rather than mint a new pair that would strand anything already sealed.
        # tr strips the line terminator: the .b64 files are exact-length, and the
        # base64 length check below would otherwise fail on the newline alone.
        grep -E '^RECOVERY_SEALING_PRIVATE_KEY=' "$env_file" | tail -n 1 | cut -d= -f2- | tr -d '\n' > "$sealing_tmp/private.b64"
        base64 -d "$sealing_tmp/private.b64" | openssl pkey -inform DER -pubout -outform DER 2>/dev/null \
            | base64 -w0 > "$sealing_tmp/public.b64"
        verify_sealing_pair "$sealing_tmp/private.b64" "$sealing_tmp/public.b64"
        {
            echo ""
            printf 'RECOVERY_SEALING_PUBLIC_KEY='; cat "$sealing_tmp/public.b64"; echo
        } >> "$env_file"
        echo "==> derived RECOVERY_SEALING_PUBLIC_KEY from the existing private half"
    else
        echo "==> generating the automatic BitLocker escrow sealing pair (RSA-3072)"
        gen_sealing_pair "$sealing_tmp"
        # From files, by concatenation - never printf with the value as an
        # argument. The runbook records why.
        {
            echo ""
            echo "# --- Automatic BitLocker escrow sealing pair (RSA-3072) ---------------------"
            echo "# Endpoints seal each recovery password to the PUBLIC half (SPKI; both APIs)."
            echo "# Only the PRIVATE half (PKCS#8; Admin API ONLY) can open them. LOSS OF THE"
            echo "# PRIVATE HALF IS PERMANENT. Rotation is a fleet re-enrolment: every device"
            echo "# credential is pinned to this key's fingerprint."
            printf 'RECOVERY_SEALING_PUBLIC_KEY=';  cat "$sealing_tmp/public.b64";  echo
            printf 'RECOVERY_SEALING_PRIVATE_KEY='; cat "$sealing_tmp/private.b64"; echo
        } >> "$env_file"
        echo "==> added RECOVERY_SEALING_PUBLIC_KEY and RECOVERY_SEALING_PRIVATE_KEY to ${env_file}"
        echo "    back it up again; devices enrolled before now must re-enrol to use automatic escrow"
    fi

    scrub_sealing_tmp
    trap - EXIT
fi

chmod 600 "$env_file"

# --- 2. TLS ------------------------------------------------------------------
#
# A local CA signs a certificate for this host. Self-signed is the honest
# option on a private network with no public DNS name: it costs a CA root on
# every machine that talks to the platform, and it is the only way to serve the
# HTTPS origin the __Host- session cookie requires.

mkdir -p "$tls_dir"
# 0755, against the 0077 umask above. nginx reads the private key as root while
# it is still the master process, but it serves /endpoint-platform-ca.crt from a
# WORKER running as an unprivileged user, and that worker has to be able to
# traverse this directory. The keys inside stay 0600.
chmod 755 "$tls_dir"

if [ -f "${tls_dir}/server.crt" ] && [ -f "${tls_dir}/server.key" ]; then
    echo "==> ${tls_dir}/server.crt already exists, keeping it"
    if ! openssl x509 -in "${tls_dir}/server.crt" -noout -checkhost "$host" >/dev/null 2>&1 \
    && ! openssl x509 -in "${tls_dir}/server.crt" -noout -checkip "$host" >/dev/null 2>&1; then
        echo "    WARNING: it does not cover ${host}. Delete ${tls_dir}/ and re-run to reissue."
    fi
else
    echo "==> issuing a certificate for ${host} from a new local CA"

    # An IP address has to appear as IP:, a name as DNS:. Browsers ignore the
    # legacy CN entirely, so getting this wrong means a certificate no client
    # accepts, however loudly it is trusted.
    if [[ "$host" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ ]]; then
        san="IP:${host}"
    else
        san="DNS:${host}"
    fi

    openssl req -x509 -newkey rsa:4096 -sha256 -days 3650 -nodes \
        -keyout "${tls_dir}/ca.key" -out "${tls_dir}/ca.crt" \
        -subj "/CN=Endpoint Platform local CA" \
        -addext "basicConstraints=critical,CA:TRUE,pathlen:0" \
        -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null

    openssl req -newkey rsa:2048 -sha256 -nodes \
        -keyout "${tls_dir}/server.key" -out "${tls_dir}/server.csr" \
        -subj "/CN=${host}" 2>/dev/null

    # Browsers cap the lifetime of a server certificate; 398 days keeps this one
    # inside every current limit.
    openssl x509 -req -in "${tls_dir}/server.csr" -sha256 -days 398 \
        -CA "${tls_dir}/ca.crt" -CAkey "${tls_dir}/ca.key" -CAcreateserial \
        -out "${tls_dir}/server.crt" \
        -extfile <(printf 'subjectAltName=%s\nbasicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n' "$san") \
        2>/dev/null

    rm -f "${tls_dir}/server.csr"

    # Served over plain HTTP at /endpoint-platform-ca.crt, so a machine that does
    # not trust the CA yet can fetch it.
    cp "${tls_dir}/ca.crt" "${tls_dir}/endpoint-platform-ca.crt"

    chmod 600 "${tls_dir}/ca.key" "${tls_dir}/server.key" "${tls_dir}/ca.srl" 2>/dev/null || true
    chmod 600 "${tls_dir}/ca.key" "${tls_dir}/server.key"
    chmod 644 "${tls_dir}/ca.crt" "${tls_dir}/server.crt" "${tls_dir}/endpoint-platform-ca.crt"

    echo "    ${tls_dir}/server.crt   (${san}, 398 days)"
    echo "    ${tls_dir}/ca.crt       install this on every machine that talks to the platform"
fi

# --- 3. pgAdmin connection ---------------------------------------------------
#
# Rendered from .env every run, so a password change here is picked up by
# re-running this script and recreating the container.

# shellcheck disable=SC1090
set -a; . "$env_file"; set +a

mkdir -p "$pgadmin_dir"

cat > "${pgadmin_dir}/servers.json" <<EOF
{
  "Servers": {
    "1": {
      "Name": "endpoint-platform",
      "Group": "Endpoint Platform",
      "Host": "postgres",
      "Port": 5432,
      "MaintenanceDB": "${POSTGRES_DB}",
      "Username": "${POSTGRES_SUPERUSER}",
      "PassFile": "/pgpass",
      "SSLMode": "prefer",
      "Comment": "Rendered by deploy/docker/generate-env.sh. Connects as the schema owner, which is the role that can read every table."
    }
  }
}
EOF
chmod 644 "${pgadmin_dir}/servers.json"

# Rewritten only when the contents actually change. pgAdmin runs as uid 5050 and
# ignores a pass file it does not own, so the ownership given by the first (root)
# run has to survive every later run made as an ordinary user.
pgpass_line="$(printf 'postgres:5432:%s:%s:%s' "$POSTGRES_DB" "$POSTGRES_SUPERUSER" "$POSTGRES_SUPERUSER_PASSWORD")"
if [ ! -f "${pgadmin_dir}/pgpass" ] || [ "$(cat "${pgadmin_dir}/pgpass" 2>/dev/null)" != "$pgpass_line" ]; then
    printf '%s\n' "$pgpass_line" > "${pgadmin_dir}/pgpass"
    chmod 600 "${pgadmin_dir}/pgpass"
    chown 5050:5050 "${pgadmin_dir}/pgpass" 2>/dev/null \
        || echo "    note: could not chown pgadmin/pgpass to 5050 (needs root); pgAdmin will ask for the database password instead"
fi

echo "==> rendered ${pgadmin_dir}/servers.json and ${pgadmin_dir}/pgpass"
echo
echo "generate-env.sh: done"
