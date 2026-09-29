import { describe, expect, it } from 'vitest'
import type { ChromeExtensionRow, ChromeOverview } from '../../api/client'
import {
  chromeStatusLabel,
  chromeStatusTone,
  compareExtensions,
  countLabel,
  enabledLabel,
  extensionSourceLabel,
  hiddenExtensionsNote,
  visibleExtensions,
  enabledTone,
  formatDateTime,
  installTypeLabel,
  matchesSearch,
  overviewTiles,
  profileSubtitle,
  profileTitle,
  scopeLabel,
  updateStatusLabel,
  updateStatusTone,
  versionLabel,
} from '../../pages/chromeView'

const OVERVIEW: ChromeOverview = {
  totalGroups: 3,
  totalDevices: 12,
  onlineDevices: 9,
  devicesReportingChrome: 10,
  devicesWithChrome: 8,
  totalProfiles: 21,
  totalExtensions: 64,
  devicesWithUpdatesAvailable: null,
}

function extension(overrides: Partial<ChromeExtensionRow>): ChromeExtensionRow {
  return {
    extensionRowId: overrides.extensionRowId ?? 'row',
    extensionId: 'abcdefghijklmnopabcdefghijklmnop',
    name: 'Tab Tidy',
    version: '1.0',
    manifestVersion: 3,
    enabled: true,
    installType: 'Internal',
    isManaged: false,
    isComponent: false,
    installedByDefault: false,
    isActive: true,
    fromWebStore: true,
    updateUrl: null,
    installedAt: null,
    updatedAt: null,
    ...overrides,
  }
}

describe('summary tiles', () => {
  it('shows em-dashes, not zeroes, before anything has loaded', () => {
    const tiles = overviewTiles(null)
    expect(tiles.map((t) => t.value)).toEqual(['—', '—', '—', '—', '—', '—'])
    expect(tiles.every((t) => t.tone === undefined)).toBe(true)
  })

  it('shows the server’s numbers once loaded', () => {
    const tiles = overviewTiles(OVERVIEW)
    expect(tiles.map((t) => [t.label, t.value])).toEqual([
      ['Total Groups', '3'],
      ['Total Devices', '12'],
      ['Devices Online', '9'],
      ['Chrome Profiles', '21'],
      ['Extensions', '64'],
      ['Devices Outdated', '—'],
    ])
  })

  it('never turns “not computable” into a zero', () => {
    // The server sends null until a release reference exists; a 0 here would
    // tell an operator that every installation is current.
    const outdated = overviewTiles(OVERVIEW).find((t) => t.label === 'Devices Outdated')!
    expect(outdated.value).toBe('—')
    expect(outdated.note).toBe('Needs a Chrome release reference')
    expect(outdated.tone).toBeUndefined()
  })

  it('turns the outdated tile amber only when there actually are outdated devices', () => {
    expect(overviewTiles({ ...OVERVIEW, devicesWithUpdatesAvailable: 0 }).at(-1)!.tone).toBeUndefined()
    expect(overviewTiles({ ...OVERVIEW, devicesWithUpdatesAvailable: 2 }).at(-1)!.tone).toBe('warn')
  })
})

describe('chrome status column', () => {
  it('tells “not reported” apart from “not installed”', () => {
    expect(chromeStatusLabel(null)).toBe('Not reported')
    expect(chromeStatusLabel('NotInstalled')).toBe('Not installed')
    expect(chromeStatusLabel('Available')).toBe('Installed')
    expect(chromeStatusLabel('Error')).toBe('Incomplete report')
  })

  it('is green only for an installation the agent actually found', () => {
    expect(chromeStatusTone('Available')).toBe('ok')
    expect(chromeStatusTone('Error')).toBe('warn')
    expect(chromeStatusTone('NotInstalled')).toBe('neutral')
    expect(chromeStatusTone(null)).toBe('neutral')
  })

  it('labels the version with its channel and never invents one', () => {
    expect(versionLabel({ chromeVersion: '131.0.6778.86', channel: 'stable' })).toBe('131.0.6778.86 (stable)')
    expect(versionLabel({ chromeVersion: '131.0.6778.86', channel: null })).toBe('131.0.6778.86')
    expect(versionLabel({ chromeVersion: null, channel: 'stable' })).toBe('—')
  })
})

describe('update status column', () => {
  it('reads Unknown as Unknown, and the future values by name', () => {
    expect(updateStatusLabel('Unknown')).toBe('Unknown')
    expect(updateStatusLabel('UpToDate')).toBe('Up to date')
    expect(updateStatusLabel('UpdateAvailable')).toBe('Update available')
    expect(updateStatusLabel('Something')).toBe('Something')
  })

  it('is neutral for Unknown: no colour claims a state nobody has checked', () => {
    expect(updateStatusTone('Unknown')).toBe('neutral')
    expect(updateStatusTone('UpToDate')).toBe('ok')
    expect(updateStatusTone('UpdateAvailable')).toBe('warn')
  })
})

describe('device search', () => {
  const row = { hostname: 'SALES-PC-07', displayName: 'Front desk' }

  it('matches the hostname or the display name, case-insensitively', () => {
    expect(matchesSearch(row, 'sales')).toBe(true)
    expect(matchesSearch(row, 'FRONT')).toBe(true)
    expect(matchesSearch(row, 'warehouse')).toBe(false)
  })

  it('matches everything on a blank search', () => {
    expect(matchesSearch(row, '')).toBe(true)
    expect(matchesSearch({ hostname: 'PC', displayName: null }, '   ')).toBe(true)
  })
})

describe('profiles', () => {
  it('shows the person’s name for a profile and falls back to Chrome’s directory key', () => {
    expect(profileTitle({ profileName: 'Work', profileKey: 'Profile 3' })).toBe('Work')
    expect(profileTitle({ profileName: '  ', profileKey: 'Profile 3' })).toBe('Profile 3')
    expect(profileTitle({ profileName: null, profileKey: 'Default' })).toBe('Default')
  })

  it('does not repeat the key in the subtitle when it is already the title', () => {
    const base = { userAccount: 'WORKGROUP\\someone', userSid: 'S-1-5-21-1-2-3-1001' }
    expect(profileSubtitle({ ...base, profileName: 'Work', profileKey: 'Profile 3' })).toBe('Profile 3 · WORKGROUP\\someone')
    expect(profileSubtitle({ ...base, profileName: null, profileKey: 'Default' })).toBe('WORKGROUP\\someone')
  })

  it('falls back to the SID when the account name could not be resolved', () => {
    expect(profileSubtitle({ profileName: null, profileKey: 'Default', userAccount: null, userSid: 'S-1-5-21-1-2-3-1001' }))
      .toBe('S-1-5-21-1-2-3-1001')
  })
})

describe('extensions', () => {
  it('names where an extension came from in an administrator’s words', () => {
    expect(installTypeLabel('Internal')).toBe('User installed')
    expect(installTypeLabel('ExternalPolicyDownload')).toBe('Policy')
    expect(installTypeLabel('ExternalPolicy')).toBe('Policy')
    expect(installTypeLabel('Component')).toBe('Chrome built-in')
    expect(installTypeLabel('ExternalComponent')).toBe('Chrome built-in')
    expect(installTypeLabel('Unpacked')).toBe('Unpacked (developer)')
    expect(installTypeLabel('CommandLine')).toBe('Command line')
    expect(installTypeLabel('Whatever')).toBe('Unknown')
  })

  it('treats a disabled extension as a state, not a fault', () => {
    expect(enabledLabel(true)).toBe('Enabled')
    expect(enabledLabel(false)).toBe('Disabled')
    expect(enabledLabel(null)).toBe('Unknown')
    expect(enabledTone(true)).toBe('ok')
    expect(enabledTone(false)).toBe('warn')
    expect(enabledTone(null)).toBe('neutral')
  })

  it('sorts components last, nameless after named, and names case-insensitively', () => {
    const rows = [
      extension({ extensionRowId: 'c', name: 'Web Store', isComponent: true, installType: 'Component', isActive: false }),
      extension({ extensionRowId: 'b', name: 'zeta' }),
      extension({ extensionRowId: 'n', name: null, extensionId: 'ppppoooonnnnmmmmllllkkkkjjjjiiii' }),
      extension({ extensionRowId: 'a', name: 'Alpha' }),
    ]
    expect([...rows].sort(compareExtensions).map((r) => r.extensionRowId)).toEqual(['a', 'b', 'n', 'c'])
  })

  it('orders two same-named extensions by id so the list never shuffles', () => {
    const first = extension({ extensionRowId: '1', extensionId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' })
    const second = extension({ extensionRowId: '2', extensionId: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' })
    expect([second, first].sort(compareExtensions).map((r) => r.extensionRowId)).toEqual(['1', '2'])
  })
})

describe('installation facts', () => {
  it('describes the installation scope without guessing', () => {
    expect(scopeLabel({ installationScope: 'Machine', installedForUser: null })).toBe('Machine-wide')
    expect(scopeLabel({ installationScope: 'User', installedForUser: 'WORKGROUP\\someone' })).toBe('Per-user (WORKGROUP\\someone)')
    expect(scopeLabel({ installationScope: 'User', installedForUser: null })).toBe('Per-user')
    expect(scopeLabel({ installationScope: null, installedForUser: null })).toBe('—')
  })

  it('shows an em-dash for a missing or unreadable time', () => {
    expect(formatDateTime(null)).toBe('—')
    expect(formatDateTime('not a date')).toBe('—')
    expect(formatDateTime('2026-09-25T10:00:00Z')).not.toBe('—')
  })

  it('pluralises counts', () => {
    expect(countLabel(1, 'profile')).toBe('1 profile')
    expect(countLabel(3, 'profile')).toBe('3 profiles')
    expect(countLabel(0, 'extension')).toBe('0 extensions')
  })
})

describe('what counts as an extension the profile has', () => {
  // The first real profile: Chrome's menu showed three, its settings held more.
  const rows = [
    extension({ extensionRowId: 'mail', name: 'Mail Merge' }),
    extension({ extensionRowId: 'phone', name: 'Softphone' }),
    extension({ extensionRowId: 'window', name: 'Window Keeper' }),
    extension({ extensionRowId: 'docs', name: 'Docs Offline', installType: 'ExternalPrefDownload', installedByDefault: true, isActive: false }),
    extension({ extensionRowId: 'vpn', name: 'Free VPN', enabled: false, isActive: false }),
    extension({ extensionRowId: 'pdf', name: 'PDF Tools', installType: 'ExternalPrefDownload', enabled: false, isActive: false }),
    extension({ extensionRowId: 'viewer', name: 'PDF Viewer', installType: 'Component', isComponent: true, isActive: false }),
  ]

  it('shows by default exactly what Chrome’s extensions menu shows', () => {
    expect(visibleExtensions(rows, false).map((r) => r.extensionRowId)).toEqual(['mail', 'phone', 'window'])
  })

  it('shows everything when asked, hiding nothing', () => {
    expect(visibleExtensions(rows, true)).toHaveLength(7)
  })

  it('says how many are not counted, and why', () => {
    expect(hiddenExtensionsNote(rows)).toBe(
      '4 more are not counted: 2 disabled, 1 installed by Chrome by default, 1 part of Chrome itself.',
    )
    expect(hiddenExtensionsNote(rows.slice(0, 3))).toBeNull()
    expect(hiddenExtensionsNote([rows[4]])).toBe('1 more is not counted: 1 disabled.')
  })

  it('calls a default-installed extension what it is, whatever channel it came by', () => {
    expect(extensionSourceLabel({ installType: 'ExternalPrefDownload', installedByDefault: true })).toBe('Chrome default')
    expect(extensionSourceLabel({ installType: 'ExternalPrefDownload', installedByDefault: false })).toBe('Third-party installer')
    expect(extensionSourceLabel({ installType: 'Internal', installedByDefault: null })).toBe('User installed')
  })

  it('orders active extensions before the rest', () => {
    const shuffled = [rows[4], rows[6], rows[0], rows[3], rows[1]]
    expect([...shuffled].sort(compareExtensions).map((r) => r.extensionRowId)).toEqual(['mail', 'phone', 'docs', 'vpn', 'viewer'])
  })
})
