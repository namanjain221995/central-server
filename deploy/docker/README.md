# Container deployment

The whole platform on one machine with Docker Compose: PostgreSQL, Redis, the
migration job, the Admin API, the Agent API, the dashboard behind nginx, and
pgAdmin for looking at the database.

This is the only deployment path. A native systemd kit once sat alongside it and was removed; this does not
replace it. That kit installs the same three .NET processes natively under
systemd; this one runs them in containers. Pick one per host — they both want
ports 80 and 443, and they both want to own the database.

| | `infra/ubuntu/` | `deploy/docker/` |
|---|---|---|
| Processes | systemd units | containers |
| PostgreSQL / Redis | installed on the host | containers, no published port |
| TLS | Let's Encrypt, or a local CA | a local CA (self-signed) |
| Upgrade | publish, swap symlink, restart | rebuild images, `compose up -d` |
| Rollback | previous release directory | previous image tag |

## Quick start

```bash
cd deploy/docker
sudo ./deploy.sh https://192.168.8.96      # or https://epp.example.com
sudo ./bootstrap-admin.sh you@example.com --generate
```

`deploy.sh` generates the secrets and the certificate on the first run, builds
the four images, starts everything in dependency order and then **proves it
works** — it exits non-zero unless the dashboard, both APIs and pgAdmin all
answer on the real public URL. Re-running it is the normal way to deploy a
change; it keeps the secrets, the certificate and the database.

`sudo` is needed for exactly one thing: giving `pgadmin/pgpass` to uid 5050 so
pgAdmin can open it. Without it everything still works and pgAdmin asks for the
database password instead.

## What is where

```
generate-env.sh      secrets, TLS, pgAdmin wiring. Idempotent; never regenerates
                     an existing .env
deploy.sh            generate-env + build + up + wait + smoke test
bootstrap-admin.sh   the first Super Administrator
remote-deploy.sh     deploy from a workstation over SSH
docker-compose.yml   the stack
Dockerfile.server    Admin API, Agent API and the migration job, from one build
Dockerfile.dashboard the dashboard build, and the nginx that fronts everything
nginx/               the site template
postgres/init/       runs infra/postgres/setup-database.sql on first start
```

Generated, git-ignored, and **not** to be lost: `.env`, `tls/`,
`pgadmin/pgpass`.

## The parts that are not arbitrary

**HTTPS is mandatory.** The session cookie is `__Host-epadmin`, which browsers
only accept as `Secure` on a single origin. Over plain HTTP the browser
silently discards it and nobody can sign in. On a private LAN with no public
DNS name that means a self-signed certificate, so `generate-env.sh` makes a
local CA and issues one for the host — including the `IP:` SAN when the origin
is an IP address, which is the part everyone forgets.

Install `tls/ca.crt` on every machine that talks to the platform (browsers, and
any endpoint running the agent). It is served for convenience over plain HTTP
at `http://<host>/endpoint-platform-ca.crt`.

**One origin for everything.** Dashboard, `/api/`, `/agent/` and `/pgadmin/` are
all served by the `web` container. Same reason: a dashboard on a different host
name or port than the Admin API cannot hold the session cookie. 5080 and 5081
are never published.

**`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` on both APIs.** TLS terminates in
nginx, so without it the APIs see plain HTTP and `UseHttpsRedirection` bounces
every proxied request. Setting that variable also clears ASP.NET Core's
loopback-only proxy restriction, which is what makes it work when the proxy is
another container rather than a process on localhost.

**nginx reaches its upstreams through variables**, with `resolver 127.0.0.11`.
A literal host name in `proxy_pass` is resolved once, while nginx parses its
config: one upstream that is not up yet then takes the entire entry point down
with `host not found in upstream`, and a container later recreated on a new
address is never re-resolved.

**The migration job holds the owner credential and nothing else does.** Both
APIs declare `service_completed_successfully` on it, so a failed migration stops
them starting against a schema they do not match. The APIs connect as
`endpoint_app`, which has no DDL rights and only INSERT/SELECT on the audit
table — that is what makes "the application cannot rewrite history" a property
of the database rather than a promise in application code (ADR-0003, ADR-0004).

**The Agent API is never given `RECOVERY_ESCROW_KEY` or `MFA_TOTP_KEY`.**
`AgentApiKeyBoundaryGuard` fails that process at startup if either appears, and
the compose file is written so it cannot.

## pgAdmin

Reachable two ways: `https://<host>/pgadmin/` through the same HTTPS origin, or
directly on `http://<host>:5050`. The login is in `.env` as `PGADMIN_EMAIL` and
`PGADMIN_PASSWORD`; the platform database is pre-registered, connecting as the
schema owner so every table is visible.

To take the direct port away and leave only the HTTPS path, delete the `ports:`
block from the `pgadmin` service.

## Day to day

```bash
docker compose ps                         # what is running
docker compose logs -f admin-api          # follow one service
docker compose restart admin-api
sudo ./deploy.sh                          # redeploy; origin is read from .env
sudo ./deploy.sh '' --no-build            # restart without rebuilding
```

After a reboot the stack comes back on its own (`restart: unless-stopped`, and
the Docker service is enabled). The migration job does **not** re-run then —
`depends_on` conditions are honoured by `compose up`, not by the daemon's
restart policy — which is correct, because the schema it would apply is already
applied. Migrations run when you deploy, which is the only time they need to.

Back up the database and the secrets **together** — one without the other is
not a restore:

```bash
docker compose exec -T postgres pg_dump -U postgres -Fc endpoint_platform > epp-$(date +%F).dump
cp .env epp-$(date +%F).env               # 0600, store it somewhere safe
```

Rotating a database or Redis password means updating `.env` **and** telling the
server about it; the init script only runs on an empty data volume. The command
for PostgreSQL is in the header of `postgres/init/10-setup-database.sh`.

## CI/CD

A developer pushes. That is the whole deployment procedure.

```
  push to main
        |
        v
  GitHub Actions: ci.yml
  .NET suites vs real PostgreSQL + Redis · dashboard lint/tests/typecheck
  all four images build · compose file validated
        |
        | green
        v
  the host notices, within a minute          <- autodeploy.timer
        |
        +-- pg_dump BEFORE anything changes
        +-- tag the running images :previous
        +-- git reset --hard <commit>
        +-- deploy.sh: build, up, smoke test the real public URL
        |
        +-- green  -> done
        +-- red    -> retag :previous, compose up, platform keeps serving
```

**Deployment is a pull, not a push, and that is not a workaround.** The host is
on a private network: no GitHub-hosted runner can reach it, and a self-hosted
runner or an inbound webhook would each mean giving an outside system a way in.
Polling needs no inbound port, no SSH key, no stored token and no credential on
GitHub's side at all. The cost is up to a minute of latency, which no one has
ever noticed.

Install it once, on the host:

```bash
cd /opt/endpoint-platform/src/deploy/docker
sudo ./install-autodeploy.sh https://github.com/<owner>/<repo>.git main
```

That makes the deployment directory a git clone of the repository — keeping
`.env`, `tls/` and `pgadmin/pgpass` exactly where they are, because they are
git-ignored and `git reset --hard` does not touch ignored files — and starts the
timer.

```bash
journalctl -u endpoint-platform-autodeploy -f     # watch it
sudo systemctl start endpoint-platform-autodeploy # do not wait for the minute
sudo systemctl stop endpoint-platform-autodeploy.timer   # pause deployments
```

Settings live in `/etc/endpoint-platform/autodeploy.conf`: the branch, the
number of backups to keep, and `REQUIRE_CI`, which is what stops a commit that
failed its tests from reaching this database.

### What a deployment cannot lose

This is the part worth being precise about, because "it redeploys itself" is
only acceptable if the answer here is *nothing*.

| | Why it survives |
|---|---|
| The database | lives in the `pgdata` volume; `compose down -v` is never issued, and the stack is only ever `up -d` |
| Uploaded packages | the `packages` volume, same reason |
| pgAdmin's saved state | the `pgadmin` volume, same reason |
| `.env` — escrow key, MFA key, passwords | git-ignored, so `git reset --hard` leaves it; `generate-env.sh` refuses to regenerate an `.env` that exists |
| `tls/` — the CA and certificate | git-ignored, same |
| `pgadmin/pgpass` | git-ignored, same |

And before every single deployment, a `pg_dump -Fc` lands in
`/var/backups/endpoint-platform/` (the last 10 are kept). If a migration ever
does something regrettable, the state from thirty seconds earlier is on disk:

```bash
ls -lt /var/backups/endpoint-platform/
docker compose exec -T postgres pg_restore -U postgres -d endpoint_platform \
    --clean --if-exists < /var/backups/endpoint-platform/<file>.dump
```

The backup is taken *before* the deploy, deliberately — a dump taken afterwards
would be a dump of the damage.

### When a deployment fails

`deploy.sh` only reports success once the dashboard, both APIs and pgAdmin all
answer over the real HTTPS origin. If they do not, the deployer retags the
`:previous` images back to `:local`, brings the stack up on them, and the
platform carries on serving the last version that worked. The bad commit is
recorded in `/var/lib/endpoint-platform/autodeploy/failed_sha` and is not
retried every minute; the next commit clears it.

### Deploying without waiting for a push

```bash
sudo ./deploy.sh                                          # on the host
./remote-deploy.sh paras-thind@192.168.8.96               # from a workstation
```

Both bypass the CI gate, so they are for hotfixes and for testing an uncommitted
change — not for ordinary work.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `host not found in upstream` | an upstream container is down; `docker compose ps` |
| Sign-in appears to succeed, then every call is 401 | the browser is on `http://`, not `https://`, so it dropped the `__Host-` cookie |
| `403` on `/endpoint-platform-ca.crt` | `tls/` is not `0755`; nginx's worker cannot traverse it |
| pgAdmin restarting | check `PGADMIN_EMAIL` — it validates the address, and special-use domains need `PGADMIN_CONFIG_ALLOW_SPECIAL_EMAIL_DOMAINS` |
| `migrations` exits non-zero | `docker compose logs migrations`; the APIs will not start until it succeeds |
| Agents cannot connect | they need `tls/ca.crt` in the machine trust store |
