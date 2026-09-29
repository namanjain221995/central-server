# Container deployment

The whole platform on one machine with Docker Compose: PostgreSQL, Redis, the
migration job, the Admin API, the Agent API, the dashboard behind nginx, and
pgAdmin for looking at the database.

This is the only deployment path. A native systemd kit (`infra/ubuntu/`) once
sat alongside it and was removed on 2026-09-24 so that exactly one path exists.
It is in git history if ever wanted, but must not be reintroduced next to this
one — two live paths is how the wrong one gets deployed.

| | |
|---|---|
| Processes | containers, one per .NET process, plus nginx |
| PostgreSQL / Redis | containers, no published port |
| TLS | a local CA (self-signed) by default; a Let's Encrypt certificate is dropped into `tls/` (see the production runbook) |
| Upgrade | rebuild images, `compose up -d` |
| Rollback | previous image tag |

## Quick start

```bash
cd deploy/docker
sudo ./deploy.sh https://<lan-address>     # or https://epp.example.com
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

**The Agent API is never given `RECOVERY_ESCROW_KEY`, `RECOVERY_SEALING_PRIVATE_KEY`
or `MFA_TOTP_KEY`.** `AgentApiKeyBoundaryGuard` fails that process at startup if
any of them appears, and the compose file is written so it cannot. It *is* given
`RECOVERY_SEALING_PUBLIC_KEY`, which only encrypts: endpoints seal BitLocker
recovery passwords to it, and only the Admin API, holding the private half, can
open them. `generate-env.sh` creates that pair once; a device enrolled before
the pair existed has to re-enrol to take part.

## A public name with a trusted certificate

The self-signed certificate is the fallback, not the goal. A **Release** build of
the Windows agent validates the server against the machine trust store and has no
override, so a PC enrols against a local CA only after `Install-AgentCaRoot.ps1`
has run on it. A name with a Let's Encrypt certificate needs nothing on any PC.

```bash
sudo ./generate-env.sh https://epp.example.com   # records the old origin as LAN_ORIGIN
printf 'dns_cloudflare_api_token = %s
' '<token>' > cloudflare.ini && chmod 600 cloudflare.ini
sudo ./issue-certificate.sh
```

`issue-certificate.sh` creates `epp.example.com  A  <LAN address>` with
**`proxied: false`**, proves the DNS-01 flow against Let's Encrypt staging, issues
the real certificate, and recreates only `web` and `certbot`. The `certbot`
service renews twice a day; `web` reloads itself every six hours to pick the new
files up, so nothing has to be restarted by hand. The token is a scoped
**Zone:DNS:Edit** API token for the one zone — Cloudflare's "Edit zone DNS"
template.

**Cloudflare is DNS only here, never proxied and never a Tunnel.** Either would
terminate TLS at Cloudflare's edge, and this platform sends revealed BitLocker
recovery keys, admin session tokens and agent credentials over that connection.
DNS-01 is what makes a publicly trusted certificate possible without any of that:
it proves control of the *name*, so the host needs no inbound connectivity. The
record points at a private address, so the name resolves to something reachable
only from the office network or a VPN — which is the intended outcome.

**The LAN address keeps the local-CA certificate.** nginx serves two HTTPS server
blocks from one set of routes (`nginx/site.conf`): the public name with the
trusted certificate, and a `default_server` — what a client connecting by IP
lands on, since it sends no SNI — with the local-CA one. Agents enrolled against
the address before the name existed trust that CA and nothing else; changing the
certificate they are served would cut every one of them off. Re-enrol them
against the name when convenient, then the address can be retired.

`LAN_ORIGIN` is kept in the Admin API's CORS list alongside `PUBLIC_ORIGIN`, so
the dashboard keeps working on both.

A router with DNS-rebinding protection may refuse to resolve a public name to a
private address. If the name does not resolve from inside the office, allow it in
the router (dnsmasq: `rebind-domain-ok=/example.com/`) or add it to local DNS.

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

`.github/workflows/ci.yml` runs on every push and pull request: the .NET suites
against a real PostgreSQL and Redis, the dashboard's lint, tests and type check,
a build of all four images, and a validation of this compose file.

**Deployment is manual and deliberate.** This platform holds BitLocker recovery
keys and can run commands as SYSTEM on every managed PC, so an unreviewed commit
must not reach it on its own. An operator deploys a reviewed, green commit:

```bash
cd /opt/endpoint-platform/src && git pull --ff-only
cd deploy/docker && sudo ./deploy.sh
```

`autodeploy.sh`, `install-autodeploy.sh` and the `systemd/` units implement
deploy-on-push (poll GitHub, wait for green CI, back up, deploy, roll back on
failure). They were installed once and **disabled on purpose** — see CLAUDE.md.
Do not re-enable the timer without that decision being revisited.

### What a deployment cannot lose

True of every deployment, manual or automatic: the answer has to be *nothing*.

| | Why it survives |
|---|---|
| The database | lives in the `pgdata` volume; `compose down -v` is never issued, and the stack is only ever `up -d` |
| Uploaded packages | the `packages` volume, same reason |
| pgAdmin's saved state | the `pgadmin` volume, same reason |
| `.env` — escrow keys, MFA key, passwords | git-ignored, so `git reset --hard` leaves it; `generate-env.sh` refuses to regenerate an `.env` that exists |
| `tls/` — the CA and certificate | git-ignored, same |
| `pgadmin/pgpass` | git-ignored, same |

Take a dump before any deployment that carries a migration — migrations are
forward-only, and a rollback restores binaries, not schema. (`autodeploy.sh` does
this itself into `/var/backups/endpoint-platform/`; by hand, use the command in
"Day to day".) Restoring one:

```bash
ls -lt /var/backups/endpoint-platform/
docker compose exec -T postgres pg_restore -U postgres -d endpoint_platform \
    --clean --if-exists < /var/backups/endpoint-platform/<file>.dump
```

The backup is taken *before* the deploy, deliberately — a dump taken afterwards
would be a dump of the damage.

### When a deployment fails

`deploy.sh` only reports success once the dashboard, both APIs and pgAdmin all
answer over the real HTTPS origin, and exits non-zero otherwise. `remote-deploy.sh
<user>@<host>` does the same from a workstation, including for an uncommitted
change — which is also why it is for testing, not for ordinary work.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `host not found in upstream` | an upstream container is down; `docker compose ps` |
| Sign-in appears to succeed, then every call is 401 | the browser is on `http://`, not `https://`, so it dropped the `__Host-` cookie |
| `403` on `/endpoint-platform-ca.crt` | `tls/` is not `0755`; nginx's worker cannot traverse it |
| pgAdmin restarting | check `PGADMIN_EMAIL` — it validates the address, and special-use domains need `PGADMIN_CONFIG_ALLOW_SPECIAL_EMAIL_DOMAINS` |
| `migrations` exits non-zero | `docker compose logs migrations`; the APIs will not start until it succeeds |
| Agents cannot connect | by IP: they need `tls/ca.crt` in the machine trust store. By name: check the certificate with `openssl s_client -connect <name>:443 -servername <name>` |
| The public name does not resolve inside the office | the router's DNS-rebinding protection is dropping a public name that points at a private address; allow the domain there |
| Dashboard says "Admin API unreachable" on the LAN address only | `LAN_ORIGIN` is missing from `.env`, so that origin is not in the CORS list |
