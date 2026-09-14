# Device groups

A device group is a partition of an organization's devices. Groups decide which
policies and software reach a device, which administrators may act on it, and
which devices a group action is sent to.

## The model

**Every device belongs to exactly one group.** A device's group is
`devices.device_group_id`, a non-null foreign key to `device_groups`. Because it
is one required column, the database itself makes "no group" and "two groups"
impossible — there is no membership table to hold duplicates.

**Every organization has one built-in group, "All Devices".** It is where a device
lives until an administrator places it elsewhere, and where it returns when it is
removed from a group or its group is deleted. It cannot be renamed or deleted, and
devices cannot be removed from it, because there would be nowhere to put them. A
partial unique index guarantees one per organization.

> "All Devices" holds only the devices that are in **no other** group. Moving a
> device into "Developers" takes it out of "All Devices". The name is the
> product's; the semantics are a fallback partition.

**Membership is only ever changed by an administrator.** There is no automatic
grouping by hostname, operating system, agent version, discovery result or any
other rule. `DeviceGroupType.Dynamic` exists as an enum value and is not
implemented.

### How a device gets its first group

`DeviceGroupAssignmentInterceptor` runs on every save. It gives a newly created
organization its "All Devices" group, and places any newly created device that
names no group into its organization's "All Devices". It never overrides a group a
device already has and never reads anything about the device. It covers every path
that creates a device — enrollment, re-enrollment of a retired machine (which
creates a new row), tests — and the non-null foreign key means a context built
without it fails its first device insert rather than creating an ungrouped device.

### Names

Group names are trimmed, 1–200 characters, contain no control characters, are
unique per organization **case-insensitively** (`ux_device_groups_organization_id_lower_name`,
an expression index on `lower(name)`), and may not be "All Devices" in any casing.
Descriptions are optional.

## Lifecycle

| Operation | Behaviour |
|---|---|
| Create | Name plus optional devices. Selected devices move in from wherever they were. One transaction. Requires all-device scope (see below). |
| Rename | Custom groups only. "All Devices" answers 409. |
| Add devices | Each device moves out of its current group. Reported per device: `Moved`, `AlreadyInGroup`, `NotFound`, `ChangedConcurrently`. |
| Remove devices | Custom groups only. Each device returns to "All Devices". Devices not in this group are reported `NotInGroup` and left alone. |
| Delete | Custom groups only. Every device in the group — active or retired — moves to "All Devices", then the group row is removed, in one transaction. **No device is ever deleted, retired or offboarded.** |

Deleting a group also removes the administrator scope rows that named it (the
existing cascade), so a scoped administrator loses authority over those devices.
That is deny-by-default working as intended; it is audited.

### Concurrency

- **Moves are conditional on the group that was authorized:**
  `UPDATE devices SET device_group_id = @to WHERE id = @device AND device_group_id = @authorizedFrom`.
  If another request moved the device first, nothing is changed and the device is
  reported `ChangedConcurrently`.
- **Deleting a group while a device is moved into it** cannot orphan the device.
  The foreign key is `RESTRICT`: a device committed into the group after the delete
  moved the existing members makes the `DELETE` fail, the whole transaction rolls
  back, and the caller gets 409.
- **Two deletes of the same group** yield one success; the other reports not found.

These are covered by `DeviceGroupConcurrencyTests`, which hold real row locks in a
second connection to force each interleaving deterministically.

## Authorization

No new permissions. Reading groups needs `group.view`; changing them needs
`group.manage`. A group action needs the same permission its single-device action
needs.

**Group membership is an administrator's device scope.** A scoped administrator
may act on exactly the devices in the groups they are scoped to
(`DeviceScopeAuthorizer`); `PlatformUser.HasAllDeviceScope` is the only grant that
reaches every device. So moving a device between groups changes who may act on it,
and every move is authorized **at both ends**:

- authority over the device's **current** group — otherwise a scoped administrator
  could pull any device into their own group and grant themselves authority over it;
- authority over the **destination** group — otherwise they could push devices onto
  administrators who never agreed to manage them.

Remove and delete are moves to "All Devices", so they need authority over "All
Devices" too. A group the caller cannot act on — and any device in one — is
answered as not found, never as forbidden, so scope does not reveal what exists.

Only an administrator with all-device scope may **create** a group: scope is
granted per group, and nothing grants the creator scope over a group that did not
exist a moment earlier.

A scope row naming "All Devices" grants only the devices currently in it, not
every device.

## Group actions

`POST /admin/v1/groups/{groupId}/actions/{action}`

| Action | Permission | Per-device task |
|---|---|---|
| `restart` | `device.restart` | `RestartDevice` |
| `shutdown` | `device.shutdown` | `ShutdownDevice` |
| `lock` | `device.lock` | `LockDevice` |
| `signout` | `device.sign_out_user` | `SignOutUser` |
| `force-stop` | `task.execute` | Force Stop, through `ApplicationForceStopService` |
| `cancel-restart` | `device.restart` | Cancels queued `RestartDevice` tasks |

There is no Sleep: the platform has no sleep action for a single device, so it has
none for a group.

**There is no group task.** A group action is an orchestration on the server that
queues the ordinary per-device task for each target, with exactly the payload the
single-device route would queue. The agent never learns that groups exist. Each
task is claimed, executed, expired and reported on its own.

For every group action the server:

1. authenticates the administrator and checks the action's permission (endpoint);
2. confirms the group exists in the caller's organization and is in their scope;
3. resolves the group's active devices **from the database at queue time** — the
   request body names no devices, and any device ids in it are ignored;
4. decides which are online with the server's clock against each device's last
   heartbeat (`OfflineAfterSeconds`, 180 by default) — not the console's rendering;
5. re-checks authority over **each** device immediately before queueing it;
6. queues the task per device, saving each task with its audit entry;
7. writes one `group.action.<action>` audit entry with the timer, counts and
   per-device outcome (hostnames and outcomes only);
8. returns a per-device result.

Queueing is per device, not all-or-nothing: a device that cannot take the action is
reported and the rest proceed.

### Online devices only

Offline devices receive nothing and are reported `Offline`. They are not queued for
later and never count as having carried out the action. A device that went offline
after the console last showed it is judged offline when the action is queued.

Per-device queue outcomes: `Queued`, `Offline`, `AlreadyInProgress`, `NotEligible`
(retired since resolution, or an agent without the executor), `NotAuthorized`.

### Results

The server's `status` describes queueing only: `AllQueued`, `QueuedWithIssues`,
`NoEligibleDevices`, `NothingQueued`. What happened on each device is that device's
own task. The console follows every queued task and shows, per device and overall:

- **All succeeded** — every device in the group did the work;
- **Completed with issues** — some did; others were offline, busy or failed;
- **No eligible online devices** — nothing was sent;
- **Failed** — nothing succeeded and something genuinely failed.

An offline device is always an issue, never a success. A restart Windows has
accepted is **Restart scheduled** until its moment passes, and only then
**Restarted**.

## Group restart

The timer contract is the single-device one, unchanged:

| `delaySeconds` | Result |
|---|---|
| absent or `0` | Restart now, after Windows' 30-second warning |
| `30`–`3600` | That many seconds' grace |
| `1`–`29`, `> 3600`, negative, non-integer, overflowing, malformed | 400, nothing queued for any device |

Every online device receives the same `graceSeconds` and the same system-defined
message (`RestartGrace.MessageFor`), so a group restart payload is byte-for-byte a
single restart payload. A quoted number (`"60"`) is read as that number by the web
JSON defaults and then validated identically; the group and single-device routes
give the same answer for every quoted value. The body is bound by the framework,
so chunked and `Content-Length` requests are treated identically.

**The countdown runs on each device from when that device executes its task.**
There is no server-side sleep, no scheduler and no group clock. Devices that check
in later restart later.

**One active restart per device** is enforced by the partial unique index
`ux_device_tasks_active_restart_per_device`. A device that already has a restart
queued or delivered — from a single-device restart or another group restart — is
reported `AlreadyInProgress`, keeps its existing restart unchanged, and the rest of
the group proceeds. Concurrent group restarts leave every device with exactly one
active restart.

### Cancelling

Only an administrator, through the console, can cancel a restart, and only while
it is still **Queued**. Once delivered the agent may already have handed the
countdown to Windows, and this platform has no way to take it back; such a restart
is reported **Already delivered — too late to cancel**, never as cancelled.
Single-device cancellation is device-scoped as well.

The Techsara notice on the device has no cancel control. Note that Windows itself
still lets a user who holds `SeShutdownPrivilege` — interactive users on client
editions do by default — abort a pending shutdown with `shutdown /a`. Removing that
is a Windows policy decision outside this platform.

## The restart notice on the device

When Windows accepts a restart, the signed-in user sees a window — **Restart
Scheduled / Your IT administrator has scheduled this device to restart. /
Restarting in HH:MM:SS / Please save your work.** — alongside Windows' own shutdown
warning. Every word is a constant; nothing from the task, the server or the
dashboard can change it. It counts down to the moment Windows will act, shows
"Restarting now…" once that passes, and goes away two minutes later if the machine
is still running. It has an OK button that hides it and no control that cancels
anything; a hidden notice returns for the final minute. It does not depend on the
console being open.

How it is delivered across the LocalSystem-to-user boundary is recorded in
[ADR-0005's session-notice amendment](adr/0005-no-shell-execution-in-agent.md).

**It is there for whoever is signed in, from the moment the agent is.** Windows
starts the notifier at sign-in (machine Run key), and the service starts it for a
user already signed in when the agent is installed, updated or its service
restarts — once when the service starts, and again for any session still without
one when a restart is announced. So the first restart after an installation or an
update shows the notice too; nobody has to sign out and in. It is one per session
(a second copy exits), it ends when the service goes away and comes back with it,
and it closes when an installer's Restart Manager asks, so an upgrade can replace
the files it shares with the service.

It ships with the agent, so it reaches a device only when that device runs an agent
release that includes it. Until then the device shows Windows' own shutdown
warning, as before.

## API

| Method | Route | Permission |
|---|---|---|
| GET | `/admin/v1/groups` | `group.view` |
| GET | `/admin/v1/groups/{id}` | `group.view` |
| POST | `/admin/v1/groups` | `group.manage` |
| PATCH | `/admin/v1/groups/{id}` | `group.manage` |
| DELETE | `/admin/v1/groups/{id}` | `group.manage` |
| GET | `/admin/v1/groups/{id}/candidates` | `group.manage` |
| POST | `/admin/v1/groups/{id}/devices` | `group.manage` |
| POST | `/admin/v1/groups/{id}/devices/remove` | `group.manage` |
| POST | `/admin/v1/groups/{id}/actions/{action}` | per action, above |

Membership bodies are capped at 64 KB and 500 devices; action and rename bodies at
8 KB. Kestrel refuses a larger body with 413 before it is read.

## Audit

`group.create`, `group.rename`, `group.delete`, `group.move_device` (per device,
with the previous and new group names), and `group.action.<action>` (timer, target
and offline counts, per-device outcome by hostname). Each queued task also has its
own `task.queue.*` entry. No credentials, tokens, paths or payload text.

## Migration

`20260914182707_ExclusiveDeviceGroups` creates "All Devices" per organization,
copies each existing membership onto its device, puts every other device —
including retired ones — into its organization's "All Devices", and only then makes
the column required and drops `device_group_memberships`.

It **refuses rather than guesses**, changing nothing, if a device is in more than
one group, if two group names differ only by case, or if a group is already named
"All Devices". Each would otherwise force a silent choice that could drop a policy
assignment or an administrator's authority. Production on 2026-09-14 had none of
these: one group (MKT, one device), four ungrouped devices, no scope rows.
`ExclusiveDeviceGroupsMigrationTests` runs the migration against production's
schema head seeded with that exact topology, every refusal, and the rollback.
