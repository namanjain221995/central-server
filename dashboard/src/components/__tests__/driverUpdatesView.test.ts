import { describe, expect, it } from 'vitest'
import { formatUpdateSize, installRebootRequired, nonPreviewUpdateIds } from '../../pages/driverUpdatesView'
import type { DriverUpdateItem } from '../../api/client'

/**
 * The Windows Driver Updates page's pure rules. The server decides what is
 * eligible and whether a reboot is owed; what is guarded here is that the
 * "auto-select non-preview" button never ticks a preview driver, that the
 * reboot handoff reads the task result without ever throwing, and that an
 * unknown size is shown as "—" rather than a fabricated number.
 */

function update(partial: Partial<DriverUpdateItem> & { updateId: string }): DriverUpdateItem {
  return {
    revision: 1,
    title: partial.updateId,
    driverClass: null,
    driverProvider: null,
    driverVerDate: null,
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
