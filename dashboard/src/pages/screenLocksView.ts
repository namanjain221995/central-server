import type { ScreenLockResult, ScreenLockRow } from '../api/client'

/**
 * Pure view logic for the Full-Screen Lock feature.
 *
 * Kept out of the components, in the same shape as announcementsView and
 * friends, so the rules an operator relies on — when a lock may be engaged,
 * when an unlock code is acceptable, what a device's badge says and what the
 * banners say — are proven without a DOM.
 */

/** The server's limits, mirrored so the form can refuse before the 400 does. */
export const LOCK_MESSAGE_MAX = 500
export const LOCK_CONTACT_MAX = 200

/** The server's minimum for the IT unlock code. */
export const UNLOCK_CODE_MIN = 6

/** How the operator chose the devices a lock reaches. */
export type ScreenLockTargetMode = 'group' | 'devices'

export interface ScreenLockFormState {
  message: string
  contactLine: string
  mode: ScreenLockTargetMode
  groupId: string | null
  deviceIds: readonly string[]
}

/**
 * Whether the text holds a control character other than tab, newline or
 * carriage return.
 *
 * The lock text is rendered full-screen on a Windows machine; a stray NUL, bell
 * or escape in it is never intended and the server rejects it, so the form
 * refuses it first. Ordinary whitespace a textarea produces is left alone —
 * rejecting a newline the operator typed on purpose would be surprising. Scanned
 * by code point rather than a regex literal, which would embed the very control
 * characters it matches.
 */
function hasControlChars(text: string): boolean {
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i)
    const isC0 = code <= 0x1f && code !== 0x09 && code !== 0x0a && code !== 0x0d
    const isDelOrC1 = code >= 0x7f && code <= 0x9f
    if (isC0 || isDelOrC1) return true
  }
  return false
}

/**
 * The first problem that would make the server refuse a lock, or null when the
 * form may be sent. One error at a time, in reading order, so the operator
 * fixes the field they are looking at rather than a list.
 */
export function validateLockForm(form: ScreenLockFormState): string | null {
  const message = form.message.trim()
  const contactLine = form.contactLine.trim()

  if (message.length === 0) return 'Enter a message.'
  if (message.length > LOCK_MESSAGE_MAX) return `The message can be at most ${LOCK_MESSAGE_MAX} characters.`
  if (hasControlChars(message)) return 'The message cannot contain control characters.'
  if (contactLine.length > LOCK_CONTACT_MAX) {
    return `The contact line can be at most ${LOCK_CONTACT_MAX} characters.`
  }
  if (contactLine.length > 0 && hasControlChars(contactLine)) {
    return 'The contact line cannot contain control characters.'
  }
  if (form.mode === 'group' && form.groupId === null) return 'Choose a group.'
  if (form.mode === 'devices' && form.deviceIds.length === 0) return 'Select at least one device.'
  return null
}

/**
 * The IT unlock code is acceptable, or the first reason it is not.
 *
 * Measured untrimmed: a code is a secret, and silently dropping the spaces an
 * operator typed would set a different code than the one they confirmed. The
 * confirm field exists so a mistyped secret is caught here rather than on a
 * locked device nobody can open.
 */
export function validateUnlockCode(code: string, confirm: string): string | null {
  if (code.length < UNLOCK_CODE_MIN) return `The code must be at least ${UNLOCK_CODE_MIN} characters.`
  if (code !== confirm) return 'The two codes do not match.'
  return null
}

/**
 * The success banner, from the server's own counts and nothing else.
 *
 * `queued` is how many devices were sent the lock; `offline` is the server's
 * count of which of those must wait for a reconnect. Nothing here is the
 * browser's guess — a banner that said "12 locked" from local state would be
 * claiming an outcome the server never reported.
 */
export function lockSummary(
  result: Pick<ScreenLockResult, 'targeted' | 'queued' | 'skipped' | 'offline'>,
): string {
  if (result.queued <= 0) {
    return result.skipped > 0
      ? `Nothing was locked — all ${result.skipped} targeted device${result.skipped === 1 ? ' was' : 's were'} skipped.`
      : 'Nothing was locked — the target resolved to no devices.'
  }

  const skipped = result.skipped > 0 ? ` (${result.skipped} skipped)` : ''
  const offline =
    result.offline > 0
      ? ` ${result.offline} offline — they'll lock on reconnect (within 7 days).`
      : ''

  return `Locked ${result.queued} device${result.queued === 1 ? '' : 's'}${skipped}.${offline}`
}

/** The badge shown against a device that is currently held on the lock screen. */
export interface LockedBadge {
  label: string
  tone: 'warn'
}

/**
 * The "Locked" badge for a device row, or null when there is none to show.
 *
 * A retired device never shows it: it is out of management and whatever it last
 * reported is history, not a live lock an operator could act on.
 */
export function deriveLockedBadge(device: {
  status: 'Active' | 'Retired'
  screenLocked?: boolean
}): LockedBadge | null {
  if (device.status === 'Retired') return null
  return device.screenLocked ? { label: 'Locked', tone: 'warn' } : null
}

export type LockStatusTone = 'warn' | 'ok' | 'info' | 'crit' | 'neutral'

export interface LockStatusCount {
  label: string
  value: number
  tone: LockStatusTone
}

/**
 * The status badges for one lock, zero counts left out: a row of "0 failed,
 * 0 skipped" badges would make every clean lock look busy. `locked` is warn
 * rather than ok — a held machine is a state to resolve, not a success.
 */
export function lockStatusCounts(
  row: Pick<ScreenLockRow, 'locked' | 'released' | 'pending' | 'failed' | 'skipped' | 'offline'>,
): LockStatusCount[] {
  const counts: LockStatusCount[] = [
    { label: 'locked', value: row.locked, tone: 'warn' },
    { label: 'released', value: row.released, tone: 'ok' },
    // Not a problem state: an offline device that has the lock queued for it.
    { label: 'pending', value: row.pending, tone: 'info' },
    { label: 'offline', value: row.offline, tone: 'neutral' },
    { label: 'failed', value: row.failed, tone: 'crit' },
    { label: 'skipped', value: row.skipped, tone: 'neutral' },
  ]
  return counts.filter((c) => c.value > 0)
}

/** A lock is still in force, and so can be released, while any device holds it. */
export function isReleasable(row: Pick<ScreenLockRow, 'locked' | 'pending'>): boolean {
  return row.locked > 0 || row.pending > 0
}

/** Newest first, without trusting the server's ordering or mutating its array. */
export function sortLocksNewestFirst(rows: readonly ScreenLockRow[]): ScreenLockRow[] {
  return [...rows].sort(
    (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
  )
}
