# Agent protocol

Status: **v1 — enrollment and heartbeat implemented (Phase 1).** Constants live
in `shared/Contracts/AgentProtocol.cs`; request/response records in
`shared/Contracts/Agent/`. Both sides compile against them, so names cannot
drift. Authentication design rationale: `docs/adr/0008-agent-authentication.md`.

## Principles

1. **Agent-initiated only.** The agent polls/connects outbound over HTTPS.
   Endpoints never open inbound ports and the server never connects to an
   agent.
2. **Versioned.** Every request carries `X-Agent-Protocol-Version` (currently
   `1`). The server rejects versions it does not understand rather than
   guessing, so mixed-version fleets fail loud, not weird.
3. **Per-device identity.** Enrollment (Phase 1) issues each device its own
   credential. There is no fleet-wide shared secret, so one compromised
   endpoint never impersonates another.
4. **Idempotent where possible.** Heartbeats and inventory uploads can be
   retried safely; the server deduplicates on device identity + timestamp.

## Transport

- HTTPS. In production the server certificate must chain to a trusted root;
  the agent refuses `AllowUntrustedServerCertificate` outside Debug builds.
- Route prefix: `/agent/v1` on the Agent API host only.

## Headers

| Header | Direction | Meaning |
|---|---|---|
| `X-Agent-Protocol-Version` | request | Protocol version (`1`); wrong values are rejected with 400 |
| `X-Agent-Credential` | request | Device credential as `keyId.secret`; TLS-only |
| `X-Agent-Device-Id` | request | Enrolled device id (informational; identity comes from the credential) |
| `X-Agent-Version` | request | Agent build version, for fleet upgrade visibility |
| `X-Correlation-Id` | both | Request tracing; server-generated if absent/invalid |

The credential uses a dedicated header rather than `Authorization: Bearer` so
that agent credentials can never be confused with, or replayed as,
administrator bearer tokens.

## Endpoints

### `POST /agent/v1/enroll` — implemented
Anonymous (the enrollment token is the credential). Body: `EnrollRequest`
(token, hostname, machine identifier, agent version, OS). Success returns
`EnrollResponse` with the device id and the credential — the credential secret's
only transmission, ever.

Refusals (unknown/expired/revoked/exhausted token, retired device) are a
uniform 403 with identical bodies, verified by test, so callers cannot probe
the token space. Every refusal is audited as `Denied` with the real reason.

Re-enrollment of a known machine identifier updates the existing device,
revokes its previous credentials and issues a fresh one (`ReEnrolled: true`).

### `POST /agent/v1/heartbeat` — implemented
Requires `X-Agent-Credential`. Body: `HeartbeatRequest` (hostname, agent
version, OS, agent-local timestamp — recorded for skew diagnostics, never
trusted for ordering). Server updates the device facts and `last_seen` from its
own clock; online/offline is derived from staleness, so a dead agent cannot
appear alive. The response returns server time and the interval the server
wants agents to use, making cadence centrally tunable.

Heartbeats do not produce per-event audit entries (volume); enrollment and all
refusals do.

### `POST /agent/v1/inventory` — implemented
Requires `X-Agent-Credential`. Body: `InventoryReport` — hardware section
(serial, manufacturer, model, CPU, RAM, fixed volumes), network interfaces
(name, MAC, IPs, up/down) and the interactively logged-on user. Uploads replace
the previous snapshot wholesale; collection sizes are capped server-side
(64 disks, 64 interfaces, 32 IPs each) and every field is length-validated
before persistence.

The optional `Chrome` section (trailing and nullable — an agent built before it
existed omits it, and the server keeps what it last knew) carries the installed
Google Chrome, every local user's Chrome profiles and each profile's extensions.
The installation is read from the Chrome uninstall entry and the Google Update
client keys in HKLM (the WOW6432Node view first, because Google Update is a
32-bit product; the architecture comes from the updater's own record, not from
which view the key was found in). Profiles come from each user's
`User Data\Local State`, reached through the machine's profile list rather than
the service's own LocalSystem profile, so signed-out users are covered;
extensions come from each profile's `Secure Preferences` and `Preferences`, as
Chrome itself records them. A profile's label is composed the way Chrome's own
menu composes it — the name the person gave it, or their account's given name
with the Workspace domain in parentheses when Chrome labelled the profile with
that domain — and no account e-mail address or id is carried: the parser has no
path to `user_name` or `gaia_id`. Nothing is read from an extension's own
directory or its `manifest.json`. Nothing under a profile is ever written and
nothing is launched. `Status` is `Available` (an installation was found),
`NotInstalled` (none was — leftover `User Data` may still yield profiles, so a
non-empty profile list never means Chrome is present) or `Error` (enumeration
was incomplete and the section carries whatever was read). The section is capped
server-side at 64 profiles and 256 extensions per profile and length-validated
like the rest. On `Available` and `NotInstalled` the stored profiles and
extensions are replaced wholesale; on `Error` the server keeps the last known
set, so a partial snapshot never overwrites a complete one. Chrome uploads are
not audited per event, for the same reason heartbeats are not. See
[chrome-management.md](chrome-management.md).

The refresh handshake is pull-based: the heartbeat response's
`InventoryRequested` is true when an administrator asked for a refresh (or no
inventory was ever received), and the agent responds by uploading. A failed
upload leaves the request pending, so the next heartbeat retries naturally. A
server sweep (every 15 minutes) raises the same flag for any active device whose
snapshot is older than `Inventory:RefreshAfterHours` (24 by default), so
inventory ages out without an administrator having to ask.

## Error handling

Errors are RFC 7807 problem-details with a `correlationId` extension. The
agent treats `401/403` as "credential invalid — do not retry with the same
credential", `409` as "identity conflict — surface to operator", and `5xx`
with exponential backoff + jitter.

### `GET /agent/v1/tasks` — implemented
Requires `X-Agent-Credential`. Claims every task queued for the device, oldest
first, up to twenty per poll, and returns `AgentTaskListResponse`. Claiming
marks each task **Delivered** under a row version (`xmin`), so two overlapping
polls cannot both receive the same task: the loser's write fails, it returns an
empty list, and the agent simply polls again. A task whose deadline has already
passed is marked **Expired** at claim time and is never returned.

Each `AgentTask` carries `taskId`, `type`, `payloadJson` and `expiresAt` — the
server's UTC deadline. The server never hands out a task past it, so the field
exists for the agent's own belt-and-braces check on destructive executors:
`RestartDevice` refuses a task whose `expiresAt` has passed rather than
restarting a machine nobody is still expecting to go down. Agents that predate
the field ignore it; it is optional and last.

### `POST /agent/v1/tasks/{taskId}/result` — implemented
Requires `X-Agent-Credential`. Body: `AgentTaskResult` (`succeeded`, `message`,
optional `resultJson`; never secrets). Accepted only while the task is
Delivered, so a stale or replayed result cannot overwrite a terminal outcome.
For a restart the `resultJson` is `{graceSeconds, restartAt, outcome, code}`:
`restartAt` is the moment Windows will act, as the agent computed it when the
shutdown API accepted the request; `outcome` is `Scheduled`, `Expired`,
`AlreadyInProgress` or `Failed`. A successful result means Windows accepted the
restart — not that the device has restarted; its next heartbeat is the proof.
