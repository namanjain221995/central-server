# ADR-0015: Phones and other MTP/PTP devices are restricted by device-instance disable, verified per device

Status: accepted (2026-10-09)

## Context

The USB control (Milestone 11a, `docs/usb-control.md`) restricted removable
mass storage only. A phone connected to a managed PC in "File transfer" mode
never appears as a disk: Windows binds it to the Windows Portable Devices
stack (setup class `WPD`, service `WUDFWpdMtp`) and Explorer reaches it over
MTP. The agent enumerated the phone, classified it as `Other`, inventoried it,
and never touched it — so an employee could copy files from the PC to the
phone, and from the phone to the PC, with the agent watching. The same holds
for "Transfer photos" (PTP), for a digital camera, and for an Android device
exposing ADB, through which `adb push`/`adb pull` move files just as well.

Windows offers several ways to stop that. They had to be weighed against the
requirement the storage control already meets: *this one approved device, for
the next two hours*, with the result reported honestly.

## Options considered

| Mechanism | Blocks MTP/PTP transfers | Already-connected phone | Per device, grantable | Side effects | Verifiable per device |
|---|---|---|---|---|---|
| **Per-instance disable** — SetupAPI `DIF_PROPERTYCHANGE`/`DICS_DISABLE` on the phone's USB devnode (the composite parent when USB debugging is on) | Yes: the WPD driver is unloaded and its device interface disappears; there is nothing for any MTP client to open. A composite parent takes the ADB function down with it. | Yes, immediately. If a program holds the device open Windows defers to the next restart and says so (`DN_NEED_RESTART`). | Yes — exactly what the storage control does. Re-enable is the grant. | None on other devices; the hub guard refuses to disable a hub. | Yes: `CM_Get_DevNode_Status` reports `CM_PROB_DISABLED`, and the subtree can be checked for any remaining disk or WPD interface. |
| Device Installation Restrictions (Group Policy `DeviceInstall\Restrictions`, deny the WPD setup class, optionally retroactive; allow-lists by instance ID with layered evaluation) | Yes, by preventing the driver from installing at all. | Only with the retroactive flag, whose behaviour for already-installed devices varies by Windows build. | Partly — allow-by-instance-ID exists but the deny is class-wide and machine-wide. | Blocks every WPD device (cameras, media players). Overwritten by any domain or MDM policy that manages the same keys; the agent would be fighting Group Policy. | No per-device state to read back beyond the install failure. |
| Removable Storage Access policy — "WPD Devices: Deny read/write access" (`Policies\Microsoft\Windows\RemovableStorageDevices\{WPD interface GUIDs}`) | Yes for the WPD path; not for ADB. | Not reliably — the policy is read at device start; a reboot may be needed (the policy family carries a "force reboot" setting for that reason). | No — machine-wide, all-or-nothing for every WPD device. | Same policy-key ownership conflict as above. | No. |
| Disabling the `WUDFWpdMtp` service or removing the WPD driver | Yes | Yes | No | Undocumented as a control; breaks every portable device; trivially noticed and reverted. | No. |
| Disabling `USBSTOR` | **No.** MTP is not mass storage. | — | — | — | — |
| A kernel-mode filter driver | Yes | Yes | Yes | The platform ships no kernel code (ADR-0005). | — |

## Decision

Portable devices are a class of their own (`PortableDevice`), classified from
the device's own evidence — the `USB\MS_COMP_MTP` / `USB\MS_COMP_PTP` /
`USB\Class_06` (still image) / `USB\Class_FF&SubClass_42&Prot_01` (ADB)
compatible IDs, the WPD setup class or its drivers — on the device or on the
interface functions of a composite device, never from its name. They are
restricted by **the same per-instance disable the storage control uses**, on
the same restricted-by-default rule, grantable at read/write only, because
MTP/PTP has no read-only mode and the platform does not pretend otherwise.

Every enforcement attempt is now **read back**: a result is reported as
verified only when Windows reports the devnode in the requested state and no
disk or portable-device interface remains beneath it. A change Windows
deferred to the next restart is reported as requiring a restart; a read-back
that fails is reported as unverified. The console calls a device "Enforced"
only for a verified state, and "Applied, not verified" for anything an agent
merely reported as done — including every report from an agent older than
1.16.0.

Class-wide Windows policies were not chosen, for the reasons the storage
control already gave and one more: on a managed estate those keys belong to
whoever runs Group Policy or MDM, and an agent that writes them is a second
owner of the same setting. Per-instance disable has no such owner.

## Consequences

- A phone, tablet or camera on a managed PC is blocked the moment the agent's
  reconcile reaches it, in both directions, until an administrator grants it.
  The first time a given device is seen there is a window of a few seconds
  between Windows starting it and the agent disabling it; the storage control
  has the same window and the same remedy (the device stays disabled from then
  on, including across reconnects, until released or granted).
- Digital cameras and media players that speak PTP/MTP are restricted too. That
  is the intended consequence: they are removable storage with a lens.
- USB tethering (a phone presenting a network adapter), Bluetooth, Wi-Fi Direct
  and cloud sync are not covered; they are not USB file transfer and are listed
  in `docs/usb-control.md` as what this control does not do.
- Charging is not affected by the disable as far as USB power delivery is
  concerned — the port stays powered — but the charging current a given phone
  negotiates through a disabled data connection has not been measured and is
  not claimed.
- The agent's device watcher now registers for PnP notifications
  (`CM_Register_Notification`) and keeps the WMI subscription only as a
  fallback, so the one-second WMI sweep of the whole PnP tree is gone on every
  supported Windows.
