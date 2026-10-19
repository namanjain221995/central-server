import { describe, expect, it } from 'vitest'
import {
  LOCK_CONTACT_MAX,
  LOCK_MESSAGE_MAX,
  UNLOCK_CODE_MIN,
  deriveLockedBadge,
  isReleasable,
  lockStatusCounts,
  lockSummary,
  sortLocksNewestFirst,
  validateLockForm,
  validateUnlockCode,
  type ScreenLockFormState,
} from '../../pages/screenLocksView'
import type { ScreenLockRow } from '../../api/client'

/** A form that passes, to be broken one field at a time. */
function validForm(overrides: Partial<ScreenLockFormState> = {}): ScreenLockFormState {
  return {
    message: 'This device has been locked by your IT department.',
    contactLine: 'Please contact your IT Department',
    mode: 'group',
    groupId: 'g1',
    deviceIds: [],
    ...overrides,
  }
}

describe('validateLockForm', () => {
  it('accepts a complete group form', () => {
    expect(validateLockForm(validForm())).toBeNull()
  })

  it('accepts a complete devices form', () => {
    expect(validateLockForm(validForm({ mode: 'devices', groupId: null, deviceIds: ['d1'] }))).toBeNull()
  })

  it('accepts an empty contact line — it is optional', () => {
    expect(validateLockForm(validForm({ contactLine: '' }))).toBeNull()
  })

  it.each([
    ['an empty message', validForm({ message: '' }), 'Enter a message.'],
    ['a whitespace message', validForm({ message: '  \n ' }), 'Enter a message.'],
    [
      'an overlong message',
      validForm({ message: 'x'.repeat(LOCK_MESSAGE_MAX + 1) }),
      `The message can be at most ${LOCK_MESSAGE_MAX} characters.`,
    ],
    [
      'a control character in the message',
      validForm({ message: 'Locked\u0007now' }),
      'The message cannot contain control characters.',
    ],
    [
      'an overlong contact line',
      validForm({ contactLine: 'x'.repeat(LOCK_CONTACT_MAX + 1) }),
      `The contact line can be at most ${LOCK_CONTACT_MAX} characters.`,
    ],
    [
      'a control character in the contact line',
      validForm({ contactLine: 'Call\u0000us' }),
      'The contact line cannot contain control characters.',
    ],
    ['group mode with no group', validForm({ groupId: null }), 'Choose a group.'],
    [
      'devices mode with no devices',
      validForm({ mode: 'devices', groupId: null, deviceIds: [] }),
      'Select at least one device.',
    ],
  ])('refuses %s', (_name, form, expected) => {
    expect(validateLockForm(form)).toBe(expected)
  })

  it('accepts a message and contact line exactly at their limits', () => {
    expect(
      validateLockForm(
        validForm({ message: 'x'.repeat(LOCK_MESSAGE_MAX), contactLine: 'y'.repeat(LOCK_CONTACT_MAX) }),
      ),
    ).toBeNull()
  })

  it('measures the trimmed text, so padding cannot smuggle an overlong message past the limit', () => {
    expect(validateLockForm(validForm({ message: `  ${'y'.repeat(LOCK_MESSAGE_MAX)}  ` }))).toBeNull()
  })

  it('allows the newlines and tabs a textarea produces', () => {
    expect(validateLockForm(validForm({ message: 'Line one\nLine two\tindented' }))).toBeNull()
  })

  it('reports the first problem in reading order', () => {
    expect(validateLockForm(validForm({ message: '', groupId: null }))).toBe('Enter a message.')
  })
})

describe('validateUnlockCode', () => {
  it('accepts a code at least the minimum length that matches its confirmation', () => {
    expect(validateUnlockCode('letmein', 'letmein')).toBeNull()
  })

  it('accepts a code exactly at the minimum length', () => {
    const code = 'x'.repeat(UNLOCK_CODE_MIN)
    expect(validateUnlockCode(code, code)).toBeNull()
  })

  it('refuses a code one short of the minimum', () => {
    const code = 'x'.repeat(UNLOCK_CODE_MIN - 1)
    expect(validateUnlockCode(code, code)).toBe(`The code must be at least ${UNLOCK_CODE_MIN} characters.`)
  })

  it('refuses a code that does not match its confirmation', () => {
    expect(validateUnlockCode('letmein', 'letmeout')).toBe('The two codes do not match.')
  })

  it('does not trim, so a code is the exact secret the operator typed', () => {
    // Trailing space makes it long enough and both fields agree: accepted as-is.
    expect(validateUnlockCode('abc   ', 'abc   ')).toBeNull()
    // A confirm that trimmed the space would be a different secret: refused.
    expect(validateUnlockCode('abcdef ', 'abcdef')).toBe('The two codes do not match.')
  })
})

describe('lockSummary', () => {
  // Only producible states: queued = targeted − skipped, offline ⊆ queued.
  it('names the offline tail from the server’s own count', () => {
    expect(lockSummary({ targeted: 12, queued: 12, skipped: 0, offline: 3 })).toBe(
      "Locked 12 devices. 3 offline — they'll lock on reconnect (within 7 days).",
    )
  })

  it('is plain when every device was online', () => {
    expect(lockSummary({ targeted: 5, queued: 5, skipped: 0, offline: 0 })).toBe('Locked 5 devices.')
  })

  it('uses the singular for one device', () => {
    expect(lockSummary({ targeted: 1, queued: 1, skipped: 0, offline: 0 })).toBe('Locked 1 device.')
  })

  it('accounts for skipped devices rather than hiding them', () => {
    expect(lockSummary({ targeted: 5, queued: 4, skipped: 1, offline: 2 })).toBe(
      "Locked 4 devices (1 skipped). 2 offline — they'll lock on reconnect (within 7 days).",
    )
  })

  it('says so when every device was skipped', () => {
    expect(lockSummary({ targeted: 3, queued: 0, skipped: 3, offline: 0 })).toBe(
      'Nothing was locked — all 3 targeted devices were skipped.',
    )
  })

  it('says so when the target resolved to nothing', () => {
    expect(lockSummary({ targeted: 0, queued: 0, skipped: 0, offline: 0 })).toBe(
      'Nothing was locked — the target resolved to no devices.',
    )
  })
})

describe('deriveLockedBadge', () => {
  it('shows the badge for a locked active device', () => {
    expect(deriveLockedBadge({ status: 'Active', screenLocked: true })).toEqual({ label: 'Locked', tone: 'warn' })
  })

  it('shows nothing for an unlocked device', () => {
    expect(deriveLockedBadge({ status: 'Active', screenLocked: false })).toBeNull()
  })

  it('treats a missing flag as not locked', () => {
    expect(deriveLockedBadge({ status: 'Active' })).toBeNull()
  })

  it('never badges a retired device, even if it last reported as locked', () => {
    expect(deriveLockedBadge({ status: 'Retired', screenLocked: true })).toBeNull()
  })
})

describe('lockStatusCounts', () => {
  it('shows only the outcomes that happened, in a fixed order', () => {
    expect(lockStatusCounts({ locked: 9, released: 0, pending: 2, failed: 1, skipped: 0, offline: 2 })).toEqual([
      { label: 'locked', value: 9, tone: 'warn' },
      { label: 'pending', value: 2, tone: 'info' },
      { label: 'offline', value: 2, tone: 'neutral' },
      { label: 'failed', value: 1, tone: 'crit' },
    ])
  })

  it('shows released on its own when a lock has been lifted', () => {
    expect(lockStatusCounts({ locked: 0, released: 4, pending: 0, failed: 0, skipped: 0, offline: 0 })).toEqual([
      { label: 'released', value: 4, tone: 'ok' },
    ])
  })

  it('is empty when nothing has been recorded yet', () => {
    expect(lockStatusCounts({ locked: 0, released: 0, pending: 0, failed: 0, skipped: 0, offline: 0 })).toEqual([])
  })
})

describe('isReleasable', () => {
  it('is releasable while any device holds the lock', () => {
    expect(isReleasable({ locked: 3, pending: 0 })).toBe(true)
  })

  it('is releasable while a device still has the lock pending', () => {
    expect(isReleasable({ locked: 0, pending: 2 })).toBe(true)
  })

  it('is not releasable once nothing is locked or pending', () => {
    expect(isReleasable({ locked: 0, pending: 0 })).toBe(false)
  })
})

describe('sortLocksNewestFirst', () => {
  const row = (id: string, createdAt: string): ScreenLockRow => ({
    id,
    message: 'm',
    contactLine: null,
    targetType: 'Devices',
    groupName: null,
    createdByDisplay: 'a',
    createdAt,
    targeted: 0,
    locked: 0,
    released: 0,
    pending: 0,
    failed: 0,
    skipped: 0,
    offline: 0,
  })

  it('orders by creation time, newest first, without mutating its input', () => {
    const rows = [row('old', '2026-10-01T10:00:00Z'), row('new', '2026-10-16T10:00:00Z'), row('mid', '2026-10-09T10:00:00Z')]
    const frozen = [...rows]

    expect(sortLocksNewestFirst(rows).map((r) => r.id)).toEqual(['new', 'mid', 'old'])
    expect(rows).toEqual(frozen)
  })
})
