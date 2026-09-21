# Operations runbook

Operational procedures for running the Endpoint Management Platform: backup and
restore, credential and token rotation, offboarding, background jobs, and the
knobs that matter at scale. Architecture is in [architecture.md](architecture.md);
this document is the "how do I operate it" companion.

## Backup and restore

All durable state is in PostgreSQL plus the package content store. Redis holds
only cache/session data and is reconstructable.

### What to back up

1. **PostgreSQL** (`endpoint_platform` database) — the system of record: devices,
   credentials (hashed), audit trail, policies, tasks, packages metadata.
2. **Package content store** (`PackageStorage:Directory`) — the installer bytes,
   addressed by SHA-256. Content-addressed, so it is safe to back up
   incrementally and to deduplicate.
3. **Configuration/secrets** — `/etc/endpoint-platform/secrets.env` on the
   deployed host (database and Redis credentials, `SECRET_PROTECTION_KEY`, and
   `RECOVERY_ESCROW_KEY`). Store these in your secret manager, never in the
   backup of the database itself. **A dump restored without this file cannot
   decrypt a single escrowed BitLocker recovery password.**

Redis is deliberately *not* on the backup list: losing it logs users out and
cold-starts caches, nothing more.

### Backup (logical dump)

```bash
# Run as the owner role. Schedule this; keep encrypted, offsite copies.
pg_dump --format=custom --no-owner \
  "host=<host> port=<port> dbname=endpoint_platform user=endpoint_owner" \
  > endpoint_platform_$(date +%Y%m%d).dump

# Package content: any file-level snapshot/rsync of PackageStorage:Directory.
```

The audit trail is append-only and enforced by database triggers and revoked
privileges (see [ADR-0004](adr/0004-append-only-audit-trail.md)); a logical dump
captures it intact. Restore re-creates the triggers via the migration job.

### Restore

```bash
# 1. Create an empty database owned by the owner role.
createdb -O endpoint_owner endpoint_platform

# 2. Restore the dump.
pg_restore --no-owner --dbname=endpoint_platform endpoint_platform_YYYYMMDD.dump

# 3. Re-apply runtime grants (idempotent) so the restricted app role has exactly
#    its privileges and the audit immutability grants are correct.
ENDPOINTPLATFORM_Database__ConnectionString="<owner conn>" \
ENDPOINTPLATFORM_Database__RuntimeRoleName="endpoint_app" \
  dotnet run --project server/Migrations

# 4. Restore the package content directory to PackageStorage:Directory.
```

Always run the migration job after a restore: it re-applies the runtime grants
and audit-immutability protections, which a plain `pg_restore` under a different
role can leave in a weaker state.

### Restore verification

- `GET /health/ready` on both APIs returns 200.
- An agent heartbeat succeeds (device recognised, credential accepted).
- `GET /admin/v1/reports/summary` returns sane counts.
- An `UPDATE` against `audit_log_entries` as the app role is **rejected** (proves
  the immutability triggers survived the restore).

## Credential and token rotation

- **Enrollment tokens** expire and are single- or multi-use with a max-use count.
  Rotate by issuing a fresh token (`platform.enrollment_token.issue`) and
  revoking the old one (`platform.enrollment_token.revoke`); revocation is
  immediate and audited. Never reuse a token across trust boundaries.
- **Device credentials** are rotated by re-enrollment: a re-enrolling machine has
  its old credentials revoked and a fresh one issued in the same transaction. To
  force rotation for a single device, offboard then reactivate it (below); the
  machine re-enrolls for a new credential.
- **Administrator sessions** are opaque and server-revocable. Disabling a user
  rotates their security stamp and invalidates every outstanding session
  immediately.

## Offboarding a device

Offboarding (`device.retire`, high-risk, audited) revokes every active credential
and retires the device: it can no longer heartbeat, upload inventory, receive
tasks, or re-enroll on the old credential. It is **logical and reversible** — it
does not wipe the machine. Reactivation returns the device to service, after
which the machine must re-enroll for a fresh credential.

A destructive remote wipe is intentionally not implemented: it would require its
own guarded, explicitly-confirmed agent-side executor (see the Phase 14 note in
[architecture.md](architecture.md) and [ADR-0005](adr/0005-no-shell-execution-in-agent.md)).

## Local account management

Managing Windows local accounts (including Standard User <-> Administrator)
requires the agent to hold administrator privilege on the endpoint.

- **Installed as a service** the agent runs as **LocalSystem**, which satisfies
  this. No configuration change is needed.
- **Run interactively for development** it inherits the launching user's token, so
  it must be started from an elevated shell. An unelevated agent reports a genuine
  access-denied failure on the task rather than silently doing nothing.
- Do **not** weaken UAC, VBS or HVCI to make this work. Elevation of the service
  identity is the supported path.

Passwords for create-user and reset-password never reach PostgreSQL. They are held
in Redis under `endpointplatform:secret:`, AES-GCM sealed, for 15 minutes, and are
redeemed exactly once by the target device. Set `SecretProtection:Key` (base64
32-byte) in configuration so in-flight secrets survive a restart and work across
instances; without it a process-local key is generated and unredeemed secrets are
invalidated by a restart (the task fails safely and can be re-issued).

If Redis is unavailable, create-user and reset-password are **refused** rather than
downgraded — every other local-account operation is unaffected.

**Device scope**: an administrator's permissions apply only to devices within their
assigned scope. New administrators start with no scope and reach nothing until
granted either specific device groups or organization-wide scope. Administrators
that predate this model were migrated to organization-wide scope by the
`LocalAccountManagementAndDeviceScope` migration.

## Background jobs

- **Task expiry sweeper** (Admin host) runs every minute and expires tasks whose
  deadline passed while Queued or Delivered. This guarantees that a task for an
  *offline* device (which never polls) still transitions to `Expired` rather than
  firing late when the machine reappears. It processes bounded batches, logs how
  many it expired, and never crashes the host on a failed tick. It is backed by
  the `device_tasks(expires_at)` index.

## Scale notes

- **Hot query paths are indexed** from the initial schema: device lookup by
  `(organization_id, machine_identifier)` and `(organization_id, last_seen_at)`;
  task claim by `(device_id, status)` and expiry by `(expires_at)`; the audit
  trail by `(organization_id, occurred_at)` and several actor/target/time
  composites. New inventory tables carry a per-device unique or covering index.
- **The runtime role cannot perform DDL** and has `SELECT, INSERT` only on the
  audit table — a compromised app credential can neither rewrite history nor add
  indexes. Schema changes go through the migration job as the owner role.
- **Agent load is pull-based**: agents poll on a server-dictated heartbeat
  interval; there is no inbound connection to a device and no server-initiated
  push, so fleet growth is bounded by poll frequency, which the server controls.
- **Task hand-out is capped** per poll (`MaxTasksPerPoll`), so one backlogged
  agent cannot pull thousands of tasks in a single request.
- **Package content is content-addressed**: identical bytes are stored once per
  organization, and the store deduplicates on upload.

## Observability

- Structured logging (Serilog) with a correlation id per request, surfaced in the
  `X-Correlation-Id` response header and quoted in error problem-details so a user
  report can be traced to a request.
- `GET /health/live` and `GET /health/ready` on both hosts; readiness checks
  PostgreSQL and Redis.
- `GET /admin/v1/reports/summary` is the single fleet rollup (device health,
  patch/security posture, policy compliance, task throughput, active packages).
