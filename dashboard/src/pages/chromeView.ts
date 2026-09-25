import type {
  ChromeDeviceRow,
  ChromeExtensionRow,
  ChromeInstallationView,
  ChromeOverview,
  ChromeProfileRow,
  ChromeReportStatus,
} from '../api/client'

/**
 * The rules behind the Chrome Management page, kept out of the components so
 * each can be tested on its own. Every one is a client-side reading of what the
 * server decided: membership, online state, counts and status all come from the
 * server, and nothing here invents a number the server did not give.
 */

export type Tone = 'ok' | 'warn' | 'crit' | 'neutral'

/** One summary tile at the top of the page. */
export interface ChromeStatTile {
  label: string
  value: string
  tone?: 'ok' | 'warn'
  note?: string
}

/**
 * The summary tiles. Before anything has loaded, and wherever the server says a
 * number is not yet computable, the tile shows an em-dash: a zero would be a
 * claim about the estate ("nothing is outdated") that nobody has checked.
 */
export function overviewTiles(overview: ChromeOverview | null): ChromeStatTile[] {
  const n = (value: number | null | undefined) => (value === null || value === undefined ? '—' : String(value))

  return [
    { label: 'Total Groups', value: n(overview?.totalGroups) },
    { label: 'Total Devices', value: n(overview?.totalDevices) },
    { label: 'Devices Online', value: n(overview?.onlineDevices), tone: overview ? 'ok' : undefined },
    { label: 'Chrome Profiles', value: n(overview?.totalProfiles) },
    { label: 'Extensions', value: n(overview?.totalExtensions), note: 'Without Chrome’s own built-ins' },
    {
      label: 'Devices Outdated',
      value: n(overview?.devicesWithUpdatesAvailable),
      tone: overview?.devicesWithUpdatesAvailable ? 'warn' : undefined,
      note: overview && overview.devicesWithUpdatesAvailable === null ? 'Needs a Chrome release reference' : undefined,
    },
  ]
}

/** How the Chrome column reads. A null status means the device has never reported the section. */
export function chromeStatusLabel(status: ChromeReportStatus | null): string {
  switch (status) {
    case 'Available':
      return 'Installed'
    case 'NotInstalled':
      return 'Not installed'
    case 'Error':
      return 'Incomplete report'
    case null:
      return 'Not reported'
  }
}

export function chromeStatusTone(status: ChromeReportStatus | null): Tone {
  switch (status) {
    case 'Available':
      return 'ok'
    case 'Error':
      return 'warn'
    case 'NotInstalled':
    case null:
      return 'neutral'
  }
}

/**
 * The update column. Only "Unknown" exists today; the two other values are the
 * ones a later phase will send once a release reference exists, mapped now so
 * the column does not need touching when they arrive.
 */
export function updateStatusLabel(status: string): string {
  switch (status) {
    case 'UpToDate':
      return 'Up to date'
    case 'UpdateAvailable':
      return 'Update available'
    case 'Unknown':
      return 'Unknown'
    default:
      return status
  }
}

export function updateStatusTone(status: string): Tone {
  switch (status) {
    case 'UpToDate':
      return 'ok'
    case 'UpdateAvailable':
      return 'warn'
    default:
      return 'neutral'
  }
}

/** "131.0.6778.86 (stable)", or an em-dash when no version was reported. */
export function versionLabel(row: { chromeVersion: string | null; channel: string | null }): string {
  if (!row.chromeVersion) return '—'
  return row.channel ? `${row.chromeVersion} (${row.channel})` : row.chromeVersion
}

/** Case-insensitive match on the hostname or the console display name. */
export function matchesSearch(row: Pick<ChromeDeviceRow, 'hostname' | 'displayName'>, search: string): boolean {
  const q = search.trim().toLowerCase()
  if (!q) return true
  return row.hostname.toLowerCase().includes(q) || (row.displayName?.toLowerCase().includes(q) ?? false)
}

/** The name a profile shows: what the person called it, falling back to the directory Chrome uses. */
export function profileTitle(profile: Pick<ChromeProfileRow, 'profileName' | 'profileKey'>): string {
  const name = profile.profileName?.trim()
  return name ? name : profile.profileKey
}

/**
 * What sits under the title: the directory key when the title is not already
 * it, and the Windows account the profile belongs to (the SID when the name
 * could not be resolved).
 */
export function profileSubtitle(profile: Pick<ChromeProfileRow, 'profileName' | 'profileKey' | 'userAccount' | 'userSid'>): string {
  const account = profile.userAccount ?? profile.userSid
  return profileTitle(profile) === profile.profileKey ? account : `${profile.profileKey} · ${account}`
}

/**
 * Chrome's install types, in words. The names are the contract's; the grouping
 * is what an administrator wants to know -- who put it there.
 */
export function installTypeLabel(installType: string): string {
  switch (installType) {
    case 'Internal':
      return 'User installed'
    case 'ExternalPolicy':
    case 'ExternalPolicyDownload':
      return 'Policy'
    case 'Component':
    case 'ExternalComponent':
      return 'Chrome built-in'
    case 'Unpacked':
      return 'Unpacked (developer)'
    case 'ExternalPref':
    case 'ExternalPrefDownload':
    case 'ExternalRegistry':
      return 'Third-party installer'
    case 'CommandLine':
      return 'Command line'
    default:
      return 'Unknown'
  }
}

export function enabledLabel(enabled: boolean | null): string {
  if (enabled === null) return 'Unknown'
  return enabled ? 'Enabled' : 'Disabled'
}

/** Disabled is amber, not red: a switched-off extension is a state, not a fault. */
export function enabledTone(enabled: boolean | null): Tone {
  if (enabled === null) return 'neutral'
  return enabled ? 'ok' : 'warn'
}

/**
 * Components last -- they are Chrome's own, and an operator opening a profile
 * is looking for what somebody installed -- then named before nameless, then by
 * name, then by id so the order is stable.
 */
export function compareExtensions(a: ChromeExtensionRow, b: ChromeExtensionRow): number {
  if (a.isComponent !== b.isComponent) return a.isComponent ? 1 : -1
  if ((a.name === null) !== (b.name === null)) return a.name === null ? 1 : -1
  const byName = (a.name ?? '').localeCompare(b.name ?? '', undefined, { sensitivity: 'base' })
  return byName !== 0 ? byName : a.extensionId.localeCompare(b.extensionId)
}

export function formatDateTime(value: string | null): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString()
}

export function formatDate(value: string | null): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleDateString()
}

/** "Machine-wide" or "Per-user (DOMAIN\name)". Null when the installation did not say. */
export function scopeLabel(installation: Pick<ChromeInstallationView, 'installationScope' | 'installedForUser'>): string {
  if (installation.installationScope === 'User') {
    return installation.installedForUser ? `Per-user (${installation.installedForUser})` : 'Per-user'
  }
  if (installation.installationScope === 'Machine') return 'Machine-wide'
  return '—'
}

/** "3 profiles", "1 extension". */
export function countLabel(count: number, singular: string, plural = `${singular}s`): string {
  return `${count} ${count === 1 ? singular : plural}`
}
