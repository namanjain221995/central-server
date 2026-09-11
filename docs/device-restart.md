# Restart Device

An administrator can restart a managed device now, or after a delay of up to
one hour. This document is the contract: what is sent, what the device does
with it, what the console shows, and what is deliberately not promised.

## The timer, in one sentence

**The delay is the grace period the device hands to Windows, and Windows counts
it down itself from the moment the device executes the task.**

There is no other clock. The server does not compute a deadline for the
restart, the agent does not sleep, and nothing has to be reconciled between two
machines' idea of the time. The only absolute time in the whole flow is the one
the agent reports *back* — the moment Windows said it would act — and that is
what the console displays.

## What is sent

`POST /admin/v1/devices/{id}/actions/restart`, permission `device.restart`,
device-scope-checked (an out-of-scope device is not found, the same answer every
device route gives). The body is optional:

```json
{ "delaySeconds": 600 }
```

| `delaySeconds` | Result |
|---|---|
| absent, or `0` | Restart now — the device gives the signed-in user a 30-second warning |
| 30 – 3600 inclusive | Restart after that many seconds, counted from when the device executes the task |
| 1 – 29 | **400.** Shorter than the warning but not zero; refused rather than rounded up |
| negative, over 3600, non-integer, overflowing | **400.** Nothing is queued |

The accepted range is defined once, in `RestartGrace` (Domain), and the API,
its tests and the console all read from it.

**Why one hour.** Every deployed agent clamps the grace period to 3600 seconds
before calling Windows. A server that accepted more would promise a later
restart than any endpoint delivers, and the machine would go down an hour before
the administrator was told. So the server refuses what the agent would silently
shorten. Raising the ceiling is an agent change first and a server change
second, never the other way round.

**Why "now" is thirty seconds.** An immediate restart still lets the signed-in
user save their work, and lets the agent record the result before the machine
goes down. Zero would do neither. This is what the restart action has always
meant here; the timer did not change it.

The queued task is the existing `RestartDevice` type with the existing
`RestartOrShutdown { graceSeconds, message }` payload. No new task type, no new
payload field, no minimum agent version: every agent that has ever shipped
executes this task with this payload.

One restart in flight per device: a second request while one is Queued or
Delivered is answered **409** naming the first. Two would not restart the
machine twice — Windows refuses a second shutdown while one is scheduled, and
the agent reports that refusal honestly — but the second task would sit in the
list as a failure the operator did not mean to create.

## Expiry: the safety bound

The task expires **15 minutes** after it is queued (the catalogue's TTL for
`RestartDevice`). That bounds only how long the device has to *receive* the
task:

- **Not picked up in time:** the claim marks it Expired and it is never sent.
  A device coming back online after the deadline does not restart on a request
  nobody is still waiting for.
- **Picked up in time:** the device hands the countdown to Windows and reports
  back within seconds. The expiry never races the countdown, because the
  countdown is not the agent's to expire.

The deadline now travels to the agent as `expiresAt` on the claimed task. The
server never hands out an expired task, so this is belt and braces: if a
restart somehow reaches the executor after its deadline — a long stall between
claim and execution, a badly skewed clock — the agent refuses it without
touching the machine and says why.

## What the device does

`RestartTaskExecutor` (agent) → `IDeviceControl.RestartAsync` →
`InitiateSystemShutdownExW` with the grace period, the message for the
signed-in user, `bForceAppsClosed = false` and `bRebootAfterShutdown = true`.
No process is launched, no shell is invoked (ADR-0005); `SeShutdownPrivilege` is
enabled on the service's own token first.

Four outcomes, each reported honestly:

| Outcome | `succeeded` | Meaning |
|---|---|---|
| `Scheduled` | true | Windows accepted the request. `restartAt` is when it will act. |
| `Expired` | false | The task's deadline had passed. Windows was not asked. |
| `AlreadyInProgress` | false | Windows reported `ERROR_SHUTDOWN_IN_PROGRESS` (1115): a restart or shutdown was already scheduled, so *this task's timing was not applied*. |
| `Failed` | false | Windows refused, or the privilege could not be enabled. The Win32 error is in the message and `code`. |

"Succeeded" means **Windows accepted the restart**. It does not mean the device
has restarted, and the console does not say so: the device's next heartbeat is
the proof it came back.

## Exactly once

Claiming a task marks it Delivered under a row version (`xmin`). Two overlapping
polls for the same device — a retried request, two agent instances during an
upgrade, a replayed request — cannot both receive the task: the second writer's
`SaveChanges` fails, it returns nothing, and the task is delivered once. A later
poll finds nothing Queued. This is the platform's existing claim, not a
restart-specific mechanism; the restart tests pin it at the service level, over
HTTP, and with two genuinely concurrent requests.

## What the console shows

The Restart button opens a dialog titled **"Restart *device*?"** with the delay
chosen in the open — *Now*, 1, 5, 10, 15, 30 minutes or 1 hour — and the choice
repeated back in a sentence before the destructive button:

- *Restart the device immediately. The signed-in user gets a 30-second warning.*
- *Restart the device in 10 minutes, counted from when the device receives the task.*

The task is then tracked to its result. The stages, and how they map onto the
existing task model:

| Shown | Server status | Meaning |
|---|---|---|
| Queued | `Queued` | Waiting for the device to check in |
| Executing | `Delivered` | The device has it and has not yet reported |
| **Scheduled** | `Succeeded`, `restartAt` in the future | Windows accepted it; the countdown is running |
| Succeeded | `Succeeded`, `restartAt` passed | Windows accepted it and the moment has passed |
| Failed | `Failed` | With the agent's reason |
| Expired | `Expired` | The device never picked it up in time; nothing restarted |
| Cancelled | `Cancelled` | Cancelled while still Queued |

*Scheduled* is the one stage the generic model lacks, and the reason the
device task list now exposes `resultJson` and `expiresAt`. Older servers that
send neither fall back to the generic wording.

## Audit

Queueing writes `task.queue.restartdevice` with the actor, the device, the
required permission and the payload — which is the requested timing and the
message the user will see. Nothing else is in it: no credential, no token, no
machine detail beyond hostname and id. The result is on the task row.

## Known limitations

- **The ceiling is one hour**, for the reason above. It is an agent limit
  surfaced honestly, not a product decision.
- **"In 10 minutes" counts from receipt, not from the click.** If the device is
  offline the countdown starts when it reconnects — within the 15-minute expiry
  — or never. The dialog says so.
- **Success is Windows accepting, not the device restarting.** A restart Windows
  accepted can still be cancelled locally by an administrator on the machine
  (`shutdown /a`). The platform does not observe that; the device's heartbeat
  does.
- **`AlreadyInProgress` is a failure for this task**, even though the machine
  will restart: the timing asked for was not applied, and reporting success
  would hide that.
