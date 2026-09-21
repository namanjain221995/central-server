# Development guide

## Prerequisites

| Tool | Version used | Notes |
|---|---|---|
| .NET SDK | 10.0.400+ | `global.json` pins 10.0.x |
| Node.js | 24.x LTS | dashboard |
| PostgreSQL | 17.x | installed natively, **not** in a container |
| Redis | 6.2+ (8.x used) | installed natively; `GETDEL` needs 6.2 |
| Git | any recent | |

Nothing in this repository runs in a container, and Docker is not used anywhere —
not for development, not for tests, not for deployment.

### Installing PostgreSQL and Redis on Windows

- **PostgreSQL**: the EnterpriseDB installer from postgresql.org. Let it run as a
  service on 5432 and remember the `postgres` superuser password — `run-local.ps1`
  needs it once, to create the database and the two roles. Add
  `C:\Program Files\PostgreSQL\17\bin` to `PATH` so `psql` is available.
- **Redis**: there is no official Windows build. Either run it in WSL2
  (`sudo apt install redis`, then `sudo service redis-server start`), or install
  [Memurai](https://www.memurai.com), which is Redis-compatible and runs as a
  Windows service. Set a password (`requirepass`) either way — see below.

On Linux or macOS, install both from your package manager.

## First-time setup

```powershell
cd c:\Projects\endpoint-platform

# 1. Create your local configuration (git-ignored).
Copy-Item infra\.env.example infra\.env
#    Edit infra\.env:
#      - POSTGRES_ADMIN_PASSWORD  = the password you gave the PostgreSQL superuser
#      - every other CHANGE_ME    = a generated value:
#          $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
#          $b = New-Object byte[] 24; $rng.GetBytes($b)
#          [Convert]::ToBase64String($b) -replace '[+/=]',''

# 2. Point Redis at the same password you put in REDIS_PASSWORD.
#    WSL2:    sudo nano /etc/redis/redis.conf   ->  requirepass <value>
#             sudo service redis-server restart
#    Memurai: edit memurai.conf, then restart the Memurai service.

# 3. Restore, build, test.
dotnet restore EndpointPlatform.slnx
dotnet build EndpointPlatform.slnx
dotnet test EndpointPlatform.slnx          # see "Tests" below for the two variables

# 4. Dashboard dependencies.
cd dashboard; npm install; cd ..
```

`run-local.ps1` creates the database and its two roles on first run, using the
same `infra/postgres/setup-database.sql` the Ubuntu host runs, so a local
database and a deployed one are created identically.

## Running the platform

```powershell
.\scripts\run-local.ps1 -WithAgent      # start everything
.\scripts\stop-local.ps1                # stop everything
```

PostgreSQL and Redis must already be running; `run-local.ps1` checks and tells
you if they are not. It never starts or stops them — they are ordinary local
services with their own lifecycle, shared with anything else on your machine.

| Switch | Use it when |
| --- | --- |
| `-SkipDatabaseSetup` | The database and roles exist (marginally faster) |
| `-SkipMigrations` | The schema is current |
| `-WithAgent` | You are testing local user / group management (raises one UAC prompt) |

Verify:

- http://localhost:5080/health/live and /health/ready
- http://localhost:5081/health/ready
- http://localhost:5080/swagger (Development only)
- http://localhost:5173 — the dashboard's "Platform status" card should show
  Admin API reachable, postgres Healthy, redis Healthy.

<details>
<summary>Running the pieces by hand</summary>

```powershell
$cfg = @{}; Get-Content infra\.env | ? { $_ -match '=' } | % { $p = $_ -split '=',2; $cfg[$p[0]] = $p[1] }

# Migrations + seed, as the OWNER role.
$env:ENDPOINTPLATFORM_Database__ConnectionString =
  "Host=localhost;Port=$($cfg['POSTGRES_PORT']);Database=$($cfg['POSTGRES_DB']);Username=$($cfg['POSTGRES_SUPERUSER']);Password=$($cfg['POSTGRES_SUPERUSER_PASSWORD'])"
$env:ENDPOINTPLATFORM_Database__RuntimeRoleName = $cfg['POSTGRES_APP_USER']
dotnet run --project server\Migrations\EndpointPlatform.Migrations.csproj

# The APIs use the RESTRICTED role, never the owner.
$env:ENDPOINTPLATFORM_Database__ConnectionString =
  "Host=localhost;Port=$($cfg['POSTGRES_PORT']);Database=$($cfg['POSTGRES_DB']);Username=$($cfg['POSTGRES_APP_USER']);Password=$($cfg['POSTGRES_APP_PASSWORD'])"
$env:ENDPOINTPLATFORM_Redis__ConnectionString =
  "localhost:$($cfg['REDIS_PORT']),password=$($cfg['REDIS_PASSWORD'])"

dotnet run --project server\Api\EndpointPlatform.Api.csproj          # terminal 1, :5080
dotnet run --project server\AgentApi\EndpointPlatform.AgentApi.csproj # terminal 2, :5081
cd dashboard; npm run dev                                             # terminal 3, :5173
dotnet run --project agent\EndpointAgent.Service\EndpointAgent.Service.csproj  # terminal 4, optional
```

</details>

## Tests

```powershell
dotnet test EndpointPlatform.slnx
```

The integration suites run against **your locally installed PostgreSQL and
Redis**, configured by two environment variables:

| Variable | Example | Used by |
|---|---|---|
| `ENDPOINTPLATFORM_TEST_POSTGRES` | `Host=127.0.0.1;Port=5432;Username=postgres;Password=...` | Infrastructure, Api, AgentApi suites |
| `ENDPOINTPLATFORM_TEST_REDIS` | `127.0.0.1:6379,password=...` | Api suite only |

```powershell
$env:ENDPOINTPLATFORM_TEST_POSTGRES = 'Host=127.0.0.1;Port=5432;Username=postgres;Password=<superuser password>'
$env:ENDPOINTPLATFORM_TEST_REDIS    = '127.0.0.1:6379,password=<redis password>'
dotnet test EndpointPlatform.slnx
```

The PostgreSQL role must be able to `CREATE DATABASE`: each fixture creates a
throwaway database named `ept_<purpose>_<guid>` and drops it — with `FORCE`, so a
pooled connection cannot block the drop — when it finishes. Suites therefore run
in parallel without colliding, and a crashed run cannot poison the next one. A
suite left behind by a hard kill shows up as a stray `ept_*` database; dropping
those by hand is safe.

Use **PostgreSQL 17**, the version the platform is deployed on. Unset variables
fail the affected suites with a message naming the variable, rather than with a
connection error.

- `EndpointPlatform.Infrastructure.Tests` exercises triggers, `jsonb`, `inet`,
  partial indexes and migrations run up and down — none of which exists in an
  in-memory or SQLite provider, so a fake one would prove nothing.
- `EndpointAgent.Windows.Tests` exercises real WMI and only runs on Windows.
  Tests needing elevation skip themselves rather than fail.
- `EndpointPlatform.Architecture.Tests` enforces layering and the agent
  no-process-execution rule; if it fails, fix the dependency, don't loosen the
  test.

`EndpointPlatform.Domain.Tests` and `EndpointAgent.Core.Tests` need neither
variable — they touch no I/O and run anywhere.

## Conventions

- Configuration: strongly-typed options, validated on start. New settings get
  a class in `Infrastructure/Configuration` (server) or
  `EndpointAgent.Core/Configuration` (agent).
- No secrets in committed files. Local secrets: `infra/.env` (git-ignored) or
  `dotnet user-secrets` (per-API). Deployment: environment files rendered by
  `infra/ubuntu/gen-env.sh`, readable only by root.
- Database identifiers are snake_case (automatic; see
  `SnakeCaseNamingConvention`).
- New permissions go in `Permissions.cs` **and** `Permissions.All` — a test
  fails if the two diverge. Role changes go in `SystemRoles.cs`; tests pin
  what Helpdesk/Auditor must never hold.
- Every timestamp comes from an injected `TimeProvider`.
- Migrations: `dotnet ef migrations add <Name> --project server\Migrations\EndpointPlatform.Migrations.csproj --startup-project server\Migrations\EndpointPlatform.Migrations.csproj --output-dir Schema`

## Troubleshooting

- **API exits at startup complaining about ConnectionString** — the
  `ENDPOINTPLATFORM_*` variables aren't set in that terminal. This is by
  design; there are no default credentials.
- **`run-local.ps1` says PostgreSQL or Redis is not listening** — the service
  isn't running, or it's on a different port than `infra/.env` says. Check with
  `Get-Service` (Windows) or `sudo service redis-server status` (WSL2).
- **Tests fail with "ENDPOINTPLATFORM_TEST_POSTGRES is not set"** — set the two
  variables above in that terminal.
- **Tests fail with "permission denied to create database"** — the role in
  `ENDPOINTPLATFORM_TEST_POSTGRES` needs `CREATEDB`, or use `postgres`.
- **Port already in use** — 5080/5081/5173 are overridable via `ASPNETCORE_URLS`
  and `vite.config.ts`; PostgreSQL and Redis ports live in `infra/.env`.
