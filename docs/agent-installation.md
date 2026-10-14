# Windows agent: installation and enrollment

How the Endpoint Platform Agent gets onto a Windows PC, how that PC becomes a
managed device, and what to do when it does not.

## What the agent is

A native Windows Service — `EndpointPlatformAgent`, displayed as **Endpoint
Platform Agent** — installed from an MSI. It runs as LocalSystem, starts
automatically, and restarts itself if it crashes.

It is **not** a remote shell. Everything it can do is one of a fixed set of typed
operations the server authorises; there is no command-execution task and adding
one is explicitly out of bounds (ADR-0005).

### Managed PCs do not need to be on the same LAN

The agent makes **outbound HTTPS connections only**. The management server never
initiates a connection to the endpoint.

That means a managed PC needs no inbound firewall rule, no port forwarding, no
public IP, no VPN, and no network relationship to the server or to any other
managed PC. An office desktop, a home machine, a laptop on hotel Wi-Fi and a PC
in another country behind carrier-grade NAT are all equivalent to this system.

**The only network requirement is outbound HTTPS to the management server.**

## Installing

1. Obtain `EndpointPlatformAgent-1.0.0-x64.msi`.
2. Copy it to the Windows PC.
3. Double-click it and complete the installer.

That is the whole procedure. No PowerShell, no Command Prompt, no .NET runtime
install, no configuration file to edit, no token to type.

Requirements: 64-bit Windows 10 / Server 2016 or later, and administrator rights
to install a service. The agent is published self-contained, so **the .NET
runtime does not need to be installed** on the endpoint.

### The installer is unsigned

There is no code-signing certificate for this build, so SmartScreen and UAC will
warn on first run. That warning is accurate and should not be worked around by
weakening any Windows setting. To sign a release build, after `build-msi.ps1`:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
        /f <certificate.pfx> /p <password> `
        build\installer\EndpointPlatformAgent-1.0.0-x64.msi
```

### What gets installed where

| Path | Contents |
|---|---|
| `C:\Program Files\EndpointPlatform\Agent\` | binaries; the service account cannot write here |
| `C:\ProgramData\EndpointPlatformAgent\` | device credential, machine config, enrollment state |
| `C:\ProgramData\EndpointPlatformAgent\Logs\` | rolling operational logs, 14 days |
| Start menu → **Company Portal** | the self-service app employees use to install approved software ([company-portal.md](company-portal.md)); runs as the signed-in user and talks only to the local service |

The ProgramData directory's ACL is replaced at install time with **SYSTEM and
Administrators only** — ProgramData otherwise inherits an entry granting all
users write access, which is not acceptable for a directory holding a device
credential.

### Pointing the agent at a different server

The server URL is baked in at build time. It is a public endpoint, not a secret,
which is why the MSI can stay a single universal binary. To install against a
different deployment without rebuilding:

```
msiexec /i EndpointPlatformAgent-1.0.0-x64.msi SERVERBASEURL=https://your-server
```

`https://` is required. The build script refuses to produce an installer with a
plain-HTTP URL for anything but `localhost`, because an agent talking HTTP would
send its device credential in clear.

## Enrollment

The MSI contains **no enrollment token, no credential and no secret of any
kind**. A machine is not admitted by holding a shared secret; it is admitted
because an administrator approved it.

```
install MSI
    ↓
service starts, finds no credential
    ↓
generates a 256-bit secret, keeps it, sends only its SHA-256
    ↓
POST /agent/v1/enroll/request          ← anonymous, rate limited
    ↓
appears in the pending list
    ↓
administrator approves                 ← authenticated, device.enroll
    ↓
POST /agent/v1/enroll/claim            ← agent proves possession
    ↓
credential issued once, stored DPAPI-protected
    ↓
heartbeat → inventory → Active
```

Two properties are worth understanding, because they are what make this safe:

**Nothing sensitive ever reaches the endpoint's disk during install.** The agent
generates its own secret and transmits only a hash of it. A copy of the MSI, or
of anything in a Downloads folder, is useless to an attacker.

**The agent never chooses its organization.** It does not send one and cannot.
The device joins whatever organization the approving administrator belongs to.

### Approving a machine

```
GET  /admin/v1/enrollments/pending
POST /admin/v1/enrollments/{requestId}/approve
POST /admin/v1/enrollments/{requestId}/reject
```

All three require authentication and the `device.enroll` permission. The pending
list shows hostname, machine identifier, OS, agent version, when it asked and
when the request expires — and no secret of any kind.

Approval and rejection are both **atomic and final**. A request can be decided
once: two administrators clicking Approve simultaneously results in exactly one
approval, and an approved or rejected request cannot be decided again. Both are
recorded in the audit trail as `enrollment.approved` / `enrollment.rejected`
with the approving administrator named.

### Rejection

A rejected machine never receives a credential. The agent is told once, stops
polling that request, and does not silently retry it. To admit the machine later,
it will make a new request.

### Expiry

A pending request lives for **15 minutes**. After that it cannot be approved or
claimed, and it disappears from the list. The agent notices and starts a new
request, so an unattended install that sat waiting overnight will still be
approvable in the morning — it will simply be a newer request.

Unknown, expired and already-claimed requests are all reported identically, so
request identifiers cannot be probed.

### Restart and reboot while waiting

The agent persists its request — DPAPI-protected, LocalMachine scope, in the
hardened state directory — **before** sending it. A service restart or a Windows
reboot mid-wait resumes the same request rather than creating a second one, so
the entry an administrator is looking at stays the entry the agent will claim.

### Known limitation: pending requests are not organization-scoped

A pending request has **no organization until it is approved**, because the agent
does not supply one. The pending list therefore shows every waiting machine to
every administrator with `device.enroll`, regardless of organization.

For a single-organization deployment this is correct. **It is not multi-tenant
safe**, and should not be described as such. Making it so requires a public,
non-secret per-organization bootstrap identifier baked into per-organization MSI
builds — deliberately not built here.

## After enrollment

Reboot behaviour is the normal path: the service starts automatically, loads the
DPAPI-protected credential, authenticates, and resumes heartbeat and inventory.
**It does not re-enrol and does not create a second device record.**

Device identity is the SMBIOS system UUID, not the hostname, so renaming the PC,
changing its network or reinstalling the agent all resolve to the same device.

## Operating

```powershell
Get-Service EndpointPlatformAgent
Restart-Service EndpointPlatformAgent
Get-Content 'C:\ProgramData\EndpointPlatformAgent\Logs\agent-*.log' -Tail 50
Get-EventLog -LogName Application -Source EndpointPlatformAgent -Newest 20
```

Logs record state transitions — service start and stop, enrollment progress,
heartbeat failures and reconnection, task execution and results, agent version.
They never contain the request secret, the device credential, or any password,
API key or infrastructure credential.

### Troubleshooting

| Symptom | Cause | Action |
|---|---|---|
| Service installed but no pending request | no outbound HTTPS to the server | check egress and TLS interception; the log records the connection failure |
| Pending request never appears | the request reached a different server | confirm `agent.config.json` names the intended server |
| Approved but still not Active | agent polls with backoff up to 5 minutes | wait one interval; the log shows each attempt |
| Repeated pending entries for one PC | state directory not writable | check the ACL on `C:\ProgramData\EndpointPlatformAgent` |
| Device shows Offline | agent stopped, or machine off | `Get-Service`; the device record is retained, never auto-deleted |

The agent retries with exponential backoff — 30 s, 60 s, 2 min, 5 min maximum —
so a network outage, a server restart or a laptop suspended overnight all
recover on their own without hammering the API.

## Uninstall

Uninstall from **Settings → Apps**, or:

```
msiexec /x EndpointPlatformAgent-1.0.0-x64.msi
```

This stops and removes the service and deletes the binaries. The device record on
the server is **not** deleted — it becomes Offline, keeping its audit history,
inventory, group memberships and policy assignments. Removing a device record is
a deliberate administrative action, never a side effect of uninstalling software
on the endpoint.

To stop managing a machine permanently, retire the device on the server as well:
that revokes its credential, so an agent reinstalled on that machine cannot
authenticate and must be approved again.
