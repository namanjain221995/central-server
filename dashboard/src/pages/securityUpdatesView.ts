import type { SecurityUpdateItem } from '../api/client'

/**
 * The Windows Security Updates page's rules, kept pure so each one is testable
 * without React: which updates "auto-select non-preview" ticks, whether an
 * install's result asks for a reboot, whether a settled install should trigger
 * one automatic re-scan, which badge tone a severity earns, and how an update's
 * KB ids and download size read.
 */

/**
 * The updateIds of every non-preview (not optional) security update.
 *
 * This is what the "auto-select non-preview" button sets the selection to:
 * preview updates are the optional ones an administrator usually does not want
 * pushed in bulk, so they are left unticked.
 */
export function nonPreviewUpdateIds(updates: readonly SecurityUpdateItem[]): string[] {
  return updates.filter((u) => !u.isPreview).map((u) => u.updateId)
}

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

/**
 * Whether a settled install task should trigger one automatic re-scan.
 *
 * The per-device pending list is rebuilt only by a scan, so after an install
 * succeeds the just-installed updates would otherwise linger as "pending" until
 * someone clicks "Scan now". This returns true exactly once per install: when
 * the install task has settled as Succeeded and no re-scan has yet been queued
 * for that task id. A scan task's id is never the install's, so the re-scan it
 * triggers can never satisfy this and cascade into another one.
 */
export function shouldAutoRescanAfterInstall(
  installTask: { id: string; status: string } | null | undefined,
  rescannedForTaskId: string | null,
): boolean {
  if (!installTask || installTask.status !== 'Succeeded') return false
  return rescannedForTaskId !== installTask.id
}

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
