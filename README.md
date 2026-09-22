# Endpoint Management Platform

Internal enterprise endpoint management for organization-owned Windows
computers: central management backend, web administrator dashboard, and a
Windows endpoint agent.

**Status: Phases 0–3 complete** (foundation; secure enrollment + heartbeat;
device inventory; authentication + RBAC + audit). See
[docs/architecture.md](docs/architecture.md) for the design and
[docs/adr/](docs/adr/) for the decisions behind it.

| Component | Tech | Where |
|---|---|---|
| Admin API (administrators) | ASP.NET Core, .NET 10 | `server/Api` · http://localhost:5080 |
| Agent API (machine identities) | ASP.NET Core, .NET 10 | `server/AgentApi` · http://localhost:5081 |
| Domain / Infrastructure | C#, EF Core 10, PostgreSQL, Redis | `server/Domain`, `server/Infrastructure` |
| Migration + seed runner | .NET console | `server/Migrations` |
| Windows agent | .NET 10 Windows Service | `agent/` |
| Dashboard | React + TypeScript + Vite | `dashboard/` · http://localhost:5173 |
| Deployment kit | Ubuntu: systemd + nginx, no containers | `infra/ubuntu/`, `infra/gcp/` |

## Quick start

Prerequisites: .NET SDK 10.0.4xx, Node.js 24 LTS, PostgreSQL 17, Redis 6.2+, Git.
PostgreSQL and Redis are installed **natively** and must already be running;
nothing here uses containers. See [docs/development.md](docs/development.md) for
installing them on Windows.

**Day to day, this is the whole thing:**

```powershell
.\scripts\run-local.ps1 -WithAgent      # start everything
.\scripts\stop-local.ps1                # stop everything
```

`run-local.ps1` checks that PostgreSQL and Redis are listening, creates the
database and its two roles if they do not exist, applies migrations, and opens a
window each for the Admin API (5080), the Agent API (5081) and the dashboard
(5173), then waits until all three report ready. Every credential is read from
`infra/.env`, so nothing is ever typed on a command line.

`-WithAgent` also starts the Windows agent and raises **one UAC prompt**.
Managing local Windows accounts requires administrator privilege; nothing here
bypasses that, and declining the prompt just leaves the agent out. Omit the
switch if you are not testing local user or group management.

Useful switches once things are already up:

| Switch | Use it when |
| --- | --- |
| `-SkipDatabaseSetup` | The database and roles already exist |
| `-SkipMigrations` | The schema is current |
| `-WithAgent` | You are testing local user / group management |

Then sign in at **http://localhost:5173**. `stop-local.ps1` stops the
applications only: PostgreSQL and Redis are ordinary local services it never
started, and it never drops a database — the database holds the audit trail, the
enrolled devices and your admin account.

First-time setup, the full instructions and troubleshooting:
[docs/development.md](docs/development.md).

## Deploying

One Ubuntu machine, three .NET processes under systemd behind nginx. From the
repository root:

```bash
bash infra/ubuntu/install.sh --host epp.example.com --email ops@example.com \
     --admin-email admin@example.com --generate-admin-password
```

Or drive it over SSH from Windows with `infra\ubuntu\Deploy-Ubuntu.ps1`. On
Google Cloud, `infra/gcp/provision-vm.sh` creates the VM first. See
[infra/ubuntu/README.md](infra/ubuntu/README.md) and
[docs/deployment.md](docs/deployment.md).

**Or in containers.** [`deploy/docker/`](deploy/docker/README.md) runs the same
three processes plus PostgreSQL, Redis and pgAdmin under Docker Compose, on one
machine, with a self-signed certificate when there is no public DNS name:

```bash
cd deploy/docker && sudo ./deploy.sh https://<host-or-ip>
```

One host, one choice: both kits want ports 80 and 443 and both want to own the
database. CI and the deployment pipeline for the container path are
`.github/workflows/ci.yml` and `.github/workflows/deploy.yml`.

Health checks: `GET /health/live`, `GET /health/ready` on both APIs. Swagger
UI at `/swagger` (Development only).

## Security posture (Phase 0)

- Two API hosts = two trust boundaries; enforced by architecture tests.
- Append-only audit trail: EF interceptor + restricted DB role + database
  triggers (UPDATE/DELETE/TRUNCATE all rejected; verified against live
  PostgreSQL, including as the schema owner).
- Permission-based RBAC catalogue seeded and reconciled on every deployment;
  out-of-band role grants are reverted automatically.
- No secrets in the repository; no default credentials anywhere. Startup
  fails with instructions if configuration is missing.
- The agent cannot launch processes or run PowerShell — its assemblies do not
  reference process creation, enforced by tests.
- Admin API: PBKDF2 passwords, revocable HttpOnly-cookie sessions, permission
  policies on every endpoint, denial auditing, lockout + login rate limiting.
  Bootstrap the first administrator with:
  `ENDPOINTPLATFORM_Bootstrap__AdminEmail=... ENDPOINTPLATFORM_Bootstrap__AdminPassword=... dotnet run --project server\Migrations -- bootstrap-admin`

## Documentation

- [Architecture](docs/architecture.md)
- [Threat model](docs/threat-model.md)
- [Agent protocol](docs/agent-protocol.md)
- [Development guide](docs/development.md)
- [Deployment](docs/deployment.md)
- [Decision records](docs/adr/)
