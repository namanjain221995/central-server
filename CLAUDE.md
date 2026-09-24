# Read this before changing anything

Internal endpoint-management platform for organisation-owned Windows PCs. It can
reveal **BitLocker recovery keys** and run commands as **SYSTEM on every managed
machine**, so a mistake here is not a bug — it is a breach.

More than one person and more than one Claude session works on this repository.
Decisions below were made deliberately and have already been undone once by
accident. **If you are about to reverse one, ask first.**

---

## How it is actually deployed

**Docker Compose, from `deploy/docker/`, on an Ubuntu PC on the office LAN.**

A native systemd + nginx kit (`infra/ubuntu/`) was the original design. It never
completed on the real machine and was **removed** on 2026-09-24 so only one
deployment path exists. It is recoverable from git history if ever needed — do not
reintroduce it alongside Docker, because two live paths is how the wrong one gets
deployed.

```bash
ssh <user>@<box>
cd /opt/endpoint-platform/src/deploy/docker
docker compose ps
docker compose up -d          # bring the stack up
docker compose logs -f admin-api
```

Deployment is **manual and deliberate**. An autodeploy timer that polled GitHub
every 60 s and deployed on every push was installed and then **disabled on
purpose** — an unreviewed commit must not reach a machine that holds recovery
keys. Do not re-enable it without asking.

---

## Decisions that must not be quietly reversed

| Decision | Why |
|---|---|
| **Two API processes, never merged** (ADR-0001) | A stolen device credential must never reach an admin endpoint. Architecture tests fail the build if either host references the other. |
| **MFA is mandatory for every administrator** | Deliberate. Not opt-in, not Super-Admins-only. |
| **Cloudflare is DNS only — never proxied, never Tunnel** | Cloudflare terminates TLS at their edge. Revealed BitLocker keys, admin session tokens, agent credentials and provisioned passwords all cross in cleartext. `setup-tls-cloudflare.sh` forces `proxied:false` for this reason. |
| **No secrets and no site-specific identifiers in the repo** | It is a **public** repository. No hostnames, internal IPs, account names or personal e-mail addresses. Removed once already in `d8ced1a`. |
| **Manual deploy, not deploy-on-push** | See above. |

---

## Things that will bite you

**Three keys are unrecoverable if lost.** All live in the deployment's env file
(`deploy/docker/.env`) and nowhere else:
- `RECOVERY_ESCROW_KEY` — losing it makes **every escrowed BitLocker password
  permanently undecryptable**, discovered only when a machine will not boot.
- `RECOVERY_SEALING_PRIVATE_KEY` — the private half of the automatic-escrow
  pair; losing it makes every **automatically** escrowed password unreadable.
  `generate-env.sh` creates the pair once and never replaces it. A device
  enrolled *before* the pair existed carries no pinned fingerprint and reads
  "automatic escrow unavailable — re-enrollment required" until it re-enrols;
  that is how the missing pair was noticed in production.
- `MFA_TOTP_KEY` — losing it makes every authenticator enrolment unreadable and
  **nobody can sign in**.

None of them may ever reach the **Agent API**, which every managed endpoint can
talk to. `AgentApiKeyBoundaryGuard` refuses to start that process if any is
present. The Agent API *does* get `RECOVERY_SEALING_PUBLIC_KEY`: it only
encrypts, and the process must know which key endpoints seal to.
`SECRET_PROTECTION_KEY` *is* given to both, so never seal anything durable with
`ISecretProtector` — it also silently falls back to a random process-local key.

**A Release build of the Windows agent validates the server certificate against
the machine trust store and has no override.** A self-signed certificate means
**no PC can enrol**, however well the dashboard works in a browser. Use a
Let's Encrypt certificate obtained by DNS-01 (needs no inbound internet, so it
works on a private LAN) rather than the self-signed fallback.

**Mandatory MFA breaks test fixtures that create their own administrator.** An
un-enrolled account is confined to the enrolment screens and every other endpoint
answers 403. Call `AdminApiPostgresFixture.EnrolMfa(user)` after `SetPasswordHash`.
`MfaEndpointTests` deliberately does not — enrolment is what it tests.

**TOTP allows one sign-in per 30-second window per account** (replay protection).
Correct in production, wrong for a test suite; the fixture clears the high-water
mark before each sign-in. Do not weaken the guard itself — it is pinned by
`A_code_cannot_be_used_twice`.

**Migrations are forward-only.** A rollback restores binaries, not schema. A
destructive migration needs a dump first and a compensating migration.

---

## Running the tests

DB-backed suites need a **CREATEDB** role. `POSTGRES_SUPERUSER` in `infra/.env` is
`endpoint_owner`, which does **not** have it — using it fails every DB test with
`42501`. Use the `endpoint_test` role, and **PostgreSQL 17, not 18**: `ON DELETE
RESTRICT` raises `23001` on 18 and `23503` on 17, and two tests depend on it.

```
ENDPOINTPLATFORM_TEST_POSTGRES = Host=localhost;Port=5433;Username=endpoint_test;Password=...
ENDPOINTPLATFORM_TEST_REDIS    = localhost:6379,password=...
```

Expect **~2,650 passing**. A handful of `EndpointAgent.Windows.Tests` failures are
environmental on a developer machine (repo under `\Downloads\`, third-party
installers claiming odd install roots, the agent service holding
SessionNoticeLauncher). Treat any *other* failure as real.

Stop the running APIs before building on Windows, or the build fails on file locks.

---

## House style

Comments explain **why**, not what — especially where a choice looks wrong until
you know the reason. Several non-obvious decisions are recorded only in comments;
read them before "simplifying" the code they sit above.

Verify rather than assume. Prefer a check that can fail (a test, a probe, a real
request) over reasoning about what the code probably does.
