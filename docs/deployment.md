# Deployment

Status: **deployable, not fully production-hardened.** Administrator
authentication (ADR-0009) and agent authentication (ADR-0008) are implemented.
What is still outstanding is hardening rather than function: managed secret
storage (a KMS or HSM rather than a key in a root-owned file), full CSP,
backup automation and observability. The [deployed topology](#deployed-topology-one-ubuntu-machine)
below is a single-machine shape; it has no autoscaling, no managed database and
no multi-AZ story, and acquiring them is a separate exercise.

## Topology

- One host runs the two API processes:
  - Admin API — reachable by administrators/dashboard only.
  - Agent API — reachable by endpoints; this is the only surface exposed to
    the endpoint network.
- PostgreSQL 17.x and Redis 8.x as backing services.
- The dashboard is a static build (`dashboard/dist`) served by any web server,
  pointed at the Admin API.
- TLS terminates in front of both APIs (reverse proxy or Kestrel certs). The
  hosts already enable HSTS + HTTPS redirection outside Development.

## Order of operations per deployment

1. Run the **migration job** once, with the owner database credential:
   `EndpointPlatform.Migrations` applies migrations, re-applies runtime role
   grants, reseeds reference data (idempotent). Non-zero exit aborts the
   deployment.
2. Start/replace the API processes, configured with the **runtime** database
   credential (restricted role).

## Configuration contract

Everything is environment variables with the `ENDPOINTPLATFORM_` prefix:

| Variable | Used by | Notes |
|---|---|---|
| `ENDPOINTPLATFORM_Database__ConnectionString` | APIs (runtime role), migration job (owner role) | required |
| `ENDPOINTPLATFORM_Database__RuntimeRoleName` | migration job | enables grant application |
| `ENDPOINTPLATFORM_Redis__ConnectionString` | APIs | required |
| `ENDPOINTPLATFORM_SecretProtection__Key` | **both APIs** | **required; identical value in both.** See below |
| `ENDPOINTPLATFORM_Cors__AllowedOrigins__0` | Admin API | dashboard origin; API refuses to start without it |
| `ASPNETCORE_ENVIRONMENT` | all | `Production` outside dev |
| `ASPNETCORE_URLS` | APIs | listen addresses |

The Windows agent is configured separately (it is not an
`ENDPOINTPLATFORM_`-prefixed process):

| Variable | Used by | Notes |
|---|---|---|
| `ENDPOINTAGENT_Agent__ServerBaseUrl` | Windows agent | Agent API base URL; **must be `https://` outside a local lab** |

Optional — every one of these has a working default, listed because they matter
for a demo host rather than because they must be set:

| Variable | Used by | Notes |
|---|---|---|
| `ENDPOINTPLATFORM_PackageStorage__Directory` | both APIs | uploaded package content; **needs a persistent volume**, or deployed installers vanish on restart |
| `ENDPOINTPLATFORM_Bootstrap__AdminEmail` | migration job | only for `-- bootstrap-admin`, to create the first administrator |
| `ENDPOINTPLATFORM_Bootstrap__AdminPassword` | migration job | same; minimum 12 characters. Supply at run time, never persist |
| `ENDPOINTPLATFORM_AdminAuth__*` | Admin API | session lifetime, lockout threshold, login rate limit |
| `ENDPOINTPLATFORM_AgentServer__*` | Agent API | heartbeat interval, offline threshold |

No configuration file in the repository contains a credential; there is
nothing to rotate out of source control.

### `ENDPOINTPLATFORM_SecretProtection__Key` (required)

This key protects the short-lived secrets used to deliver a new or reset local
Windows account password to an endpoint. The password itself is never stored in
PostgreSQL, never placed in the task payload, and never written to a log; it is
sealed with AES-GCM, held in Redis under a one-time device-bound reference, and
redeemed exactly once.

**The Admin API seals and the Agent API redeems, so the two processes must be
given the same key.** They are separate processes — separate containers in the
demo topology — and each derives its cipher from this configuration value alone.

The failure mode is why this deserves its own section: **when the key is unset,
each process silently generates its own random key at startup.** Nothing fails
and nothing is logged. A single-process deployment works by luck. A two-process
deployment starts cleanly, serves every page, and then fails the first
create-user or reset-password task with *"the ephemeral secret could not be
unsealed"* — an error that points at Redis rather than at configuration. Set it
explicitly, always.

Requirements:

- Cryptographically random, base64-encoded **32 bytes (256-bit)**. A value that
  is not 32 bytes is rejected at startup.
- **Identical** in the Admin API and the Agent API.
- Supplied through the environment or a secrets manager — never committed,
  never logged, never placed in `appsettings.json`.
- Held in `infra/.env` as `SECRET_PROTECTION_KEY` for local runs
  (`scripts/run-local.ps1` maps it to the `ENDPOINTPLATFORM_` variable), and in
  `/etc/endpoint-platform/secrets.env` on a deployed host, from which
  `gen-env.sh` renders it into **both** service environment files.

Generate one with:

```powershell
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$b = New-Object byte[] 32; $rng.GetBytes($b); [Convert]::ToBase64String($b)
```

Rotating the key invalidates only secrets that are in flight at that moment.
They fail their task safely and can be re-issued; no stored data is affected.

## Deployed topology: one Ubuntu machine

One machine, **no containers**. Three .NET processes under systemd, with
PostgreSQL, Redis and nginx installed as ordinary packages.

```
                          Ubuntu machine
   browser ─── 443 ──▶ nginx ──┬──▶ /           dashboard (static build)
   agent   ─── 443 ──▶  (TLS)  ├──▶ /api/    ─▶ Admin API  127.0.0.1:5080
                               └──▶ /agent/  ─▶ Agent API  127.0.0.1:5081
                                                     │
                                    PostgreSQL 17 ◀──┤   loopback only
                                    Redis         ◀──┘   loopback only
                                          ▲
                                          │ HTTPS, outbound from the endpoint
                                   Windows Endpoint
                                          │
                                   Windows Agent (service, LocalSystem)
                                          │
                                   Windows APIs (netapi32 / SAM)
```

Install it with [`infra/ubuntu/install.sh`](../infra/ubuntu/install.sh), or over
SSH from Windows with `infra/ubuntu/Deploy-Ubuntu.ps1`. On Google Cloud,
[`infra/gcp/provision-vm.sh`](../infra/gcp/provision-vm.sh) creates the VM,
the static IP and the firewall rules first — a Compute Engine VM running Ubuntu
is exactly the machine described here, so nothing else differs. Full operating
instructions: [infra/ubuntu/README.md](../infra/ubuntu/README.md).

**Both APIs bind to loopback only.** nginx is the single public entry point.
Ports 5080 and 5081 are never published, and an endpoint reaches the Agent API
over the same public HTTPS origin at `/agent/`.

**The dashboard and the Admin API share one origin.** This is a requirement, not
a preference. The dashboard calls `/api/...` as a relative path, and the session
cookie is issued with the `__Host-` prefix, `Secure`, and `SameSite=Strict`.
The `__Host-` prefix forbids a `Domain` attribute, so the cookie is pinned to
exactly the host that set it. Serving the dashboard from a different hostname
than the API breaks sign-in outright.

**HTTPS is mandatory.** `Secure` cookies are exempted only for `localhost`.
Over plain HTTP on a real host name the browser discards the session cookie and
nobody can sign in. A machine on a private LAN cannot use Let's Encrypt (the
HTTP-01 challenge needs a public name and port 80); install a certificate from
your own CA instead — infra/ubuntu/README.md has the steps.

**PostgreSQL and Redis are private.** Both listen on loopback only and are
password-protected. Redis holds the in-flight sealed account secrets; it is never
reachable from outside the machine.

**One account per process.** `epp-admin-api`, `epp-agent-api` and
`epp-migrations` are separate service accounts, because two processes under one
UID can read each other's environment through `/proc`. The migration job is the
only one given the owner database credential; the Admin API is the only one given
the recovery-escrow keys. systemd reads each `EnvironmentFile=` as PID 1, before
dropping privilege, so no application account can read any of those files.

**The Windows agent is NOT containerized and must not be.** It manages local
Windows accounts through `netapi32` and requires real Windows elevation
(LocalSystem as a service). It stays installed natively on each managed endpoint.

**The agent connects outbound.** It dials the Agent API over HTTPS; the server
never initiates a connection to an endpoint. A managed PC therefore needs no
inbound firewall rule, no VPN and no public IP.

**Privileged Windows work stays local.** The server only ever queues a typed
task. The decision to act, the Windows API call, and the verification of the
resulting state all happen on the endpoint itself. Nothing in this topology gives
the server direct control of the machine.

Agent certificate validation is enforced: the "accept any certificate" escape
hatch is gated on both an explicit option and a Debug build, so a Release agent
requires a genuinely trusted certificate. That check must not be weakened to make
a deployment easier.

### Where configuration comes from

`infra/ubuntu/gen-env.sh` generates `/etc/endpoint-platform/secrets.env`
**once** (`root:root 0600`) and renders one environment file per service from it
on every run. Nothing in this repository holds a deployed credential, and there
is nothing to rotate out of source control.

| Variable | Value on the deployed host |
|---|---|
| `ENDPOINTPLATFORM_Cors__AllowedOrigins__0` | `https://<host>` |
| `ENDPOINTPLATFORM_SecretProtection__Key` | one generated key, **same in both APIs** |
| `ENDPOINTPLATFORM_Database__ConnectionString` | `Host=127.0.0.1;...` — owner role for the migration job, restricted role for the APIs |
| `ENDPOINTPLATFORM_Redis__ConnectionString` | `127.0.0.1:6379,password=...` |
| `ENDPOINTPLATFORM_PackageStorage__Directory` | `/var/lib/endpoint-platform/packages` |
| `ASPNETCORE_URLS` | `http://127.0.0.1:5080` / `:5081` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ENDPOINTAGENT_Agent__ServerBaseUrl` | `https://<host>` (on each Windows endpoint) |

## Windows agent

- `EndpointAgent.Service` runs as a Windows Service (`EndpointPlatformAgent`),
  LocalSystem.
- Minimum OS: Windows 10 1809 / Server 2019.
- Configuration: `appsettings.json` next to the binary
  (`Agent:ServerBaseUrl`, heartbeat interval). No credential material in the
  file — enrollment (Phase 1) stores the device credential DPAPI-protected.
- Install (elevated):
  ```powershell
  dotnet publish agent\EndpointAgent.Service\EndpointAgent.Service.csproj -c Release -p:PublishAgent=true -o "C:\Program Files\EndpointPlatformAgent"
  sc.exe create EndpointPlatformAgent binPath= "C:\Program Files\EndpointPlatformAgent\EndpointAgent.Service.exe" start= auto obj= LocalSystem
  sc.exe start EndpointPlatformAgent
  ```
- Agent releases and remote self-update are shipped, not future work: upload the
  built MSI on the dashboard's Agent page, publish it, and queue `UpdateAgent`
  per device. See [agent-updates.md](agent-updates.md).
- Before piloting a new agent build on a machine, run
  `infra/ubuntu/assert-pilot-machine-is-safe.sh` on the production host. A pilot
  install on an already-enrolled machine overwrites its device credential and
  silently takes that endpoint offline — see
  [runbooks/agent-pilot-safety.md](runbooks/agent-pilot-safety.md).
- **Release trust mode** — `ENDPOINTPLATFORM_AgentReleases__TrustMode`:
  `Internal` (the default, and what this deployment runs) or `Public`. Internal
  requires no Authenticode certificate and reads no signature; integrity is the
  server-computed SHA-256, re-verified over the stored bytes at publish and
  again by the agent over the downloaded bytes before install, over HTTPS, under
  authorization and audit. `Public` additionally requires an Authenticode
  signature whose subject matches
  `ENDPOINTPLATFORM_AgentReleases__ExpectedSignerSubject`, and the API refuses
  to start in that mode without one configured.
- An Internal build is trusted by *this platform, for these machines* — not by
  Windows. SmartScreen, AppLocker and WDAC will treat the MSI as an unsigned
  installer, because it is one. That, not publishability, is what a code-signing
  certificate buys; distributing beyond the managed estate is the case that
  needs `Public`, which is a configuration change rather than a code change.

## Backup / restore

Three things, and any one alone is not a restore:

1. **PostgreSQL** — `pg_dump` of the `endpoint_platform` database captures
   everything, audit history included.
2. **`/var/lib/endpoint-platform/packages`** — the uploaded installer bytes,
   content-addressed by SHA-256.
3. **`/etc/endpoint-platform/secrets.env`** — store it with the dump, encrypted.
   It holds `RECOVERY_ESCROW_KEY`, and a dump restored without it cannot decrypt
   a single escrowed BitLocker recovery password.

Redis is disposable by design. Always run the migration job after a restore: it
re-applies the runtime grants and the audit-immutability protections, which a
plain `pg_restore` under a different role can leave in a weaker state. Commands
are in [infra/ubuntu/README.md](../infra/ubuntu/README.md) and
[operations.md](operations.md).
