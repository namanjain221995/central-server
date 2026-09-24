# Agent pilot safety: never pilot on a production-enrolled machine

> **The gate script this runbook calls (`infra/ubuntu/assert-pilot-machine-is-safe.sh`)
> was removed with the native deployment kit on 2026-09-24 and has no container
> equivalent yet.** The *rule* it enforced still stands and is the point of this
> document: never install a pilot agent on the machine that runs the platform.
> Check that by hand until the gate is reinstated — it is in git history.

An agent pilot repoints a machine's agent at an isolated pilot server. This
document exists because that is more dangerous than it looks, and because the
danger is invisible to every check made on the pilot side.

## The rule

**Use a machine that has never been enrolled in production.** Not "a machine
that is not currently enrolled". Never enrolled.

Run the gate before installing a pilot agent, and treat a non-zero exit as a
stop:

On the **production host**, with the candidate machine's identifier and name:

```bash
sudo bash infra/ubuntu/assert-pilot-machine-is-safe.sh \
  --machine-id <UUID> --hostname <NAME>
```

Read those two values on the candidate machine first:

```powershell
(Get-CimInstance Win32_ComputerSystemProduct).UUID
$env:COMPUTERNAME
```

It asks production, read-only, and refuses when:

| Condition | Result |
|---|---|
| Machine identifier exists in production, any status | **FAIL**, unconditional |
| Hostname is an Active production device, different machine identifier | **FAIL** unless `--approve-hostname-collision --reason "<why>"` |
| Production cannot be reached, or answers unexpectedly | **FAIL** — it never assumes a pass |

A machine-identifier match is always fatal and has no override, because a shared
machine identifier means it is the same machine.

## Why checking the pilot database is not enough

The pilot database cannot know that a machine belongs to production. It has
never seen it. Asked whether a machine is safe to use, it can only ever say yes.

Every pilot-side signal stays green through the entire failure:

- the pilot device enrolls and reports inventory correctly;
- the pilot database contains exactly one new device, as intended;
- production receives no task, no token, no audit entry, and no write of any
  kind, so an audit of the production database also comes back clean.

Meanwhile the production endpoint has gone dark. Only production can answer the
question that matters, so only production may be asked.

## What happened on 2026-09-11

The feature being piloted below (application removal) was subsequently withdrawn,
and its code is gone. The incident and the rule it produced are not: they are
about piloting an agent build on a machine that already belongs to production,
which is a hazard of any agent pilot and has nothing to do with what was being
tested.


`DESKTOP-PJCC143`, machine identifier `5A4839FE-996A-4748-9411-7EA29DC6978A`,
was chosen as the disposable VM for the 1.10.0 RemoveApplication pilot. It was
already an Active production device (`01a03a45-847f-704c-a3ca-c7c8c4c4dce5`,
agent 1.7.0, enrolled 2026-08-25), and had been heartbeating production until
16:47:28 UTC — 59 minutes before it enrolled into the pilot at 17:46:55 UTC.

The 1.10.0 pilot MSI shares the agent's `UpgradeCode`, so it performed a major
upgrade over 1.7.0. The agent then enrolled against the pilot server and wrote a
new DPAPI credential, overwriting the production one. From that moment the
machine could no longer authenticate to production.

Nothing was written to production. Its device row was untouched: still 1.7.0,
still Active, `enrolled_at` unchanged. The damage was entirely on the machine,
and the only visible symptom in production was a `last_seen_at` that stopped
advancing while every other device kept reporting.

The pilot database was checked before enrollment and was clean. Production was
not checked. That omission is the whole incident.

## Recovering a machine that was piloted by mistake

Re-enrollment matches on `(OrganizationId, MachineIdentifier, Status == Active)`
in `AgentEnrollmentService`. So long as the production device row is still
**Active**, the original device ID is recovered rather than duplicated:
`Device.ReEnroll` updates it in place, every prior credential is revoked, a fresh
one is issued, and the audit records `device.re_enroll`.

This makes recovery a normal, supported operation. Do not edit the production
database, mint credentials or tokens by hand, or bypass enrollment. Do not
retire or delete the production device to tidy the incident away: retiring it
would stop it matching, and the machine would come back as a **new** device ID,
losing its history for no reason.

1. Install the production-built agent MSI on the machine — the one whose
   `serverBaseUrl` is the production URL. Never the pilot package.
2. Remove the pilot override, or it will win over the baked URL:
   `Remove-ItemProperty -Path HKLM:\SYSTEM\CurrentControlSet\Services\EndpointPlatformAgent -Name Environment`
3. Supply a production enrollment token issued through the console.
4. Restart the service and confirm production shows the **original** device ID
   with a fresh `last_seen_at` and the new agent version.

If the production row has already been retired, accept the new device ID; do not
try to graft the old one back on.

## Keeping the safeguard out of production's way

`assert-pilot-machine-is-safe.sh` runs four `SELECT` statements against the
local production database, in a session PostgreSQL itself holds read-only
(`default_transaction_read_only=on`), and has no code path that writes. It is a pilot-side gate that reads production, not a
change to production behaviour: no server code, schema, container or
configuration is involved, and nothing about it ships to an endpoint.
