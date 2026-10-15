import type { AnnouncementRow, AnnouncementSendResult } from '../api/client'

/**
 * Pure view logic for the Announcements page.
 *
 * Separated from the component so the rules an operator relies on — when the
 * form may be sent, which devices read as online, and what the banners say —
 * are proven without a DOM, in the same shape as deploymentView and friends.
 */

/** The server's limits, mirrored so the form can refuse before the 400 does. */
export const TITLE_MAX = 80
export const MESSAGE_MAX = 500

/** How the operator chose recipients. */
export type AnnouncementTargetMode = 'group' | 'devices'

export interface AnnouncementFormState {
  title: string
  message: string
  mode: AnnouncementTargetMode
  groupId: string | null
  deviceIds: readonly string[]
}

/**
 * The first problem that would make the server refuse, or null when the form
 * may be sent. One error at a time, in reading order: the operator fixes the
 * field they are looking at rather than a list.
 */
export function validateAnnouncementForm(form: AnnouncementFormState): string | null {
  const title = form.title.trim()
  const message = form.message.trim()

  if (title.length === 0) return 'Enter a title.'
  if (title.length > TITLE_MAX) return `The title can be at most ${TITLE_MAX} characters.`
  if (message.length === 0) return 'Enter a message.'
  if (message.length > MESSAGE_MAX) return `The message can be at most ${MESSAGE_MAX} characters.`
  if (form.mode === 'group' && form.groupId === null) return 'Choose a group.'
  if (form.mode === 'devices' && form.deviceIds.length === 0) return 'Select at least one device.'
  return null
}

/** The device list's staleness threshold: silent for 3 minutes means offline. */
export const ONLINE_WINDOW_MS = 180_000

/**
 * Whether a device reads as online, from its last heartbeat.
 *
 * The server computes `isOnline` for its own lists with the same three-minute
 * rule; this derivation exists so a picker refreshed less often than the
 * heartbeat still judges rows against "now" rather than against fetch time.
 * Exactly 180 s old is offline. A timestamp in the future — clock skew, not
 * time travel — is a heartbeat just received, so it counts as online; an
 * unparseable one counts as offline, the claim the evidence supports.
 */
export function deriveOnline(lastSeenAt: string | null, now: Date): boolean {
  if (!lastSeenAt) return false
  return now.getTime() - new Date(lastSeenAt).getTime() < ONLINE_WINDOW_MS
}

/** The target column: the group's name, or how many devices were picked by hand. */
export function summarizeTarget(
  row: Pick<AnnouncementRow, 'targetType' | 'groupName' | 'targeted'>,
): string {
  if (row.targetType === 'Group') return row.groupName ?? 'Deleted group'
  return `${row.targeted} device${row.targeted === 1 ? '' : 's'}`
}

/**
 * The success banner, from the server's own counts and nothing else.
 *
 * `queued` is how many devices were sent the announcement, and `offline` is
 * the server's count of which of those must wait for a reconnect. Nothing here
 * is the browser's guess — a banner that said "12 devices saw it" from local
 * state would be claiming delivery the server never reported.
 */
export function sendSummary(
  result: Pick<AnnouncementSendResult, 'targeted' | 'queued' | 'skipped' | 'offline'>,
): string {
  if (result.queued <= 0) {
    return result.skipped > 0
      ? `Nothing was sent — all ${result.skipped} targeted device${result.skipped === 1 ? ' was' : 's were'} skipped.`
      : 'Nothing was sent — the target resolved to no devices.'
  }

  const skipped = result.skipped > 0 ? ` (${result.skipped} skipped)` : ''
  const offline =
    result.offline > 0
      ? ` ${result.offline} offline — they'll get it on reconnect (within 24 hours).`
      : ''

  return `Sent to ${result.queued} device${result.queued === 1 ? '' : 's'}${skipped}.${offline}`
}

export type DeliveryTone = 'ok' | 'info' | 'crit' | 'neutral'

export interface DeliveryCount {
  label: string
  value: number
  tone: DeliveryTone
}

/**
 * The delivery badges for one announcement, zero counts left out: a row of
 * "0 failed, 0 skipped" badges would make every healthy broadcast look busy.
 */
export function deliveryCounts(
  row: Pick<AnnouncementRow, 'succeeded' | 'pending' | 'failed' | 'skipped'>,
): DeliveryCount[] {
  const counts: DeliveryCount[] = [
    { label: 'delivered', value: row.succeeded, tone: 'ok' },
    // Not a problem state: the device is offline and the message waits for it.
    { label: 'pending', value: row.pending, tone: 'info' },
    { label: 'failed', value: row.failed, tone: 'crit' },
    { label: 'skipped', value: row.skipped, tone: 'neutral' },
  ]
  return counts.filter((c) => c.value > 0)
}

/** Newest first, without trusting the server's ordering or mutating its array. */
export function sortNewestFirst(rows: readonly AnnouncementRow[]): AnnouncementRow[] {
  return [...rows].sort(
    (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
  )
}

/** A one-line preview of the message under the title, so a 500-character broadcast does not become a 500-character row. */
export function messagePreview(message: string, max = 96): string {
  const text = message.trim()
  if (text.length <= max) return text
  return `${text.slice(0, max - 1).trimEnd()}…`
}
