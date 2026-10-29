import { describe, expect, it } from 'vitest'
import * as view from '../../pages/securityUpdatesView'
import * as driverView from '../../pages/driverUpdatesView'
import {
  activeInstallMessage,
  chipBadgeClass,
  detailNeedsFollowUp,
  FOLLOW_UP_FIRST_DELAY_MS,
  FOLLOW_UP_GIVE_UP_MS,
  FOLLOW_UP_MAX_DELAY_MS,
  followUpPolled,
  followUpSignature,
  followUpTimerMs,
  formatKbIds,
  formatUpdateSize,
  HIDE_REASON_MAX_LENGTH,
  hiddenUpdatesLabel,
  hideReasonValid,
  installRebootRequired,
  needsRestartText,
  nonPreviewUpdateIds,
  overviewCounts,
  pendingRebootCount,
  RE_OFFERED_EXPLANATION,
  recentInstallNote,
  reOfferedSelection,
  reOfferedWarning,
  ROLLED_BACK_LABEL,
  settlingSelectionNotes,
  severityBadgeClass,
  showRebootBanner,
  startFollowUp,
  statusChip,
  wuErrorText,
} from '../../pages/securityUpdatesView'
import type {
  SecurityInstallStatus,
  SecurityUpdateDeviceRow,
  SecurityUpdateItem,
  SecurityUpdateOverview,
} from '../../api/client'

/**
 * The Windows Security Updates page's pure rules. The server decides what is
 * eligible, what state each update is in, and whether a reboot is owed — and it
 * queues every follow-up rescan itself. What is guarded here is that the page
 * words the server's verdict faithfully (every known installStatus, and an
 * unknown one passed through rather than guessed), that a Windows Update error
 * code reads exactly as it does on the Driver Updates page, that "auto-select
 * non-preview" never ticks a preview or an update still settling but DOES retry
 * one that rolled back (unlike drivers), that a rolled-back update reads "Rolled
 * back — retry" while drivers keep "Re-offered by Windows", that a hide reason
 * must be non-blank (another deliberate difference from drivers),
 * that the reboot banner survives a reload, that the follow-up poll runs on the
 * drivers' schedule and never for a pending restart, that there is no
 * browser-side auto-rescan any more, that a severity earns the badge tone the
 * page promises, and that unknown KB ids and sizes are shown as "—" rather than
 * fabricated.
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

function deviceRow(partial: Partial<SecurityUpdateDeviceRow> = {}): SecurityUpdateDeviceRow {
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

  it('leaves installing, restart-waiting and verifying updates unticked, keeps every other state', () => {
    const updates = [
      update({ updateId: 'none', installStatus: 'none' }),
      update({ updateId: 'reoffered', installStatus: 'reOffered' }),
      update({ updateId: 'installing', installStatus: 'installing' }),
      update({ updateId: 'failed', installStatus: 'failed' }),
      update({ updateId: 'reboot', installStatus: 'pendingReboot' }),
      update({ updateId: 'verifying', installStatus: 'verifying' }),
      update({ updateId: 'legacy' }), // an older server sends no installStatus
    ]

    expect(nonPreviewUpdateIds(updates)).toEqual(['none', 'reoffered', 'failed', 'legacy'])
  })

  it('ticks a rolled-back (re-offered) security update so auto-select retries it — unlike drivers', () => {
    expect(nonPreviewUpdateIds([update({ updateId: 'cu', installStatus: 'reOffered' })])).toEqual(['cu'])
    // The driver page still leaves a re-offered update unticked: retrying cannot complete it there.
    expect(
      driverView.nonPreviewUpdateIds([
        {
          updateId: 'fw',
          revision: 1,
          title: 'fw',
          driverClass: null,
          driverProvider: null,
          driverVerDate: null,
          isPreview: false,
          sizeBytes: null,
          installStatus: 'reOffered',
        },
      ]),
    ).toEqual([])
  })

  it('does not retry a rolled-back update that is a preview', () => {
    expect(nonPreviewUpdateIds([update({ updateId: 'p', isPreview: true, installStatus: 'reOffered' })])).toEqual([])
  })

  it('never auto-selects an update waiting for a restart or being verified — reinstalling only fails installer-busy', () => {
    expect(nonPreviewUpdateIds([update({ updateId: 'r', installStatus: 'pendingReboot' })])).toEqual([])
    expect(nonPreviewUpdateIds([update({ updateId: 'v', installStatus: 'verifying' })])).toEqual([])
  })

  it('still never ticks a preview, whatever its state', () => {
    expect(nonPreviewUpdateIds([update({ updateId: 'p', isPreview: true, installStatus: 'failed' })])).toEqual([])
  })
})

describe('settlingSelectionNotes', () => {
  const updates = [
    update({ updateId: 'r1', title: 'Cumulative Update', installStatus: 'pendingReboot' }),
    update({ updateId: 'r2', title: '.NET Framework Update', installStatus: 'pendingReboot' }),
    update({ updateId: 'v1', title: 'Servicing Stack Update', installStatus: 'verifying' }),
    update({ updateId: 'n', title: 'Malicious Software Removal Tool', installStatus: 'none' }),
    update({ updateId: 'x', title: 'Recovery Update', installStatus: 'reOffered' }),
  ]

  it('says nothing when no hand-ticked update is settling', () => {
    expect(settlingSelectionNotes(updates, new Set())).toEqual([])
    expect(settlingSelectionNotes(updates, new Set(['n', 'x']))).toEqual([])
    // Unselected settling updates are not mentioned.
    expect(settlingSelectionNotes(updates, new Set(['n']))).toEqual([])
  })

  it('a hand-ticked restart-waiting update says to restart the device instead', () => {
    expect(settlingSelectionNotes(updates, new Set(['r1']))).toEqual([
      "“Cumulative Update” is waiting for a restart — installing again won't help; restart the device instead.",
    ])
  })

  it('a hand-ticked verifying update says to wait for the scan', () => {
    expect(settlingSelectionNotes(updates, new Set(['v1']))).toEqual([
      '“Servicing Stack Update” is being verified — wait for the scan.',
    ])
  })

  it('counts and names several, restart note first, and both notes together', () => {
    expect(settlingSelectionNotes(updates, new Set(['v1', 'r2', 'r1', 'n']))).toEqual([
      "2 of the selected updates are waiting for a restart (“Cumulative Update”, “.NET Framework Update”) — installing again won't help; restart the device instead.",
      '“Servicing Stack Update” is being verified — wait for the scan.',
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

  it('reads the reboot flag from a 1.28.0 result that also carries outcomes and postScan', () => {
    const result = JSON.stringify({
      rebootRequired: true,
      installed: [{ updateId: 'a', title: 'a', resultCode: 2, hresult: 0 }],
      failed: [],
      outcomes: [{ updateId: 'a', title: 'a', resultCode: 2, hresult: 0, rebootRequired: true, status: 'PendingReboot' }],
      verified: true,
      postScan: { scannedAt: '2026-10-29T08:00:00Z', updates: [] },
    })
    expect(installRebootRequired(result)).toBe(true)
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

describe('shared with the Driver Updates page', () => {
  it.each([
    'chipBadgeClass',
    'wuErrorText',
    'startFollowUp',
    'followUpTimerMs',
    'followUpHidden',
    'followUpVisible',
    'followUpPolled',
  ] as const)('%s is the very same function the driver page uses', (name) => {
    // One implementation, so an error code or a polling rule can never read
    // differently between the two pages.
    expect(view[name]).toBe(driverView[name])
  })

  it('the polling constants are the drivers’', () => {
    expect(FOLLOW_UP_FIRST_DELAY_MS).toBe(driverView.FOLLOW_UP_FIRST_DELAY_MS)
    expect(FOLLOW_UP_MAX_DELAY_MS).toBe(driverView.FOLLOW_UP_MAX_DELAY_MS)
    expect(FOLLOW_UP_GIVE_UP_MS).toBe(driverView.FOLLOW_UP_GIVE_UP_MS)
  })
})

describe('statusChip', () => {
  it.each<[SecurityInstallStatus, string, view.ChipTone]>([
    ['none', 'Pending', 'neutral'],
    ['installing', 'Installing…', 'pending'],
    ['verifying', 'Verifying…', 'pending'],
    ['pendingReboot', 'Needs restart to finish', 'warn'],
    ['reOffered', 'Rolled back — retry', 'warn'],
  ])('%s reads "%s" (%s)', (installStatus, label, tone) => {
    expect(statusChip({ installStatus })).toEqual({ label, tone })
  })

  it('failed names the reason from the last HRESULT (signed, as the server stores it), in the bad tone', () => {
    // 0x80240016 as a signed int32.
    expect(statusChip({ installStatus: 'failed', lastHresult: 0x80240016 | 0 })).toEqual({
      label: 'Failed: Another install was in progress or a restart is pending (0x80240016)',
      tone: 'bad',
    })
    expect(chipBadgeClass(statusChip({ installStatus: 'failed' }).tone)).toBe('crit')
  })

  it('an unknown status is passed through; a missing one reads as plain pending', () => {
    expect(statusChip({ installStatus: 'superseded' })).toEqual({ label: 'superseded', tone: 'neutral' })
    expect(statusChip(update({ updateId: 'legacy' }))).toEqual({ label: 'Pending', tone: 'neutral' })
  })

  it('a re-offered security update reads "Rolled back — retry" in warn, whatever its last code', () => {
    expect(ROLLED_BACK_LABEL).toBe('Rolled back — retry')
    expect(statusChip({ installStatus: 'reOffered' })).toEqual({ label: 'Rolled back — retry', tone: 'warn' })
    expect(statusChip({ installStatus: 'reOffered', lastHresult: 0x80070005 })).toEqual({
      label: 'Rolled back — retry',
      tone: 'warn',
    })
    expect(chipBadgeClass(statusChip({ installStatus: 'reOffered' }).tone)).toBe('warn')
  })

  it('drivers still read "Re-offered by Windows": the override is security-only', () => {
    expect(driverView.statusChip({ installStatus: 'reOffered' })).toEqual({ label: 'Re-offered by Windows', tone: 'warn' })
    expect(view.statusChip).not.toBe(driverView.statusChip)
  })

  it.each(['none', 'installing', 'verifying', 'pendingReboot', 'failed', 'superseded', undefined])(
    'every other state (%s) reads exactly as on the driver page',
    (installStatus) => {
      const item = { installStatus, lastHresult: -2145124329 }
      expect(statusChip(item)).toEqual(driverView.statusChip(item))
    },
  )

  it('words an unmapped security-install code as its exact hex', () => {
    expect(wuErrorText(0x800f0922 | 0)).toBe('Windows Update error 0x800F0922')
  })
})

describe('hide reason (required for a security update)', () => {
  it('accepts 1 to 200 characters once trimmed', () => {
    expect(HIDE_REASON_MAX_LENGTH).toBe(200)
    expect(hideReasonValid('x')).toBe(true)
    expect(hideReasonValid('Breaks the line-of-business app; vendor fix pending')).toBe(true)
    expect(hideReasonValid('  padded  ')).toBe(true)
    expect(hideReasonValid('x'.repeat(200))).toBe(true)
    // Surrounding whitespace does not count toward the limit.
    expect(hideReasonValid(`  ${'x'.repeat(200)}  `)).toBe(true)
  })

  it('refuses a blank reason, so Hide stays disabled until one is given', () => {
    expect(hideReasonValid('')).toBe(false)
    expect(hideReasonValid('   ')).toBe(false)
    expect(hideReasonValid('\t\n')).toBe(false)
    expect(hideReasonValid(null)).toBe(false)
    expect(hideReasonValid(undefined)).toBe(false)
  })

  it('refuses one over 200 characters, as the server does', () => {
    expect(hideReasonValid('x'.repeat(201))).toBe(false)
  })
})

describe('page wording', () => {
  it('explains a rolled-back update in plain words: retry after fixing the cause, or hide with a reason', () => {
    expect(RE_OFFERED_EXPLANATION).toContain('Windows offered this update again after the restart')
    expect(RE_OFFERED_EXPLANATION).toContain('rolled back')
    expect(RE_OFFERED_EXPLANATION).toContain('A retry after fixing the cause usually works')
    expect(RE_OFFERED_EXPLANATION).toContain('free disk space')
    expect(RE_OFFERED_EXPLANATION).toContain('pending restarts')
    expect(RE_OFFERED_EXPLANATION).toContain('error code shown')
    expect(RE_OFFERED_EXPLANATION).toContain('hide it here with a reason')
    expect(RE_OFFERED_EXPLANATION).toContain('still leaves the device without that fix')
    // No longer claims a retry is futile.
    expect(RE_OFFERED_EXPLANATION).not.toContain('cannot be fixed by retrying')
    // Not the driver page's firmware wording.
    expect(RE_OFFERED_EXPLANATION).not.toContain('firmware')
  })

  it('a recent install says it was verified, and that Settings may lag', () => {
    expect(recentInstallNote(1)).toBe(
      "Installed — verified no longer offered. The device's Settings page may keep showing it until the machine's next own update check.",
    )
    expect(recentInstallNote(3)).toBe(
      "Installed — verified no longer offered. The device's Settings page may keep showing them until the machine's next own update check.",
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

describe('warn before retrying a rolled-back (re-offered) update', () => {
  const updates = [
    update({ updateId: 'k1', title: 'Update A', installStatus: 'reOffered' }),
    update({ updateId: 'k2', title: 'Update B', installStatus: 'reOffered' }),
    update({ updateId: 'k3', title: 'Update C', installStatus: 'none' }),
  ]

  it('picks only the selected updates that Windows re-offered', () => {
    expect(reOfferedSelection(updates, new Set(['k1', 'k3'])).map((u) => u.updateId)).toEqual(['k1'])
    expect(reOfferedSelection(updates, new Set(['k3']))).toEqual([])
    expect(reOfferedSelection(updates, new Set())).toEqual([])
  })

  it('says nothing when no selected update was re-offered', () => {
    expect(reOfferedWarning([])).toBeNull()
  })

  it('names the one update, says it likely rolled back, and that a retry after fixing the cause usually works', () => {
    expect(reOfferedWarning(reOfferedSelection(updates, new Set(['k1'])))).toBe(
      '“Update A” was offered again by Windows after the restart — it often means the install rolled back. ' +
        'Retrying usually works once the cause is fixed — free disk space, finish any pending restarts, and check ' +
        'the error code shown — otherwise it may roll back again.',
    )
  })

  it('counts and names several', () => {
    expect(reOfferedWarning(reOfferedSelection(updates, new Set(['k1', 'k2', 'k3'])))).toBe(
      '2 of the selected updates were offered again by Windows after the restart (“Update A”, “Update B”) — ' +
        'it often means the install rolled back. Retrying usually works once the cause is fixed — free disk ' +
        'space, finish any pending restarts, and check the error code shown — otherwise they may roll back again.',
    )
  })

  it('is a warning, not a block: auto-select still ticks the update it warns about', () => {
    const picked = new Set(nonPreviewUpdateIds(updates))
    expect(picked.has('k1')).toBe(true)
    expect(reOfferedWarning(reOfferedSelection(updates, picked))).not.toBeNull()
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
        activeInstall: { taskId: 't', status: 'Delivered', updateIds: ['a'], queuedAt: '2026-10-29T08:00:00Z' },
        updates: [],
      }),
    ).toBe(true)
  })

  it.each<SecurityInstallStatus>(['installing', 'verifying'])(
    'follows while an update is %s, so the server-queued rescan lands unclicked',
    (installStatus) => {
      expect(detailNeedsFollowUp({ updates: [update({ updateId: 'a', installStatus })] })).toBe(true)
    },
  )

  it('does not poll for a restart — the usual end state of a security install — the server resolves it on its own', () => {
    expect(
      detailNeedsFollowUp({
        updates: [
          update({ updateId: 'a', installStatus: 'pendingReboot' }),
          update({ updateId: 'b', installStatus: 'pendingReboot' }),
        ],
      }),
    ).toBe(false)
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

describe('follow-up polling schedule (the drivers’, through this page’s module)', () => {
  const T0 = 1_000_000

  it('backs off 10s → 20s → 40s → 60s and gives up after 15 quiet minutes', () => {
    let state = startFollowUp(T0, false)
    let now = T0
    const waits: number[] = []
    for (let i = 0; i < 100 && !state.stopped; i++) {
      const wait = followUpTimerMs(state, now)
      if (wait === null) throw new Error('no timer')
      waits.push(wait)
      now += wait
      state = followUpPolled(state, { changed: false, needsFollowUp: true, now })
    }
    expect(waits.slice(0, 5)).toEqual([10_000, 20_000, 40_000, 60_000, 60_000])
    expect(state.stopped).toBe('gaveUp')
    expect(waits.reduce((a, b) => a + b, 0)).toBe(910_000)
  })

  it('pauses while the page is hidden', () => {
    expect(followUpTimerMs(startFollowUp(T0, true), T0)).toBeNull()
  })
})

describe('followUpSignature', () => {
  const base = {
    lastScanAt: '2026-10-29T08:00:00Z',
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
    expect(followUpSignature({ ...base, lastScanAt: '2026-10-29T08:05:00Z' })).not.toBe(sig)
    expect(followUpSignature({ ...base, rebootPending: true })).not.toBe(sig)
    expect(followUpSignature({ ...base, updates: [{ ...base.updates[0], installStatus: 'pendingReboot' }] })).not.toBe(sig)
    expect(followUpSignature({ ...base, updates: [{ ...base.updates[0], attempts: 2 }] })).not.toBe(sig)
    expect(followUpSignature({ ...base, updates: [{ ...base.updates[0], lastHresult: -2145124329 }] })).not.toBe(sig)
    expect(followUpSignature({ ...base, updates: [] })).not.toBe(sig)
    expect(
      followUpSignature({
        ...base,
        activeInstall: { taskId: 't', status: 'Queued', updateIds: ['a'], queuedAt: '2026-10-29T08:00:00Z' },
      }),
    ).not.toBe(sig)
    expect(
      followUpSignature({
        ...base,
        hidden: [{ updateId: 'b', title: 'b', reason: 'why', hiddenBy: null, hiddenAt: '2026-10-29T08:00:00Z' }],
      }),
    ).not.toBe(sig)
    expect(followUpSignature({ ...base, recentInstalls: [{ updateId: 'b', title: 'b', confirmedAt: 'x' }] })).not.toBe(sig)
  })

  it('ignores the columns that do not move while an install settles (KB, severity, size)', () => {
    const sig = followUpSignature(base)
    expect(
      followUpSignature({
        ...base,
        updates: [{ ...base.updates[0], kbIds: 'KB5031354', severity: 'Critical', sizeBytes: 1024 }],
      }),
    ).toBe(sig)
  })
})

describe('activeInstallMessage', () => {
  it('a delivered install says the device may read offline meanwhile', () => {
    expect(activeInstallMessage({ status: 'Delivered', updateIds: ['a', 'b'] }, true)).toBe(
      'Installing 2 security updates on the device — it may show offline until the install finishes.',
    )
    // Offline is expected while it installs; the wording does not change.
    expect(activeInstallMessage({ status: 'Delivered', updateIds: ['a'] }, false)).toBe(
      'Installing 1 security update on the device — it may show offline until the install finishes.',
    )
  })

  it('a queued install says what it waits for', () => {
    expect(activeInstallMessage({ status: 'Queued', updateIds: ['a'] }, true)).toBe(
      'Install of 1 security update queued — waiting for the device to check in.',
    )
    expect(activeInstallMessage({ status: 'Queued', updateIds: ['a', 'b', 'c'] }, false)).toBe(
      'Install of 3 security updates queued — the device is offline; it runs when the agent reconnects.',
    )
  })

  it('does not print "0 security updates" when the ids are not listed', () => {
    expect(activeInstallMessage({ status: 'Delivered', updateIds: [] }, true)).toBe(
      'Installing security updates on the device — it may show offline until the install finishes.',
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
    const overview: SecurityUpdateOverview = {
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
    const overview: SecurityUpdateOverview = {
      summary: { devicesWithPending: 2, totalPending: 3, devicesRebootPending: 1 },
      devices: rows,
    }
    expect(overviewCounts(overview)).toEqual({ actionable: 3, cannotComplete: 4, needsRestart: 1 })
  })

  it('claims nothing against an older server that sends neither', () => {
    const overview: SecurityUpdateOverview = {
      summary: { devicesWithPending: 1, totalPending: 4, devicesRebootPending: 0 },
      devices: [deviceRow({ pendingCount: 4 })],
    }
    expect(overviewCounts(overview)).toEqual({ actionable: 4, cannotComplete: 0, needsRestart: 0 })
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
