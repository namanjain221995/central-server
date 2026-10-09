# USB and peripheral control

How removable storage and portable devices (phones, tablets, cameras) are
restricted on managed endpoints, how temporary access is granted, and — set out
plainly — what this control does and does not actually prevent.

## The rule

**A USB storage device or portable device with no live grant is restricted.**
Not "restricted once the server says so": restricted by default, including on
a machine that has never enrolled, cannot reach the network, or has just booted
with a stick or a phone already in the port. Access is the exception, and it
requires a positive, unexpired, administrator-issued grant naming that exact
device.

There are exactly three states, and only two of them can be granted:

| State | What the user gets | How it is reached |
| --- | --- | --- |
| **Restricted** | Nothing. The device instance is disabled: a stick gets no drive letter, a phone never appears under This PC, no MTP/PTP session can be opened. | The default. Also where revoke and expiry land. |
| **Read-only** | Files can be opened and copied *off* the device. Windows refuses writes, creates, renames and deletes. **Storage only** — see below. | An explicit grant. |
| **Enabled** | Ordinary Windows read/write access. | An explicit grant, named as such. |

**Restricted is not grantable.** It is the absence of a grant, reached by
revoking rather than by asking for it, and every layer rejects an attempt to
grant it — the domain throws, the API returns 400, the agent drops the entry.
Attaching an expiry to a state that has none would mean a device silently
becoming accessible when the "grant" lapsed.

Read-only is the default everywhere a level is not stated: an omitted API field,
a grant cached by an older agent, an unparseable policy value. Read/write has to
be named explicitly, in exactly that spelling, to be reached at all — the ordinal
`2` and the string `"2"` are both refused, so a payload carrying a bare number
cannot obtain write access without naming it.

Access, when granted, is always:

- **time-boxed** — an absolute deadline between 5 minutes and 24 hours, chosen
  at grant time and never extended;
- **per device** — keyed to one Windows device instance ID on one endpoint;
- **justified and audited** — a reason is required, and the grant, its
  revocation and its expiry are each an audit record. A read/write grant is
  audited as `usb.access.enable` rather than `usb.access.grant`, so the widest
  decisions can be reported on separately from the narrow ones.

Other peripherals — keyboards, mice, hubs, webcams, audio, printers, network
adapters — are inventoried and never restricted. Disabling an input device
would lock the user out of their own machine.

## Phones, tablets and cameras (portable devices)

A phone plugged into a PC in "File transfer" mode is a writable disk in
everything but name: Explorer lists it under This PC and copies files both
ways. It is not *mass storage*, though — Windows reaches it over **MTP**
through the Windows Portable Devices stack, with no drive letter — and the
original control, which recognised storage by its disk driver and drive
letter, let it straight through. The same holds for "Transfer photos" (PTP),
for a digital camera, for an iPhone's photo access, and for an Android device
with USB debugging on, through which `adb push`/`adb pull` move files just as
well.

Since agent **1.16.0** these are a class of their own, `PortableDevice`, and
the rule above applies to them unchanged. Recognition uses the same kind of
evidence that identifies a stick, never the friendly name:

| Evidence | Where it comes from | Means |
|---|---|---|
| `USB\MS_COMP_MTP`, `USB\MS_COMP_PTP` in the compatible IDs | Microsoft OS descriptor the device itself reports | MTP / PTP |
| `USB\Class_06…` (still-image class) in the compatible IDs | The device's interface descriptor | PTP (Android "Transfer photos", cameras, iPhone) |
| `USB\Class_FF&SubClass_42&Prot_01` in the compatible IDs | The device's interface descriptor | Android ADB |
| Setup class `WPD` (`{EEC5AD98-…}`) or driver `WUDFWpdMtp` | What Windows bound the device to | Windows treats it as a portable device |

Compatible IDs survive the device being disabled, so a restricted phone stays
recognised — the lesson of Milestone 11a, applied again. A phone with USB
debugging on is a *composite* device whose functions sit on interface children
(`&MI_00` for MTP, `&MI_01` for ADB) and whose own compatible IDs say only
"composite"; it is classified from those functions, and when it is disabled
and they are gone, from the interfaces Windows has recorded for that
vendor/product. Interface children are folded into their parent, so one phone
is one row and one disable.

Three things differ from storage, and the console says so:

- **There is no read-only.** Read-only is a disk attribute; an MTP/PTP device
  has no disk. A portable device is restricted or enabled, nothing between.
  The console offers only read/write for it, the API refuses read-only with
  `400`, the domain throws, and an agent that is nonetheless handed a
  read-only grant for one keeps it restricted and reports why, rather than
  widening to read/write or pretending.
- **Switching USB mode is a new device.** MTP, PTP and ADB modes carry
  different product IDs, so each is a separate instance, restricted on its
  own and granted on its own.
- **Storage wins.** A phone in the old USB-mass-storage mode, or a composite
  device that carries both a disk and an MTP function, is storage.

## How it is enforced on Windows

Per-device, documented public API, no shell command and no kernel driver
(ADR-0005):

| State | Mechanism | Effect |
|---|---|---|
| Restricted | SetupAPI `DIF_PROPERTYCHANGE` / `DICS_DISABLE` on the device instance (the composite parent, for a phone with several functions) | The device does not start. A stick: no volume, no drive letter. A phone: the portable-device driver is unloaded and its device interface disappears, so there is nothing for Explorer or any other MTP/PTP client — or ADB — to open. |
| Read-only (storage) | Device enabled, then `IOCTL_DISK_SET_DISK_ATTRIBUTES` with `DISK_ATTRIBUTE_READ_ONLY` on each disk beneath it | Windows itself refuses writes, creates, renames and deletes. |
| Enabled | Device enabled, then the same IOCTL with the read-only bit *cleared* (no disks for a phone: enabled is the grant) | Ordinary Windows behaviour. |

The attribute mask is limited to the read-only bit in both directions, so
nothing else Windows tracks on the disk — the OFFLINE bit in particular — is
disturbed.

Restricting is what Device Manager's *Disable* does. The read-only attribute is
set with `Persist = false`, so it governs this endpoint only and does not follow
the stick to other machines — altering someone's hardware is not the platform's
business.

The class-wide alternatives — `StorageDevicePolicies\WriteProtect`, the
removable-storage and "WPD Devices: Deny read/write access" Group Policy values,
and device-installation restrictions on the WPD setup class — were considered
and not used (ADR-0015 weighs each). All are all-or-nothing for every such
device on the machine, so none can express "this one approved device, for the
next two hours"; the policy keys are overwritten by any domain or MDM policy
that manages them; and none can be verified per device. Disabling `USBSTOR`
does not touch MTP at all.

### Verified, not assumed

A SetupAPI call returning success is not proof that a device is blocked, and
since 1.16.0 the agent does not treat it as one. After every state change it
**reads the device back** and reports one of four outcomes:

| Outcome | What the agent found |
|---|---|
| **Verified** | `CM_Get_DevNode_Status` reports the devnode in the requested state — disabled (`CM_PROB_DISABLED`) for a restriction, running for a grant — and, for a restriction, no disk interface and no portable-device interface remains anywhere beneath the device. Those interfaces are the handles files move through; with none present, no transfer is possible. For read-only, each disk's attribute is read back as before. |
| **Unverified** | The call succeeded but the state could not be read back. Reported with the reason; never promoted to verified. |
| **Requires restart** | Windows accepted the change but set `DN_NEED_RESTART`: the device keeps running until the endpoint restarts, usually because a program was holding it open. **The control is not in place.** |
| **Failed** | The state could not be applied, or the read-back contradicts it. |

The console calls a device *Enforced* only for a verified outcome. A report
from an agent older than 1.16.0, which verified only read-only, is shown as
*Applied, not verified*.

The two grant paths fail in opposite directions, deliberately. If read-only
cannot be applied, the device is **restricted** instead — never left enabled and
writable, because the alternative is a writable disk the console believes is
read-only. If read/write cannot be fully applied, the device is **left as it
is** and the failure reported: the device ends up narrower than the grant
allows, which is the safe direction, and re-restricting would revoke a decision
an administrator had just made.

## How a grant travels

```
administrator grants access (usb.manage, level, justification, duration)
        ↓ audited; device marked ReadOnly or Enabled with an absolute deadline
ApplyUsbPolicy task queued, carrying the endpoint's COMPLETE grant set
        ↓
agent replaces its cached policy and reconciles every attached storage and portable device
        ↓
enforcement result reported back on the next USB report
```

Two things make this robust:

**The policy is whole state, not a delta.** Every task carries every live grant.
A device absent from the list is restricted, so revocation is the *absence* of
an entry rather than a second message that could be lost. Re-sending is
therefore always safe and repairs drift.

**There are two delivery channels, computed from one source.** The pushed task
is for immediacy; the response to the agent's own USB report is for
convergence. An agent that missed a task — offline when the grant was issued,
or the task expired — gets the right answer the moment the user next plugs
something in. Both channels are built by the same function, so they cannot
disagree, and both fail to the same safe default.

The agent reports on device arrival and removal rather than only on the
inventory cycle, so a stick or a phone appearing in the console takes seconds
rather than up to a quarter of an hour. Since 1.16.0 the notification is a
kernel push (`CM_Register_Notification` for the USB device interface class,
which every USB device exposes the moment it enumerates, before any driver
loads); the original WMI subscription — which had WMI sweep every PnP entity
on the machine once a second — is kept only as a fallback. Notification is a
latency optimisation, not the mechanism: a reconcile runs every minute
regardless, and sooner when a grant is about to lapse, so a watcher that fails
to start delays enforcement rather than losing it.

## Ordering, and the gap it closes

Inside each cycle the agent **enforces before it reports**. A newly attached
device is restricted from the locally cached policy before the server is
contacted, so access never waits on a network round trip. A device the
administrator has already approved therefore goes restricted-then-read-only
within a cycle: the user sees a drive that takes a moment to appear, never one
they could have written to.

## Expiry

A grant expires against the **endpoint's own clock**. The deadline travels with
the grant and is cached on disk, so access ends on schedule on a laptop that has
not reached the server in days.

Three independent things stop a lapsed grant, and any one of them suffices:

1. the agent restricts the device when the deadline passes;
2. the server computes published policy from the clock, so a lapsed grant is
   absent from every policy it hands out — regardless of stored status;
3. a background sweep marks the request `Expired` and returns the console view
   to Restricted.

The sweep is bookkeeping. If it never ran again, no endpoint would keep access
past its deadline; only the console would show a stale row.

## Failure behaviour

Every path lands on Restricted.

| Situation | Outcome |
|---|---|
| never enrolled / offline / server unreachable | restricted (local default) |
| grant cache missing, damaged, or sealed on another machine | restricted; the cache is treated as empty |
| `ApplyUsbPolicy` never delivered, or expired | restricted; converges on the next USB report |
| malformed policy payload | rejected; the previous policy stays in force |
| one malformed or already-expired grant entry | that entry dropped, the rest honoured |
| policy names an access level this agent does not implement | that grant dropped |
| policy names an access level as a number rather than a name | that grant dropped — a bare ordinal can never reach read/write |
| grant cached by an older agent, with no level recorded | read as read-only, never as read/write |
| stale policy arrives after a newer one | ignored (issued-at wins), so a late task cannot reinstate revoked access |
| read-only cannot be applied | device restricted, task reported failed |
| read-only grant names a portable device | device kept restricted, reported failed with the reason; never widened to read/write |
| Windows defers a disable to the next restart | reported *Restart required*; the control is not claimed to be in place |
| device state cannot be read back after a change | reported *Applied, not verified*; never shown as enforced |
| device enumeration fails | nothing evaluated; already-restricted devices stay disabled |
| agent lacks privilege | enforcement fails loudly and is reported unenforced — never silently skipped |

## Decided versus enforced

The console shows two different facts side by side and never collapses them:

- **Policy** — what an administrator decided.
- **Enforcement** — what the endpoint has confirmed it is actually doing.

| Shown | Means |
|---|---|
| Enforced | The endpoint applied the policy **and verified it** against Windows: the devnode is in that state and no storage or portable-device interface is exposed. |
| Applied, not verified | The agent reported success but did not verify the result — every agent before 1.16.0, or a newer one whose read-back failed (the reason is shown). |
| Not confirmed | No report yet — the machine may be offline, or the policy may still be in flight. |
| Drifted | The endpoint reports a different state from the one set. Usually a local administrator changing it by hand. |
| Restart required | Windows accepted the change for the next restart. **The control is not in place yet.** |
| Enforcement failed | The agent could not apply it. The reason is shown; unless it says the device was kept restricted, **the control is not in place.** |

A console that rendered the desired state as though it were the enforced state
would show a reassuring "Restricted" for a machine that has never been told
anything. The distinction between *Not confirmed* and *Drifted* is kept for the
same reason: only one of them needs investigating. *Applied* and *Enforced* are
kept apart because a call returning is not the same as a device being blocked.

The agent reports enforcement on **every** USB report, not only after a policy
task, so drift surfaces on the next report rather than never.

## Permissions

| Permission | Grants | Held by |
|---|---|---|
| `usb.view` | See the peripheral inventory and access states | Super Administrator, IT Administrator, Helpdesk, Auditor |
| `usb.manage` | Grant, revoke and re-apply USB storage and portable-device access | Super Administrator, IT Administrator |

Split deliberately. Seeing which stick is in which laptop is support
information — half of every "my drive isn't showing up" call — while opening a
read path off that laptop is a security decision. Helpdesk gets the first and
not the second. Auditor holds `usb.view` and nothing else here, consistent with
being read-only throughout.

`SystemRoleTests` asserts the whole-catalogue property that *only* those two
roles hold `usb.manage`, so a role added later cannot pick it up quietly.

## Requests come from administrators

An administrator raises and approves the grant in one act, on a user's behalf,
after the user has asked through whatever channel the organisation already uses.
The console is the only place a grant can originate.

There is deliberately no endpoint-initiated path yet. The agent is a
LocalSystem service in Session 0 with no user interface, so accepting requests
from the machine itself means shipping a user-session component — and a local
listener that could approve its own request would be a hole, not a feature. The
request record carries a `source` field (`Administrator` / `Endpoint`) so that
flow stays additive rather than a schema migration.

## Enforcement lasts exactly as long as the agent runs

This is a deliberate boundary, and the most important thing to understand about
how the control behaves in practice: **the product controls USB only while the
agent is running.** Stop the agent and the machine goes back to being an
ordinary Windows PC.

| Agent state | USB storage and portable-device behaviour |
| --- | --- |
| **Running** | Restricted by default. Read-only or read/write only where an administrator has granted it, and only until the grant expires. Enforced locally, with no dependency on the server being reachable. |
| **Stopped** | Not enforced. Devices already attached become usable again; newly inserted devices behave normally. |
| **Restarted** | The persisted policy is reloaded and enforcement is re-established, with no server contact required. A grant that expired during the downtime is not restored. |
| **Uninstalled** | Not enforced, permanently. Nothing this product installed continues to restrict USB. |

The reason this needs stating is that neither mechanism is naturally temporary.
Disabling a devnode writes `CONFIGFLAG_DISABLED` into the device's registry key,
which Windows honours indefinitely — across reboots, across the service being
stopped, and across the product being removed. Left alone, stopping the agent
would freeze the machine in whatever state it was last in, and an administrator
could not lift a restriction because the agent that would receive the
instruction is not running. Uninstalling would be worse: devices would stay
disabled with no remaining mechanism to restore them short of Device Manager, by
hand, per device.

So the agent explicitly stands enforcement down when it stops. It keeps a plain
JSON record — `usb-restricted-devices.json` in the state directory — of every
device instance it has applied state to, and on shutdown it re-enables each one
and clears the read-only attribute.

Two distinctions matter here:

- **Policy is durable; enforcement is not.** The grant set survives a stop, which
  is what lets a restart re-establish the right state offline. Only the
  mechanical enforcement is undone.
- **Release is not the same as revoke.** Revoking a grant returns a device to
  *Restricted*, because the machine is still managed. Release returns it to
  *normal*, because the product is standing down. Collapsing the two would mean
  revoking access handed the user a writable stick.

### The boundary, precisely

Release is user-mode cleanup. It runs when the agent is given the chance to run
it, and not otherwise.

| Ending | Release runs? | Result |
| --- | --- | --- |
| Service stop (`Stop-Service`, SCM stop) | Yes | Devices returned to normal. |
| Service restart, reboot, shutdown | Yes | Released on the way down, re-enforced on the way up. |
| MSI upgrade or uninstall | Yes — the installer stops the service first (`Stop="both"`) | Devices returned to normal. |
| Forced process termination (`taskkill /F`, SCM kill after timeout) | **No** | Devnodes stay disabled. |
| Crash, bugcheck (BSOD), power loss | **No** | Devnodes stay disabled. |

In the failure rows, a previously disabled devnode **remains disabled** until the
agent next starts, at which point the ledger tells it what to release and it
re-applies current policy. Uninstalling the product *while in that state* is the
one case that can leave a device disabled with nothing left to fix it
automatically; the ledger file is what makes that recoverable by hand.

This is not a gap that can be closed from user mode. Windows provides no
mechanism to make a SetupAPI device disable revert automatically when the process
that applied it dies — no lease, no session-scoped handle, no cleanup callback.
Only a kernel-mode filter driver could tie enforcement to a live component, and
this platform deliberately does not ship one (ADR-0005). **No stronger guarantee
than the table above should be claimed for this feature.**

## What this does NOT do

Stated explicitly, because a security control that is oversold is worse than
one that is absent.

- **Read/write access is exactly what it says.** A device under an `Enabled`
  grant behaves like one on an unmanaged machine for the life of the grant: data
  can be copied off the endpoint onto it. The controls that remain are that it is
  time-boxed, keyed to one device instance, attributable to a named
  administrator, and recorded. It is not a data-loss-prevention control.
- **Read-only does not prevent malware.** It stops the endpoint writing *to*
  the device. A malicious file already on the stick can still be copied *from*
  it and run. This is a data-egress and device-hygiene control, not an
  anti-malware one. Nothing here scans, inspects or blocks file content.
- **It does not stop a local administrator.** Someone holding administrator
  rights on the endpoint can stop the agent service or re-enable the device in
  Device Manager. No user-mode agent can prevent that; only a kernel driver
  could, which this platform deliberately does not ship. What the platform does
  guarantee is that such tampering becomes *visible*: the next report shows the
  device as Drifted rather than Enforced. The control is aimed at the ordinary
  user, who cannot do any of that — the grant cache is DPAPI-sealed at
  LocalMachine scope in a directory ACL'd to SYSTEM and Administrators, so a
  standard user can neither read it nor forge one.
- **It does not cover non-USB paths.** Optical drives, SD readers on a
  non-USB bus, network shares, cloud sync clients and personal email are all
  untouched by this feature.
- **It does not cover a phone's other radios and modes.** USB tethering (the
  phone presenting itself as a network adapter), Bluetooth file transfer,
  Wi-Fi Direct and the phone's own cloud sync are not USB file transfer and
  are not touched. A phone that is restricted over USB can still be used as
  a hotspot.
- **The first attachment of a device has a window.** Windows starts a device
  before the agent hears of it; the agent disables it within a few seconds of
  the arrival notification. A user who begins a copy inside that window may
  move a small file. From then on the device instance stays disabled — across
  reconnects, across reboots while the agent is installed — so the window
  exists once per device instance, and switching USB mode on a phone opens it
  once more for the new instance. Closing it entirely would need the
  class-wide installation restriction ADR-0015 rejects.
- **Charging is not measured.** A disabled data connection does not cut power
  to the port, but the current a given phone negotiates through it has not
  been measured and is not claimed either way.
- **There is a sub-second window when access is granted.** Read-only is applied
  after the device is enabled and its disk appears, because the disk does not
  exist while the device is disabled. The agent polls at 100 ms and forces a
  volume re-read, but between the volume mounting and the attribute landing
  there is a brief period where a user actively racing the grant could write.
  Restricted has no such window — the device never starts.
- **It does not track what was copied.** The platform records that access was
  open, to what, for whom, and when. It does not record which files were read;
  doing so would mean an agent that reads and reports file names, which is a
  different feature with a different privacy conversation.

## Verification

Domain invariants, RBAC, the wire format, the agent's fail-closed behaviour and
both HTTP channels are covered by automated suites — including the cases that
matter most: an unreadable grant cache restricting everything, a grant lapsing
with no server contact, a stale policy failing to reinstate revoked access, an
agent's own report being unable to grant it anything, and a numeric enum value
on the wire being dropped rather than honoured.

Enumeration is exercised against real Windows hardware in
`WindowsUsbEnumeratorTests` (read-only SetupAPI queries, safe on any machine):
instance-ID shape and uniqueness, hex vendor/product ids, repeatability, and the
parsing rule that a Windows-synthesised port-path segment is reported as *no
serial* rather than passed off as one — because a grant keyed to a port would
follow the port rather than the approved device.

Portable-device classification is pinned by `WindowsUsbPortableDeviceTests`,
built from the properties Windows recorded for a real phone in each of its
USB modes (MTP and PTP), plus synthetic composite trees for a phone with USB
debugging on and for an iPhone: a restricted phone still classifies by its
compatible IDs; a webcam, printer, audio device, fingerprint reader, keyboard,
mouse and network adapter do not; a hub with a phone plugged in is still a
hub; storage wins over portable; conflicting metadata resolves towards the
control; and interface children fold into their parent. The same file
registers and withdraws the kernel PnP notification on the machine it runs on,
which exercises the filter structure cfgmgr32 checks on registration.
`UsbPortableDeviceTests` (agent) pins the policy manager — restricted by
default, read-only refused and reported, read/write granted and lapsing, the
restart-required and unverified outcomes reaching the report, release and
restart — and `UsbPortableDeviceTests` (domain), `UsbEnforcementStateTests`,
`UsbPortableDeviceReportTests` and `UsbPortableDeviceEndpointTests` pin the
server side: the class, the refusal of read-only, the audit event, the status
as reported, and the console states.

The agent lifecycle — running, stopped, restarted, uninstalled — is covered by
`UsbAgentLifecycleTests`, which drives one simulated machine across several agent
lifetimes over shared persistent state. It pins the transitions that cannot be
inferred from the single-lifetime tests: stopping releases every device the agent
was enforcing; the policy survives that release; a restart restores enforcement
from local state alone; a grant that expired during downtime is *not* restored;
a device that fails to release is kept for the next attempt rather than
re-restricted; uninstall releases a device even when it is no longer plugged in;
and release touches only devices this agent applied state to, leaving alone any
that an administrator disabled by hand.

Enforcement itself changes real hardware state and is verified on a designated
test endpoint, never on a developer machine or a CI runner. The manual
acceptance script for the lifecycle is:

1. **Running** — attach an unapproved stick; confirm no drive letter appears.
   Grant read-only; confirm the drive appears and a write is refused. Revoke;
   confirm the drive disappears again.
2. **Stopped** — `Stop-Service EndpointPlatformAgent`; confirm the stick becomes
   accessible and writable, and that a *different*, never-seen stick also mounts
   normally.
3. **Restarted** — `Start-Service EndpointPlatformAgent`; confirm the previously
   restricted stick is restricted again without the server being involved
   (verifiable by disconnecting the network first).
4. **Uninstalled** — remove the MSI; confirm both sticks mount and write
   normally, and that `usb-restricted-devices.json` is gone with the state
   directory.

## Acceptance — portable devices: NOT VERIFIED

The portable-device control (agent 1.16.0) has **not yet been exercised on
hardware**. The phone whose recorded properties the tests are built from was
not attached while the change was made, and the only Windows machine available
runs the production agent, which is not replaced with test builds. The
classification, the enforcement path, the reporting and the console are
covered by the automated suites above; the claim that a phone is blocked in
both directions is **not demonstrated** until the script below has been run on
a designated test endpoint and its evidence recorded here, as Milestone 11a's
was. Until then the security objective is implemented, not verified.

The script, to be run with a data-capable cable, the agent on 1.16.0 and the
server on a build that includes `PortableDevice`:

1. **Windows path** — connect the phone in "File transfer" mode; confirm with
   `Get-PnpDevice -Class WPD -PresentOnly` that Windows exposes it as a WPD
   device and that it appears under This PC.
2. **Detection** — within ten seconds the console lists the phone in the
   *USB storage and portable devices* table as *Phone / portable device*,
   Restricted, Enforced, and the phone has disappeared from This PC.
3. **Blocked both ways** — attempt to copy a file from the PC to the phone
   and from the phone to the PC. Both must be impossible: there is no device
   to copy to or from. Record how each attempt fails.
4. **Grant** — grant read/write for 30 minutes; confirm the phone reappears,
   both copies succeed, and the console shows Read/write, Enforced.
5. **Revoke** — revoke; confirm the phone disappears again and a copy in
   progress is cut off.
6. **Reconnect** — unplug and replug; confirm the phone does not reappear
   (the instance stays disabled) and the console shows it attached and
   Restricted.
7. **Mode switch** — switch the phone to "Transfer photos"; confirm it is
   detected as a new instance, restricted within seconds, and listed
   separately. Switch USB debugging on with file transfer; confirm one row
   for the composite device and that `adb devices` on the PC shows nothing.
8. **Agent restart** — `Restart-Service EndpointPlatformAgent` with the phone
   attached; confirm it is briefly released and restricted again within
   seconds of the service starting, with no server contact needed.
9. **Reboot** — reboot with the phone attached; confirm it is restricted
   again once the agent starts, and note how long the phone was reachable
   between sign-in and that moment.
10. **Collateral** — keyboard, mouse, webcam and network remain working
    throughout; a USB stick keeps its own policy.
11. **Restart required** — with a file copy running under a grant, revoke;
    if Windows defers the disable, the console must show *Restart required*,
    not Enforced.
12. **Charging** — note whether the phone reports charging while restricted,
    and at what rate if it shows one. This is observation, not a claim.

## Acceptance — Milestone 11a, closed 2026-08-27

Signed off against **Agent 1.1.4** on `LAPTOP-LVCHEQ2H`, with real removable
media on a real machine. Every state, transition and lifecycle path below was
exercised by hand and behaved as specified:

| Exercised | Result |
|---|---|
| Default state on attach | Restricted — no drive letter |
| Read-only grant | Files readable and copyable *off* the device; writes refused by Windows |
| Read/write grant | Ordinary Windows access, including writing to the device |
| Timed access | Access ended on its own deadline |
| Revoke | Returned to Restricted immediately |
| Agent stopped | Device returned to normal, unmanaged Windows behaviour |
| Agent restarted | Persisted policy re-applied without server involvement |

Two defects were found by this acceptance rather than by the automated suites,
which is the argument for running it on hardware at all:

**A hub was classified from what was plugged into it.** The classifier gathered
driver services from the whole devnode subtree — and a hub's subtree is every
device on the bus. A root hub with a stick attached therefore collected
`USBSTOR` from that stick, classified as storage, and was disabled: the webcam,
fingerprint reader, Bluetooth radio and composite device on that hub all went
dark at the same instant, and the stick itself never appeared in inventory
because it now sat behind a dead hub. No automated test caught it because none
of them had a real device tree. Fixed by deciding hub-ness from the device's own
identity, before any storage rule, and by keeping the subtree walk inside one
physical device.

**Restricting a device destroyed the evidence that it was storage.** Disabling a
devnode unloads its driver and removes its child devnodes — the two signals the
classifier used to recognise removable storage. A restricted stick therefore
reappeared as an anonymous peripheral, dropped out of the console's storage
table, and could never be granted access again. Fixed by also recognising
storage from compatible IDs, which come from the device's own descriptors and
survive being disabled.

Both are covered by tests now, and the guard added alongside them refuses to
disable a hub regardless of classification — a guard that consulted the
classification it guards against would agree with it, including when it is
wrong.

The boundary stated earlier in this document is unchanged and was not
re-litigated by this acceptance: release is user-mode cleanup, so forced
termination, a crash, a bugcheck or power loss leave a disabled devnode disabled
until the agent next starts. No stronger claim is made.
