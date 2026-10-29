import type {
  DeviceSecurityUpdates,
  SecurityActiveInstall,
  SecurityInstallStatus,
  SecurityUpdateDeviceRow,
  SecurityUpdateItem,
  SecurityUpdateOverview,
} from '../api/client'
import { statusChip as sharedStatusChip, type StatusChip } from './windowsUpdateView'

/**
 * The Windows Security Updates page's rules, kept pure so each one is testable
 * without React: which updates "auto-select non-preview" ticks, whether an
 * install's result asks for a reboot, how each update's install state reads,
 * what a Windows Update error code means, when a hide reason is good enough to
 * submit, which badge tone a severity earns, and how an update's KB ids and
 * download size read.
 *
 * The server owns the truth -- it remembers every install attempt, derives each
 * update's installStatus, and queues every follow-up rescan itself (after an
 * install result, after a reboot, after an expiry). Nothing here decides state;
 * it only words what the server reported.
 */

/**
 * The updateIds of every non-preview (not optional) security update the
 * platform can usefully install now.
 *
 * This is what the "auto-select non-preview" button sets the selection to:
 * preview updates are the optional ones an administrator usually does not want
 * pushed in bulk, so they are left unticked. Left unticked too, because one click
 * should pick only what an install can actually move:
 * - already installing;
 * - waiting for a restart to finish (`pendingReboot`) -- installing again only
 *   fails with "another install or a restart is pending"; the restart finishes it;
 * - being verified (`verifying`) -- the install landed and the server's
 *   verification scan is still to report.
 * Ticking any of them by hand stays possible, with an inline note saying why it
 * will not help ({@link settlingSelectionNotes}).
 *
 * Unlike drivers, a re-offered (`reOffered`) security update IS ticked: it is
 * usually a cumulative update that rolled back during the restart, and a retry
 * after fixing the cause usually works. {@link reOfferedWarning} still says so
 * before "Start update" -- a warning, not a block.
 */
export function nonPreviewUpdateIds(updates: readonly SecurityUpdateItem[]): string[] {
  return updates.filter((u) => !u.isPreview && !NOT_AUTO_SELECTED.has(u.installStatus ?? 'none')).map((u) => u.updateId)
}

/** The install states "auto-select non-preview" leaves unticked; see {@link nonPreviewUpdateIds}. */
const NOT_AUTO_SELECTED: ReadonlySet<string> = new Set(['installing', 'pendingReboot', 'verifying'])

/**
 * Whether an install task's structured result reports a reboot is now pending.
 *
 * Reads the `rebootRequired` boolean the install task's `resultJson` carries.
 * Tolerant by design: a missing, empty or unparseable result -- an older agent,
 * or a task with no result yet -- is "no reboot known", never a thrown error on
 * a path that is only trying to decide whether to offer a Restart button.
 */
export function installRebootRequired(resultJson: string | null | undefined): boolean {
  if (!resultJson) return false
  try {
    const parsed = JSON.parse(resultJson) as { rebootRequired?: unknown }
    return parsed.rebootRequired === true
  } catch {
    return false
  }
}

// ------------------------------------------- shared with Driver Updates

// The chip badge classes, the Windows Update error text and the follow-up
// polling schedule are the same on the Driver Updates page; they live in
// windowsUpdateView and are re-exported here so this page imports one module.
// The status chip is shared too, with one security override (below).
export {
  chipBadgeClass,
  FOLLOW_UP_FIRST_DELAY_MS,
  FOLLOW_UP_GIVE_UP_MS,
  FOLLOW_UP_MAX_DELAY_MS,
  followUpHidden,
  followUpPolled,
  followUpTimerMs,
  followUpVisible,
  startFollowUp,
  wuErrorText,
} from './windowsUpdateView'
export type { ChipTone, FollowUpState, FollowUpStop, StatusChip } from './windowsUpdateView'

// ------------------------------------------------------------ status chips

/** The Status chip of a security update Windows offered again after its install. */
export const ROLLED_BACK_LABEL = 'Rolled back — retry'

/**
 * One pending security update's install state, worded for the Status column:
 * the shared chip (windowsUpdateView), except `reOffered`.
 *
 * For a driver, re-offered means the platform cannot complete it ("Re-offered by
 * Windows"). For a security update it usually means the cumulative update rolled
 * back during the restart ("undoing changes"), and a retry once the cause is
 * fixed usually works -- so it reads "Rolled back — retry", in warn, and stays
 * actionable. Every other state, an unknown one included, reads exactly as on
 * the Driver Updates page.
 */
export function statusChip(item: { installStatus?: string | null; lastHresult?: number | null }): StatusChip {
  if (item.installStatus === 'reOffered') return { label: ROLLED_BACK_LABEL, tone: 'warn' }
  return sharedStatusChip(item)
}

// ---------------------------------------------------------- page wording

/**
 * What the "Rolled back — retry" chip expands to. Plain words, because the
 * operator's real question is "why is it back, and what now?" -- and, for a
 * security update, what hiding it costs.
 */
export const RE_OFFERED_EXPLANATION =
  'Windows offered this update again after the restart — often because it rolled back while the device ' +
  'restarted ("undoing changes"). A retry after fixing the cause usually works: free disk space, finish ' +
  'any pending restarts, and look up the error code shown (here, or in the device’s update history). ' +
  'Then install it again from this list. If it keeps rolling back, hide it here with a reason — a hidden ' +
  'security update still leaves the device without that fix, so the reason is recorded.'

/**
 * What a recently confirmed install says. The platform verified against the
 * real offered set; the device's Settings page is a cache of its own last scan
 * and redraws only on that machine's next own check -- said out loud so nobody
 * walks to the device to find out.
 */
export function recentInstallNote(count: number): string {
  const pronoun = count > 1 ? 'them' : 'it'
  return (
    'Installed — verified no longer offered. ' +
    `The device's Settings page may keep showing ${pronoun} until the machine's next own update check.`
  )
}

/** The collapsed hidden-updates section's heading. */
export function hiddenUpdatesLabel(hidden: readonly unknown[] | null | undefined): string {
  return `Hidden updates (${hidden?.length ?? 0})`
}

/** The longest hide reason the server accepts, counted after trimming. */
export const HIDE_REASON_MAX_LENGTH = 200

/**
 * Whether a hide reason may be submitted: 1 to {@link HIDE_REASON_MAX_LENGTH}
 * characters once trimmed. Unlike a driver, a security update is never hidden
 * without saying why -- the device stays without that fix -- so a blank reason
 * keeps "Hide" disabled. The server applies the same rule and refuses a blank
 * one (400); this only spares the round trip.
 */
export function hideReasonValid(reason: string | null | undefined): boolean {
  const trimmed = reason?.trim() ?? ''
  return trimmed.length > 0 && trimmed.length <= HIDE_REASON_MAX_LENGTH
}

/**
 * The selected updates Windows offered again after an earlier install (usually
 * a rollback during the restart) -- the ones to warn about before "Start
 * update": retrying usually works, but only once whatever rolled it back is
 * fixed, or it rolls back again.
 */
export function reOfferedSelection(
  updates: readonly SecurityUpdateItem[],
  selected: ReadonlySet<string>,
): SecurityUpdateItem[] {
  return updates.filter((u) => selected.has(u.updateId) && u.installStatus === 'reOffered')
}

/**
 * The inline warning shown before "Start update", or null when there is nothing
 * to warn about. A warning, not a block: retrying a rolled-back security update
 * is the expected next step, and auto-select ticks it.
 */
export function reOfferedWarning(items: readonly Pick<SecurityUpdateItem, 'title'>[]): string | null {
  if (items.length === 0) return null
  const one = items.length === 1
  const tail =
    ` Retrying usually works once the cause is fixed — free disk space, finish any pending restarts, and check` +
    ` the error code shown — otherwise ${one ? 'it' : 'they'} may roll back again.`
  if (one) {
    return `“${items[0].title}” was offered again by Windows after the restart — it often means the install rolled back.${tail}`
  }
  const titles = items.map((i) => `“${i.title}”`).join(', ')
  return `${items.length} of the selected updates were offered again by Windows after the restart (${titles}) — it often means the install rolled back.${tail}`
}

/**
 * The inline notes shown before "Start update" for selected updates that are
 * still settling -- waiting for a restart, or being verified -- where installing
 * again cannot help. Auto-select leaves them unticked; these cover a hand tick.
 * Empty when there is nothing to say. Same pattern as {@link reOfferedWarning}:
 * a note, not a block.
 */
export function settlingSelectionNotes(
  updates: readonly SecurityUpdateItem[],
  selected: ReadonlySet<string>,
): string[] {
  const picked = (status: SecurityInstallStatus) =>
    updates.filter((u) => selected.has(u.updateId) && u.installStatus === status)
  const notes: string[] = []
  const reboot = selectionNote(
    picked('pendingReboot'),
    'is waiting for a restart',
    'are waiting for a restart',
    "installing again won't help; restart the device instead.",
  )
  if (reboot) notes.push(reboot)
  const verifying = selectionNote(picked('verifying'), 'is being verified', 'are being verified', 'wait for the scan.')
  if (verifying) notes.push(verifying)
  return notes
}

function selectionNote(
  items: readonly Pick<SecurityUpdateItem, 'title'>[],
  singular: string,
  plural: string,
  advice: string,
): string | null {
  if (items.length === 0) return null
  if (items.length === 1) return `“${items[0].title}” ${singular} — ${advice}`
  const titles = items.map((i) => `“${i.title}”`).join(', ')
  return `${items.length} of the selected updates ${plural} (${titles}) — ${advice}`
}

/** How many of a device's pending updates are waiting for a restart to finish. */
export function pendingRebootCount(updates: readonly SecurityUpdateItem[]): number {
  return updates.filter((u) => u.installStatus === 'pendingReboot').length
}

/** "1 update needs a restart to finish" / "3 updates need a restart to finish". */
export function needsRestartText(count: number): string {
  return count === 1 ? '1 update needs a restart to finish' : `${count} updates need a restart to finish`
}

/**
 * Whether the device panel should keep re-reading the detail on its own.
 *
 * The server queues the follow-up work itself -- the install, the verification
 * scan after it -- and none of that is a task this browser tracked. So while the
 * detail shows something settling on a timescale of minutes (an active install,
 * an update installing or being verified) the panel polls, and the server's
 * verdict lands without anyone clicking. Quiet otherwise.
 *
 * `pendingReboot` is deliberately NOT a trigger -- and for security updates it
 * is the usual outcome: a restart can be hours away (or never come), and the
 * server resolves it on its own after the device reboots and rescans. The panel
 * offers a Refresh button for that instead of polling a device that is simply
 * waiting for its user.
 */
export function detailNeedsFollowUp(
  details: Pick<DeviceSecurityUpdates, 'activeInstall' | 'updates'> | null,
): boolean {
  if (!details) return false
  if (details.activeInstall) return true
  return details.updates.some((u) => u.installStatus === 'installing' || u.installStatus === 'verifying')
}

// ------------------------------------------------------ follow-up polling

// The schedule itself (FollowUpState and its transitions) is shared; see
// windowsUpdateView. What is security-specific is what counts as a change.

/**
 * What a follow-up poll compares to tell "changed" from "no change": every field
 * of the detail that moves while an install settles, in a fixed order.
 */
export function followUpSignature(details: DeviceSecurityUpdates | null): string {
  if (!details) return ''
  return JSON.stringify([
    details.lastScanAt,
    details.rebootPending,
    details.activeInstall ? [details.activeInstall.taskId, details.activeInstall.status] : null,
    details.updates.map((u) => [u.updateId, u.revision, u.installStatus ?? null, u.attempts ?? null, u.lastAttemptAt ?? null, u.lastHresult ?? null]),
    (details.hidden ?? []).map((h) => h.updateId),
    (details.recentInstalls ?? []).map((r) => r.updateId),
  ])
}

/**
 * Whether the device panel shows its reboot banner (and Restart-now hand-off).
 *
 * Durable server state first -- the detail's `rebootPending`, which is any
 * PendingReboot record or the inventory flag, and any update chip saying it
 * needs a restart -- so the banner survives a reload. The install task's own
 * result (`installReboot`) is the immediate source, before the detail catches up.
 */
export function showRebootBanner(
  details: Pick<DeviceSecurityUpdates, 'rebootPending' | 'updates'> | null,
  device: Pick<SecurityUpdateDeviceRow, 'rebootPending'>,
  installReboot: boolean,
): boolean {
  if (installReboot) return true
  if (details) return details.rebootPending || pendingRebootCount(details.updates) > 0
  return device.rebootPending
}

/**
 * The banner for an install still in flight, read from the server's
 * `activeInstall` so it survives a page reload. While the device runs an
 * install its check-ins pause, so the console may read it as offline -- the
 * wording says so instead of letting that look like a failure. Security
 * updates are large downloads, so a delivered install may run a long while.
 */
export function activeInstallMessage(
  active: Pick<SecurityActiveInstall, 'status' | 'updateIds'>,
  deviceOnline: boolean,
): string {
  const n = active.updateIds.length
  const what = n > 0 ? `${n} security update${n === 1 ? '' : 's'}` : 'security updates'
  if (active.status === 'Queued') {
    return deviceOnline
      ? `Install of ${what} queued — waiting for the device to check in.`
      : `Install of ${what} queued — the device is offline; it runs when the agent reconnects.`
  }
  return `Installing ${what} on the device — it may show offline until the install finishes.`
}

export interface OverviewCounts {
  /**
   * Pending updates not hidden (the server's pending count). Unlike drivers, a
   * rolled-back (re-offered) security update stays actionable -- a retry usually
   * works; install still skips any that are settling.
   */
  actionable: number
  /**
   * "Needs attention": rolled back after a restart (re-offered), or hidden. It
   * overlaps `actionable` on purpose -- a rolled-back update is both something
   * to retry and something to look at.
   */
  cannotComplete: number
  /** Installed, waiting for a restart to finish. */
  needsRestart: number
}

/**
 * The overview cards' numbers. The summary's own counts win; when a server
 * predates them, the per-device rows are summed instead (and an older server's
 * rows lack them too, which sums to 0 -- nothing claimed).
 */
export function overviewCounts(overview: SecurityUpdateOverview | null): OverviewCounts | null {
  if (!overview) return null
  const { summary, devices } = overview
  const sum = (pick: (d: SecurityUpdateDeviceRow) => number | undefined) =>
    devices.reduce((acc, d) => acc + (pick(d) ?? 0), 0)
  return {
    actionable: summary.totalPending,
    cannotComplete: summary.cannotCompleteCount ?? sum((d) => d.cannotCompleteCount),
    needsRestart: summary.needsRestartCount ?? sum((d) => d.needsRestartCount),
  }
}

// ------------------------------------------------------- security columns

/**
 * The badge tone for an update's MSRC severity: Critical reads as critical,
 * Important as a warning, and anything else -- Moderate, Low, an unrated update
 * or a severity the server did not have -- as neutral. Case-insensitive because
 * the string comes from Windows Update by way of the server, not from an enum
 * the dashboard controls.
 */
export function severityBadgeClass(severity: string | null): 'crit' | 'warn' | 'neutral' {
  const s = severity?.trim().toLowerCase()
  if (s === 'critical') return 'crit'
  if (s === 'important') return 'warn'
  return 'neutral'
}

/**
 * An update's KB article id(s) in words: "—" when Windows reported none (the
 * server sent null or an empty string), otherwise the server's string as-is.
 */
export function formatKbIds(kbIds: string | null): string {
  const trimmed = kbIds?.trim()
  return trimmed ? trimmed : '—'
}

/**
 * A security update's download size in words: "—" when the size is unknown (the
 * server sent null), otherwise a human unit. Mirrors the device page's byte
 * formatting so sizes read the same across the console.
 */
export function formatUpdateSize(bytes: number | null): string {
  if (bytes == null) return '—'
  if (bytes === 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value >= 100 || unit === 0 ? 0 : 1)} ${units[unit]}`
}
