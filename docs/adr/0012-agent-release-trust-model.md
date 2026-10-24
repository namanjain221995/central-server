# ADR-0012: Agent release trust modes — Internal by default, Public opt-in

Status: accepted

## Context

The platform ships its own agent. A published agent release is fetched by every
enrolled machine and handed to Windows Installer, which runs it as SYSTEM.
Whoever can put bytes into that channel owns the estate, so publishing needs a
gate — and the gate has to be one this deployment can actually satisfy.

The first implementation required a CA-issued Authenticode signature before any
release could be published. That is the correct gate for software distributed to
the public. It is the wrong gate here: Techsara is one company, on a private
network, managing its own controlled PCs, with no code-signing certificate. The
result was an agent build (1.4.1) that could be uploaded, stored and verified but
never published, and a fleet left on older agents indefinitely — the security
control blocked the security fix.

Three options were on the table:

1. Block self-update until a certificate is bought.
2. Add a `RequireCertificate = false` configuration flag.
3. Name the deployment model explicitly and make the requirement follow from it.

## Decision

**A two-member trust mode, `AgentReleases:TrustMode`, defaulting to `Internal`,
with `Public` available by configuration.**

Internal is *not* "Public with the check disabled". It is a mode in which the
Authenticode path **does not execute**: no signature stream is read, no
certificate is examined, no publisher is compared. `ReleasePublishVerifier`
returns before `IAuthenticodeVerifier` is reached, and a test asserts the
verifier is never called on that path.

**What does not vary by mode** — the checks the whole model rests on run first,
in both modes, and a build failing any of them is refused whatever it is signed
with:

- the artifact is present in the content-addressed store;
- it is a Windows Installer package (an OLE2 compound file — a structural shape
  check, not a cryptographic one);
- its bytes, re-read from disk at publish time, still hash to the SHA-256 **the
  server computed** when it stored them. A client-supplied hash is an advisory
  transit cross-check only;
- the caller is an authenticated administrator holding `Software.Deploy`;
- the release is a Draft, the lifecycle is one-way, and the transition is
  audited in the same transaction;
- transport is HTTPS with full TLS validation, and the agent re-computes the
  SHA-256 over the downloaded bytes and refuses a mismatch before scheduling
  anything.

**What Public adds:** a valid Authenticode signature chaining to a trusted root,
carrying the Code Signing EKU, whose subject contains
`AgentReleases:ExpectedSignerSubject`. The signer is read from the artifact and
never accepted from the uploader — there is deliberately no signer field in the
upload form. The API refuses to start in Public mode with no publisher
configured, rather than discovering it at the first publish.

`AuthenticodeVerifier` and its tests are **retained**, not deleted, so reaching
Public is a configuration change rather than a code change.

Both the console and the agent state plainly that no signature is checked under
Internal, rather than implying a refusal that will not happen.

## Consequences

- **Integrity, not provenance.** The SHA-256 proves the bytes installed are the
  bytes the server stored; it says nothing about who built them. Authenticity
  rests on the administrator who uploaded them, the authorization that permitted
  it, and the audit entry naming them.
- **An Internal release is trusted by this platform, for these machines — never
  by Windows.** SmartScreen, AppLocker and WDAC treat the MSI as an unsigned
  installer, because it is one. This is a limitation to accept, not a setting to
  work around on the endpoint.
- Every Internal publish records a **null signer**. That is the contract, not a
  gap, and nothing downstream may treat it as a defect.
- A stolen `Software.Deploy` session can publish a hostile build in either mode;
  Public additionally requires the publisher's signing key. That is the security
  difference the mode buys, and why the switch exists.
- Neither mode addresses a compromised build pipeline handing a malicious
  artifact to a legitimate administrator — the hash then matches, because it is
  computed over exactly those bytes. See T8 in the threat model.
- **Out of scope, unchanged:** the software-package install path keeps its
  *mandatory* Authenticode signer pin (ADR-0005 amendment), as does driver
  installation. This mode governs agent releases only.

## Alternatives rejected

- **A `RequireCertificate = false` flag.** A global that silently turns a
  security control off, with nowhere to record *what the deployment is* and no
  way to reason about which checks a given environment actually applies. A mode
  is a statement about the deployment; every check that depends on it is written
  against the mode by name.
- **A self-signed certificate as a stand-in.** Trust-shaped without being trust.
  It would satisfy the gate while proving nothing, and would teach operators to
  expect a green signature field that means nothing.
- **Blocking self-update until a certificate exists.** Leaves the estate on old
  agents, which is the larger real risk — the unpatched fleet is a certain
  exposure, the unsigned-installer risk a contingent one.

## Note — 2026-10-24: step-up on the dangerous routes; Public-mode latents fixed

**Step-up is now required on publish, artifact replace and update-agent.** The
stolen-`Software.Deploy`-session consequence above, plus the 1.16.0 accident (a
broken localhost build published fleet-wide that uninstalled the agent from the
machines it updated), motivated gating the three actions that put SYSTEM-level
code on the path to devices the way a recovery-key reveal is gated: a per-user
rate limit (5 actions per 15-minute fixed window, consumed before judging, fail
open without Redis), then re-verification of the caller's own password through
the sign-in verifier (wrong attempts count toward the account lockout), then the
action, with a mandatory justification recorded in the success audit entry.
Denials are audited as `agent_release.publish_denied` /
`artifact_replace_denied` / `update_agent_denied`. Permissions are unchanged;
revoke deliberately carries no step-up — withdrawing a build is the safe
direction. See `AgentReleaseStepUpService` and `ReleaseActionRateLimiter`.

**Two latent defects that would have broken a future flip to Public were
fixed.** (1) The compose file passed no
`ENDPOINTPLATFORM_AgentReleases__ExpectedSignerSubject`, so setting
`AGENT_RELEASE_TRUST_MODE=Public` would have crash-looped admin-api at options
validation with no way to wire the publisher through; the variable
(`AGENT_RELEASE_EXPECTED_SIGNER`) now flows through compose and
`generate-env.sh`. (2) The signature stream-name constant carried its mandatory
U+0005 prefix as a raw, invisible control byte that every editor rendered as
plain `"DigitalSignature"` — it was misread exactly that way in review, and one
innocent cleanup would have silently made Public mode refuse every genuinely
signed MSI as unsigned, with the test suite staying green because the test
artifact writer shares the constant. The prefix is an explicit `\u0005` escape
now, a regression test pins the literal value, and a second test proves a
stream named plain `DigitalSignature` is treated as not signed.

**Still outstanding before Public can be enabled:** a CA-issued code-signing
certificate; the signing step and its secrets in CI; the fleet staged onto
agent 1.20.1 or later (earlier agents' `WinVerifyTrust` call used the
cache-only flag that fails fresh machines with 0x800B010A); and a dry-run
confirming the certificate subject as .NET-on-Linux renders it matches
`ExpectedSignerSubject` — the substring-match semantics are deliberately
unchanged until a real certificate exists to pin a cross-platform test against.

## References

- `server/Infrastructure/Agents/ReleasePublishVerifier.cs` — the gate and the mode
- `server/Infrastructure/Agents/AgentReleaseOptions.cs` — validation, Public start-up refusal
- `server/Infrastructure/Agents/AgentReleaseStepUpService.cs` — step-up on the dangerous routes
- [threat-model.md](../threat-model.md) — T10
- [agent-updates.md](../agent-updates.md) — operator-facing behaviour
- [ADR-0005](0005-no-shell-execution-in-agent.md) — the unchanged software-package signer pin
