#!/bin/sh
# ---------------------------------------------------------------------------
# Picks up renewed certificates without anyone restarting anything.
#
# nginx reads certificate files once, at start or reload. The certbot service
# renews into a shared volume, but it cannot signal this container - that would
# mean handing it the Docker socket, which is root on the host. So this
# container reloads itself every six hours instead.
#
# Six hours is irrelevant against a certificate renewed thirty days before it
# expires, and a reload is graceful: in-flight connections finish on the old
# workers. The first reload waits the full interval, so it can never race nginx
# starting up.
#
# Run by the nginx image's entrypoint, which executes /docker-entrypoint.d/*.sh
# before starting nginx. Backgrounded, so the entrypoint carries on.
# ---------------------------------------------------------------------------
(
    while sleep 21600; do
        nginx -s reload 2>/dev/null || true
    done
) &
