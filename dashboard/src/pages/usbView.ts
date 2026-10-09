import type { UsbDeviceRow, UsbEnforcementState, UsbGrantablePolicy } from '../api/client'

/**
 * The USB panel's rules, kept pure so they can be tested without a DOM.
 *
 * Two things are decided here and nowhere else: which devices policy applies
 * to and at which levels, and how each enforcement state is presented. The
 * server is the authority on both; this module only has to agree with it, and
 * the tests pin that it does.
 */

export interface EnforcementPresentation {
  badge: 'ok' | 'warn' | 'crit' | 'neutral'
  label: string
  hint: string
}

/**
 * How each enforcement state is presented.
 *
 * `Enforced` is the only one that gets the ok treatment, and it is reserved for
 * a state the endpoint has verified against Windows. Everything else is a
 * warning or worse, because the honest message in those cases is "the control
 * may not be in place", and a neutral grey badge would read as "fine".
 */
export const ENFORCEMENT: Record<UsbEnforcementState, EnforcementPresentation> = {
  Enforced: {
    badge: 'ok',
    label: 'Enforced',
    hint: 'The endpoint applied this and verified it: Windows reports the device in this state and no storage or portable-device interface is exposed.',
  },
  Applied: {
    badge: 'warn',
    label: 'Applied, not verified',
    hint: 'The agent reported that it applied this but did not verify the result. Agents before 1.16.0 never verify; a newer agent reports this when it could not read the device state back.',
  },
  Pending: {
    badge: 'warn',
    label: 'Not confirmed',
    hint: 'The endpoint has not reported on this device yet — it may be offline, or the policy may still be on its way.',
  },
  Drifted: {
    badge: 'crit',
    label: 'Drifted',
    hint: 'The endpoint reports a different state from the one set here. Someone with local administrator rights may have changed it by hand.',
  },
  RequiresRestart: {
    badge: 'crit',
    label: 'Restart required',
    hint: 'Windows accepted the change but applies it only after the endpoint restarts — usually because a program was holding the device open. Until then the control is NOT in place.',
  },
  Failed: {
    badge: 'crit',
    label: 'Enforcement failed',
    hint: 'The agent could not apply this policy. Read the reason: unless it says the device was kept restricted, the control is NOT in place on this device.',
  },
  NotApplicable: {
    badge: 'neutral',
    label: '—',
    hint: 'Access policy applies to USB storage and portable devices only.',
  },
}

/** A human label for the platform's device class. */
export function describeUsbClass(deviceClass: string): string {
  switch (deviceClass) {
    case 'Storage':
      return 'Removable storage'
    case 'PortableDevice':
      return 'Phone / portable device'
    case 'NetworkAdapter':
      return 'Network adapter'
    case 'Keyboard':
    case 'Mouse':
    case 'Hub':
      return deviceClass
    case 'Other':
      return 'Other peripheral'
    default:
      return 'Unknown'
  }
}

export function describeDevice(device: Pick<UsbDeviceRow, 'product' | 'manufacturer' | 'deviceClass'>): string {
  return device.product ?? device.manufacturer ?? describeUsbClass(device.deviceClass)
}

/**
 * The levels that can be granted to this device. Read-only exists only for
 * storage: a phone connects through MTP/PTP, which has no read-only mode, so
 * for it access is all or nothing.
 */
export function grantablePolicies(device: Pick<UsbDeviceRow, 'supportsReadOnly'>): UsbGrantablePolicy[] {
  return device.supportsReadOnly ? ['ReadOnly', 'Enabled'] : ['Enabled']
}

/**
 * The level preselected in the grant dialog: the narrowest one available, so
 * read/write is something an administrator chooses rather than arrives at by
 * leaving a control alone.
 */
export function defaultGrantPolicy(device: Pick<UsbDeviceRow, 'supportsReadOnly'>): UsbGrantablePolicy {
  return grantablePolicies(device)[0]
}

/**
 * Either grantable level counts as live access. Restricted is the absence of
 * one, so it is deliberately not here.
 */
export function hasLiveGrant(device: Pick<UsbDeviceRow, 'policy' | 'liveRequestId'>): boolean {
  return (device.policy === 'ReadOnly' || device.policy === 'Enabled') && device.liveRequestId !== null
}

/** The label on the grant dialog's submit button, naming the level being granted. */
export function grantButtonLabel(policy: UsbGrantablePolicy): string {
  return policy === 'Enabled' ? 'Grant read/write access' : 'Grant read-only access'
}
