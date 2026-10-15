import { describe, expect, it } from 'vitest'
import {
  MESSAGE_MAX,
  ONLINE_WINDOW_MS,
  TITLE_MAX,
  deliveryCounts,
  deriveOnline,
  messagePreview,
  sendSummary,
  sortNewestFirst,
  summarizeTarget,
  validateAnnouncementForm,
  type AnnouncementFormState,
} from '../../pages/announcementsView'
import type { AnnouncementRow } from '../../api/client'

/** A form that passes, to be broken one field at a time. */
function validForm(overrides: Partial<AnnouncementFormState> = {}): AnnouncementFormState {
  return {
    title: 'Scheduled maintenance',
    message: 'The platform restarts at 18:00.',
    mode: 'group',
    groupId: 'g1',
    deviceIds: [],
    ...overrides,
  }
}

describe('validateAnnouncementForm', () => {
  it('accepts a complete group form', () => {
    expect(validateAnnouncementForm(validForm())).toBeNull()
  })

  it('accepts a complete devices form', () => {
    expect(validateAnnouncementForm(validForm({ mode: 'devices', groupId: null, deviceIds: ['d1'] }))).toBeNull()
  })

  it.each([
    ['an empty title', validForm({ title: '' }), 'Enter a title.'],
    ['a whitespace title', validForm({ title: '   ' }), 'Enter a title.'],
    [
      'an overlong title',
      validForm({ title: 'x'.repeat(TITLE_MAX + 1) }),
      `The title can be at most ${TITLE_MAX} characters.`,
    ],
    ['an empty message', validForm({ message: '' }), 'Enter a message.'],
    ['a whitespace message', validForm({ message: ' \n ' }), 'Enter a message.'],
    [
      'an overlong message',
      validForm({ message: 'x'.repeat(MESSAGE_MAX + 1) }),
      `The message can be at most ${MESSAGE_MAX} characters.`,
    ],
    ['group mode with no group', validForm({ groupId: null }), 'Choose a group.'],
    [
      'devices mode with no devices',
      validForm({ mode: 'devices', groupId: null, deviceIds: [] }),
      'Select at least one device.',
    ],
  ])('refuses %s', (_name, form, expected) => {
    expect(validateAnnouncementForm(form)).toBe(expected)
  })

  it('accepts a title and message exactly at their limits', () => {
    expect(
      validateAnnouncementForm(
        validForm({ title: 'x'.repeat(TITLE_MAX), message: 'y'.repeat(MESSAGE_MAX) }),
      ),
    ).toBeNull()
  })

  it('measures the trimmed text, so padding cannot smuggle an overlong message past the limit', () => {
    expect(validateAnnouncementForm(validForm({ message: `  ${'y'.repeat(MESSAGE_MAX)}  ` }))).toBeNull()
  })

  it('reports the first problem in reading order', () => {
    expect(validateAnnouncementForm(validForm({ title: '', message: '', groupId: null }))).toBe('Enter a title.')
  })
})

describe('deriveOnline', () => {
  const now = new Date('2026-10-16T12:00:00.000Z')
  const seenAgo = (ms: number) => new Date(now.getTime() - ms).toISOString()

  it('a device never seen is offline', () => {
    expect(deriveOnline(null, now)).toBe(false)
  })

  it('a heartbeat just now is online', () => {
    expect(deriveOnline(seenAgo(0), now)).toBe(true)
  })

  it('one millisecond inside the window is online', () => {
    expect(deriveOnline(seenAgo(ONLINE_WINDOW_MS - 1), now)).toBe(true)
  })

  it('exactly 180 s of silence is offline — the boundary belongs to offline', () => {
    expect(deriveOnline(seenAgo(ONLINE_WINDOW_MS), now)).toBe(false)
  })

  it('beyond the window is offline', () => {
    expect(deriveOnline(seenAgo(ONLINE_WINDOW_MS + 1), now)).toBe(false)
  })

  it('a future timestamp is clock skew on a heartbeat just received: online', () => {
    expect(deriveOnline(seenAgo(-5_000), now)).toBe(true)
  })

  it('an unparseable timestamp is offline, not online by accident', () => {
    expect(deriveOnline('not a date', now)).toBe(false)
  })
})

describe('summarizeTarget', () => {
  it('names the group', () => {
    expect(summarizeTarget({ targetType: 'Group', groupName: 'Developers', targeted: 9 })).toBe('Developers')
  })

  it('says a group target whose group is gone was a deleted group, not a blank', () => {
    expect(summarizeTarget({ targetType: 'Group', groupName: null, targeted: 9 })).toBe('Deleted group')
  })

  it.each([
    [1, '1 device'],
    [12, '12 devices'],
  ])('counts hand-picked devices (%i)', (targeted, expected) => {
    expect(summarizeTarget({ targetType: 'Devices', groupName: null, targeted })).toBe(expected)
  })
})

describe('sendSummary', () => {
  // Only producible states: queued = targeted − skipped, offline ⊆ queued.
  it('names the offline tail from the server’s own count', () => {
    expect(sendSummary({ targeted: 12, queued: 12, skipped: 0, offline: 3 })).toBe(
      "Sent to 12 devices. 3 offline — they'll get it on reconnect (within 24 hours).",
    )
  })

  it('is plain when everything was online', () => {
    expect(sendSummary({ targeted: 5, queued: 5, skipped: 0, offline: 0 })).toBe('Sent to 5 devices.')
  })

  it('uses the singular for one device', () => {
    expect(sendSummary({ targeted: 1, queued: 1, skipped: 0, offline: 0 })).toBe('Sent to 1 device.')
  })

  it('accounts for skipped devices rather than hiding them', () => {
    expect(sendSummary({ targeted: 5, queued: 4, skipped: 1, offline: 2 })).toBe(
      "Sent to 4 devices (1 skipped). 2 offline — they'll get it on reconnect (within 24 hours).",
    )
  })

  it('says so when every device was skipped', () => {
    expect(sendSummary({ targeted: 3, queued: 0, skipped: 3, offline: 0 })).toBe(
      'Nothing was sent — all 3 targeted devices were skipped.',
    )
  })

  it('says so when the target resolved to nothing', () => {
    expect(sendSummary({ targeted: 0, queued: 0, skipped: 0, offline: 0 })).toBe(
      'Nothing was sent — the target resolved to no devices.',
    )
  })
})

describe('deliveryCounts', () => {
  it('shows only the outcomes that happened, in a fixed order', () => {
    expect(deliveryCounts({ succeeded: 9, pending: 2, failed: 1, skipped: 0 })).toEqual([
      { label: 'delivered', value: 9, tone: 'ok' },
      { label: 'pending', value: 2, tone: 'info' },
      { label: 'failed', value: 1, tone: 'crit' },
    ])
  })

  it('includes skipped when it occurred', () => {
    expect(deliveryCounts({ succeeded: 0, pending: 0, failed: 0, skipped: 4 })).toEqual([
      { label: 'skipped', value: 4, tone: 'neutral' },
    ])
  })

  it('is empty when nothing has been recorded yet', () => {
    expect(deliveryCounts({ succeeded: 0, pending: 0, failed: 0, skipped: 0 })).toEqual([])
  })
})

describe('sortNewestFirst', () => {
  const row = (id: string, createdAt: string): AnnouncementRow => ({
    id,
    title: 't',
    message: 'm',
    targetType: 'Devices',
    groupName: null,
    createdByDisplay: 'a',
    createdAt,
    targeted: 0,
    queued: 0,
    succeeded: 0,
    pending: 0,
    failed: 0,
    skipped: 0,
  })

  it('orders by creation time, newest first, without mutating its input', () => {
    const rows = [row('old', '2026-10-01T10:00:00Z'), row('new', '2026-10-16T10:00:00Z'), row('mid', '2026-10-09T10:00:00Z')]
    const frozen = [...rows]

    expect(sortNewestFirst(rows).map((r) => r.id)).toEqual(['new', 'mid', 'old'])
    expect(rows).toEqual(frozen)
  })
})

describe('messagePreview', () => {
  it('leaves a short message alone', () => {
    expect(messagePreview('Back at 18:00.')).toBe('Back at 18:00.')
  })

  it('truncates a long message with an ellipsis within the budget', () => {
    const long = 'a'.repeat(300)
    const preview = messagePreview(long, 96)
    expect(preview.endsWith('…')).toBe(true)
    expect(preview.length).toBeLessThanOrEqual(96)
  })
})
