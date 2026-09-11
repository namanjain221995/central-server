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

The refresh handshake is pull-based: the heartbeat response's
`InventoryRequested` is true when an administrator asked for a refresh (or no
inventory was ever received), and the agent responds by uploading. A failed
upload leaves the request pending, so the next heartbeat retries naturally.

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
