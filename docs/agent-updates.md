# Agent releases and self-update

How new agent builds are distributed, how an enrolled machine updates itself,
and exactly what is — and is not — guaranteed.

## The release model

An **agent release** is one distributable MSI build: version (three numeric
parts), platform/architecture (`windows/x64`), filename, a SHA-256 the server
computed over the bytes it stored, a signer subject (recorded only under the
Public trust model, from the artifact's own signature -- null under Internal),
release notes, and a one-way lifecycle:

```
Draft ──publish──▶ Published ──revoke──▶ Revoked   (terminal)
```

A revoked release can never be re-published; upload a fresh build instead, so
every audit entry refers to an immutable artifact.

### Release trust model

Techsara runs an **Internal** release trust model: one company, a private
network, controlled Windows PCs, no public distribution. Configured by
`AgentReleases:TrustMode` (`Internal`, the default, or `Public`).

Under **Internal**, publishing a release requires:

1. an authenticated administrator holding `Software.Deploy`;
2. the release exists and is a Draft;
3. its artifact exists in the content store;
4. the artifact is a Windows Installer package (an OLE2 compound file);
5. the bytes on disk, re-read at that moment, still hash to the release's
   recorded SHA-256;
6. the **ProductVersion inside the package** — read by the server from the
   MSI's own Property table — is the version the release declares. A package
   whose version cannot be read is refused, and the declared version is never
   rewritten to match;
7. **no other release, of any status, already records these bytes** as its
   artifact — one package is one release;
8. version and lifecycle rules hold (one release per platform/architecture/
   version; Draft → Published → Revoked, one way);
9. an audit entry is written, naming the actor.

Any failure answers **422** naming the check, the release stays a Draft, and
the refusal is itself audited with a category (`ProductVersionMismatch`,
`DuplicateArtifact`, …). There is no UI-side gate: a direct API call is refused
identically.

Checks 4, 6 and 7 are applied at **upload** as well, so a draft that says one
thing and holds another cannot be registered in the first place (answer:
**400**, naming both versions). Publishing still re-reads and re-checks
everything; upload-time validation is never trusted on its own. See
[ADR-0013](adr/0013-agent-release-artifact-agrees-with-metadata.md) for why
this exists.

**No CA-issued Authenticode certificate is required under Internal, and no
signature is read.** Not "checked and ignored" — the Authenticode verifier is
not on the Internal code path at all, and a test proves it is never called. An
unsigned build and a signed build publish identically, and neither records a
signer. `AgentReleases:ExpectedSignerSubject` has no effect in Internal mode and
may be left unset.

**What Internal does not mean.** An MSI published this way is trusted by *this
platform*, for *these machines*, on the strength of its hash, its transport, and
the administrator who published it. It is **not** thereby trusted by Windows:
SmartScreen, AppLocker or a WDAC policy will treat it as an unsigned installer,
because that is what it is. Public distribution would need the stronger policy
below, which can be switched on without touching the Internal path.

Under **Public**, everything above applies, and additionally the artifact must
carry an Authenticode signature (`\005DigitalSignature` stream) that is a
well-formed `SignedData`, verifies, chains to a trusted root under the server's
system trust store, carries the Code Signing EKU, and whose subject contains
`ExpectedSignerSubject`. Public mode refuses to start without a configured
publisher. What the server does *not* reproduce even in Public mode is
Authenticode's binding of the signature to the exact bytes under Windows
Installer's stream-hashing rules — that is enforced on the endpoint by
`WinVerifyTrust` before anything installs. Revocation is checked **nowhere**:
the server skips it by design (it may have no CRL/OCSP route), and the agent's
`WinVerifyTrust` call runs with revocation checking deliberately disabled
(`WTD_REVOKE_NONE` plus `WTD_REVOCATION_CHECK_NONE` in
`agent/EndpointAgent.Windows/WinTrust.cs`), so installs never depend on
revocation-infrastructure reachability. A revoked publisher certificate is
handled operationally — rotate `ExpectedSignerSubject` and revoke the affected
releases — not by a certificate status lookup.

Two values are server-derived in every mode and never accepted from an upload.
The **SHA-256** is computed over the bytes the server stores; a hash sent by the
client is only a transit cross-check (mismatch → 400). The **signer**, where a
mode verifies one, is read from the artifact's signature; a signer typed into
the form is discarded.

A draft's artifact may be **replaced** (`PUT /agent-releases/{id}/artifact`):
the hash is recomputed over the new bytes and the release must be re-verified
to publish. A published release's artifact is frozen — changing it means a new
draft, a new artifact, a new hash, and a new publish.

The agent's side is unchanged by the mode. Every downloaded MSI is hashed and
compared to the SHA-256 the server offered before anything is scheduled; a
mismatch is never installed. Transport is HTTPS with full TLS validation in
every mode. Internal deployment does not mean insecure transport. Uploading is
`Software.Deploy`; viewing and downloading are `Software.View`. The MSI bytes
live in the same content-addressed store as software packages, keyed by hash —
the store recomputes the SHA-256 while writing and refuses a mismatch, so a
release row can never describe bytes it does not have.

Releases are deliberately **not** software packages. Packages are per-tenant
and installed in-process by the agent; a release is platform infrastructure
with version ordering, and its installer must survive the agent's own death
(below).

## The update flow

```
administrator publishes a release
        ↓
dashboard shows "Update available" per outdated device
        ↓
administrator queues UpdateAgent (typed task; Software.Deploy; audited)
        ↓
agent claims the task and re-fetches release metadata over its own
authenticated channel — the payload is a claim, never an instruction
        ↓ refuse on any mismatch with the server's offer
agent enforces: strictly newer version, x64, then downloads
        ↓
SHA-256 over the actual bytes            → mismatch: DO NOT INSTALL
        ↓
release declares a signer (Public mode): Authenticode via WinVerifyTrust
+ signer-subject pin, against the signer the server verified at publish
                                         → failure: DO NOT INSTALL
        ↓
release declares none (Internal mode): there is no signature to check; the
agent logs that it is installing on hash verification alone, and continues
        ↓
state snapshot (credential + enrollment files → update-backup/)
        ↓
one-shot Task Scheduler entry: msiexec /i <verified file> /qn
        ↓ task result posted: "update started" — never "succeeded"
installer stops the service, upgrades, starts the new one
        ↓
new agent restores state from the snapshot if the installer removed it
        ↓
same DeviceId, same credential, new version on the next heartbeat
```

Task Scheduler is what makes this survivable: the upgrade stops the agent
service, so nothing running inside the agent can carry the install to
completion. The scheduled entry is executed by Windows as SYSTEM, decoupled
from the agent's lifetime. It is not a command channel — the executable is the
fixed system `msiexec.exe` and the only variable content is the path of a file
the agent itself just verified.

## Version policy

- Comparison is numeric, three parts (`1.0.10 > 1.0.9`; never lexicographic).
- **No downgrades and no same-version reinstalls** — enforced independently by
  the server (when queueing) and by the agent (when executing). Recovering from
  a bad release means publishing a *newer* fixed build, which also keeps the
  audit history linear.
- Unparseable versions are never "newer": every comparison fails closed.
- Updates are administrator-initiated per device. There is no automatic
  fleet-wide push; the release being published makes it *available*, not
  *mandatory*.

## Failure behaviour

| Failure | Outcome |
|---|---|
| download interrupted | partial file deleted; current install untouched |
| hash mismatch | refused before anything else is considered; file discarded |
| signature/pin failure *(only where the release declares a signer, i.e. Public mode)* | refused; file discarded |
| no signer declared *(Internal mode)* | **not a failure**: no signature is read, the agent logs it and installs on hash verification alone |
| downgrade / same version | refused before any download |
| wrong architecture | refused before any download |
| task vs server mismatch | refused before any download |
| install fails mid-upgrade | Windows Installer rolls the transaction back; the old version is restored from the cached MSI |
| device offline | task waits Queued; expires after its TTL if never claimed |
| agent dies holding the task | task expires via the sweeper; a late result is rejected |

There is no bespoke rollback beyond MSI's own transactional rollback, and none
is claimed: a failed *execute phase* restores the previous version; a machine
that wants the previous version after a *successful* install gets it by
publishing that build as a newer version number.

## Identity preservation

The state directory (`C:\ProgramData\EndpointPlatformAgent`) holds the DPAPI
credential and enrollment identity. From 1.1.0 the installer never removes it —
not on upgrade and not on uninstall. Removing a machine from management is a
server-side retire (which revokes the credential and makes the residue
useless); cleaning the directory afterwards is a documented manual step.

Belt and braces: before scheduling an install the agent snapshots its state
files, and the freshly started service restores any that went missing, then
consumes the snapshot. This exists for one concrete reason:

> **Known limitation — the 1.0.0 → 1.1.0 hop.** The 1.0.0 installer removed
> the state directory during upgrades (its uninstall ran `RemoveFolderEx`, and
> a major upgrade runs the old uninstall). A machine upgraded manually from
> 1.0.0 therefore re-enrolls — same device row via its machine identifier, but
> requiring a fresh approval. 1.0.0 agents also predate the `UpdateAgent`
> executor, so a task-driven update queued to one fails honestly as
> "Unsupported task type". The first hop is manual; every hop after 1.1.0 is
> remote and identity-preserving.

## Verified on real Windows

Both hops were run end to end against an enrolled physical machine on the
production deployment, not a test double.

**1.0.0 → 1.1.0 (manual, the documented one-time hop).** MSI downloaded from
the dashboard's Agent page and installed by hand. As predicted, the 1.0.0
uninstall had already destroyed the DPAPI credential, so the agent raised a
fresh enrollment request, which an administrator approved in the dashboard.
Result: back to Active/Online reporting **1.1.0**, resolved by machine
identifier to the **same DeviceId** — one device row, no duplicate — with
inventory and heartbeat resuming on cadence.

**1.1.0 → 1.1.1 (remote self-update, the mechanism this document describes).**
Release published, `UpdateAgent` queued from the dashboard. The agent
re-fetched the release metadata over its own authenticated channel, verified
version, architecture and SHA-256, scheduled the install, and reported *"Update
to 1.1.1 verified and started"* — never "succeeded", because the process
saying it was about to be stopped. The device returned online **40 seconds**
later reporting **1.1.1**, with:

- the same DeviceId and machine identifier,
- **the same agent credential** (identical key id before and after),
- no duplicate row and **zero pending enrollments** — no re-enrollment at all,
- inventory advancing afterwards (274 services, 60 processes, 6 users, 37 NICs).

Re-queueing the same version afterwards is refused with 409, so a duplicate
update task cannot be created against an already-current device.

The Milestone 9 actions that the Session 0 fix made possible were exercised on
the updated agent: `LockDevice` → *"Workstation locked."* and `SignOutUser` →
*"Interactive user signed out."*

## Building a release

`.github/workflows/build-agent-msi.yml` builds the MSI on a pinned
`windows-2022` runner using this repository's `build-msi.ps1` — the same script
used locally, not a second build definition — with WiX pinned to 5.0.2 and the
SDK taken from `global.json`. It runs on manual dispatch with a version and
server URL, reads the ProductVersion back out of the built package to confirm
it matches what was asked for, publishes the MSI plus a `SHA256SUMS.txt` as
artifacts, and prints the hash in the run summary.

It deliberately **does not publish or deploy**. Promoting a build stays a
human act: upload it on the dashboard's Agent page with that SHA-256, which the
server recomputes while storing the bytes.

## Signing

Signing is a property of the **trust mode**, not a missing feature. Under
Internal — the default, and what this deployment runs — no Authenticode
certificate is required, no signature is read at publish, and every published
release records a null signer. Under Public a signature by the configured
publisher is required at publish and re-verified by the agent before install.
Both paths are built and tested; which one runs is configuration.

- **Verification is fully built, and runs wherever a signature exists**:
  WinVerifyTrust plus a signer-subject pin, enforced by the agent whenever a
  release declares a signer. A release that declares none — every Internal
  release — has no signature to check: the agent logs that it is installing on
  hash verification alone and proceeds. It does not pretend to check a signature
  that is not there, and the console says the same rather than promising a
  refusal that will not happen.
- **Integrity does not depend on signing in either mode.** The SHA-256 is the
  server's, computed over the bytes it stored, re-checked over those bytes at
  publish and re-checked by the agent over the downloaded bytes before anything
  is scheduled. A mismatch is never installed.
- **The CI signing step already exists and is wired**, in
  `build-agent-msi.yml`. It activates only when both
  `AGENT_SIGNING_CERT_PFX_BASE64` and `AGENT_SIGNING_CERT_PASSWORD` repository
  secrets are set: the .pfx is written to the runner's temp directory, used by
  `signtool` with an RFC 3161 timestamp, verified, and deleted in a `finally`
  block. Nothing is echoed and no certificate or key is ever committed.
- With no certificate configured the build still succeeds and the artifact is
  named `…-UNSIGNED`, with the run summary saying so — a silent skip that
  produced an artifact indistinguishable from a signed one would be the
  dangerous outcome. Such a build is publishable under Internal, and is what the
  estate runs today.
- **To move to Public distribution**: obtain an Authenticode certificate, add
  those two secrets so CI signs the MSI, then set
  `AgentReleases:TrustMode=Public` and `AgentReleases:ExpectedSignerSubject` on
  the server. From that point publishing refuses an unsigned artifact, the
  server records the signer it read from the signature itself, and the agent's
  existing pin enforces publisher identity on every install. There is
  deliberately **no signer field in the upload form** and none is planned: the
  signer is a fact read from the artifact, never a value the party supplying the
  build declares. No self-signed certificate is used as a stand-in for real
  trust.
- **What signing does not change.** An Internal build is trusted by this
  platform for these machines, never by Windows: SmartScreen, AppLocker and WDAC
  treat an unsigned MSI as unsigned whatever mode this platform is in. See
  [threat-model.md](threat-model.md) (T10).
