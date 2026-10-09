# Threat model

This platform ships privileged software to every managed endpoint and
concentrates control over those endpoints in one place. That makes the platform
itself the highest-value target in the estate: whoever controls it controls
every enrolled machine. Design decisions below are driven by that fact.

Status: written at Phase 0. Sections marked *(future)* describe controls whose
implementation phase has not arrived yet; they are recorded now so the design
does not paint itself into a corner.

## Assets

| Asset | Why it matters |
|---|---|
| Agent fleet | Runs as LocalSystem on every managed computer |
| Agent release channel | A published MSI is installed as SYSTEM on every enrolled machine |
| Device credentials | Each proves an endpoint's identity to the platform |
| Admin credentials/sessions | Grant control over devices via the platform |
| Audit trail | The evidence record for every privileged action |
| Platform database | Devices, identities, policies, audit |
| Enrollment tokens *(future)* | Convert an unknown machine into a trusted device |
| Task/script pipeline *(future)* | Executes privileged actions on endpoints |

## Adversaries considered

1. **External attacker** with network access to the APIs but no credentials.
2. **Malicious or compromised endpoint** holding a valid device credential.
3. **Compromised administrator workstation / phished admin** (browser-level).
4. **Limited insider**: a low-privileged platform user (Helpdesk, Auditor)
   attempting to exceed their role.
5. **Application-level compromise**: an attacker who has obtained the API
   process's database credential (e.g. via SSRF/RCE in the app).

A malicious DBA / full database-host compromise is **out of scope** for
database-level controls (see Known limitations).

## Key threats and mitigations

### T1. Cross-boundary credential replay
*An agent credential used against admin endpoints, or vice versa.*

- Admin API and Agent API are separate processes on separate ports with
  separate authentication schemes (ADR-0001).
- Architecture tests fail the build if either host references the other, and
  API tests assert agent routes 404 on the admin host.

### T2. Audit trail tampering
*An attacker (or embarrassed insider) rewrites history.*

Three independent layers, all verified by automated tests against real
PostgreSQL and manually against the live database:

1. `AuditImmutabilityInterceptor` — fails fast on accidental mutation in code.
2. The runtime database role holds only `SELECT, INSERT` on
   `audit_log_entries` (verified: `UPDATE`/`DELETE`/`TRUNCATE`/DDL all denied).
3. `BEFORE UPDATE/DELETE/TRUNCATE` triggers raise exceptions **regardless of
   role** (verified against the owner role, including the TRUNCATE path that
   row-level triggers do not cover).

The domain type itself exposes no mutators (reflection-verified by test).

### T3. Secret leakage through logs and telemetry
- Serilog request logging never records query strings or bodies.
- Health endpoints return check names/statuses only — the default writer's
  exception text (which can include connection strings) is replaced.
- Problem-details responses carry a correlation id, never stack traces.
- EF sensitive-data logging is refused outside Development by code, not
  convention.
- The domain's `PlatformUser` only ever holds an encoded hash; there is no
  code path that accepts a plaintext password into an entity.

### T4. Header/log injection via client-controlled values
- `X-Correlation-Id` is the only client value echoed into headers/logs. It is
  length-capped (128) and restricted to `[A-Za-z0-9._-]`; anything else is
  discarded in favour of the server-generated id. Unit tests cover CRLF,
  control characters and over-length payloads.

### T5. Cross-origin abuse of the credentialed Admin API
- CORS is an explicit origin allow-list; the API refuses to start without one.
  Wildcard + credentials is impossible by construction. Tests assert that
  non-listed origins receive no CORS headers.
- The Agent API has no CORS at all — browsers have no business there.

### T6. Privilege escalation within the platform
- Authorization is permission-based; role names are never checked in code.
- Built-in role grants are reconciled against the code catalogue on every
  deployment — a grant added directly in the database is **reverted** (covered
  by an integration test).
- Auditor is provably read-only and Helpdesk provably cannot perform
  high-impact operations (unit tests pin both).
- Separation of duties: IT Administrator cannot manage platform users/roles.

### T7. Compromised application database credential (adversary 5)
- The runtime role cannot alter audit history (T2 layers 2–3).
- The runtime role has no DDL: it cannot drop the audit triggers, create
  tables, or modify the schema.
- The migration job runs under the owner role in a separate, short-lived
  process — the owner credential is never present in an API process.

### T8. Supply-chain / dependency risks
- Central package management pins every dependency version in one reviewed
  file (`Directory.Packages.props`) with transitive pinning enabled.
- The shared `Contracts` assembly (shipped to every endpoint) is
  dependency-free, enforced by test.

### T9. Agent as an injection vector *(largely future)*
The agent will run as LocalSystem, so its request-handling surface is a
privileged boundary:

- The agent codebase cannot launch processes or embed PowerShell — the
  assemblies do not reference process creation at all, enforced by tests
  (ADR-0005). Windows work is done through APIs with no command line.
- **(Implemented, Phase 1)** Enrollment: scoped, expiring, limited-use tokens
  stored only as hashes; per-device credentials; no fleet-wide shared secret;
  refusals indistinguishable on the wire and audited with reasons; optimistic
  concurrency on the last remaining use. See ADR-0008.
- **(Implemented, Phase 1)** Device credential stored DPAPI-protected at
  machine scope with extra entropy; directory ACL'd to SYSTEM/Administrators
  when elevated; corrupt blobs degrade to re-enrollment, never crash.
- **(Implemented, Phase 1)** Re-enrollment revokes prior credentials; retired
  devices fail closed on both heartbeat and re-enrollment.
- *(Phase 10)* Only typed tasks; scripts require hash + signature + recorded
  approval. No arbitrary command endpoint will exist.
- The agent refuses to disable TLS certificate validation outside Debug builds
  (enforced in options validation, not convention).

### T10. A hostile or altered agent release

*Whoever can put bytes in the release channel owns the estate: an agent MSI is
installed by Windows Installer as SYSTEM on every machine it reaches.*

The gate is a **trust mode**, not a switch (`AgentReleases:TrustMode`) — a
`RequireCertificate = false` flag would be a global that quietly turns a control
off, whereas a mode is a statement about the deployment, and every check that
depends on it is written against the mode by name.

- **Internal** (the default, and what this deployment runs). Publishing requires
  an authenticated administrator holding `Software.Deploy`; a Draft release; the
  artifact present in the content-addressed store; the bytes being a Windows
  Installer package; and those bytes, re-read from disk at that moment, still
  hashing to the SHA-256 **the server computed** over what it stored. A hash
  supplied by the client is a transit cross-check only (mismatch → 400). The
  package's own ProductVersion, read by the server from the MSI's Property
  table, must be the version the release declares, and the same bytes cannot
  be registered or published as more than one release (ADR-0013) — so a row
  cannot say one version while its artifact is another, which is how a
  "1.5.1" carrying the 1.5.0 package once came to exist. The agent
  re-computes the SHA-256 over the bytes it downloaded and refuses a mismatch
  before anything is scheduled. Transport is HTTPS with full TLS
  validation; publish, revoke and every queued update are audited. There is no
  UI-side gate — a direct API call is refused identically.
- **Public.** Everything Internal requires, plus a valid Authenticode signature
  that chains to a trusted root, carries the Code Signing EKU, and whose subject
  contains the configured publisher. The API refuses to start in this mode with
  no publisher configured. The signer is read from the artifact and never
  accepted from an upload. The agent then re-verifies that signature on the
  endpoint (`WinVerifyTrust` plus a signer-subject pin) before installing.

**What Internal does not guarantee.** No CA-issued Authenticode signature is
required and **none is read** — the Authenticode verifier is not on the Internal
code path at all, and a test asserts it is never called. Every Internal publish
therefore records a null signer, and the agent, told there is no signer,
performs **no signature check**: it logs that it is installing on hash
verification alone, and installs. Consequently:

- **Integrity, not provenance.** The SHA-256 proves the bytes installed are the
  bytes the server stored. It says nothing about who built them. The
  authenticity claim rests entirely on the administrator who uploaded them, the
  authorization that permitted it, and the audit entry naming them.
- **Absence of Authenticode does not mean Windows trusts the MSI.** SmartScreen,
  AppLocker and a WDAC policy will treat it as an unsigned installer, because
  that is what it is. An Internal release is trusted by *this platform, for
  these machines* — never by the operating system. Nothing here weakens or
  should be worked around in any Windows setting.
- **A stolen `Software.Deploy` session publishes a hostile build in either
  mode.** Public raises the bar to also holding the publisher's signing key.
  That is precisely why the mode exists and why reaching it is configuration
  rather than a code change.
- **Not addressed by either mode:** a compromised build pipeline handing a
  malicious artifact to a legitimate administrator. The hash then matches,
  because it is computed over exactly those bytes. See T8.

Unaffected by the mode in either direction: the **software package** install
path keeps its *mandatory* Authenticode signer pin (ADR-0005 amendment), as does
driver installation. The trust mode governs agent releases only. See
[ADR-0012](adr/0012-agent-release-trust-model.md).

## Known limitations (accepted at this phase)

| Limitation | Risk | Planned remedy |
|---|---|---|
| Audit records stay on the same PostgreSQL host | Table owner/superuser can drop triggers and rewrite history | Phase 15: ship audit stream to append-only external storage |
| Enrollment token is a bearer bootstrap secret | Holder of an unused token can enroll a hostile machine into management | Expiry ≤30 days, use caps, revocation, one-time display; operational handling guidance in deployment docs |
| Development runs plain HTTP on localhost | Local traffic unencrypted | TLS termination is a deployment concern; HSTS+redirect already active outside Development |
| Login rate limiting is in-memory, per instance | Resets on restart; not shared across replicas | Phase 15: Redis-backed limiter (account lockout already covers the account dimension) |
| Single-node PostgreSQL/Redis | Availability, not security | Phase 15 |
| Agent releases are published without Authenticode under the Internal trust mode | Provenance rests on administrator authorization and audit, not a signature; Windows itself treats the installer as unsigned | Switch `AgentReleases:TrustMode` to `Public` once a code-signing certificate exists — configuration, not code (T10) |

## Standing rules (all phases)

1. Never store plaintext passwords; never log secrets, tokens or key material.
2. Every privileged mutation produces an audit event before it is reported
   successful.
3. Authorization decisions happen server-side, against permissions.
4. All input — including from enrolled agents — is untrusted and validated.
5. The agent initiates all connections; no listening sockets on endpoints.
6. No arbitrary command execution surface, on server or agent, ever.

## Local administrator posture (Milestone 11b)

**What it answers.** Whether an endpoint's interactive accounts are standard
users, derived from the accounts the agent reports rather than stored as a
cached verdict — the facts it needs already live on the account rows, and a
second copy could disagree with them.

**Three verdicts.** `Unknown` is distinct from `Compliant` and never collapses
into it. A machine that has reported nothing is not evidence of good posture,
and rendering it as clean would overstate how much of the estate has been
checked. The API returns a null `lastReportedAt` alongside it, so the absence of
evidence is visible rather than implied.

**Two exclusions**, both answering whether a person could actually sign in and
act as an administrator:

- *Disabled* accounts confer nothing interactively. Still reported: a disabled
  administrator is one setting away from being live.
- *Built-in* accounts are excluded because the platform refuses to delete or
  disable RID 500 precisely so an organization cannot lock itself out. Counting
  an account we protect by policy would mark every Windows machine permanently
  non-compliant with no available remedy, and a finding nobody can act on is not
  a finding.

Excluded is not hidden — every discounted account is returned with its reason.

**Built-ins are matched by RID, never by name.** Renaming the built-in
Administrator is standard hardening and the names are localized, so a name-based
rule would fail on a German install or after a rename — silently, in the
direction of excusing an administrator. A malformed SID resolves to *not*
built-in, which counts the account towards the verdict rather than excusing it:
the SID arrives from an endpoint, so it is input, and a garbled report must not
be able to produce a clean bill of health.

**It evaluates; it does not remediate.** No path in 11b removes an account from
Administrators, and the console offers no control to do so. Remediation is
Milestone 12, behind its own explicit authorization.

**Audit.** `localuser.posture.changed` is raised on a *transition*, detected at
inventory ingest where the previous and new account sets are both in hand. There
is deliberately no per-evaluation event: the verdict is derived on read, so
evaluating is not a mutation, and an endpoint reporting every few minutes would
otherwise bury real transitions under thousands of identical rows in an
append-only store. The first report after enrollment is not treated as a
transition — that is evidence arriving, not the machine changing.

**Limitation, stated in the payload itself.** Administrator rights held only
through a nested group are not detected; membership is read from direct
membership of the local Administrators group. The limitation travels with the
verdict so a caller acting on it can see its scope without reading this
document.

### Acceptance - Milestone 11b, closed 2026-08-27

Signed off on `LAPTOP-LVCHEQ2H` (Agent 1.1.4) with a real Windows account, using
`scripts/Invoke-M11bAcceptance.ps1`. The endpoint carried an enabled interactive
administrator (`Techsara`) and a disabled built-in Administrator throughout.

| | Verified |
|---|---|
| C1 | A standard user is reported, is not an offender, and does not change the device verdict |
| C2 | Promoting it makes it a counted offender, named in `interactiveAdministrators` |
| C3 | RID 500 discounted correctly, by RID rather than by name |
| C4 | Disabling it discounts it *and keeps it reported*, with its reason |
| C6-C12 | Account removed; no pre-existing account altered; no unexpected account; **no task created** |
| C13 | USB state unchanged - all 12 records identical in instance, policy, enforcement and connection state |
| C14-C15 | DeviceId, MachineIdentifier and agent version unchanged; service never stopped |

**The load-bearing result is C7 with C12.** 11b observes and reports: no
pre-existing account changed, and evaluating posture queued no task. A reporting
feature that quietly remediated would be a worse defect than one that reported
wrongly, and would stay invisible until somebody's machine changed under them.

**Limitations, recorded rather than waived:**

- **C5 (Unknown)** needs an endpoint that has never reported. Not reproducible on
  a machine already in inventory; covered by domain and API tests.
- **A1 / A3** - the positive audit paths. Zero `localuser.posture.changed` events
  were written during the run, which is *correct*: the device-level verdict never
  transitioned, because `Techsara` held it Non-Compliant from before the run to
  after it, and the event fires only on a transition. The acceptance therefore
  proved the absence of spurious events but could not exercise the positive case.
  Demonstrating it physically needs an endpoint whose only interactive
  administrator is the test account.
- **A2 (no duplicates)** - PASS, server-side: 11 inventory reports across the
  run produced **zero** posture-change events.

**Two acceptance-script defects were found and fixed; no product code changed.**
The first conflated account-level and device-level compliance, asserting a device
could become Compliant while an unrelated enabled administrator existed - the
implementation was right and the criterion was wrong. The second recorded a
*count* of USB records instead of the records themselves, which made a later
discrepancy impossible to diagnose after the fact. The comparison now keys on
`instanceId|policy|enforcementState|isConnected`, sorted, excluding inventory
timestamps that change by design, and refuses to compare a response whose shape
is not what it expects rather than guessing.

## Administrator password change (Milestone 12-S)

**STATUS: COMPLETE** - deployed as `daa51d5`, verified in production 2026-08-27.

Until this slice the platform could create its first administrator and then never
change that credential: no change-password endpoint, no platform-user
management, and a bootstrapper that refuses to run once a Super Administrator
exists. The only recovery paths were outside the product.

**Session invalidation needed no new machinery.** `SetPasswordHash` already
rotates the account's security stamp, and `AdminSession` pins a snapshot of that
stamp which `IsUsable` compares against the user's current value. Every session
therefore dies the moment the password changes - the caller's included. Sessions
are additionally revoked explicitly, but only so the reason is visible to
someone auditing the table later; **the stamp remains the single source of
truth**, not a competing one.

**The current password is re-verified server-side** even though the caller holds
a live session. A session proves who signed in; it does not prove who is at the
keyboard now, and that is what stops a borrowed session from locking an account's
owner out of their own account. A wrong current password counts towards the same
lockout the sign-in path uses, so an authenticated attacker guesses no more
cheaply than an anonymous one - but deliberately **not** behind the per-address
login rate limiter, which exists to blunt credential stuffing and would otherwise
let one noisy client stop an administrator from securing their account.

**Policy weights length over composition**: a 12-character floor, no digit,
symbol or case requirement, and a 256-character ceiling that bounds hasher work.
Composition rules reliably produce `Password1!` and a sticky note. The floor is
pinned by test against the bootstrapper's, so the two cannot drift and quietly
make the weaker one the real policy.

### Acceptance evidence

Verified in production after deployment:

| | |
|---|---|
| Health | live/ready/dashboard all 200 |
| UI in served bundle | `index-D6luwssF.js`, all nine markers present |
| Endpoint live | `POST /admin/v1/auth/change-password` - 400 on mismatch, 401 unauthenticated (not 404) |
| Migration head | `20260826140247_UsbEnabledPolicy` - **no migration introduced** |
| Production data | devices/local_users/usb_dev/usb_req/tasks/releases all unchanged |

The production administrator password was then **rotated manually through the
dashboard**, and the rotation verified from the database:

- `password_updated_at` moved from `2026-08-19 20:42:05` to `2026-08-27 17:58:30`
- exactly one audit row: `platform.user.password_changed`, Success
- **80 of 81 sessions revoked, 1 live** - every pre-existing session died,
  including the one that made the change; the single live session is the
  operator's subsequent sign-in with the new password

**No credential material exists anywhere in the repository, the audit trail or
the logs.** The audit row's state documents contain only `sessionsRevoked`
counts - no password, no hash, no length, no prefix. A test scans the whole row
for both passwords and for `argon`/`pbkdf2` markers, because an append-only trail
cannot take a leak back.

### Known limitation

Repeated wrong *current* passwords count towards the account lockout, which is
reachable from an authenticated endpoint. With a single administrator account,
someone holding a stolen session could lock the only administrator out for the
lockout window. That is the correct trade against brute-forcing and matches the
sign-in path, but it is worth knowing while the deployment has one administrator.
A second account removes the concern; platform-user management does not exist yet.

## Drivers and BitLocker (Milestone 13)

Two read-only inventories and one gated write capability. The write half --
installing an approved driver package -- exists in the server and agent but has
never been exercised against real hardware; see the deferred criteria below.

**Driver health does not treat a disabled device as a fault.** This platform
disables devices itself: USB storage restriction (Milestone 11a) sets
`CM_PROB_DISABLED`, so folding problem code 22 into the fault counts would make
every correctly-restricted endpoint in the estate report as damaged, and would
bury real faults underneath. `Disabled` is therefore its own state, counted and
shown but never a fault.

**Unknown is never healthy, and never unencrypted.** A device whose problem code
could not be read is `Unknown`; a machine whose BitLocker query was refused
reports `AccessDenied` rather than an empty volume list. An agent that loses its
elevation must not make an encrypted estate look like plaintext.

**Superseded (M13, decision J-4): no BitLocker recovery key is collected.** The
original position is kept here rather than deleted, because it was correct on its
own terms and the reasons it was given up should be readable beside it:

> The agent calls `GetKeyProtectors`, which returns protector identifiers, and
> never `GetKeyProtectorNumericalPassword`, which returns the 48-digit password.
> No column in `device_bitlocker_volumes` can hold key material, no API field
> exposes one, and the dashboard has no control that could request one. What is
> recorded is that a recovery protector exists and the GUID naming it; a
> protector GUID unlocks nothing.

That guarantee was absolute and cheap: an agent that cannot read a recovery
password cannot leak one, whatever else goes wrong on the endpoint.

**Current position: automatic recovery-password escrow is approved and the agent
does call `GetKeyProtectorNumericalPassword`.** The reversal was made
deliberately, to escrow recovery credentials without an administrator
transcribing 48 digits per machine, which did not scale and left most machines
with no filed key at all.

**The cost is aggregation, and it is material.** The platform now collects the
disk-unlock credential for every enrolled encrypted endpoint, automatically,
rather than holding the few an administrator individually chose to file. Two
consequences follow. The exfiltration value of the database rises sharply: it
becomes a single place from which an entire estate's disks can be unlocked. And
the limitation recorded under J-2 -- that an attacker holding both a database
dump and the host configuration can decrypt escrowed passwords -- now applies
estate-wide rather than to a handful of records.

What bounds it:

- **Plaintext never reaches any server process during ingestion.** The endpoint
  seals with AES-256-GCM under a per-record key, wraps that key with RSA-3072-OAEP,
  and uploads only the envelope. The Agent API, which every managed endpoint can
  reach, holds the public key alone and cannot decrypt what it stores. The private
  key stays in the Admin API, and plaintext returns only through the existing
  reveal path: permission, device scope, step-up password, rate limit, audit.
- **The sealing key is pinned at enrollment.** An agent seals only to the key
  matching the SHA-256 SPKI fingerprint it was given during authenticated
  enrollment, so impersonating the server does not yield recovery passwords.
  Trust-on-first-use was considered and rejected for exactly that reason.
- **Devices enrolled before this feature are not eligible** and must re-enroll.
  Their credentials carry no pinned fingerprint, and the agent will not read a
  recovery password without one. This is an operational cost accepted knowingly.
- **Retrieval is gated before it happens, not after.** Eligibility, the
  fingerprint, and whether the protector is already escrowed are all checked
  before Windows is asked for anything, so an ineligible or already-escrowed
  machine never materialises its password at all.

**Known limitation: the retrieved password cannot be reliably erased from
memory.** WMI returns it as a .NET `string`, which is immutable, garbage
collected, and may be copied by the runtime during heap compaction. Buffers used
during sealing are zeroed; that string cannot be, and no claim is made that it
is. The exposure is narrowed by never retrieving unless an escrow is actually
owed, and by using the value once rather than holding it.

**Driver installation is gated seven ways** and never through a shell: the
archive hash is verified before a single entry is extracted, extraction refuses
traversal and oversized or over-numerous entries, the catalogue signature and a
*mandatory* signer pin are checked before the driver store is opened, only
devices matching the package's hardware id are touched, a downgrade is refused
unless explicitly authorised, every affected instance is verified individually
afterwards, and a reboot requirement is reported rather than acted on. The agent
never reboots.

### Acceptance - Milestone 13, read-only path closed 2026-08-28

Run against `LAPTOP-LVCHEQ2H` (agent 1.3.0, device `01a01bc4-...`) with
`scripts/Invoke-M13Acceptance.ps1`, which mutates nothing by default.

**27 PASS / 0 FAIL / 10 DEFERRED.**

| | |
|---|---|
| Agent | reports 1.3.0; enrollment identity retained across the upgrade |
| Driver inventory | 200 of 200 present devices - matches `Get-PnpDevice -PresentOnly` exactly |
| Driver health | 0 faults; Windows independently reports 0 devices in Error |
| BitLocker | `Available`; C: `NotEncrypted` at 0%; TPM present and enabled -> `ReadyToEncrypt` |
| Recovery keys | no key field, no key-shaped value, protectors are GUIDs only |
| M11/M12 regression | USB, posture, elevations and local accounts all unchanged |
| Mutation | none - no driver installed, no task queued, no BitLocker change |

**Deferred, and why.** These are not passes and are not counted as any.

- **C7d - `CM_PROB_DISABLED` live verification.** No device currently reports
  problem code 22: the restricted USB storage device is not attached, and an
  absent device is not enumerated. The classification is covered by unit tests at
  every layer but has not been demonstrated on live data. Proving it needs that
  device physically connected and one inventory refresh.
- **C10-C18 - real driver deployment.** No driver package has been approved, so
  the hash gate, archive gates, catalogue signature and signer pin, hardware
  targeting, downgrade refusal, per-instance verification, reboot honesty and the
  stale/withdrawn/old-agent refusals have not been exercised end to end. Proving
  them needs a signed vendor package and a machine that may have a driver
  installed on it and be rebooted.

### Known limitations

Recovery-key **escrow** status is not determined. `Win32_EncryptableVolume`
exposes no reliable per-volume "backed up to a directory" property, and
`manage-bde` is a command-line tool this agent structurally cannot run
(ADR-0005). The readiness payload states this rather than guessing.

Driver health does not flag an outdated-but-working driver. A device with no
problem code is not a fault, whatever version it runs.

The 1.3.0 agent MSI is **unsigned**: no code-signing certificate exists, and the
build refuses to fabricate a self-signed stand-in that would look like trust
without being it. It was installed on one endpoint for acceptance under explicit
authorisation. That is a statement about *Windows-level* trust, not about
publishability — under the Internal release trust mode (T10) an unsigned build
is publishable, and every release the publish gate produces records a null
signer. What an unsigned MSI costs is SmartScreen/AppLocker/WDAC standing, not a
place in the release channel.

## USB portable devices (phones, MTP/PTP) — 2026-10-09

**The gap.** The storage control restricted removable mass storage only. A
phone in "File transfer" mode is not mass storage — Windows reaches it over MTP
through the Windows Portable Devices stack, with no drive letter — so the agent
classified it as `Other`, inventoried it, and let files move in both
directions. Found on a developer machine with a real phone: Windows had
recorded it as class `WPD`, service `WUDFWpdMtp`, compatible ID
`USB\MS_COMP_MTP` in one mode and `USB\MS_COMP_PTP` in the other; none of
those matched a rule.

**The fix.** Portable devices are a class of their own, recognised from the
device's descriptors and driver binding (ADR-0015), restricted by default by
the same per-instance disable as storage, and grantable at read/write only.
Composite phones (file transfer plus USB debugging) are restricted at the
composite parent, which takes the MTP and the ADB function down together; a
restricted composite is still recognised from the interfaces Windows recorded
for it, so it does not drop out of the policy table once disabled.

**Honesty about enforcement.** Every enforcement result is now read back from
Windows. A device is "Enforced" in the console only when the endpoint verified
the devnode state and found no disk or portable-device interface beneath it; a
change Windows deferred to the next restart is "Restart required"; a report the
agent could not verify, and every report from an agent older than 1.16.0, is
"Applied, not verified". The console never calls a device protected on the
strength of a call having returned.

**Not verified on hardware yet.** The phone that produced the evidence was not
attached while the change was built, and the development machine runs the
production agent, which must not be replaced by a test build. Classification
is covered by tests built from that phone's recorded properties; the real
block-both-directions acceptance (`docs/usb-control.md`, "Acceptance —
portable devices") is **NOT VERIFIED** until it is run on a designated test
endpoint with the phone attached. Until then the security objective is
implemented, not demonstrated.

**Remaining bypasses, stated.** USB tethering (the phone as a network adapter),
Bluetooth file transfer, Wi-Fi Direct, cloud sync and e-mail are not USB file
transfer and are untouched. The first-ever attachment of a given device has a
window of a few seconds before the agent disables it, as for storage. A local
administrator can re-enable the device; that is visible as Drifted, not
prevented. Charging through a restricted connection is not measured and not
claimed.
