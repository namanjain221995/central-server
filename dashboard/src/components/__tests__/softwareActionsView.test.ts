import { describe, expect, it } from 'vitest'
import {
  RUNNING_FILTERS,
  emptySoftwareListMessage,
  matchesRunningFilter,
  reportsRunningState,
  rowActions,
  runningState,
  softwareRowKey,
} from '../../pages/softwareView'
import type { DeviceSoftwareItem } from '../../api/client'

/**
 * The rules behind a software row's Actions menu and the running-state filter:
 * what the last inventory could say about a row, which actions it supports,
 * and how a refusal is worded. All client-side hints — the server decides — so
 * what is guarded here is that the console never offers what is certain to be
 * refused, and never claims what the platform has not determined.
 */

function item(overrides: Partial<DeviceSoftwareItem> = {}): DeviceSoftwareItem {
  return {
    name: 'Google Chrome',
    version: '152.0.1',
    publisher: 'Google LLC',
    installDate: null,
    architecture: 'x86',
    installationScope: 'Machine',
    installedForUser: null,
    productCode: '{A1B2C3D4-0000-4000-8000-000000000001}',
    installLocation: 'C:\\Program Files\\Google\\Chrome\\Application',
    identityKind: 'WindowsInstaller',
    category: 'Application',
    confidence: 'Installed',
    ...overrides,
  }
}

const ANY = { canExecuteTasks: true }

describe('row key', () => {
  /**
   * One machine holds the same application once per user. A busy flag or an
   * open menu keyed by name alone lights up every duplicate when one is acted
   * on, which is exactly the row an operator is trying to tell apart.
   */
  it('separates per-user duplicates of the same application', () => {
    const alice = item({ installationScope: 'User', installedForUser: 'PC-001\\alice' })
    const bob = item({ installationScope: 'User', installedForUser: 'PC-001\\bob' })

    expect(softwareRowKey(alice)).not.toBe(softwareRowKey(bob))
    expect(softwareRowKey(alice)).toBe(softwareRowKey({ ...alice }))
  })

  it('separates two versions of the same application', () => {
    expect(softwareRowKey(item({ version: '1.0' }))).not.toBe(softwareRowKey(item({ version: '2.0' })))
  })
})

describe('running state', () => {
  it('takes the server’s answer when it gives one', () => {
    expect(runningState(item({ isRunning: true }))).toBe('running')
    expect(runningState(item({ isRunning: false }))).toBe('stopped')
  })

  /**
   * Null is the server saying the row has no evidence at all — an agent older
   * than 1.9.0. That is not "stopped", and reading it as stopped would list
   * every application on every un-upgraded device under Not running.
   */
  it('treats a null answer as unknown, not as stopped', () => {
    expect(runningState(item({ isRunning: null }))).toBe('unknown')
    expect(runningState(item({ isRunning: null, evidence: [{ source: 'RunningProcess', name: 'chrome.exe', detail: null }] })))
      .toBe('unknown')
  })

  /** A server that predates the field sends nothing; the evidence it does send is read the same way. */
  it('falls back to the evidence when the server did not say', () => {
    expect(runningState(item({ evidence: [{ source: 'RunningProcess', name: 'chrome.exe', detail: null }] })))
      .toBe('running')
    expect(runningState(item({ evidence: [{ source: 'WindowsInstaller', name: null, detail: null }] })))
      .toBe('stopped')
    expect(runningState(item({ evidence: [] }))).toBe('unknown')
    expect(runningState(item())).toBe('unknown')
  })
})

describe('running filter', () => {
  it('offers All, Running and Not running in that order', () => {
    expect(RUNNING_FILTERS.map((f) => f.key)).toEqual(['all', 'running', 'stopped'])
    expect(RUNNING_FILTERS.map((f) => f.label)).toEqual(['All', 'Running', 'Not running'])
  })

  it('matches running and stopped rows to their own view', () => {
    expect(matchesRunningFilter(item({ isRunning: true }), 'running')).toBe(true)
    expect(matchesRunningFilter(item({ isRunning: true }), 'stopped')).toBe(false)
    expect(matchesRunningFilter(item({ isRunning: false }), 'stopped')).toBe(true)
    expect(matchesRunningFilter(item({ isRunning: false }), 'running')).toBe(false)
  })

  /**
   * "Not running" is a claim. A row the inventory could not judge appears only
   * under All, so the filter never asserts a state the platform did not see.
   */
  it('shows a row of unknown state only under All', () => {
    const unknown = item({ isRunning: null })

    expect(matchesRunningFilter(unknown, 'all')).toBe(true)
    expect(matchesRunningFilter(unknown, 'running')).toBe(false)
    expect(matchesRunningFilter(unknown, 'stopped')).toBe(false)
  })

  it('All takes everything', () => {
    expect(matchesRunningFilter(item({ isRunning: true }), 'all')).toBe(true)
    expect(matchesRunningFilter(item({ isRunning: false }), 'all')).toBe(true)
  })
})

describe('row actions', () => {
  it('lists Force Stop, enabled, for a stoppable row', () => {
    expect(rowActions(item(), ANY)).toEqual([
      { key: 'force-stop', label: 'Force Stop', enabled: true, reason: null },
    ])
  })

  /**
   * Hidden, not disabled, without the permission: the convention across the
   * console, and an operator who cannot act should not be told a row-level
   * reason that is not the real one.
   */
  it('omits an action the operator lacks permission for', () => {
    expect(rowActions(item(), { canExecuteTasks: false })).toEqual([])
  })

  /** Force Stop needs an install path; without one it stays listed, disabled, with the reason it always had. */
  it('disables Force Stop with its reason when there is no install location', () => {
    const [forceStop] = rowActions(item({ installLocation: null }), ANY)

    expect(forceStop.key).toBe('force-stop')
    expect(forceStop.enabled).toBe(false)
    expect(forceStop.reason).toBe('No install location was reported for this application')
  })

  /**
   * Force Stop is decided on the install path and nothing else.
   *
   * This menu briefly also carried an application-remove action, gated on the
   * row's installer identity and on the device's agent version. That feature
   * was withdrawn, and no trace of its gating may survive in this one: a row
   * whose identity would once have blocked Remove is still perfectly
   * stoppable.
   */
  it('ignores installer identity', () => {
    for (const row of [
      item({ identityKind: 'Executable', productCode: null }),
      item({ identityKind: 'Registered', productCode: null }),
      item({ identityKind: 'Package', productCode: null, packageFamilyName: 'A.B_abc123' }),
      item({ name: 'Endpoint Platform Agent' }),
    ]) {
      const [forceStop] = rowActions(row, ANY)

      expect(forceStop.key).toBe('force-stop')
      expect(forceStop.enabled).toBe(true)
      expect(forceStop.reason).toBeNull()
    }
  })
})

describe('whether a device reports running state at all', () => {
  /**
   * Process evidence arrived with agent 1.9.0. Every row an older agent sends
   * is unknown, which is not the same fact as every row being stopped, and the
   * console has to be able to tell the two apart to explain an empty list.
   */
  it('is false when no row on the device was ever judged', () => {
    expect(reportsRunningState([item(), item({ isRunning: null })])).toBe(false)
  })

  it('is true as soon as one row carries an answer', () => {
    expect(reportsRunningState([item({ isRunning: null }), item({ isRunning: false })])).toBe(true)
    expect(reportsRunningState([item({ isRunning: true })])).toBe(true)
  })

  it('is false for a device with no software at all', () => {
    expect(reportsRunningState([])).toBe(false)
  })
})

describe('an emptied software list', () => {
  const emptied = (input: Partial<Parameters<typeof emptySoftwareListMessage>[0]> = {}) =>
    emptySoftwareListMessage({
      filter: 'all',
      searching: false,
      hidingSystem: true,
      reportsRunningState: true,
      ...input,
    })

  /**
   * The defect this exists for. On a device whose agent predates process
   * evidence, every row is unknown and both filtered views exclude all of them,
   * so the table rendered a header with nothing under it — indistinguishable
   * from a page that failed to load, and silent about the one thing that would
   * explain it.
   */
  it('names the inventory’s silence when nothing on the device can be judged', () => {
    const message = emptied({ filter: 'running', reportsRunningState: false })

    expect(message.title).toMatch(/does not report running state/i)
    expect(message.detail).toMatch(/1\.9\.0/)
    expect(message.detail).toMatch(/All/)
  })

  /**
   * That case outranks every other: no change to the search box or the
   * components toggle can make a filtered view match a row the inventory never
   * judged, so pointing at the search would send someone to retype it.
   */
  it('blames the inventory even while a search is also narrowing the list', () => {
    expect(emptied({ filter: 'stopped', searching: true, reportsRunningState: false }).title)
      .toMatch(/does not report running state/i)
  })

  /** A device that does report state has a different, actionable answer. */
  it('blames the filter, not the agent, when other rows were judged', () => {
    const message = emptied({ filter: 'running' })

    expect(message.title).toBe('No applications match this filter')
    expect(message.detail).toMatch(/reported running at its last inventory/)
    expect(message.detail).not.toMatch(/1\.9\.0/)
  })

  /** "Not running" is what the last inventory reported, never a live claim. */
  it('words a not-running filter as something the inventory reported', () => {
    expect(emptied({ filter: 'stopped' }).detail).toMatch(/reported not running at its last inventory/)
  })

  it('points at the search when the search is what emptied the list', () => {
    expect(emptied({ searching: true }).title).toMatch(/search/i)
    expect(emptied({ searching: true, filter: 'running' }).detail).toMatch(/both the search and/i)
  })

  /** The rows exist and are one toggle away, so the toggle is named. */
  it('offers the components toggle when only components were reported', () => {
    expect(emptied({ hidingSystem: true }).detail).toMatch(/Show Windows components/)
  })

  /** Never a bare header: there is always something to say. */
  it('always has a title and a detail', () => {
    for (const filter of ['all', 'running', 'stopped'] as const) {
      for (const searching of [true, false]) {
        for (const hidingSystem of [true, false]) {
          for (const reports of [true, false]) {
            const message = emptySoftwareListMessage({
              filter, searching, hidingSystem, reportsRunningState: reports,
            })

            expect(message.title.length).toBeGreaterThan(0)
            expect(message.detail.length).toBeGreaterThan(0)
          }
        }
      }
    }
  })
})
