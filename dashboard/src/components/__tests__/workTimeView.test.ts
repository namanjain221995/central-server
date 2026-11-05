import { describe, expect, it } from 'vitest'
import {
  clockOfMinutes,
  decimalHours,
  formatBytes,
  hoursMinutes,
  isoDate,
  isoDaysAgo,
  minutesOfClock,
  offsetLabel,
  shiftTargets,
  storagePercent,
} from '../../pages/workTimeView'

describe('clock and minutes conversion', () => {
  it('formats minutes-of-day as a zero-padded 24-hour clock', () => {
    expect(clockOfMinutes(1170)).toBe('19:30')
    expect(clockOfMinutes(270)).toBe('04:30')
    expect(clockOfMinutes(0)).toBe('00:00')
  })

  it('wraps out-of-range minutes rather than overflowing', () => {
    expect(clockOfMinutes(1440)).toBe('00:00')
    expect(clockOfMinutes(-30)).toBe('23:30')
  })

  it('parses a valid HH:MM back to minutes', () => {
    expect(minutesOfClock('19:30')).toBe(1170)
    expect(minutesOfClock('04:30')).toBe(270)
    expect(minutesOfClock('00:00')).toBe(0)
  })

  it('rejects malformed or out-of-range times', () => {
    expect(minutesOfClock('')).toBeNull()
    expect(minutesOfClock('24:00')).toBeNull()
    expect(minutesOfClock('19:60')).toBeNull()
    expect(minutesOfClock('seven')).toBeNull()
  })

  it('round-trips every minute of the day', () => {
    for (const m of [0, 1, 59, 60, 330, 719, 1170, 1439]) {
      expect(minutesOfClock(clockOfMinutes(m))).toBe(m)
    }
  })
})

describe('duration formatting', () => {
  it('formats whole minutes as h:mm', () => {
    expect(hoursMinutes(425)).toBe('7:05')
    expect(hoursMinutes(540)).toBe('9:00')
    expect(hoursMinutes(0)).toBe('0:00')
  })

  it('clamps negative durations to zero', () => {
    expect(hoursMinutes(-5)).toBe('0:00')
  })

  it('gives a decimal-hours label', () => {
    expect(decimalHours(420)).toBe('7.0 h')
    expect(decimalHours(450)).toBe('7.5 h')
  })
})

describe('offset label', () => {
  it('signs the offset and formats it as a clock', () => {
    expect(offsetLabel(330)).toBe('UTC+5:30')
    expect(offsetLabel(0)).toBe('UTC+0:00')
    expect(offsetLabel(-300)).toBe('UTC-5:00')
  })
})

describe('shift targets', () => {
  it('derives gross, break and the working target', () => {
    const t = shiftTargets({ shiftLengthMinutes: 540, breakMinutes: 60 })
    expect(t.grossMinutes).toBe(540)
    expect(t.breakMinutes).toBe(60)
    expect(t.workingTargetMinutes).toBe(480)
  })

  it('never lets the working target go negative', () => {
    const t = shiftTargets({ shiftLengthMinutes: 30, breakMinutes: 60 })
    expect(t.workingTargetMinutes).toBe(0)
  })
})

describe('storage formatting', () => {
  it('scales bytes through binary units', () => {
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(1024)).toBe('1.0 KB')
    expect(formatBytes(12 * 1024 * 1024)).toBe('12.0 MB')
    expect(formatBytes(3 * 1024 * 1024 * 1024)).toBe('3.0 GB')
  })

  it('drops the decimal for large values of a unit', () => {
    expect(formatBytes(512 * 1024)).toBe('512 KB')
  })

  it('computes a bounded percentage of the database', () => {
    expect(storagePercent(50, 100)).toBe(50)
    expect(storagePercent(0, 100)).toBe(0)
    expect(storagePercent(200, 100)).toBe(100) // clamped
    expect(storagePercent(10, 0)).toBe(0) // no divide-by-zero
  })
})

describe('date helpers', () => {
  it('formats a local date as yyyy-MM-dd', () => {
    expect(isoDate(new Date(2026, 0, 5))).toBe('2026-01-05')
    expect(isoDate(new Date(2026, 10, 30))).toBe('2026-11-30')
  })

  it('subtracts days in local time, crossing a month boundary', () => {
    const today = new Date(2026, 10, 5) // 2026-11-05
    expect(isoDaysAgo(0, today)).toBe('2026-11-05')
    expect(isoDaysAgo(6, today)).toBe('2026-10-30')
  })
})
