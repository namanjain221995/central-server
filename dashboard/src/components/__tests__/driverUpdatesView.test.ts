import { describe, expect, it } from 'vitest'
import * as view from '../../pages/driverUpdatesView'
import {
  activeInstallMessage,
  chipBadgeClass,
  detailNeedsFollowUp,
  FOLLOW_UP_FIRST_DELAY_MS,
  FOLLOW_UP_GIVE_UP_MS,
  FOLLOW_UP_MAX_DELAY_MS,
  followUpHidden,
  followUpPolled,
  followUpSignature,
  followUpTimerMs,
  followUpVisible,
  formatUpdateSize,
  hiddenUpdatesLabel,
  installRebootRequired,
  needsRestartText,
  nonPreviewUpdateIds,
  overviewCounts,
  pendingRebootCount,
  RE_OFFERED_EXPLANATION,
  recentInstallNote,
  reOfferedSelection,
  reOfferedWarning,
  settlingSelectionNotes,
  showRebootBanner,
  startFollowUp,
  statusChip,
  wuErrorText,
} from '../../pages/driverUpdatesView'
import type {
  DriverInstallStatus,
  DriverUpdateDeviceRow,
  DriverUpdateItem,
  DriverUpdateOverview,
} from '../../api/client'

/**
 * The Windows Driver Updates page's pure rules. The server decides what is
 * eligible, what state each update is in, and whether a reboot is owed — and it
 * queues every follow-up rescan itself. What is guarded here is that the page
 * words the server's verdict faithfully (every known installStatus, and an
 * unknown one passed through rather than guessed), that a Windows Update error
 * code reads as words or as its exact hex, that "auto-select non-preview" never
 * ticks a preview or an update retrying cannot complete, that the reboot banner
 * survives a reload, and that there is no browser-side auto-rescan any more.
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

function deviceRow(partial: Partial<DriverUpdateDeviceRow> = {}): DriverUpdateDeviceRow {
  return {
    deviceId: 'd',
    hostname: 'PC-01',
    displayName: null,
    isOnline: true,
    lastSeenAt: null,
    pendingCount: 0,
    previewCount: 0,
    lastScanAt: null,
    rebootPending: false,
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

  it('leaves re-offered, installing, restart-waiting and verifying updates unticked, keeps every other state', () => {
    const updates = [
      update({ updateId: 'none', installStatus: 'none' }),
      update({ updateId: 'reoffered', installStatus: 'reOffered' }),
      update({ updateId: 'installing', installStatus: 'installing' }),
      update({ updateId: 'failed', installStatus: 'failed' }),
      update({ updateId: 'reboot', installStatus: 'pendingReboot' }),
      update({ updateId: 'verifying', installStatus: 'verifying' }),
      update({ updateId: 'legacy' }), // an older server sends no installStatus
    ]

    expect(nonPreviewUpdateIds(updates)).toEqual(['none', 'failed', 'legacy'])
  })

  it('never auto-selects an update waiting for a restart or being verified — reinstalling only fails installer-busy', () => {
    expect(nonPreviewUpdateIds([update({ updateId: 'r', installStatus: 'pendingReboot' })])).toEqual([])
    expect(nonPreviewUpdateIds([update({ updateId: 'v', installStatus: 'verifying' })])).toEqual([])
  })
})

describe('settlingSelectionNotes', () => {
  const updates = [
    update({ updateId: 'r1', title: 'Audio driver', installStatus: 'pendingReboot' }),
    update({ updateId: 'r2', title: 'Chipset', installStatus: 'pendingReboot' }),
    update({ updateId: 'v1', title: 'Camera driver', installStatus: 'verifying' }),
    update({ updateId: 'n', title: 'USB driver', installStatus: 'none' }),
    update({ updateId: 'x', title: 'Firmware', installStatus: 'reOffered' }),
  ]

  it('says nothing when no hand-ticked update is settling', () => {
    expect(settlingSelectionNotes(updates, new Set())).toEqual([])
    expect(settlingSelectionNotes(updates, new Set(['n', 'x']))).toEqual([])
    // Unselected settling updates are not mentioned.
    expect(settlingSelectionNotes(updates, new Set(['n']))).toEqual([])
  })

  it('a hand-ticked restart-waiting update says to restart the device instead', () => {
    expect(settlingSelectionNotes(updates, new Set(['r1']))).toEqual([
      "“Audio driver” is waiting for a restart — installing again won't help; restart the device instead.",
    ])
  })

  it('a hand-ticked verifying update says to wait for the scan', () => {
    expect(settlingSelectionNotes(updates, new Set(['v1']))).toEqual(['“Camera driver” is being verified — wait for the scan.'])
  })

  it('counts and names several, restart note first, and both notes together', () => {
    expect(settlingSelectionNotes(updates, new Set(['v1', 'r2', 'r1', 'n']))).toEqual([
      "2 of the selected updates are waiting for a restart (“Audio driver”, “Chipset”) — installing again won't help; restart the device instead.",
      '“Camera driver” is being verified — wait for the scan.',
    ])
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

describe('no browser-side auto-rescan', () => {
  it('the auto-rescan helper is gone: the server owns every follow-up rescan now', () => {
    // It used to queue one scan from the browser after an install succeeded --
    // lost on reload, navigation or a closed lid. Its return would silently
    // reintroduce a second, racing rescan path.
    expect('shouldAutoRescanAfterInstall' in view).toBe(false)
  })
})

describe('statusChip', () => {
  it.each<[DriverInstallStatus, string, view.ChipTone]>([
    ['none', 'Pending', 'neutral'],
    ['installing', 'Installing…', 'pending'],
    ['verifying', 'Verifying…', 'pending'],
    ['pendingReboot', 'Needs restart to finish', 'warn'],
    ['reOffered', 'Re-offered by Windows', 'warn'],
  ])('%s reads "%s" (%s)', (installStatus, label, tone) => {
    expect(statusChip({ installStatus })).toEqual({ label, tone })
  })

  it('failed names the reason from the last HRESULT, in the bad tone', () => {
    expect(statusChip({ installStatus: 'failed', lastHresult: 0x80070005 })).toEqual({
      label: 'Failed: Access denied (0x80070005)',
      tone: 'bad',
    })
    expect(statusChip({ installStatus: 'failed', lastHresult: 0x80249999 })).toEqual({
      label: 'Failed: Windows Update error 0x80249999',
      tone: 'bad',
    })
  })

  it('failed without a code says so rather than inventing one', () => {
    expect(statusChip({ installStatus: 'failed', lastHresult: null })).toEqual({
      label: 'Failed: Windows Update gave no error code',
      tone: 'bad',
    })
    expect(statusChip({ installStatus: 'failed' }).tone).toBe('bad')
  })

  it('an unknown status is passed through as the raw word in neutral — the server is the source of truth', () => {
    expect(statusChip({ installStatus: 'superseded' })).toEqual({ label: 'superseded', tone: 'neutral' })
    expect(statusChip({ installStatus: 'installed' })).toEqual({ label: 'installed', tone: 'neutral' })
  })

  it('a missing status (an older server) reads as plain pending', () => {
    expect(statusChip({})).toEqual({ label: 'Pending', tone: 'neutral' })
    expect(statusChip({ installStatus: null })).toEqual({ label: 'Pending', tone: 'neutral' })
    expect(statusChip({ installStatus: '' })).toEqual({ label: 'Pending', tone: 'neutral' })
    expect(statusChip(update({ updateId: 'legacy' }))).toEqual({ label: 'Pending', tone: 'neutral' })
  })

  it('ignores a stale HRESULT on any non-failed state', () => {
    expect(statusChip({ installStatus: 'pendingReboot', lastHresult: 0x80070005 }).label).toBe(
      'Needs restart to finish',
    )
  })
})

describe('chipBadgeClass', () => {
  it('maps each tone onto an existing badge class', () => {
    expect(chipBadgeClass('neutral')).toBe('neutral')
    expect(chipBadgeClass('pending')).toBe('info')
    expect(chipBadgeClass('ok')).toBe('ok')
    expect(chipBadgeClass('warn')).toBe('warn')
    expect(chipBadgeClass('bad')).toBe('crit')
  })
})

describe('wuErrorText', () => {
  it.each([
    [0x80240017, 'Not applicable to this device (0x80240017)'],
    [0x8024001e, 'The Windows Update service stopped or the device was shutting down (0x8024001E)'],
    [0x8024001f, 'No network connection to Windows Update (0x8024001F)'],
    [0x80240016, 'Another install was in progress or a restart is pending (0x80240016)'],
    [0x80240009, 'Another Windows Update operation was in progress (0x80240009)'],
    [0x80242016, 'Unexpected state after the restart step; restart the device and scan again (0x80242016)'],
    [0x80070005, 'Access denied (0x80070005)'],
  ])('maps HRESULT %d to "%s"', (hresult, expected) => {
    expect(wuErrorText(hresult)).toBe(expected)
  })

  it('maps the signed 32-bit spelling the server stores (.NET int) the same as the unsigned one', () => {
    // 0x80240017 as a signed int32 is -2145124329.
    expect(wuErrorText(-2145124329)).toBe('Not applicable to this device (0x80240017)')
    expect(wuErrorText(0x80070005 | 0)).toBe('Access denied (0x80070005)')
  })

  it('falls back to "Windows Update error 0x%08X" for any other code, negative or positive', () => {
    expect(wuErrorText(0x80249999)).toBe('Windows Update error 0x80249999')
    expect(wuErrorText(0x80249999 | 0)).toBe('Windows Update error 0x80249999')
    expect(wuErrorText(-1)).toBe('Windows Update error 0xFFFFFFFF')
    expect(wuErrorText(-0x80000000)).toBe('Windows Update error 0x80000000')
    expect(wuErrorText(0xffffffff)).toBe('Windows Update error 0xFFFFFFFF')
    // Zero-padded to eight digits.
    expect(wuErrorText(5)).toBe('Windows Update error 0x00000005')
    expect(wuErrorText(0x1f)).toBe('Windows Update error 0x0000001F')
  })

  it('never wraps a value outside 32 bits onto a different (possibly mapped) code', () => {
    // 0x1_80240017 would wrap to 0x80240017 ("Not applicable") under >>> 0.
    expect(wuErrorText(0x180240017)).toBe('Windows Update error 0x180240017')
    expect(wuErrorText(-0x80000001)).toBe('Windows Update error -2147483649')
  })

  it('says there was no code for null, undefined or zero', () => {
    expect(wuErrorText(null)).toBe('Windows Update gave no error code')
    expect(wuErrorText(undefined)).toBe('Windows Update gave no error code')
    expect(wuErrorText(0)).toBe('Windows Update gave no error code')
  })

  it('does not throw on a non-integer', () => {
    expect(wuErrorText(1.5)).toBe('Windows Update error 1.5')
    expect(wuErrorText(Number.NaN)).toBe('Windows Update error NaN')
  })
})

describe('page wording', () => {
  it('explains a re-offered update in plain words, with both ways out', () => {
    expect(RE_OFFERED_EXPLANATION).toBe(
      "Windows Update accepted this install and then offered the update again. Some vendor firmware/driver " +
        "packages cannot be completed by the platform's update channel (Windows reports success without a " +
        'durable install). It cannot be fixed by retrying. Options: install it on the laptop with the ' +
        "vendor's tool (e.g. the manufacturer's update app), or hide it here.",
    )
  })

  it('a recent install says it was verified, and that Settings may lag', () => {
    expect(recentInstallNote(1)).toBe(
      "Installed — verified no longer offered. The laptop's Settings page may keep showing it until the machine's next own update check.",
    )
    expect(recentInstallNote(3)).toBe(
      "Installed — verified no longer offered. The laptop's Settings page may keep showing them until the machine's next own update check.",
    )
  })

  it('counts the hidden updates in their section heading', () => {
    expect(hiddenUpdatesLabel([{}, {}])).toBe('Hidden updates (2)')
    expect(hiddenUpdatesLabel([])).toBe('Hidden updates (0)')
    expect(hiddenUpdatesLabel(undefined)).toBe('Hidden updates (0)')
  })

  it('words the restart count in the singular and plural', () => {
    expect(needsRestartText(1)).toBe('1 update needs a restart to finish')
    expect(needsRestartText(3)).toBe('3 updates need a restart to finish')
  })
})

describe('warn before installing a re-offered update', () => {
  const updates = [
    update({ updateId: 'fw1', title: 'Firmware A', installStatus: 'reOffered' }),
    update({ updateId: 'fw2', title: 'Firmware B', installStatus: 'reOffered' }),
    update({ updateId: 'usb', title: 'USB driver', installStatus: 'none' }),
  ]

  it('picks only the selected updates that Windows re-offered', () => {
    expect(reOfferedSelection(updates, new Set(['fw1', 'usb'])).map((u) => u.updateId)).toEqual(['fw1'])
    expect(reOfferedSelection(updates, new Set(['usb']))).toEqual([])
    expect(reOfferedSelection(updates, new Set())).toEqual([])
  })

  it('says nothing when no selected update was re-offered', () => {
    expect(reOfferedWarning([])).toBeNull()
  })

  it('names the one update, and says retrying will not complete it', () => {
    expect(reOfferedWarning(reOfferedSelection(updates, new Set(['fw1'])))).toBe(
      '“Firmware A” was re-offered by Windows after an earlier install. Installing again will not complete it — ' +
        'Windows reports success without a durable install. Consider hiding it instead.',
    )
  })

  it('counts and names several', () => {
    expect(reOfferedWarning(reOfferedSelection(updates, new Set(['fw1', 'fw2', 'usb'])))).toBe(
      '2 of the selected updates were re-offered by Windows after an earlier install (“Firmware A”, “Firmware B”). ' +
        'Installing again will not complete them — Windows reports success without a durable install. ' +
        'Consider hiding them instead.',
    )
  })
})

describe('showRebootBanner', () => {
  const quiet = { rebootPending: false, updates: [update({ updateId: 'a' })] }

  it('shows on the durable server flag, so a reload keeps it', () => {
    expect(showRebootBanner({ ...quiet, rebootPending: true }, deviceRow(), false)).toBe(true)
  })

  it('shows when any update needs a restart to finish, even if the flag lags', () => {
    const details = { rebootPending: false, updates: [update({ updateId: 'a', installStatus: 'pendingReboot' })] }
    expect(showRebootBanner(details, deviceRow(), false)).toBe(true)
  })

  it('shows on the install task result before the detail catches up', () => {
    expect(showRebootBanner(quiet, deviceRow(), true)).toBe(true)
  })

  it('falls back to the overview row until the detail has loaded', () => {
    expect(showRebootBanner(null, deviceRow({ rebootPending: true }), false)).toBe(true)
    expect(showRebootBanner(null, deviceRow({ rebootPending: false }), false)).toBe(false)
  })

  it('stays hidden when the loaded detail says no restart is owed (it wins over the overview row)', () => {
    expect(showRebootBanner(quiet, deviceRow({ rebootPending: false }), false)).toBe(false)
    expect(showRebootBanner(quiet, deviceRow({ rebootPending: true }), false)).toBe(false)
  })

  it('counts the updates waiting for a restart', () => {
    expect(
      pendingRebootCount([
        update({ updateId: 'a', installStatus: 'pendingReboot' }),
        update({ updateId: 'b', installStatus: 'reOffered' }),
        update({ updateId: 'c', installStatus: 'pendingReboot' }),
      ]),
    ).toBe(2)
  })
})

describe('detailNeedsFollowUp', () => {
  it('is quiet before the detail loads and when nothing is settling', () => {
    expect(detailNeedsFollowUp(null)).toBe(false)
    expect(detailNeedsFollowUp({ activeInstall: null, updates: [] })).toBe(false)
    expect(
      detailNeedsFollowUp({
        updates: [
          update({ updateId: 'a', installStatus: 'none' }),
          update({ updateId: 'b', installStatus: 'reOffered' }),
          update({ updateId: 'c', installStatus: 'failed' }),
          update({ updateId: 'd' }),
        ],
      }),
    ).toBe(false)
  })

  it('follows an active install even before any chip says so', () => {
    expect(
      detailNeedsFollowUp({
        activeInstall: { taskId: 't', status: 'Queued', updateIds: ['a'], queuedAt: '2026-10-27T08:00:00Z' },
        updates: [],
      }),
    ).toBe(true)
  })

  it.each<DriverInstallStatus>(['installing', 'verifying'])(
    'follows while an update is %s, so the server-queued rescan lands unclicked',
    (installStatus) => {
      expect(detailNeedsFollowUp({ updates: [update({ updateId: 'a', installStatus })] })).toBe(true)
    },
  )

  it('does not poll for a restart: it can be hours away, and the server resolves it on its own', () => {
    expect(detailNeedsFollowUp({ updates: [update({ updateId: 'a', installStatus: 'pendingReboot' })] })).toBe(false)
    expect(
      detailNeedsFollowUp({
        updates: [
          update({ updateId: 'a', installStatus: 'pendingReboot' }),
          update({ updateId: 'b', installStatus: 'verifying' }),
        ],
      }),
    ).toBe(true)
  })
})

describe('follow-up polling schedule', () => {
  const T0 = 1_000_000
  const unchanged = (now: number) => ({ changed: false, needsFollowUp: true, now })

  /** Polls `n` times, each exactly when the timer says, with no change; returns the state and the waits used. */
  function pollQuietly(n: number, start = startFollowUp(T0, false), from = T0) {
    let state = start
    let now = from
    const waits: number[] = []
    for (let i = 0; i < n && !state.stopped; i++) {
      const wait = followUpTimerMs(state, now)
      if (wait === null) throw new Error('no timer')
      waits.push(wait)
      now += wait
      state = followUpPolled(state, unchanged(now))
    }
    return { state, now, waits }
  }

  it('starts with a 10s wait', () => {
    expect(followUpTimerMs(startFollowUp(T0, false), T0)).toBe(FOLLOW_UP_FIRST_DELAY_MS)
    expect(FOLLOW_UP_FIRST_DELAY_MS).toBe(10_000)
  })

  it('backs off 10s → 20s → 40s → 60s and stays capped at 60s while nothing changes', () => {
    expect(pollQuietly(6).waits).toEqual([10_000, 20_000, 40_000, 60_000, 60_000, 60_000])
    expect(FOLLOW_UP_MAX_DELAY_MS).toBe(60_000)
  })

  it('drops back to 10s and resets the quiet clock as soon as a poll sees a change', () => {
    const { state, now } = pollQuietly(4)
    const after = followUpPolled(state, { changed: true, needsFollowUp: true, now: now + 60_000 })
    expect(after.stopped).toBeNull()
    expect(after.quietMs).toBe(0)
    expect(followUpTimerMs(after, now + 60_000)).toBe(10_000)
  })

  it('gives up after 15 minutes of no change, and a stopped schedule sets no timer', () => {
    expect(FOLLOW_UP_GIVE_UP_MS).toBe(15 * 60_000)
    const { state, now, waits } = pollQuietly(100)
    expect(state.stopped).toBe('gaveUp')
    // 10 + 20 + 40 + 60×14 = 910s: the first poll at or past 15 minutes of quiet.
    expect(waits.reduce((a, b) => a + b, 0)).toBe(910_000)
    expect(now - T0).toBeGreaterThanOrEqual(FOLLOW_UP_GIVE_UP_MS)
    expect(followUpTimerMs(state, now)).toBeNull()
  })

  it('does not give up one poll early', () => {
    // 10 + 20 + 40 + 60×13 = 850s of quiet: still under 15 minutes.
    const { state } = pollQuietly(16)
    expect(state.stopped).toBeNull()
    expect(state.quietMs).toBe(850_000)
  })

  it('stops as settled when the detail no longer needs following, even mid-backoff', () => {
    const { state, now } = pollQuietly(3)
    const after = followUpPolled(state, { changed: true, needsFollowUp: false, now: now + 60_000 })
    expect(after.stopped).toBe('settled')
    expect(followUpTimerMs(after, now + 60_000)).toBeNull()
  })

  it('a stopped schedule ignores later polls and visibility changes', () => {
    const settled = followUpPolled(startFollowUp(T0, false), { changed: false, needsFollowUp: false, now: T0 + 10_000 })
    expect(followUpPolled(settled, { changed: true, needsFollowUp: true, now: T0 + 20_000 })).toBe(settled)
    expect(followUpHidden(settled, T0 + 20_000)).toBe(settled)
    expect(followUpVisible(settled, T0 + 20_000)).toBe(settled)
  })

  it('pauses while the page is hidden: no timer at all', () => {
    expect(followUpTimerMs(startFollowUp(T0, true), T0)).toBeNull()
    const hidden = followUpHidden(startFollowUp(T0, false), T0 + 4_000)
    expect(followUpTimerMs(hidden, T0 + 60 * 60_000)).toBeNull()
  })

  it('resumes where it paused: hidden time counts neither toward the next poll nor toward giving up', () => {
    // 4s visible, then an hour hidden, then visible again.
    let state = followUpHidden(startFollowUp(T0, false), T0 + 4_000)
    const back = T0 + 4_000 + 60 * 60_000
    state = followUpVisible(state, back)
    expect(followUpTimerMs(state, back)).toBe(6_000)

    // Read straight away on return: only the 4s visible wait is counted as quiet.
    const polled = followUpPolled(state, unchanged(back))
    expect(polled.stopped).toBeNull()
    expect(polled.quietMs).toBe(4_000)
    expect(followUpTimerMs(polled, back)).toBe(20_000)
  })

  it('a schedule started on a hidden page waits for it to become visible', () => {
    let state = startFollowUp(T0, true)
    state = followUpVisible(state, T0 + 30 * 60_000)
    expect(followUpTimerMs(state, T0 + 30 * 60_000)).toBe(10_000)
  })

  it('a long-hidden quiet tab never gives up on its own while hidden', () => {
    const { state, now } = pollQuietly(10)
    const hidden = followUpHidden(state, now)
    const back = followUpVisible(hidden, now + 24 * 60 * 60_000)
    expect(back.stopped).toBeNull()
    expect(back.quietMs).toBe(state.quietMs)
  })
})

describe('followUpSignature', () => {
  const base = {
    lastScanAt: '2026-10-27T08:00:00Z',
    rebootPending: false,
    updates: [update({ updateId: 'a', installStatus: 'verifying', attempts: 1 })],
    activeInstall: null,
    hidden: [],
    recentInstalls: [],
  }

  it('is stable for the same detail', () => {
    expect(followUpSignature(base)).toBe(followUpSignature({ ...base, updates: [...base.updates] }))
    expect(followUpSignature(null)).toBe('')
  })

  it('changes when anything that moves while an install settles moves', () => {
    const sig = followUpSignature(base)
    expect(followUpSignature({ ...base, lastScanAt: '2026-10-27T08:05:00Z' })).not.toBe(sig)
    expect(followUpSignature({ ...base, rebootPending: true })).not.toBe(sig)
    expect(followUpSignature({ ...base, updates: [{ ...base.updates[0], installStatus: 'failed' }] })).not.toBe(sig)
    expect(followUpSignature({ ...base, updates: [{ ...base.updates[0], attempts: 2 }] })).not.toBe(sig)
    expect(followUpSignature({ ...base, updates: [] })).not.toBe(sig)
    expect(
      followUpSignature({
        ...base,
        activeInstall: { taskId: 't', status: 'Queued', updateIds: ['a'], queuedAt: '2026-10-27T08:00:00Z' },
      }),
    ).not.toBe(sig)
    expect(followUpSignature({ ...base, recentInstalls: [{ updateId: 'b', title: 'b', confirmedAt: 'x' }] })).not.toBe(sig)
  })
})

describe('activeInstallMessage', () => {
  it('a delivered install says the device may read offline meanwhile', () => {
    expect(activeInstallMessage({ status: 'Delivered', updateIds: ['a', 'b'] }, true)).toBe(
      'Installing 2 driver updates on the device — it may show offline until the install finishes.',
    )
    // Offline is expected while it installs; the wording does not change.
    expect(activeInstallMessage({ status: 'Delivered', updateIds: ['a'] }, false)).toBe(
      'Installing 1 driver update on the device — it may show offline until the install finishes.',
    )
  })

  it('a queued install says what it waits for', () => {
    expect(activeInstallMessage({ status: 'Queued', updateIds: ['a'] }, true)).toBe(
      'Install of 1 driver update queued — waiting for the device to check in.',
    )
    expect(activeInstallMessage({ status: 'Queued', updateIds: ['a', 'b', 'c'] }, false)).toBe(
      'Install of 3 driver updates queued — the device is offline; it runs when the agent reconnects.',
    )
  })

  it('does not print "0 driver updates" when the ids are not listed', () => {
    expect(activeInstallMessage({ status: 'Delivered', updateIds: [] }, true)).toBe(
      'Installing driver updates on the device — it may show offline until the install finishes.',
    )
  })
})

describe('overviewCounts', () => {
  const rows = [
    deviceRow({ deviceId: 'a', pendingCount: 2, cannotCompleteCount: 3, needsRestartCount: 1 }),
    deviceRow({ deviceId: 'b', pendingCount: 1, cannotCompleteCount: 1, needsRestartCount: 0 }),
  ]

  it('is null before the overview loads', () => {
    expect(overviewCounts(null)).toBeNull()
  })

  it('prefers the summary’s own counts; actionable is the server’s pending count', () => {
    const overview: DriverUpdateOverview = {
      summary: {
        devicesWithPending: 2,
        totalPending: 3,
        devicesRebootPending: 1,
        cannotCompleteCount: 7,
        needsRestartCount: 5,
      },
      devices: rows,
    }
    expect(overviewCounts(overview)).toEqual({ actionable: 3, cannotComplete: 7, needsRestart: 5 })
  })

  it('sums the rows when the summary lacks the new counts', () => {
    const overview: DriverUpdateOverview = {
      summary: { devicesWithPending: 2, totalPending: 3, devicesRebootPending: 1 },
      devices: rows,
    }
    expect(overviewCounts(overview)).toEqual({ actionable: 3, cannotComplete: 4, needsRestart: 1 })
  })

  it('claims nothing against an older server that sends neither', () => {
    const overview: DriverUpdateOverview = {
      summary: { devicesWithPending: 1, totalPending: 4, devicesRebootPending: 0 },
      devices: [deviceRow({ pendingCount: 4 })],
    }
    expect(overviewCounts(overview)).toEqual({ actionable: 4, cannotComplete: 0, needsRestart: 0 })
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
