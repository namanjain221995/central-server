#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Configures the local Redis for the platform.
#
#   sudo bash infra/ubuntu/setup-redis.sh
#
# Writes /etc/redis/endpoint-platform.conf and includes it from the END of
# /etc/redis/redis.conf, so these directives win over the packaged defaults
# without editing them:
#
#   bind 127.0.0.1 -::1     loopback only. Redis holds the in-flight sealed
#                           account secrets; it must never face the network.
#   requirepass             password-protected even on loopback. An
#                           unauthenticated Redis is a well-known foothold for
#                           anything else that lands on the host.
#   appendonly no, save ""  no persistence. Redis is cache and transient state
#                           only; nothing that must survive a restart lives here.
#   maxmemory 256mb, allkeys-lru
#
# Idempotent. Re-running it re-applies REDIS_PASSWORD from secrets.env, which is
# how a rotated password is pushed into the server.
# ---------------------------------------------------------------------------
set -euo pipefail
umask 077

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=infra/ubuntu/common.sh
. "${script_dir}/common.sh"

require_root "$@"

main_conf=/etc/redis/redis.conf
our_conf=/etc/redis/endpoint-platform.conf
include_line="include ${our_conf}"

[ -f "$EPP_SECRETS_FILE" ] || { echo "${EPP_SECRETS_FILE} is missing; run gen-env.sh first" >&2; exit 1; }
[ -f "$main_conf" ] || { echo "${main_conf} is missing; run host-prep.sh first" >&2; exit 1; }

load_env_file "$EPP_SECRETS_FILE"
[ -n "${REDIS_PASSWORD:-}" ] || { echo "REDIS_PASSWORD is not set in ${EPP_SECRETS_FILE}" >&2; exit 1; }

echo "==> writing ${our_conf}"
tmp="${our_conf}.tmp.$$"
trap 'rm -f "$tmp"' EXIT
cat > "$tmp" <<EOF
# Managed by infra/ubuntu/setup-redis.sh. Included from the end of redis.conf.
bind 127.0.0.1 -::1
protected-mode yes
requirepass ${REDIS_PASSWORD}
appendonly no
save ""
maxmemory 256mb
maxmemory-policy allkeys-lru
EOF
chown root:redis "$tmp"
chmod 640 "$tmp"
mv "$tmp" "$our_conf"
trap - EXIT

if ! grep -qxF "$include_line" "$main_conf"; then
    echo "==> including it from ${main_conf}"
    printf '\n# endpoint platform overrides (infra/ubuntu/setup-redis.sh)\n%s\n' "$include_line" >> "$main_conf"
fi

systemctl restart redis-server

# REDISCLI_AUTH keeps the password out of the process list.
echo "==> verifying an authenticated PING"
pong=""
for _ in 1 2 3 4 5 6 7 8 9 10; do
    pong="$(REDISCLI_AUTH="$REDIS_PASSWORD" redis-cli -h 127.0.0.1 -p 6379 ping 2>/dev/null || true)"
    [ "$pong" = "PONG" ] && break
    sleep 1
done
if [ "$pong" != "PONG" ]; then
    echo "Redis did not answer an authenticated PING; see: journalctl -u redis-server -n 50" >&2
    exit 1
fi

# And that the password is actually enforced.
if [ "$(redis-cli -h 127.0.0.1 -p 6379 ping 2>/dev/null || true)" = "PONG" ]; then
    echo "Redis answered PING without a password; requirepass did not take effect." >&2
    exit 1
fi

echo
echo "setup-redis.sh: done"
