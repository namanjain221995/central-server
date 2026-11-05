// Pure formatting helpers for the Work Time report and settings. No React, no
// network — kept here so they can be unit-tested without a DOM.

/** Minutes-of-day (0–1439) to a 24-hour clock string, e.g. 1170 → "19:30". */
export function clockOfMinutes(minutesOfDay: number): string {
  const safe = ((Math.round(minutesOfDay) % 1440) + 1440) % 1440
  const h = Math.floor(safe / 60)
  const m = safe % 60
  return `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}`
}

/**
 * A 24-hour "HH:MM" clock string to minutes-of-day, or null when it is not a
 * valid time. Used to read the shift-time inputs back.
 */
export function minutesOfClock(value: string): number | null {
  const match = /^(\d{1,2}):(\d{2})$/.exec(value.trim())
  if (!match) return null
  const h = Number(match[1])
  const m = Number(match[2])
  if (h < 0 || h > 23 || m < 0 || m > 59) return null
  return h * 60 + m
}

/** Whole minutes to "h:mm", e.g. 425 → "7:05". Negative clamps to "0:00". */
export function hoursMinutes(minutes: number): string {
  const safe = Math.max(0, Math.round(minutes))
  return `${Math.floor(safe / 60)}:${(safe % 60).toString().padStart(2, '0')}`
}

/** A compact decimal-hours label for a tooltip, e.g. 425 → "7.1 h". */
export function decimalHours(minutes: number): string {
  return `${(Math.max(0, minutes) / 60).toFixed(1)} h`
}

/** A UTC-offset in minutes to a signed clock label, e.g. 330 → "UTC+5:30". */
export function offsetLabel(minutes: number): string {
  const sign = minutes < 0 ? '-' : '+'
  const abs = Math.abs(minutes)
  return `UTC${sign}${Math.floor(abs / 60)}:${(abs % 60).toString().padStart(2, '0')}`
}

/**
 * The nominal shift figures, all in minutes: gross is the whole shift window,
 * the break is the allowance, and the target is what is left to work. Derived
 * once from the settings so the report header and the column maths agree.
 */
export interface ShiftTargets {
  grossMinutes: number
  breakMinutes: number
  workingTargetMinutes: number
}

export function shiftTargets(settings: {
  shiftLengthMinutes: number
  breakMinutes: number
}): ShiftTargets {
  return {
    grossMinutes: settings.shiftLengthMinutes,
    breakMinutes: settings.breakMinutes,
    workingTargetMinutes: Math.max(0, settings.shiftLengthMinutes - settings.breakMinutes),
  }
}

/** Human-readable bytes, binary (1024) units: 12_582_912 → "12.0 MB". */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB', 'TB', 'PB']
  let value = bytes / 1024
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  return `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`
}

/** A row's share of the database, 0–100, for the storage bar. */
export function storagePercent(totalBytes: number, databaseBytes: number): number {
  if (databaseBytes <= 0) return 0
  return Math.min(100, Math.round((totalBytes / databaseBytes) * 100))
}

/** yyyy-MM-dd for a Date in local time (not UTC — avoids an off-by-one at the day edge). */
export function isoDate(date: Date): string {
  const y = date.getFullYear()
  const m = (date.getMonth() + 1).toString().padStart(2, '0')
  const d = date.getDate().toString().padStart(2, '0')
  return `${y}-${m}-${d}`
}

/** The ISO date `days` before today, local time. */
export function isoDaysAgo(days: number, today: Date): string {
  const d = new Date(today)
  d.setDate(d.getDate() - days)
  return isoDate(d)
}
