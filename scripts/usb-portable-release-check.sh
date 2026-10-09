#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Staging verification for the USB portable-device release (agent 1.16.0,
# migration 20261009153245_UsbPortableDevices). Runs ON the server, in
# deploy/docker, as a user that can run docker. Prints evidence; changes state
# only in the subcommands marked MUTATING. Never for production.
#
#   usb-portable-release-check.sh identify            facts about this host; refuses a fleet-sized database
#   usb-portable-release-check.sh keep previous|new   tag the running :$IMAGE_TAG images as :previous / :new
#   usb-portable-release-check.sh verify <label>      migration, schema, health, routing, error scan
#   usb-portable-release-check.sh agent enroll        MUTATING: a simulated agent (SQL-issued token, real enrol)
#   usb-portable-release-check.sh agent 115|116|rows  USB reports shaped as agent 1.15.0 / 1.16.0, and the rows
#   usb-portable-release-check.sh rollback-test       MUTATING: previous images, the PortableDevice failure,
#                                        the compensating update, then forward again
#
# Environment (defaults suit the staging host):
#   PROJECT=endpoint-platform  IMAGE_TAG=local  ORIGIN=<PUBLIC_ORIGIN from .env>
#   REDEPLOY="sudo ./deploy.sh '' --no-build"   how to restart without building
#   MAX_ACTIVE_DEVICES=5   identify refuses above this (a fleet means production)
#   STATE_DIR=$HOME/.epp-staging-check
# ---------------------------------------------------------------------------
set -uo pipefail

PROJECT="${PROJECT:-endpoint-platform}"
IMAGE_TAG="${IMAGE_TAG:-local}"
REDEPLOY="${REDEPLOY:-sudo ./deploy.sh '' --no-build}"
MAX_ACTIVE_DEVICES="${MAX_ACTIVE_DEVICES:-5}"
STATE_DIR="${STATE_DIR:-$HOME/.epp-staging-check}"
IMAGES="migrations admin-api agent-api web"
MIGRATION=20261009153245_UsbPortableDevices
COMPENSATE="UPDATE endpoint_platform.usb_devices SET device_class = 'Unknown' WHERE device_class = 'PortableDevice'"

[ -f .env ] || { echo "run from deploy/docker: no .env here" >&2; exit 1; }
DB="$(grep -E '^POSTGRES_DB=' .env | tail -n 1 | cut -d= -f2-)"
ORIGIN="${ORIGIN:-$(grep -E '^PUBLIC_ORIGIN=' .env | tail -n 1 | cut -d= -f2-)}"
mkdir -p "$STATE_DIR" && chmod 700 "$STATE_DIR"

ts() { date -u +%Y-%m-%dT%H:%M:%SZ; }
say() { echo "$(ts) $*"; }
pass() { echo "$(ts) PASS  $*"; }
fail() { echo "$(ts) FAIL  $*"; FAILED=1; }
FAILED=0

container() { echo "${PROJECT}-$1-1"; }
sql() { docker exec -i "$(container postgres)" psql -U postgres -d "$DB" -At -v ON_ERROR_STOP=1 -c "$1"; }

# Probe the origin on THIS machine whatever DNS says, as deploy.sh does.
origin_hostport="${ORIGIN#https://}"; origin_hostport="${origin_hostport%%/*}"
origin_host="${origin_hostport%%:*}"
origin_port="${origin_hostport#*:}"; [ "$origin_port" = "$origin_hostport" ] && origin_port=443
http() { # http <method> <path> [curl args...] -> prints "<code> <body>"
    local method="$1" path="$2"; shift 2
    curl -sk --max-time 20 --resolve "${origin_host}:${origin_port}:127.0.0.1" \
        -X "$method" -w '\n%{http_code}' "$@" "${ORIGIN}${path}" | { body="$(cat)"; echo "$(tail -n1 <<<"$body") $(head -n -1 <<<"$body" | tr '\n' ' ' | cut -c1-220)"; }
}

wait_healthy() {
    local deadline=$(( $(date +%s) + 300 )) a g w
    while :; do
        a="$(docker inspect -f '{{.State.Health.Status}}' "$(container admin-api)" 2>/dev/null || echo missing)"
        g="$(docker inspect -f '{{.State.Health.Status}}' "$(container agent-api)" 2>/dev/null || echo missing)"
        w="$(docker inspect -f '{{.State.Health.Status}}' "$(container web)" 2>/dev/null || echo missing)"
        [ "$a" = healthy ] && [ "$g" = healthy ] && [ "$w" = healthy ] && { say "healthy: admin-api, agent-api, web"; return 0; }
        [ "$(date +%s)" -ge "$deadline" ] && { say "not healthy after 300 s: admin=$a agent=$g web=$w"; return 1; }
        sleep 3
    done
}

image_ids() {
    for img in $IMAGES; do
        printf '%s  %-11s running=%s  :%s=%s  :previous=%s  :new=%s\n' "$(ts)" "$img" \
            "$(docker inspect -f '{{.Image}}' "$(container "$img")" 2>/dev/null | cut -c8-19)" "$IMAGE_TAG" \
            "$(docker image inspect -f '{{.Id}}' "endpoint-platform/$img:$IMAGE_TAG" 2>/dev/null | cut -c8-19)" \
            "$(docker image inspect -f '{{.Id}}' "endpoint-platform/$img:previous" 2>/dev/null | cut -c8-19)" \
            "$(docker image inspect -f '{{.Id}}' "endpoint-platform/$img:new" 2>/dev/null | cut -c8-19)"
    done
}

identify() {
    say "host $(hostname)  origin ${ORIGIN}  project ${PROJECT}  source $(git -C ../.. rev-parse --short HEAD 2>/dev/null || echo '(no git)')"
    image_ids
    local total active versions
    total="$(sql "SELECT count(*) FROM endpoint_platform.devices")"
    active="$(sql "SELECT count(*) FROM endpoint_platform.devices WHERE status = 'Active' AND last_seen_at > now() - interval '7 days'")"
    versions="$(sql "SELECT coalesce(string_agg(agent_version || ' x' || n, ', '), 'none') FROM (SELECT agent_version, count(*) n FROM endpoint_platform.devices GROUP BY 1 ORDER BY 1) v")"
    say "devices: ${total} total, ${active} active in the last 7 days; agent versions: ${versions}"
    if [ "${active:-0}" -gt "$MAX_ACTIVE_DEVICES" ]; then
        fail "${active} active devices: this looks like a fleet, not staging. Refusing (MAX_ACTIVE_DEVICES=${MAX_ACTIVE_DEVICES})."
        return 3
    fi
    pass "device count is staging-sized; the operator must still confirm this host is not production"
}

keep() {
    local as="$1"
    for img in $IMAGES; do
        docker tag "endpoint-platform/$img:$IMAGE_TAG" "endpoint-platform/$img:$as" \
            && say "tagged endpoint-platform/$img:$as = $(docker image inspect -f '{{.Id}}' "endpoint-platform/$img:$as" | cut -c8-19)"
    done
}

verify() {
    local label="$1" code body ready since
    say "==== verify: ${label}"
    image_ids
    code="$(docker inspect -f '{{.State.ExitCode}}' "$(container migrations)" 2>/dev/null)"
    [ "$code" = 0 ] && pass "migration job exited 0" || fail "migration job exit code ${code:-missing}"
    docker logs "$(container migrations)" 2>&1 | grep -E "Applying migration '|up to date|completed successfully" | tail -3 | sed "s/^/$(ts)       /"
    [ "$(sql "SELECT count(*) FROM endpoint_platform.__ef_migrations_history WHERE \"MigrationId\" = '${MIGRATION}'")" = 1 ] \
        && pass "${MIGRATION} recorded in the migration history" || fail "${MIGRATION} not in the migration history"
    [ "$(sql "SELECT is_nullable FROM information_schema.columns WHERE table_schema='endpoint_platform' AND table_name='usb_devices' AND column_name='enforcement_status'")" = YES ] \
        && pass "usb_devices.enforcement_status present, nullable" || fail "usb_devices.enforcement_status missing"
    ready="$(docker exec "$(container admin-api)" curl -s http://localhost:5080/health/ready)"
    grep -q '"status":"Healthy"' <<<"$ready" && grep -q '"name":"postgres","status":"Healthy"' <<<"$ready" && grep -q '"name":"redis","status":"Healthy"' <<<"$ready" \
        && pass "admin-api /health/ready: postgres and redis healthy" || fail "admin-api /health/ready: ${ready}"
    for check in "GET / 200" "GET /healthz 200" "GET /api/health/ready 200" "POST /agent/v1/heartbeat 400|401|405" "POST /api/admin/v1/auth/login 400|401"; do
        set -- $check
        read -r code body <<<"$(http "$1" "$2" -H 'Content-Type: application/json' -H 'X-Requested-With: XMLHttpRequest' -H 'X-Agent-Protocol-Version: 1' --data '{}')"
        [ "$1" = GET ] && read -r code body <<<"$(http GET "$2")"
        [[ "$code" =~ ^($3)$ ]] && pass "web $1 $2 -> ${code}" || fail "web $1 $2 -> ${code} (expected $3) ${body}"
    done
    body="$(curl -sk --max-time 20 --resolve "${origin_host}:${origin_port}:127.0.0.1" "${ORIGIN}/")"
    grep -q '<div id="root"' <<<"$body" && grep -q '<title>Endpoint Platform</title>' <<<"$body" \
        && pass "dashboard index served (title and app root present)" || fail "dashboard index not recognised"
    local asset
    asset="$(grep -o -E '/assets/index-[A-Za-z0-9_-]+\.js' <<<"$body" | head -n 1)"
    if [ -n "$asset" ]; then
        read -r code body <<<"$(http GET "$asset")"
        [ "$code" = 200 ] && pass "dashboard bundle ${asset} -> 200" || fail "dashboard bundle ${asset} -> ${code}"
    else
        fail "dashboard index names no bundle"
    fi
    since="$(docker inspect -f '{{.State.StartedAt}}' "$(container agent-api)")"
    local errors
    errors="$(docker logs --since "$since" "$(container agent-api)" 2>&1 | grep -c -E 'Cannot convert string value|responded 5[0-9][0-9]')"
    say "agent-api 5xx / conversion errors since it started: ${errors}"
    say "devices reporting since agent-api started: $(sql "SELECT count(*) FROM endpoint_platform.devices WHERE last_seen_at > '${since}'"); USB rows updated: $(sql "SELECT count(*) FROM endpoint_platform.usb_devices WHERE updated_at > '${since}'")"
}

agent() {
    local cred_file="$STATE_DIR/credential" phone='USB\\VID_2717&PID_FF40\\STAGINGSIM01' stick='USB\\VID_0781&PID_5581\\STAGINGSIM02'
    post() { # post <route> <json> [credential]
        local args=(-H 'Content-Type: application/json' -H 'X-Agent-Protocol-Version: 1' -H 'X-Agent-Version: staging-check' --data "$2")
        [ -n "${3:-}" ] && args+=(-H "X-Agent-Credential: $3")
        http POST "/agent/v1$1" "${args[@]}"
    }
    case "$1" in
        enroll)
            say "MUTATING: one enrolment token inserted for a simulated agent named STAGING-SIM"
            local secret hash org out
            secret="$(openssl rand -hex 32)"; hash="$(printf '%s' "$secret" | sha256sum | cut -d' ' -f1)"
            org="$(sql "SELECT id FROM endpoint_platform.organizations ORDER BY created_at LIMIT 1")"
            sql "INSERT INTO endpoint_platform.enrollment_tokens (id, organization_id, name, secret_hash, created_by_user_id, created_by_display, expires_at, max_uses, use_count, created_at, updated_at)
                 VALUES (gen_random_uuid(), '$org', 'staging-check simulated agent', '$hash', gen_random_uuid(), 'staging-check', now() + interval '15 minutes', 1, 0, now(), now())" >/dev/null
            out="$(post /enroll "{\"enrollmentToken\":\"$secret\",\"hostname\":\"STAGING-SIM\",\"machineIdentifier\":\"staging-sim-$(openssl rand -hex 8)\",\"agentVersion\":\"1.15.0\",\"operatingSystem\":\"Windows 11 Pro\"}")"
            local key sec dev
            key="$(sed -n 's/.*"credentialKeyId":"\([^"]*\)".*/\1/p' <<<"$out")"; sec="$(sed -n 's/.*"credentialSecret":"\([^"]*\)".*/\1/p' <<<"$out")"; dev="$(sed -n 's/.*"deviceId":"\([^"]*\)".*/\1/p' <<<"$out")"
            if [ -n "$key" ] && [ -n "$sec" ]; then
                umask 077; printf '%s.%s\n%s\n' "$key" "$sec" "$dev" > "$cred_file"
                pass "simulated agent enrolled as device ${dev}"
            else
                fail "enrol: ${out%% *}"
            fi
            ;;
        115|116)
            local cred status_field="" class=Other enforced=null code body expect="${2:-200}"
            cred="$(sed -n 1p "$cred_file")"
            if [ "$1" = 116 ]; then status_field=',"enforcementStatus":"Verified"'; class=PortableDevice; enforced='"Restricted"'; fi
            read -r code body <<<"$(post /usb "{\"devices\":[
              {\"instanceId\":\"$phone\",\"deviceClass\":\"$class\",\"vendorId\":\"2717\",\"productId\":\"FF40\",\"serialNumber\":\"STAGINGSIM01\",\"manufacturer\":\"Xiaomi\",\"product\":\"Phone\",\"hardwareIds\":\"USB\\\\VID_2717&PID_FF40\",\"isConnected\":true,\"enforcedPolicy\":$enforced,\"enforcementError\":null$status_field},
              {\"instanceId\":\"$stick\",\"deviceClass\":\"Storage\",\"vendorId\":\"0781\",\"productId\":\"5581\",\"serialNumber\":\"STAGINGSIM02\",\"manufacturer\":\"SanDisk\",\"product\":\"Cruzer\",\"hardwareIds\":\"USB\\\\VID_0781&PID_5581\",\"isConnected\":true,\"enforcedPolicy\":\"Restricted\",\"enforcementError\":null$status_field}],
              \"collectedAt\":\"$(ts)\"}" "$cred")"
            local shape="agent 1.${1:1:2}.0"
            [[ "$code" =~ ^($expect)$ ]] && pass "USB report shaped as ${shape} -> ${code}" || fail "USB report shaped as ${shape} -> ${code} (expected ${expect}) ${body}"
            ;;
        rows)
            local dev; dev="$(sed -n 2p "$cred_file")"
            say "rows: $(sql "SELECT string_agg(instance_id || ' class=' || device_class || ' policy=' || policy || ' enforced=' || coalesce(enforced_policy,'null') || ' status=' || coalesce(to_jsonb(u)->>'enforcement_status','null'), ' | ' ORDER BY instance_id) FROM endpoint_platform.usb_devices u WHERE device_id = '$dev'")"
            ;;
    esac
}

redeploy() {
    say "MUTATING: ${REDEPLOY}"
    eval "$REDEPLOY" 2>&1 | tail -4 | sed "s/^/$(ts)       /"
    wait_healthy
}

retag() { # retag <from>
    for img in $IMAGES; do
        docker tag "endpoint-platform/$img:$1" "endpoint-platform/$img:$IMAGE_TAG" \
            && say "endpoint-platform/$img:$IMAGE_TAG <- :$1 = $(docker image inspect -f '{{.Id}}' "endpoint-platform/$img:$IMAGE_TAG" | cut -c8-19)"
    done
}

rollback_test() {
    for img in $IMAGES; do
        docker image inspect "endpoint-platform/$img:previous" >/dev/null 2>&1 || { fail "no endpoint-platform/$img:previous image; run 'keep previous' before deploying the release"; return 1; }
    done
    keep new
    agent 116; agent rows
    say "==== rollback to the previous images"
    retag previous; redeploy
    verify "previous images, before the compensating update"
    local portable; portable="$(sql "SELECT count(*) FROM endpoint_platform.usb_devices WHERE device_class = 'PortableDevice'")"
    say "rows labelled PortableDevice: ${portable}"
    [ "$portable" -gt 0 ] && agent 116 500
    say "==== MUTATING: compensating update"
    sql "$COMPENSATE" | sed "s/^/$(ts)       /"
    [ "$(sql "SELECT count(*) FROM endpoint_platform.usb_devices WHERE device_class = 'PortableDevice'")" = 0 ] \
        && pass "no PortableDevice rows remain" || fail "PortableDevice rows remain"
    agent 116; agent 115; agent rows
    verify "previous images, after the compensating update"
    say "==== forward again to the release images"
    retag new; redeploy
    verify "release images after the rollback test"
    agent 116; agent rows
    [ "$(sql "SELECT count(*) FROM endpoint_platform.usb_devices WHERE device_class = 'PortableDevice'")" -ge 1 ] \
        && pass "the next 1.16.0 report restored the PortableDevice class" || fail "PortableDevice class not restored"
}

# Anything that writes refuses to run unless identify passes: a fleet-sized
# database is production, and nothing here may touch production.
guard() { identify >/dev/null || { identify; echo "$(ts) refusing: not a staging-sized database" >&2; exit 3; }; }

case "${1:-}" in
    identify) identify ;;
    keep) guard; keep "${2:?previous or new}" ;;
    verify) verify "${2:-manual}" ;;
    agent) [ "${2:-}" = rows ] || guard; agent "${2:?enroll, 115, 116 or rows}" "${3:-}" ;;
    rollback-test) guard; rollback_test ;;
    *) sed -n '2,24p' "$0"; exit 1 ;;
esac
say "result: $([ "$FAILED" = 0 ] && echo 'no failures' || echo 'FAILURES above')"
exit "$FAILED"
