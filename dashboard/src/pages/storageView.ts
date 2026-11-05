import type {
  DeviceStorageRow,
  FleetStorageSummary,
  StorageSeverity,
  VolumeStorageRow,
} from '../api/client'

/**
 * Pure presentation logic for the fleet storage overview. No rendering and no
 * fetching here, so the wording and the severity-to-badge mapping can be asserted
 * directly. The server decides each severity; this module only labels it.
 */

/** The badge tone reused across the console: crit (red), warn (amber), ok (green), muted (grey). */
export type BadgeTone = 'crit' | 'warn' | 'ok' | 'muted'

/** Human-readable size, e.g. "931 GB". Mirrors the device detail page's formatter. */
export function formatBytes(bytes: number | null): string {
  if (bytes == null) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`
}

/** Badge tone and label for a severity. Unknown is muted, not alarming. */
export function severityBadge(severity: StorageSeverity): { tone: BadgeTone; label: string } {
  switch (severity) {
    case 'Critical':
      return { tone: 'crit', label: 'Critical' }
    case 'Low':
      return { tone: 'warn', label: 'Low' }
    case 'Ok':
      return { tone: 'ok', label: 'OK' }
    default:
      return { tone: 'muted', label: 'No data' }
  }
}

/** Used space as a whole percent, for the fill bar. Unknown/zero-size volumes read 0. */
export function usedPercent(volume: VolumeStorageRow): number {
  if (volume.sizeBytes <= 0) return 0
  const used = Math.round(((volume.sizeBytes - volume.freeBytes) / volume.sizeBytes) * 100)
  return Math.min(100, Math.max(0, used))
}

/** "128 GB free of 931 GB (13.7%)" — or a plain note when there is no usable reading. */
export function freeText(volume: VolumeStorageRow): string {
  if (volume.sizeBytes <= 0) return 'Size not reported'
  return `${formatBytes(volume.freeBytes)} free of ${formatBytes(volume.sizeBytes)} (${volume.freePercent}%)`
}

/**
 * One-line headline for the top of the page. Leads with what needs attention so a
 * clean fleet reads as reassuring and a troubled one reads as urgent.
 */
export function fleetHeadline(summary: FleetStorageSummary): string {
  const parts: string[] = []
  if (summary.criticalDiskDeviceCount > 0) parts.push(`${summary.criticalDiskDeviceCount} critical`)
  if (summary.lowDiskDeviceCount > 0) parts.push(`${summary.lowDiskDeviceCount} low on space`)

  const devices = `${summary.devicesReporting} of ${summary.activeDevices} device${
    summary.activeDevices === 1 ? '' : 's'
  } reporting`

  if (parts.length === 0) {
    return `All clear — ${devices}.`
  }
  return `${parts.join(', ')} — ${devices}.`
}

/** Whether the row has any attention-worthy volume (drives default expand/highlight). */
export function needsAttention(row: DeviceStorageRow): boolean {
  return row.severity === 'Critical' || row.severity === 'Low'
}
