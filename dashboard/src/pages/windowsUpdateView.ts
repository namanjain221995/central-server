/**
 * The rules the Windows Driver Updates and Windows Security Updates pages share,
 * kept pure so each one is testable without React: how a pending update's
 * install state reads as a status chip, what a Windows Update error code means,
 * and the device panel's follow-up polling schedule.
 *
 * None of this knows which feature it serves -- each page's own view module
 * (driverUpdatesView, securityUpdatesView) re-exports it alongside the rules
 * that do, so a page imports everything from one place. Nothing here decides
 * state; it only words what the server reported and paces how often to ask.
 */

// ------------------------------------------------------------ status chips

/** How a status chip is coloured. */
export type ChipTone = 'neutral' | 'pending' | 'ok' | 'warn' | 'bad'

export interface StatusChip {
  label: string
  tone: ChipTone
}

/**
 * One pending update's install state, worded for the Status column.
 *
 * Takes the server's `installStatus` verbatim. A value this build does not know
 * (a newer server) is shown as the raw word in neutral rather than guessed at:
 * the server is the source of truth, and a wrong confident label is worse than
 * an unfamiliar honest one. Absent (an older server) reads as plain pending.
 */
export function statusChip(item: { installStatus?: string | null; lastHresult?: number | null }): StatusChip {
  const status = item.installStatus || 'none'
  switch (status) {
    case 'none':
      return { label: 'Pending', tone: 'neutral' }
    case 'installing':
      return { label: 'Installing…', tone: 'pending' }
    case 'verifying':
      return { label: 'Verifying…', tone: 'pending' }
    case 'pendingReboot':
      return { label: 'Needs restart to finish', tone: 'warn' }
    case 'reOffered':
      return { label: 'Re-offered by Windows', tone: 'warn' }
    case 'failed':
      return { label: `Failed: ${wuErrorText(item.lastHresult)}`, tone: 'bad' }
    default:
      return { label: status, tone: 'neutral' }
  }
}

/** The console's badge class for a chip tone. */
export function chipBadgeClass(tone: ChipTone): string {
  switch (tone) {
    case 'pending':
      return 'info'
    case 'ok':
      return 'ok'
    case 'warn':
      return 'warn'
    case 'bad':
      return 'crit'
    default:
      return 'neutral'
  }
}

/**
 * The Windows Update errors this fleet actually hits, in plain words. Keys are
 * the unsigned 32-bit HRESULT. Anything else falls back to the hex code, which
 * is what an administrator would search for.
 */
const WU_ERRORS: ReadonlyMap<number, string> = new Map([
  [0x80240017, 'Not applicable to this device'],
  [0x8024001e, 'The Windows Update service stopped or the device was shutting down'],
  [0x8024001f, 'No network connection to Windows Update'],
  [0x80240016, 'Another install was in progress or a restart is pending'],
  [0x80240009, 'Another Windows Update operation was in progress'],
  [0x80242016, 'Unexpected state after the restart step; restart the device and scan again'],
  [0x80070005, 'Access denied'],
])

function hex8(value: number): string {
  return `0x${value.toString(16).toUpperCase().padStart(8, '0')}`
}

/**
 * A Windows Update HRESULT in words.
 *
 * The server stores HRESULTs as signed 32-bit integers (.NET `int`), so
 * 0x80240017 usually arrives as -2145124329; both spellings map the same.
 * Mapped codes read "<words> (0x8024....)"; any other 32-bit code reads
 * "Windows Update error 0x%08X". A value outside the 32-bit range is shown as
 * its own hex rather than silently wrapped onto a different code; a missing or
 * zero code says so instead of inventing one.
 */
export function wuErrorText(hresult: number | null | undefined): string {
  if (hresult == null || hresult === 0) return 'Windows Update gave no error code'
  if (!Number.isSafeInteger(hresult)) return `Windows Update error ${String(hresult)}`

  // Signed 32-bit → unsigned; positive values up to 0xFFFFFFFF pass unchanged.
  const inRange = hresult >= -0x80000000 && hresult <= 0xffffffff
  if (!inRange) {
    return hresult > 0 ? `Windows Update error ${hex8(hresult)}` : `Windows Update error ${String(hresult)}`
  }
  const code = hresult < 0 ? hresult >>> 0 : hresult
  const words = WU_ERRORS.get(code)
  return words ? `${words} (${hex8(code)})` : `Windows Update error ${hex8(code)}`
}

// ------------------------------------------------------ follow-up polling

/** The first (and post-change) wait between follow-up polls. */
export const FOLLOW_UP_FIRST_DELAY_MS = 10_000
/** The longest wait between follow-up polls; the doubling stops here. */
export const FOLLOW_UP_MAX_DELAY_MS = 60_000
/** Visible time with no change after which the panel stops polling on its own. */
export const FOLLOW_UP_GIVE_UP_MS = 15 * 60_000

/** Why follow-up polling stopped: the detail settled, or it went quiet for too long. */
export type FollowUpStop = 'settled' | 'gaveUp'

/**
 * The device panel's follow-up poll, as plain data so its schedule is testable
 * without timers or a DOM. Every transition takes `now` (epoch ms) explicitly.
 *
 * - Backs off 10s → 20s → 40s → 60s (cap) while polls see no change, and drops
 *   back to 10s the moment one does.
 * - Gives up after {@link FOLLOW_UP_GIVE_UP_MS} of no change -- a stuck
 *   "verifying" row must not be polled forever.
 * - Pauses while the page is hidden; hidden time counts neither toward the next
 *   poll nor toward giving up, so a tab left in the background does not come
 *   back to a panel that stopped checking while nobody was looking.
 */
export interface FollowUpState {
  /** Visible time to wait between the previous poll and the next. */
  delayMs: number
  /** Visible time the detail has gone without changing, across polls. */
  quietMs: number
  /** Visible time already waited toward the next poll, banked when the page was hidden. */
  waitedMs: number
  /** When the current visible stretch began (epoch ms); null while the page is hidden. */
  visibleSince: number | null
  /** Why polling stopped, or null while it runs. */
  stopped: FollowUpStop | null
}

/** A fresh follow-up: first poll in 10s, nothing banked; paused at once if the page is hidden. */
export function startFollowUp(now: number, hidden: boolean): FollowUpState {
  return {
    delayMs: FOLLOW_UP_FIRST_DELAY_MS,
    quietMs: 0,
    waitedMs: 0,
    visibleSince: hidden ? null : now,
    stopped: null,
  }
}

function visibleWaited(state: FollowUpState, now: number): number {
  return state.waitedMs + (state.visibleSince === null ? 0 : Math.max(0, now - state.visibleSince))
}

/** How long until the next poll, or null for no timer at all (stopped, or the page is hidden). */
export function followUpTimerMs(state: FollowUpState, now: number): number | null {
  if (state.stopped || state.visibleSince === null) return null
  return Math.max(0, state.delayMs - visibleWaited(state, now))
}

/** The page went hidden: bank the visible time waited so far and pause. */
export function followUpHidden(state: FollowUpState, now: number): FollowUpState {
  if (state.stopped || state.visibleSince === null) return state
  return { ...state, waitedMs: visibleWaited(state, now), visibleSince: null }
}

/** The page is visible again: resume counting from where it paused. */
export function followUpVisible(state: FollowUpState, now: number): FollowUpState {
  if (state.stopped || state.visibleSince !== null) return state
  return { ...state, visibleSince: now }
}

/**
 * A poll came back. `changed` is whether the detail differs from the previous
 * read (each page's `followUpSignature`); `needsFollowUp` is that page's
 * `detailNeedsFollowUp` of the new detail (true when the read failed -- a failed
 * read settles nothing).
 */
export function followUpPolled(
  state: FollowUpState,
  poll: { changed: boolean; needsFollowUp: boolean; now: number },
): FollowUpState {
  if (state.stopped) return state
  const waited = visibleWaited(state, poll.now)
  const restarted = { waitedMs: 0, visibleSince: state.visibleSince === null ? null : poll.now }
  if (!poll.needsFollowUp) return { ...state, ...restarted, stopped: 'settled' }
  if (poll.changed) return { ...state, ...restarted, delayMs: FOLLOW_UP_FIRST_DELAY_MS, quietMs: 0 }
  const quietMs = state.quietMs + waited
  if (quietMs >= FOLLOW_UP_GIVE_UP_MS) return { ...state, ...restarted, quietMs, stopped: 'gaveUp' }
  return { ...state, ...restarted, quietMs, delayMs: Math.min(state.delayMs * 2, FOLLOW_UP_MAX_DELAY_MS) }
}
