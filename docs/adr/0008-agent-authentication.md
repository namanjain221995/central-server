# ADR-0008: Agent enrollment and authentication (Phase 1)

Status: accepted (Phase 1)

## Context

Each endpoint needs its own server-verifiable identity, established through a
controlled admission process, revocable individually, with no fleet-wide shared
secret. The spec offers "mTLS or equivalent device-specific asymmetric identity".

## Decision

### Enrollment
- Admin issues an **enrollment token**: name, expiry (≤ 30 days), max uses
  (1–10,000). The 256-bit secret is generated server-side, shown exactly once,
  stored only as SHA-256.
- The agent presents the token over TLS with hostname, SMBIOS machine
  identifier, agent version, OS description.
- The server validates (hash lookup → expiry → revocation → remaining uses,
  with PostgreSQL `xmin` optimistic concurrency so two agents cannot both take
  the last use), then creates the device and issues its credential atomically.
- **Re-enrollment** (amended 2026-10-24): a machine identifier matching an
  **active** device no longer re-enrolls on the token alone — that would let
  one leaked-but-valid token plus a non-secret SMBIOS UUID revoke the real
  device's credentials and hand its identity to the caller. The attempt parks a
  pending re-enrollment for **administrator approval** (24 h, refreshed by the
  agent's 5-minute retries, which consume no token uses); the retry after
  approval updates the existing device and **revokes its previous
  credentials**. Approving requires device scope over the matched device, not
  just `device.enroll`. The rule is uniform — an honest reinstall waits exactly
  like a hostile request, because the server cannot tell them apart. Retired
  devices do not match; a retired machine enrolls again as a new device.
- All refusals return an indistinguishable 403 (tested) — including "approval
  pending", so active machine identifiers cannot be probed; reasons go to the
  audit trail, which records refused attempts as `Denied` security signals.

### Ongoing authentication
- The credential is an opaque pair `keyId (128-bit) . secret (256-bit)`, sent
  in the `X-Agent-Credential` header over TLS. Server stores SHA-256 of the
  secret and compares in constant time.
- A dedicated header, not `Authorization: Bearer`, so agent credentials can
  never be confused with or replayed as administrator tokens.
- Revocation is a row update; retired-device credentials fail closed even
  before explicit revocation (device status is checked on every request).
- On the endpoint, the credential is stored DPAPI-protected (LocalMachine
  scope, extra entropy, write-then-rename) in a directory ACL'd to
  SYSTEM/Administrators when the service is elevated.

### Why an opaque credential instead of mTLS client certificates now
The properties that matter — per-device identity, individual revocation, no
shared secret, hash-at-rest, TLS-protected transport — are identical for this
phase, without operating a CA, CRL/OCSP distribution and renewal windows.
The model migrates cleanly: the credential row becomes certificate metadata
(thumbprint replacing secret hash) if mTLS is adopted at the transport layer
later. Revisit when the platform terminates TLS itself in production (Phase 15).

### Trust and validation notes
- The SMBIOS machine identifier is a **dedup hint, not authentication** — it is
  spoofable by design; identity comes only from the credential.
- Server heartbeat responses carry the desired interval, so cadence is tuned
  centrally; `last_seen` uses only the server clock.
- Heartbeats are deliberately not audited per-event (volume would bury real
  events); enrollment, re-enrollment and every refusal are.

## Consequences

- A database leak yields no enrollable or replayable material.
- One compromised endpoint's credential identifies and revokes exactly that
  endpoint.
- The enrollment token is the bootstrap trust anchor: whoever holds an unused
  token can enroll a hostile machine. Mitigated by expiry, use caps, revocation
  and one-time display; operational handling guidance belongs to deployment
  docs.
- No replay protection beyond TLS within the credential scheme itself (no
  nonce/timestamp signing). Acceptable while transport is TLS-only; revisit
  with request signing if any non-TLS hop ever appears.
