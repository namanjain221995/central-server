# Restart Management

Restart a whole department at a chosen moment, and call it off, for the whole
department or for chosen devices, before or after the restart has gone out.
This document is the contract: what a schedule is, when the devices hear about
it, what cancelling does at each stage, and what is deliberately not promised.
It builds on [device-restart.md](device-restart.md) (one device) and
[device-groups.md](device-groups.md) (a group); it changes neither.

## Departments are groups

A department is a device group, with the Groups page's membership and
authorization. "All Devices" is the devices in no other group, exactly as on
the Groups and Chrome pages. Nothing here defines membership; a device moved
into a department before the restart goes out is included, one moved out is
not, and an administrator scoped to some groups sees and schedules only those.

## A schedule, in one sentence

**The server holds the restart until shortly before the moment, then sends
every online member the ordinary restart task with that lead time as the
warning Windows shows.**

So a scheduled restart has two clocks, deliberately:

1. **The server's**, until `restartAt - warning`. A schedule is a row and
   nothing else. No device has heard anything, and cancelling is deleting a
   plan.
2. **Windows'**, after that. The sweeper queues a `RestartDevice` task per
   online member with `graceSeconds = warning`, each device hands the
   countdown to Windows exactly as for a single restart, and the device's
   task says how it went. There is no "scheduled restart" task type: a
   department restart of six devices is six ordinary restart tasks.

The warning is 5 minutes by default (`RestartSchedules:WarningSeconds`), or
the whole delay when the delay is shorter: "restart in 2 minutes" hands
Windows 2 minutes and goes out on the next sweep. It is clamped to what every
deployed agent accepts as a grace period, 30 seconds to one hour.

## Offline-safe restarts (agent 1.15.0+)

A device on agent 1.15.0 or later does not wait for the sweep. **The moment the
schedule is saved, the server queues a `ScheduleRestart` task for every active
member on 1.15.0+, online or not**, with a deadline of the restart moment.
The device claims it on its next check-in (within seconds when online), stores
it in `armed-restarts.json` under the agent's ProgramData state directory, and
reports `Armed`. From then on the device needs no network: 5 minutes before the
moment (the warning) its own scheduler hands Windows the usual countdown.

- **Offline when scheduled.** The task waits in the queue. If the device checks
  in before the moment, it is armed then; if not, the task expires and nothing
  restarts.
- **Off or asleep at the moment.** A device that comes back within 15 minutes
  of the moment restarts with the full warning; later than that, the restart is
  skipped and dropped, because a restart hours late is a surprise.
- **Clock skew.** The claim carries the server's time, and the agent places
  the moment on its own clock from the difference, so a device whose clock is
  wrong still restarts at the server's moment.
- **Older agents** are unchanged: they get the ordinary restart from the sweep
  shortly before the moment, and only if online then.
- **The sweep still runs** at `dispatchAt` for everyone without an armed task.
  If the server misses it, the armed devices still restart; the rest are
  recorded as `SkippedMissed` and the schedule shows Dispatched rather than
  Missed.

Because an armed task "succeeds" when the device stores it, not when it
restarts, the page judges the result from the device's boot time: agents
1.15.0+ send `bootedAt` on every heartbeat and the server keeps
`devices.last_boot_at`. Booted after the moment means `Restarted`; online again
more than 15 minutes after the moment without a new boot means `NotRestarted`.

Cancelling still works, through the device: a queued armed task is withdrawn
where it sits, and an armed one gets a `CancelRestart` that removes it from the
device (or aborts the countdown if it has begun). **A device that is offline
cannot be told**: cancel it and it keeps the restart until it next checks in;
if that is after the moment, it has already restarted.

## What is accepted

`POST /admin/v1/restart-schedules`, permission `device.restart`:

```json
{ "groupId": "…", "delaySeconds": 7200 }
```

| `delaySeconds` | Result |
|---|---|
| 60 to 604800 (one minute to seven days) | **201** with the schedule |
| anything else | **400** |
| a group outside the caller's scope, or unknown | **404** |
| the department already has a pending schedule | **409**, naming it |

The console offers 1, 2, 5, 10, 15, 30 minutes, 1 hour, 2 hours, or any whole
number of minutes or hours within the range. One pending schedule per
department: to change the time, cancel and schedule again. The rules live once,
in `RestartScheduleDelay` (Domain), and the API, its tests and the console read
from it.

## The sweep

`RestartScheduleSweeper` runs in the Admin host every 15 seconds
(`RestartSchedules:SweepIntervalSeconds`) and dispatches every pending schedule
whose `dispatchAt` has passed, **each in one transaction**: the schedule becomes
Dispatched together with every task it queued, or nothing changes and the next
tick tries again. For each active member of the group, in hostname order:

| The device is | Outcome | A task is queued? |
|---|---|---|
| excluded by an administrator | `Excluded` | no |
| offline (no heartbeat within 180 s) | `Offline` | no |
| outside the scheduler's scope now | `NotAuthorized` | no |
| already restarting from elsewhere | `AlreadyInProgress` | no |
| retired since, or its agent refused | `NotEligible` | no |
| online and eligible | `Queued` | yes, on the scheduler's behalf |

The scheduler's own authority is re-checked per device at dispatch, not at
scheduling: a scope removed, or an account disabled, in between means the
device is not restarted on their behalf. Offline devices are skipped, as a
group restart skips them, and are **not** restarted when they come back; the
page says so before the restart is confirmed.

**Missed.** A schedule found more than 5 minutes
(`RestartSchedules:MissedAfterSeconds`) past its dispatch moment, because the
server was not running, is marked Missed and nothing is sent. A restart hours
after it was expected is a surprise, not a maintenance window.

## Cancelling

`POST /admin/v1/restart-schedules/{id}/cancel`, permission `device.restart`.
The body is optional: `{ "deviceIds": [ … ] }` cancels for those devices only;
no body cancels for the department.

### Before the restart goes out

Nothing has reached a device, so:

- **Department:** the schedule becomes Cancelled. Final; nothing was ever sent.
- **Devices:** each is recorded as an exclusion and skipped at dispatch. The
  rest of the department still restarts. A device excluded twice is one
  exclusion; a device not in the department is reported `NotInSchedule`.

A cancel that lands in the same instant as the sweep is settled by the
schedule's row version: if the sweep won, the cancel is carried out through
the devices instead, as below, so the administrator never gets "try again".

### After it has gone out

Each device is handled by where its restart is, and the answer is per device:

| The device's restart is | What happens | Reported as |
|---|---|---|
| Queued (device not checked in yet) | the task is cancelled where it sits | `CancelledBeforeDelivery` |
| Delivered, or accepted with the moment still ahead | a `CancelRestart` task is queued | `CancelRequested` |
| accepted, on an agent before 1.14.0 | nothing can reach the countdown | `Unsupported` |
| past its moment, failed, expired or cancelled | nothing a device can undo | `NothingToCancel` |
| already being cancelled | left alone | `AlreadyRequested` / `AlreadyCancelled` |

`CancelRestart` is the undo of `RestartDevice`: the agent calls
`AbortSystemShutdown`, which Windows honours up to the moment it begins
shutting down, and reports `Cancelled`, `NothingToCancel` (Windows had nothing
pending: the restart already happened, or was aborted locally) or `Failed`
with the Win32 error. Unlike a restart, a late cancellation is still executed:
aborting what is still pending is always the safe choice. On `Cancelled` the
server marks the restart task **Cancelled** too, because "Succeeded" meant
Windows had accepted it and that is no longer the truth. The signed-in user's
restart notice changes to "Restart Cancelled" and goes away after a minute.

**What is not promised.** A cancellation reaches a device on its next heartbeat,
about 15 seconds. A device whose countdown has less than that left may restart
before the cancellation arrives; the device then reports `NothingToCancel` and
the page shows `Cancel failed` with that reason, or `Cancel failed` with "did
not pick up the cancellation in time" if it never answered. A device that lost
its network after accepting the restart cannot be reached and will restart. The
page never says "cancelled" about a device that has not confirmed it.

## What the console shows

**Restart Management** (Configuration) is one page: a department selector, the
department's pending or latest restart, and its recent history.

Before dispatch each device is `Will restart`, `Offline now` (skipped if still
offline at the moment) or `Cancelled` (excluded). After dispatch the state is
the device's restart task, in the words the Restart dialog already uses:
`Queued`, `Executing`, `Counting down` (Windows accepted; `restartAt` ahead),
`Restarted` (`restartAt` passed, which still only means Windows accepted the
request), `Failed`, `Expired`, `Cancelled`; plus `Cancel requested`, `Cancel
failed` and `Cannot cancel` (agent too old) from a cancellation, and `Skipped
(…)` for devices not targeted. A restart that has not happened is never green.

The cancel checkboxes are offered only where the server would honour them:
any device before dispatch; after it, a device whose restart is still ahead
and, once Windows has accepted it, only if its agent can abort. The page polls
every 10 seconds while anything is pending or in flight.

## Audit

- `restart_schedule.create` — who, which group, delay, warning, `restartAt`,
  `dispatchAt`.
- `restart_schedule.cancel` — who, department or which devices, and the
  per-device outcomes.
- `restart_schedule.dispatch` (system actor) — on whose behalf, the grace, and
  the per-device outcomes; plus the ordinary `task.queue.restartdevice` entry
  per task, under the scheduler's name.
- `restart_schedule.missed` (system actor, failure) — when it was due and when
  it was found.
- `task.queue.cancelrestart` / `task.result.cancelrestart`, and
  `task.cancel.restartdevice` (agent actor) when a device's abort retires its
  restart task.

Hostnames, outcomes and times only: no credential, no payload text.

## Storage

Three tables, added by migration `RestartSchedules`:

- `restart_schedules` — the plan; one Pending row per group (partial unique
  index), row-versioned by `xmin` so the sweep and a cancel cannot both win.
- `restart_schedule_exclusions` — devices taken out of a pending plan.
- `restart_schedule_devices` — what dispatch did to each member and how its
  cancellation went; the live state is the task the row points at.

Deleting a group deletes its schedules, as it deletes its scopes. Retiring a
device leaves its rows as history.

## Configuration

| Setting | Default | Meaning |
|---|---|---|
| `RestartSchedules:WarningSeconds` | 300 | Lead time, and the warning Windows shows (30–3600) |
| `RestartSchedules:MissedAfterSeconds` | 300 | How late a dispatch may still go out (60–86400) |
| `RestartSchedules:SweepIntervalSeconds` | 15 | How often due schedules are looked for (5–300) |

Admin API only, as `ENDPOINTPLATFORM_RestartSchedules__WarningSeconds` and so
on in the container's environment. Validated on start.

## Known limitations

- **No recurring schedules.** Every schedule is one moment. A weekly restart
  is a later feature, not a missing option.
- **The moment is "now plus a delay", not a clock time.** The console turns the
  delay into the time it means and shows it before confirming.
- **Offline devices on older agents are skipped, not caught up.** The rule
  every group action follows. Agents 1.15.0+ are armed in advance instead; see
  above.
- **An armed device that is offline cannot be cancelled** until it checks in.
- **A device switched off for more than 15 minutes past the moment** does not
  restart late; it is reported `NotRestarted` once it is back.
- **Cancellation after dispatch needs agent 1.14.0.** Older agents accept a
  restart and cannot be told to abort it; the page says so per device, and the
  Agent page is where to update them.
