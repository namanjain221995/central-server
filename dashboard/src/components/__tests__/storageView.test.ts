import { describe, expect, it } from 'vitest'
import type { DeviceStorageRow, FleetStorageSummary, VolumeStorageRow } from '../../api/client'
import {
  fleetHeadline,
  formatBytes,
  freeText,
  needsAttention,
  severityBadge,
  usedPercent,
} from '../../pages/storageView'

const GiB = 1024 ** 3

function volume(partial: Partial<VolumeStorageRow> = {}): VolumeStorageRow {
  return {
    name: 'C:',
    fileSystem: 'NTFS',
    sizeBytes: 100 * GiB,
    freeBytes: 50 * GiB,
    freePercent: 50,
    severity: 'Ok',
    ...partial,
  }
}

function summary(partial: Partial<FleetStorageSummary> = {}): FleetStorageSummary {
  return {
    activeDevices: 12,
    devicesReporting: 10,
    lowDiskDeviceCount: 0,
    criticalDiskDeviceCount: 0,
    criticalFreePercent: 5,
    lowFreePercent: 10,
    truncated: false,
    devices: [],
    ...partial,
  }
}

describe('storageView.formatBytes', () => {
  it('scales units and drops the decimal above 100', () => {
    expect(formatBytes(0)).toBe('0.0 B')
    expect(formatBytes(512)).toBe('512 B')
    expect(formatBytes(2 * 1024)).toBe('2.0 KB')
    expect(formatBytes(50 * GiB)).toBe('50.0 GB')
    expect(formatBytes(931 * GiB)).toBe('931 GB')
  })

  it('renders a null size as an em dash', () => {
    expect(formatBytes(null)).toBe('—')
  })
})

describe('storageView.severityBadge', () => {
  it('maps each severity to a tone and label', () => {
    expect(severityBadge('Critical')).toEqual({ tone: 'crit', label: 'Critical' })
    expect(severityBadge('Low')).toEqual({ tone: 'warn', label: 'Low' })
    expect(severityBadge('Ok')).toEqual({ tone: 'ok', label: 'OK' })
    expect(severityBadge('Unknown')).toEqual({ tone: 'muted', label: 'No data' })
  })
})

describe('storageView.usedPercent', () => {
  it('is the complement of free space, clamped to 0..100', () => {
    expect(usedPercent(volume({ sizeBytes: 100 * GiB, freeBytes: 10 * GiB }))).toBe(90)
    expect(usedPercent(volume({ sizeBytes: 0, freeBytes: 0 }))).toBe(0)
    // Free reported larger than size (bad inventory) never yields a negative bar.
    expect(usedPercent(volume({ sizeBytes: 100 * GiB, freeBytes: 200 * GiB }))).toBe(0)
  })
})

describe('storageView.freeText', () => {
  it('reads "<free> free of <size> (<pct>%)"', () => {
    expect(freeText(volume({ sizeBytes: 100 * GiB, freeBytes: 50 * GiB, freePercent: 50 }))).toBe(
      '50.0 GB free of 100 GB (50%)',
    )
  })

  it('states when a volume reported no size instead of inventing a percentage', () => {
    expect(freeText(volume({ sizeBytes: 0, freeBytes: 0 }))).toBe('Size not reported')
  })
})

describe('storageView.fleetHeadline', () => {
  it('reassures when nothing needs attention', () => {
    expect(fleetHeadline(summary())).toBe('All clear — 10 of 12 devices reporting.')
  })

  it('leads with what needs attention', () => {
    expect(fleetHeadline(summary({ criticalDiskDeviceCount: 2, lowDiskDeviceCount: 1 }))).toBe(
      '2 critical, 1 low on space — 10 of 12 devices reporting.',
    )
  })

  it('singularises a one-device fleet', () => {
    expect(fleetHeadline(summary({ activeDevices: 1, devicesReporting: 1 }))).toBe(
      'All clear — 1 of 1 device reporting.',
    )
  })
})

describe('storageView.needsAttention', () => {
  const row = (severity: DeviceStorageRow['severity']): DeviceStorageRow => ({
    deviceId: 'd',
    hostname: 'H',
    collectedAt: null,
    severity,
    volumes: [],
  })

  it('is true only for Low and Critical', () => {
    expect(needsAttention(row('Critical'))).toBe(true)
    expect(needsAttention(row('Low'))).toBe(true)
    expect(needsAttention(row('Ok'))).toBe(false)
    expect(needsAttention(row('Unknown'))).toBe(false)
  })
})
