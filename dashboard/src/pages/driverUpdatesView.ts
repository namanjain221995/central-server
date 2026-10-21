import type { DriverUpdateItem } from '../api/client'

/**
 * The Windows Driver Updates page's rules, kept pure so each one is testable
 * without React: which updates "auto-select non-preview" ticks, whether an
 * install's result asks for a reboot, and how a driver's download size reads.
 */

/**
 * The updateIds of every non-preview (not optional) driver update.
 *
 * This is what the "auto-select non-preview" button sets the selection to:
 * preview drivers are the optional ones an administrator usually does not want
 * pushed in bulk, so they are left unticked.
 */
export function nonPreviewUpdateIds(updates: readonly DriverUpdateItem[]): string[] {
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
