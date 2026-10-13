import { describe, expect, it } from 'vitest'
import type { AuditLogEntry } from '../../api/client'
import { auditResultTone, describeAuditAction, describeAuditSubject, formatWhen } from '../../pages/auditView'

function entry(overrides: Partial<AuditLogEntry> = {}): AuditLogEntry {
  return {
    id: '1',
    occurredAt: '2026-10-14T10:00:00Z',
    actorType: 'PlatformUser',
    actorDisplay: 'admin@example.com',
    action: 'usb.access.grant',
    result: 'Success',
    deviceId: null,
    deviceDisplay: null,
    targetType: null,
    targetDisplay: null,
    failureReason: null,
    ...overrides,
  }
}

describe('describeAuditAction', () => {
  it('uses the wording for a known action', () => {
    expect(describeAuditAction('usb.portable_device.connected')).toBe('Phone connected')
    expect(describeAuditAction('auth.sign_in')).toBe('Signed in')
  })

  it('spells out an action it has no wording for instead of hiding it', () => {
    expect(describeAuditAction('software.application.force_stop')).toBe('Software application force stop')
    expect(describeAuditAction('usb.something_new')).toBe('USB something new')
  })

  it('returns an unparseable action unchanged', () => {
    expect(describeAuditAction('...')).toBe('...')
  })
})

describe('describeAuditSubject', () => {
  it('names the device and the target', () => {
    expect(describeAuditSubject(entry({ deviceDisplay: 'LAPTOP-1', targetDisplay: 'Redmi Note 14' }))).toBe(
      'LAPTOP-1 · Redmi Note 14',
    )
  })

  it('does not repeat a target that is the device itself', () => {
    expect(describeAuditSubject(entry({ deviceDisplay: 'LAPTOP-1', targetDisplay: 'LAPTOP-1' }))).toBe('LAPTOP-1')
  })

  it('is null when nothing was recorded', () => {
    expect(describeAuditSubject(entry())).toBeNull()
  })
})

describe('formatWhen', () => {
  const now = new Date('2026-10-14T12:00:00Z')

  it('says how long ago within the day', () => {
    expect(formatWhen('2026-10-14T11:59:30Z', now)).toBe('just now')
    expect(formatWhen('2026-10-14T11:55:00Z', now)).toBe('5 min ago')
    expect(formatWhen('2026-10-14T09:00:00Z', now)).toBe('3 h ago')
  })

  it('shows the date for anything older than a day', () => {
    expect(formatWhen('2026-10-10T09:00:00Z', now)).toBe(new Date('2026-10-10T09:00:00Z').toLocaleDateString())
  })

  it('treats a clock slightly ahead of the browser as just now', () => {
    expect(formatWhen('2026-10-14T12:00:05Z', now)).toBe('just now')
  })

  it('shows a dash for a timestamp it cannot read', () => {
    expect(formatWhen('not a date', now)).toBe('—')
  })
})

describe('auditResultTone', () => {
  it('marks denials and failures', () => {
    expect(auditResultTone('Success')).toBe('ok')
    expect(auditResultTone('Denied')).toBe('warn')
    expect(auditResultTone('Failure')).toBe('crit')
  })
})
