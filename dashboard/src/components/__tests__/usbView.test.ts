import { describe, expect, it } from 'vitest'
import type { UsbDeviceRow, UsbEnforcementState } from '../../api/client'
import {
  ENFORCEMENT,
  defaultGrantPolicy,
  describeDevice,
  describeUsbClass,
  grantButtonLabel,
  grantablePolicies,
  hasLiveGrant,
} from '../../pages/usbView'

function row(overrides: Partial<UsbDeviceRow> = {}): UsbDeviceRow {
  return {
    id: 'u1',
    instanceId: 'USB\\VID_2717&PID_FF40\\EXAMPLE0SERIAL01',
    deviceClass: 'PortableDevice',
    isStorage: false,
    isPortableDevice: true,
    isRestrictable: true,
    supportsReadOnly: false,
    vendorId: '2717',
    productId: 'FF40',
    serialNumber: 'EXAMPLE0SERIAL01',
    manufacturer: 'Xiaomi',
    product: 'Redmi Note 14 Pro 5G',
    isConnected: true,
    firstSeenAt: '2026-10-09T10:00:00Z',
    lastSeenAt: '2026-10-09T10:05:00Z',
    disconnectedAt: null,
    policy: 'Restricted',
    policyExpiresAt: null,
    enforcementState: 'Enforced',
    enforcedAt: '2026-10-09T10:05:00Z',
    enforcementError: null,
    liveRequestId: null,
    ...overrides,
  }
}

/**
 * A phone has no read-only mode, so the dialog offers read/write or nothing;
 * a stick keeps both levels with read-only preselected. Mirrors the server,
 * which refuses read-only for a portable device with a 400.
 */
describe('grantable levels', () => {
  it('offers only read/write for a phone', () => {
    expect(grantablePolicies(row())).toEqual(['Enabled'])
    expect(defaultGrantPolicy(row())).toBe('Enabled')
  })

  it('offers both levels for storage, read-only first', () => {
    const stick = row({ deviceClass: 'Storage', isStorage: true, isPortableDevice: false, supportsReadOnly: true })
    expect(grantablePolicies(stick)).toEqual(['ReadOnly', 'Enabled'])
    expect(defaultGrantPolicy(stick)).toBe('ReadOnly')
  })

  it('names the level on the button', () => {
    expect(grantButtonLabel('ReadOnly')).toBe('Grant read-only access')
    expect(grantButtonLabel('Enabled')).toBe('Grant read/write access')
  })
})

describe('live grants', () => {
  it('counts either level as live only while a request is in force', () => {
    expect(hasLiveGrant(row({ policy: 'Enabled', liveRequestId: 'r1' }))).toBe(true)
    expect(hasLiveGrant(row({ policy: 'ReadOnly', liveRequestId: 'r1' }))).toBe(true)
    expect(hasLiveGrant(row({ policy: 'Enabled', liveRequestId: null }))).toBe(false)
    expect(hasLiveGrant(row({ policy: 'Restricted', liveRequestId: 'r1' }))).toBe(false)
  })
})

describe('presentation', () => {
  it('reserves the ok badge for a verified state', () => {
    const states: UsbEnforcementState[] = [
      'Enforced',
      'Applied',
      'Pending',
      'Drifted',
      'RequiresRestart',
      'Failed',
      'NotApplicable',
    ]

    for (const state of states) {
      expect(ENFORCEMENT[state].badge === 'ok').toBe(state === 'Enforced')
    }
  })

  it('says what a restart-required or unverified state means for the control', () => {
    expect(ENFORCEMENT.RequiresRestart.label).toBe('Restart required')
    expect(ENFORCEMENT.RequiresRestart.hint).toMatch(/NOT in place/)
    expect(ENFORCEMENT.Applied.label).toBe('Applied, not verified')
    expect(ENFORCEMENT.Applied.badge).toBe('warn')
  })

  it('labels classes for people', () => {
    expect(describeUsbClass('PortableDevice')).toBe('Phone / portable device')
    expect(describeUsbClass('Storage')).toBe('Removable storage')
    expect(describeUsbClass('NetworkAdapter')).toBe('Network adapter')
    expect(describeUsbClass('Keyboard')).toBe('Keyboard')
    expect(describeUsbClass('TeleportationPad')).toBe('Unknown')
  })

  it('describes a device by product, then manufacturer, then class', () => {
    expect(describeDevice(row())).toBe('Redmi Note 14 Pro 5G')
    expect(describeDevice(row({ product: null }))).toBe('Xiaomi')
    expect(describeDevice(row({ product: null, manufacturer: null }))).toBe('Phone / portable device')
  })
})
