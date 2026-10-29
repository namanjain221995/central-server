import type {
  DeviceDriverUpdates,
  DriverActiveInstall,
  DriverInstallStatus,
  DriverUpdateDeviceRow,
  DriverUpdateItem,
  DriverUpdateOverview,
} from '../api/client'

/**
 * The Windows Driver Updates page's rules, kept pure so each one is testable
 * without React: which updates "auto-select non-preview" ticks, whether an
 * install's result asks for a reboot, how each update's install state reads,
 * what a Windows Update error code means, and how a driver's download size reads.
 *
 * The server owns the truth -- it remembers every install attempt, derives each
 * update's installStatus, and queues every follow-up rescan itself (after an
 * install result, after a reboot, after an expiry). Nothing here decides state;
 * it only words what the server reported.
 */

/**
 * The updateIds of every non-preview (not optional) driver update the platform
 * can usefully install now.
 *
 * This is what the "auto-select non-preview" button sets the selection to:
 * preview drivers are the optional ones an administrator usually does not want
 * pushed in bulk, so they are left unticked. Left unticked too, because one click
 * should pick only what an install can actually move:
 * - re-offered by Windows after an earlier install (retrying cannot complete them);
 * - already installing;
 * - waiting for a restart to finish (`pendingReboot`) -- installing again only
 *   fails with "another install or a restart is pending"; the restart finishes it;
 * - being verified (`verifying`) -- the install landed and the server's
 *   verification scan is still to report.
 * Ticking any of them by hand stays possible, with an inline note saying why it
 * will not help ({@link reOfferedWarning}, {@link settlingSelectionNotes}).
 */
export function nonPreviewUpdateIds(updates: readonly DriverUpdateItem[]): string[] {
  return updates.filter((u) => !u.isPreview && !NOT_AUTO_SELECTED.has(u.installStatus ?? 'none')).map((u) => u.updateId)
}

/** The install states "auto-select non-preview" leaves unticked; see {@link nonPreviewUpdateIds}. */
const NOT_AUTO_SELECTED: ReadonlySet<string> = new Set(['reOffered', 'installing', 'pendingReboot', 'verifying'])

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

// ---------------------------------------- shared with Security Updates

// The status chips, the Windows Update error text and the follow-up polling
// schedule are the same on the Security Updates page; they live in
// windowsUpdateView and are re-exported here so this page imports one module.
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
  statusChip,
  wuErrorText,
} from './windowsUpdateView'
export type { ChipTone, FollowUpState, FollowUpStop, StatusChip } from './windowsUpdateView'

// ---------------------------------------------------------- page wording

/**
 * What the "Re-offered by Windows" chip expands to. Plain words, because the
 * operator's real question is "why did the install do nothing, and what now?".
 */
export const RE_OFFERED_EXPLANATION =
  "Windows Update accepted this install and then offered the update again. Some vendor firmware/driver " +
  "packages cannot be completed by the platform's update channel (Windows reports success without a " +
  'durable install). It cannot be fixed by retrying. Options: install it on the laptop with the ' +
  "vendor's tool (e.g. the manufacturer's update app), or hide it here."

/**
 * What a recently confirmed install says. The platform verified against the
 * real offered set; the laptop's Settings page is a cache of its own last scan
 * and redraws only on that machine's next own check -- said out loud so nobody
 * walks to the laptop to find out.
 */
export function recentInstallNote(count: number): string {
  const pronoun = count > 1 ? 'them' : 'it'
  return (
    'Installed — verified no longer offered. ' +
    `The laptop's Settings page may keep showing ${pronoun} until the machine's next own update check.`
  )
}

/** The collapsed hidden-updates section's heading. */
export function hiddenUpdatesLabel(hidden: readonly unknown[] | null | undefined): string {
  return `Hidden updates (${hidden?.length ?? 0})`
}

/**
 * The selected updates Windows already re-offered after an earlier install --
 * the ones to warn about before "Start update", since installing them again
 * will report success and change nothing.
 */
export function reOfferedSelection(
  updates: readonly DriverUpdateItem[],
  selected: ReadonlySet<string>,
): DriverUpdateItem[] {
  return updates.filter((u) => selected.has(u.updateId) && u.installStatus === 'reOffered')
}

/** The inline warning shown before "Start update", or null when there is nothing to warn about. */
export function reOfferedWarning(items: readonly Pick<DriverUpdateItem, 'title'>[]): string | null {
  if (items.length === 0) return null
  const tail =
    ' Installing again will not complete ' +
    (items.length === 1 ? 'it' : 'them') +
    ' — Windows reports success without a durable install. Consider hiding ' +
    (items.length === 1 ? 'it' : 'them') +
    ' instead.'
  if (items.length === 1) {
    return `“${items[0].title}” was re-offered by Windows after an earlier install.${tail}`
  }
  const titles = items.map((i) => `“${i.title}”`).join(', ')
  return `${items.length} of the selected updates were re-offered by Windows after an earlier install (${titles}).${tail}`
}

/**
 * The inline notes shown before "Start update" for selected updates that are
 * still settling -- waiting for a restart, or being verified -- where installing
 * again cannot help. Auto-select leaves them unticked; these cover a hand tick.
 * Empty when there is nothing to say. Same pattern as {@link reOfferedWarning}:
 * a note, not a block.
 */
export function settlingSelectionNotes(
  updates: readonly DriverUpdateItem[],
  selected: ReadonlySet<string>,
): string[] {
  const picked = (status: DriverInstallStatus) =>
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
  items: readonly Pick<DriverUpdateItem, 'title'>[],
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
export function pendingRebootCount(updates: readonly DriverUpdateItem[]): number {
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
 * `pendingReboot` is deliberately NOT a trigger: a restart can be hours away (or
 * never come), and the server resolves it on its own after the device reboots
 * and rescans. The panel offers a Refresh button for that instead of polling a
 * device that is simply waiting for its user.
 */
export function detailNeedsFollowUp(
  details: Pick<DeviceDriverUpdates, 'activeInstall' | 'updates'> | null,
): boolean {
  if (!details) return false
  if (details.activeInstall) return true
  return details.updates.some((u) => u.installStatus === 'installing' || u.installStatus === 'verifying')
}

// ------------------------------------------------------ follow-up polling

// The schedule itself (FollowUpState and its transitions) is shared; see
// windowsUpdateView. What is driver-specific is what counts as a change.

/**
 * What a follow-up poll compares to tell "changed" from "no change": every field
 * of the detail that moves while an install settles, in a fixed order.
 */
export function followUpSignature(details: DeviceDriverUpdates | null): string {
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
  details: Pick<DeviceDriverUpdates, 'rebootPending' | 'updates'> | null,
  device: Pick<DriverUpdateDeviceRow, 'rebootPending'>,
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
 * wording says so instead of letting that look like a failure.
 */
export function activeInstallMessage(
  active: Pick<DriverActiveInstall, 'status' | 'updateIds'>,
  deviceOnline: boolean,
): string {
  const n = active.updateIds.length
  const what = n > 0 ? `${n} driver update${n === 1 ? '' : 's'}` : 'driver updates'
  if (active.status === 'Queued') {
    return deviceOnline
      ? `Install of ${what} queued — waiting for the device to check in.`
      : `Install of ${what} queued — the device is offline; it runs when the agent reconnects.`
  }
  return `Installing ${what} on the device — it may show offline until the install finishes.`
}

export interface OverviewCounts {
  /** Pending updates not hidden or re-offered (the server's pending count; install still skips any that are settling). */
  actionable: number
  /** Re-offered by Windows after an install, or hidden. */
  cannotComplete: number
  /** Installed, waiting for a restart to finish. */
  needsRestart: number
}

/**
 * The overview cards' numbers. The summary's own counts win; when a server
 * predates them, the per-device rows are summed instead (and an older server's
 * rows lack them too, which sums to 0 -- nothing claimed).
 */
export function overviewCounts(overview: DriverUpdateOverview | null): OverviewCounts | null {
  if (!overview) return null
  const { summary, devices } = overview
  const sum = (pick: (d: DriverUpdateDeviceRow) => number | undefined) =>
    devices.reduce((acc, d) => acc + (pick(d) ?? 0), 0)
  return {
    actionable: summary.totalPending,
    cannotComplete: summary.cannotCompleteCount ?? sum((d) => d.cannotCompleteCount),
    needsRestart: summary.needsRestartCount ?? sum((d) => d.needsRestartCount),
  }
}

/**
 * A driver update's download size in words: "—" when the size is unknown (the
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
