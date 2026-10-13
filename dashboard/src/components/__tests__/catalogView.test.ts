import { describe, expect, it } from 'vitest'
import {
  ICON_MAX_BYTES,
  describeVisibility,
  iconUrl,
  installOutcomeLabel,
  outcomeTone,
  placeholderLetter,
  sortCatalog,
  validateCatalogForm,
  type CatalogFormInput,
} from '../../pages/catalogView'
import type { CatalogAppRow, InstallRequestRow } from '../../api/client'

function app(overrides: Partial<CatalogAppRow> = {}): CatalogAppRow {
  return {
    id: 'a1',
    packageId: 'p1',
    packageName: 'Google Chrome',
    packageVersion: '152.0.1',
    packageWithdrawn: false,
    displayName: 'Chrome',
    description: null,
    category: null,
    hasIcon: false,
    visibility: 'AllDevices',
    groupIds: [],
    isPublished: false,
    createdByDisplay: 'admin',
    publishedByDisplay: null,
    createdAt: '2026-10-01T09:00:00Z',
    updatedAt: '2026-10-02T09:00:00Z',
    ...overrides,
  }
}

function request(overrides: Partial<InstallRequestRow> = {}): InstallRequestRow {
  return {
    id: 'r1',
    deviceId: 'd1',
    hostname: 'PC-001',
    catalogAppId: 'a1',
    appName: 'Chrome',
    requestedBy: 'PC-001\\alice',
    outcome: 'Queued',
    taskId: 't1',
    taskStatus: null,
    requestedAt: '2026-10-03T09:00:00Z',
    ...overrides,
  }
}

const GROUPS = [
  { id: 'g1', name: 'Finance' },
  { id: 'g2', name: 'Front desk' },
]

describe('visibility', () => {
  it('names the whole fleet with nothing to expand', () => {
    expect(describeVisibility(app(), GROUPS)).toEqual({ label: 'All devices', title: null })
  })

  it('counts the groups in the cell and names them in the tooltip', () => {
    const described = describeVisibility(app({ visibility: 'SelectedGroups', groupIds: ['g2', 'g1'] }), GROUPS)

    expect(described.label).toBe('2 groups')
    expect(described.title).toBe('Front desk, Finance')
    expect(describeVisibility(app({ visibility: 'SelectedGroups', groupIds: ['g1'] }), GROUPS).label)
      .toBe('1 group')
  })

  /**
   * A group deleted after the app was scoped to it is still part of the rule
   * the server enforces, so it must stay in the count rather than disappear
   * and make "2 groups" read as "1 group".
   */
  it('keeps an unknown group in the count and says it is unknown', () => {
    const described = describeVisibility(app({ visibility: 'SelectedGroups', groupIds: ['g1', 'gone'] }), GROUPS)

    expect(described.label).toBe('2 groups')
    expect(described.title).toBe('Finance, Unknown group')
  })

  it('says plainly when a group-scoped app is scoped to nobody', () => {
    expect(describeVisibility(app({ visibility: 'SelectedGroups', groupIds: [] }), GROUPS))
      .toEqual({ label: 'No groups', title: null })
  })

  /** Group names are a decoration: without them the count still stands. */
  it('does not need the groups list to count', () => {
    expect(describeVisibility(app({ visibility: 'SelectedGroups', groupIds: ['g1'] }), []).label).toBe('1 group')
  })
})

describe('the icon tile', () => {
  /** A replaced icon must not be served from the cache of the one it replaced. */
  it('versions the icon URL by the last update', () => {
    expect(iconUrl(app({ id: 'a1', updatedAt: '2026-10-02T09:00:00Z' })))
      .toBe('/api/admin/v1/catalog/a1/icon?v=2026-10-02T09%3A00%3A00Z')
  })

  it('encodes the id so it cannot escape its path segment', () => {
    expect(iconUrl(app({ id: 'a/b' }))).toMatch(/^\/api\/admin\/v1\/catalog\/a%2Fb\/icon/)
  })

  it('shows the first letter of the name, upper-cased, as the placeholder', () => {
    expect(placeholderLetter('slack')).toBe('S')
    expect(placeholderLetter('  zoom')).toBe('Z')
    expect(placeholderLetter('')).toBe('?')
  })
})

describe('catalogue order', () => {
  it('sorts by display name regardless of case, then by version', () => {
    const rows = [
      app({ id: '1', displayName: 'zoom', packageVersion: '6.2' }),
      app({ id: '2', displayName: 'Chrome', packageVersion: '152.0' }),
      app({ id: '3', displayName: 'Chrome', packageVersion: '9.0' }),
      app({ id: '4', displayName: 'Slack' }),
    ]

    expect(sortCatalog(rows).map((r) => r.id)).toEqual(['3', '2', '4', '1'])
  })

  it('leaves the server’s array alone', () => {
    const rows = [app({ id: 'b', displayName: 'B' }), app({ id: 'a', displayName: 'A' })]
    sortCatalog(rows)
    expect(rows.map((r) => r.id)).toEqual(['b', 'a'])
  })
})

describe('install outcomes', () => {
  it('spells each outcome out instead of showing the enum', () => {
    expect(installOutcomeLabel(request({ outcome: 'AlreadyInstalled' }))).toBe('Already installed')
    expect(installOutcomeLabel(request({ outcome: 'AlreadyInProgress' }))).toBe('Already in progress')
    expect(installOutcomeLabel(request({ outcome: 'Refused' }))).toBe('Refused')
  })

  /**
   * Queued only says the request was accepted. What the device then did is the
   * task's status, and that is the fact an administrator came to read.
   */
  it('appends the task status to a queued request', () => {
    expect(installOutcomeLabel(request({ outcome: 'Queued', taskStatus: 'Succeeded' }))).toBe('Queued · Succeeded')
    expect(installOutcomeLabel(request({ outcome: 'Queued', taskStatus: 'Failed' }))).toBe('Queued · Failed')
  })

  it('does not say queued twice', () => {
    expect(installOutcomeLabel(request({ outcome: 'Queued', taskStatus: 'Queued' }))).toBe('Queued')
    expect(installOutcomeLabel(request({ outcome: 'Queued', taskStatus: null }))).toBe('Queued')
  })

  it('gives a refusal the failure tone and a duplicate the neutral one', () => {
    expect(outcomeTone(request({ outcome: 'Refused' }))).toBe('crit')
    expect(outcomeTone(request({ outcome: 'AlreadyInstalled' }))).toBe('neutral')
    expect(outcomeTone(request({ outcome: 'AlreadyInProgress' }))).toBe('info')
  })

  /** A queued request whose install failed is a failure, not something pending. */
  it('takes the tone of the task once there is one', () => {
    expect(outcomeTone(request({ outcome: 'Queued', taskStatus: null }))).toBe('info')
    expect(outcomeTone(request({ outcome: 'Queued', taskStatus: 'Delivered' }))).toBe('info')
    expect(outcomeTone(request({ outcome: 'Queued', taskStatus: 'Succeeded' }))).toBe('ok')
    expect(outcomeTone(request({ outcome: 'Queued', taskStatus: 'Failed' }))).toBe('crit')
    expect(outcomeTone(request({ outcome: 'Queued', taskStatus: 'Expired' }))).toBe('warn')
    expect(outcomeTone(request({ outcome: 'Queued', taskStatus: 'Cancelled' }))).toBe('warn')
  })
})

describe('the catalogue form', () => {
  function form(overrides: Partial<CatalogFormInput> = {}): CatalogFormInput {
    return {
      packageId: 'p1',
      displayName: 'Chrome',
      visibility: 'AllDevices',
      groupIds: [],
      icon: null,
      ...overrides,
    }
  }

  it('accepts a complete form', () => {
    expect(validateCatalogForm(form())).toBeNull()
    expect(validateCatalogForm(form({ visibility: 'SelectedGroups', groupIds: ['g1'] }))).toBeNull()
    expect(validateCatalogForm(form({ icon: { name: 'chrome.png', type: 'image/png', size: 4096 } }))).toBeNull()
  })

  it('needs a package and a name', () => {
    expect(validateCatalogForm(form({ packageId: '' }))).toMatch(/package/i)
    expect(validateCatalogForm(form({ displayName: '   ' }))).toMatch(/name/i)
  })

  /** The server rejects this with a 400; saying it here saves the round trip. */
  it('refuses selected groups with no group selected', () => {
    expect(validateCatalogForm(form({ visibility: 'SelectedGroups', groupIds: [] }))).toMatch(/at least one group/i)
  })

  it('ignores stale group picks once the app is visible to everyone', () => {
    expect(validateCatalogForm(form({ visibility: 'AllDevices', groupIds: [] }))).toBeNull()
  })

  it('checks the icon is a PNG within the size limit', () => {
    expect(validateCatalogForm(form({ icon: { name: 'icon.jpg', type: 'image/jpeg', size: 10 } }))).toMatch(/PNG/)
    expect(validateCatalogForm(form({ icon: { name: 'icon.png', type: 'image/png', size: ICON_MAX_BYTES + 1 } })))
      .toMatch(/64 KB/)
    expect(validateCatalogForm(form({ icon: { name: 'icon.png', type: 'image/png', size: ICON_MAX_BYTES } }))).toBeNull()
  })

  /** A picked file with no reported type is judged by its extension, not refused outright. */
  it('falls back to the extension when the browser reports no type', () => {
    expect(validateCatalogForm(form({ icon: { name: 'icon.PNG', type: '', size: 10 } }))).toBeNull()
    expect(validateCatalogForm(form({ icon: { name: 'icon.ico', type: '', size: 10 } }))).toMatch(/PNG/)
  })
})
