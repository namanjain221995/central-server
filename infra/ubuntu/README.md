# Ubuntu deployment kit

Installs the whole management plane — dashboard, Admin API, Agent API,
PostgreSQL, Redis — onto one Ubuntu machine. **Nothing runs in a container.**
The platform ends up as three .NET processes under systemd, behind nginx.

```
                      Ubuntu machine
  browser ─── 443 ──▶ nginx ──┬──▶ /            dashboard (static files)
  agent   ─── 443 ──▶         ├──▶ /api/    ──▶ Admin API  127.0.0.1:5080
                              └──▶ /agent/  ──▶ Agent API  127.0.0.1:5081
                                                    │
                                   PostgreSQL 17 ◀──┤  (loopback only)
                                   Redis        ◀───┘  (loopback only)
```

Both APIs bind to loopback only. nginx is the single public entry point, which
is what keeps the dashboard and both APIs on **one origin** — a hard requirement
of the `__Host-` session cookie, not a preference.

## Two ways in

**On the machine itself**, from the repository root:

```bash
bash infra/ubuntu/install.sh --host epp.example.com --email ops@example.com \
     --admin-email admin@example.com --generate-admin-password
```

**From a Windows machine over SSH** — packages this working tree, uploads it and
drives the same scripts:

```powershell
.\infra\ubuntu\Deploy-Ubuntu.ps1 -SshHost 192.168.1.50 -SshUser naman `
    -KeyPath C:\keys\epp.pem -PublicHostName epp.example.com `
    -CertbotEmail ops@example.com -AdminEmail admin@example.com `
    -GenerateAdminPassword
```

The first run takes 10–25 minutes, most of it apt and the .NET SDK download.
Every script is idempotent, so re-running is safe and is how a .NET patch
release gets picked up. A redeploy afterwards is `bash infra/ubuntu/deploy.sh`
on the machine, or the same PowerShell command with `-SkipHostPrep -SkipCert`,
and takes about three minutes.

## Requirements

- Ubuntu 22.04 or 24.04 LTS, x86_64 or arm64
- **2 GB RAM or more.** Under 3 GB the kit adds a 2 GB swapfile automatically;
  `dotnet publish` was seen to OOM in 1 GB even so.
- 20+ GB disk, outbound internet (apt, nuget, npm, Let's Encrypt)
- a login that can `sudo` **without a password prompt** — the steps run
  non-interactively. Cloud images' `ubuntu` user already can; otherwise
  `sudo visudo` and add `<user> ALL=(ALL) NOPASSWD:ALL`
- inbound TCP **80 and 443**, and 22 from wherever you drive the deploy

## What each script does

| Script | Runs as | Purpose |
|---|---|---|
| `install.sh` | you | Drives everything below, in order. The only one you normally call. |
| `common.sh` | *sourced* | Every path, account and unit name, defined once. |
| `host-prep.sh` | root | PostgreSQL 17 (apt.postgresql.org), Redis (packages.redis.io), .NET 10 SDK (`/opt/dotnet`), Node.js 24 (`/opt/nodejs`, SHA-256 checked), nginx, certbot, the three service accounts, the directory layout, swap, `vm.overcommit_memory=1`, and ufw rules **only if ufw is already active**. |
| `gen-env.sh` | root | Generates `/etc/endpoint-platform/secrets.env` **once**, then renders one environment file per service from it on every run. Prints variable names only. |
| `setup-postgres.sh` | root | Creates the database and its two roles by running `infra/postgres/setup-database.sql`. Re-applies passwords, so it is also how a rotation reaches the server. |
| `setup-redis.sh` | root | Loopback-only, password-protected, no persistence. Verifies that the password is actually enforced. |
| `setup-nginx.sh` | root | The public server block, then a Let's Encrypt certificate via certbot's nginx plugin. |
| `deploy.sh` | you | `dotnet publish` ×3 + `npm ci && npm run build`, then calls `install-release.sh`. **Not root:** the build runs package-feed code. |
| `install-release.sh` | root | Copies the build to `/opt/endpoint-platform/releases/<label>`, installs the units, repoints `current`, restarts, waits for health, and **rolls back** to the previous release if it does not come up. |
| `bootstrap-admin.sh` | root | The first Super Administrator. Password on stdin or `--generate`. |
| `assert-pilot-machine-is-safe.sh` | root | Read-only gate: refuses to let a machine be used for an agent pilot if it is already enrolled here. See [agent-pilot-safety.md](../../docs/runbooks/agent-pilot-safety.md). |

## Where things live

| Path | What | Permissions |
|---|---|---|
| `/etc/endpoint-platform/secrets.env` | **every secret**, source of truth | `root:root 0600` |
| `/etc/endpoint-platform/{migrations,admin-api,agent-api}.env` | rendered per service | `root:root 0600` |
| `/opt/endpoint-platform/releases/<label>` | published binaries + dashboard | root-owned, read-only to the services |
| `/opt/endpoint-platform/current` | symlink to the running release | |
| `/var/lib/endpoint-platform/packages` | uploaded installer content | `epp-admin-api:endpoint-platform 2750` |

systemd reads `EnvironmentFile=` as PID 1, **before** it drops to the service
account, so no application account can read any of those files. That is what
makes the key split real rather than promised: the Agent API is reachable by
every managed endpoint and never holds the escrow keys the Admin API does.

One account per process (`epp-admin-api`, `epp-agent-api`, `epp-migrations`) for
the same reason — two processes under one UID can read each other's environment
through `/proc`.

## Configuration and rotation

`secrets.env` is generated once and **never regenerated**: `RECOVERY_ESCROW_KEY`
seals every escrowed BitLocker recovery password at rest, and a new one would
make all of them unreadable — discovered on the day a machine will not boot.

To change something:

```bash
sudo nano /etc/endpoint-platform/secrets.env
sudo bash infra/ubuntu/gen-env.sh https://epp.example.com   # re-render
sudo bash infra/ubuntu/setup-postgres.sh   # only if a database password changed
sudo bash infra/ubuntu/setup-redis.sh      # only if REDIS_PASSWORD changed
sudo systemctl restart endpoint-platform-admin-api endpoint-platform-agent-api
```

`PUBLIC_ORIGIN` is the one value `gen-env.sh` updates in place, because it is not
a secret and legitimately changes when the machine is renamed.

## HTTPS is mandatory

The session cookie carries the `__Host-` prefix with `Secure` and
`SameSite=Strict`. Browsers exempt only `localhost` from `Secure`, so on any real
host name **a plain-HTTP dashboard is one nobody can sign in to.** Release agents
also validate the server certificate and have no "accept any certificate" switch.

### Let's Encrypt (the default)

certbot uses the HTTP-01 challenge: Let's Encrypt connects to
`http://<host>/.well-known/acme-challenge/...`, so the name must resolve to this
machine **from the public internet** and port 80 must be reachable. Renewal runs
from certbot's systemd timer; `sudo certbot certificates` shows the expiry.

### A machine on a private network: use your own CA

A LAN machine cannot satisfy HTTP-01. Run with `--skip-cert`, then install a
certificate issued by your internal CA (AD Certificate Services, for example):

1. Put the chain and key on the machine, e.g. `/etc/ssl/epp/fullchain.pem` and
   `/etc/ssl/epp/privkey.pem` (mode 600).
2. In `/etc/nginx/sites-available/endpoint-platform`, add a `listen 443 ssl;`
   server with `ssl_certificate` / `ssl_certificate_key` and the same three
   `location` blocks, and turn the port 80 server into
   `return 301 https://$host$request_uri;`.
3. `sudo nginx -t && sudo systemctl reload nginx`
4. Make sure every managed Windows machine trusts that CA root — Group Policy
   normally does this already for an AD CS root.

`PUBLIC_ORIGIN` must still be `https://<name>` for the name on the certificate.

## Operating

```bash
systemctl status endpoint-platform-admin-api endpoint-platform-agent-api
journalctl -u 'endpoint-platform-*' -f          # all three, following
journalctl -u endpoint-platform-migrations -n 80 # why a deploy failed
curl -fsS https://epp.example.com/api/health/ready
cat /opt/endpoint-platform/current/RELEASE       # which release is running
sudo certbot certificates                        # certificate expiry
```

### Rollback

`install-release.sh` rolls back on its own when a release does not become
healthy. To go back deliberately:

```bash
ls -t /opt/endpoint-platform/releases          # pick the one you want
sudo systemctl stop endpoint-platform-admin-api endpoint-platform-agent-api
sudo ln -sfn /opt/endpoint-platform/releases/<label> /opt/endpoint-platform/current.next
sudo mv -T /opt/endpoint-platform/current.next /opt/endpoint-platform/current
sudo systemctl start endpoint-platform-admin-api endpoint-platform-agent-api
```

Migrations are forward-only and additive by design, so older code normally runs
against a newer schema. A migration that drops or renames a column is the
exception and must be handled deliberately — take a dump first and write a
compensating migration rather than reversing one in place.

## Backups

Three things, taken together. Any one alone is not a restore.

```bash
# 1. the database: devices, administrators, audit trail, escrowed keys
sudo -u postgres pg_dump -Fc endpoint_platform > ~/epp-db-$(date +%F-%H%M).dump

# 2. uploaded package content (agent MSIs published through the dashboard)
sudo tar -czf ~/epp-packages-$(date +%F-%H%M).tgz -C /var/lib/endpoint-platform packages

# 3. the secrets
sudo cp /etc/endpoint-platform/secrets.env ~/epp-secrets-$(date +%F-%H%M).env
```

**Store `secrets.env` with the dump, encrypted.** It holds `RECOVERY_ESCROW_KEY`:
a database restore without it cannot decrypt a single escrowed BitLocker
recovery password, and without the PostgreSQL passwords the restored database
cannot be opened at all.

Redis is disposable and is not backed up — losing it logs administrators out and
cold-starts caches, nothing more.

After a restore, run the migration job again
(`sudo systemctl start endpoint-platform-migrations`): it re-applies the runtime
grants and the audit-immutability protections, which a plain `pg_restore` under a
different role can leave in a weaker state.

## Installing the Windows agent afterwards

Build or download the MSI, then on each managed machine (elevated):

```powershell
msiexec /i EndpointPlatformAgent-<version>-x64.msi SERVERBASEURL=https://epp.example.com
```

`SERVERBASEURL` overrides the URL baked into the MSI; the agent appends
`/agent/v1/...` itself, so it is just the origin. The device then appears under
**Enrollments** in the dashboard, where an administrator approves it.

The agent is **not** containerised and must not be: it manages local accounts
through `netapi32` and needs real Windows elevation. It dials this machine
outbound over HTTPS, so a managed PC needs no inbound rule, no VPN and no public
IP.

## Deploying on Google Cloud

A Compute Engine VM running Ubuntu is exactly the machine described above, so
this kit applies unchanged. [`infra/gcp/`](../gcp/) has a script that provisions
the VM, the static IP and the firewall rules first.
