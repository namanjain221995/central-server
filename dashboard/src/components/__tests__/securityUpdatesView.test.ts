import { describe, expect, it } from 'vitest'
import {
  formatKbIds,
  formatUpdateSize,
  installRebootRequired,
  nonPreviewUpdateIds,
  severityBadgeClass,
  shouldAutoRescanAfterInstall,
} from '../../pages/securityUpdatesView'
import type { SecurityUpdateItem } from '../../api/client'

/**
 * The Windows Security Updates page's pure rules. The server decides what is
 * eligible and whether a reboot is owed; what is guarded here is that the
 * "auto-select non-preview" button never ticks a preview update, that the
 * reboot handoff reads the task result without ever throwing, that a successful
 * install triggers exactly one automatic re-scan (and a scan never cascades into
 * another), that a severity earns the badge tone the page promises (Critical is
 * critical, Important is a warning, the rest neutral), and that unknown KB ids
 * and sizes are shown as "—" rather than fabricated.
 */

function update(partial: Partial<SecurityUpdateItem> & { updateId: string }): SecurityUpdateItem {
  return {
    revision: 1,
    title: partial.updateId,
    kbIds: null,
    severity: null,
    isPreview: false,
    sizeBytes: null,
    ...partial,
  }
}

describe('nonPreviewUpdateIds', () => {
  it('returns the ids of only the non-preview updates, in order', () => {
    const updates = [
      update({ updateId: 'a', isPreview: false }),
      update({ updateId: 'b', isPreview: true }),
      update({ updateId: 'c', isPreview: false }),
    ]

    expect(nonPreviewUpdateIds(updates)).toEqual(['a', 'c'])
  })

  it('is empty when every pending update is a preview', () => {
    const updates = [update({ updateId: 'a', isPreview: true }), update({ updateId: 'b', isPreview: true })]

    expect(nonPreviewUpdateIds(updates)).toEqual([])
  })

  it('is empty for no updates at all', () => {
    expect(nonPreviewUpdateIds([])).toEqual([])
  })
})

describe('installRebootRequired', () => {
  it('is true only when the result says rebootRequired: true', () => {
    expect(installRebootRequired('{"rebootRequired":true,"installed":3}')).toBe(true)
    expect(installRebootRequired('{"rebootRequired":false,"installed":3}')).toBe(false)
    expect(installRebootRequired('{"installed":3}')).toBe(false)
  })

  it('treats a missing, empty or unparseable result as no reboot, never throwing', () => {
    expect(installRebootRequired(null)).toBe(false)
    expect(installRebootRequired(undefined)).toBe(false)
    expect(installRebootRequired('')).toBe(false)
    expect(installRebootRequired('not json')).toBe(false)
  })

  it('does not treat a truthy non-boolean as a reboot', () => {
    // The server sends a boolean; "true" the string, or 1, is a shape we do not
    // trust to mean a reboot is owed.
    expect(installRebootRequired('{"rebootRequired":"true"}')).toBe(false)
    expect(installRebootRequired('{"rebootRequired":1}')).toBe(false)
  })
})

describe('shouldAutoRescanAfterInstall', () => {
  it('re-scans once when the install settled as Succeeded and none has been queued yet', () => {
    expect(shouldAutoRescanAfterInstall({ id: 'install-1', status: 'Succeeded' }, null)).toBe(true)
  })

  it('does not re-scan again for an install already re-scanned (no loop on the scan it queued)', () => {
    // After the first success sets the guard to this install's id, every later
    // settle — the re-scan's own among them — sees the same id and stands down.
    expect(shouldAutoRescanAfterInstall({ id: 'install-1', status: 'Succeeded' }, 'install-1')).toBe(false)
  })

  it('re-scans for a new install even after an earlier one was handled', () => {
    expect(shouldAutoRescanAfterInstall({ id: 'install-2', status: 'Succeeded' }, 'install-1')).toBe(true)
  })

  it('never re-scans on a non-success or a missing install task', () => {
    expect(shouldAutoRescanAfterInstall({ id: 'install-1', status: 'Failed' }, null)).toBe(false)
    expect(shouldAutoRescanAfterInstall({ id: 'install-1', status: 'Expired' }, null)).toBe(false)
    expect(shouldAutoRescanAfterInstall({ id: 'install-1', status: 'Delivered' }, null)).toBe(false)
    expect(shouldAutoRescanAfterInstall(undefined, null)).toBe(false)
    expect(shouldAutoRescanAfterInstall(null, null)).toBe(false)
  })
})

describe('severityBadgeClass', () => {
  it('maps Critical to the critical tone and Important to the warning tone', () => {
    expect(severityBadgeClass('Critical')).toBe('crit')
    expect(severityBadgeClass('Important')).toBe('warn')
  })

  it('is case-insensitive and ignores surrounding whitespace, since the string comes from Windows Update', () => {
    expect(severityBadgeClass('critical')).toBe('crit')
    expect(severityBadgeClass('CRITICAL')).toBe('crit')
    expect(severityBadgeClass(' Important ')).toBe('warn')
  })

  it('maps everything else — Moderate, Low, unrated, unknown — to neutral', () => {
    expect(severityBadgeClass('Moderate')).toBe('neutral')
    expect(severityBadgeClass('Low')).toBe('neutral')
    expect(severityBadgeClass('Unspecified')).toBe('neutral')
    expect(severityBadgeClass('')).toBe('neutral')
    expect(severityBadgeClass(null)).toBe('neutral')
  })
})

describe('formatKbIds', () => {
  it('shows an em dash when Windows reported no KB ids', () => {
    expect(formatKbIds(null)).toBe('—')
    expect(formatKbIds('')).toBe('—')
    expect(formatKbIds('   ')).toBe('—')
  })

  it('shows the server string as-is otherwise', () => {
    expect(formatKbIds('KB5031354')).toBe('KB5031354')
    expect(formatKbIds('KB5031354, KB5031356')).toBe('KB5031354, KB5031356')
  })
})

describe('formatUpdateSize', () => {
  it('shows an em dash for an unknown size', () => {
    expect(formatUpdateSize(null)).toBe('—')
  })

  it('formats bytes up through the units without decimals for whole byte counts', () => {
    expect(formatUpdateSize(0)).toBe('0 B')
    expect(formatUpdateSize(512)).toBe('512 B')
    expect(formatUpdateSize(1024)).toBe('1.0 KB')
    expect(formatUpdateSize(5 * 1024 * 1024)).toBe('5.0 MB')
  })

  it('drops the decimal once the value is large within a unit', () => {
    expect(formatUpdateSize(150 * 1024 * 1024)).toBe('150 MB')
  })
})
