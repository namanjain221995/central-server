import { describe, expect, it } from 'vitest'
import {
  AgentFloorNote,
  canRemove,
  canRemoveEverywhere,
  dormantDays,
  dormantHint,
  removalTallyBadges,
  removeEverywhereSummary,
  removeProfileLabel,
  removeRestartLabel,
  removeSuggested,
  removeTitle,
  RestartFinishesNote,
  ScheduledRemovalNote,
  statusBadge,
  tallyLabel,
} from '../../pages/chromeProfilesView'

/** A fixed clock so day arithmetic is exact, whatever machine runs the suite. */
const NOW = new Date('2026-10-24T12:00:00Z')

describe('statusBadge', () => {
  it('maps all four server statuses', () => {
    expect(statusBadge('active', '2026-10-23T09:00:00Z', NOW)).toEqual({ label: 'Active', tone: 'ok', hint: null })

    const broken = statusBadge('signInBroken', '2026-10-23T09:00:00Z', NOW)
    expect(broken.label).toBe('Sign-in broken')
    expect(broken.tone).toBe('warn')
    expect(broken.hint).toBe('Account attached but sign-in is no longer working.')

    const dormant = statusBadge('dormant', '2026-10-10T12:00:00Z', NOW)
    expect(dormant.label).toBe('Dormant')
    expect(dormant.tone).toBe('neutral')
    expect(dormant.hint).toBe('unused 14 days')

    expect(statusBadge('notSignedIn', null, NOW)).toEqual({ label: 'Not signed in', tone: 'neutral', hint: null })
  })

  it('says "never used" under Dormant when the profile has no last-active date', () => {
    expect(statusBadge('dormant', null, NOW).hint).toBe('never used')
  })

  it('renders a status this build does not know as-is — the server is the source of truth', () => {
    expect(statusBadge('archived', null, NOW)).toEqual({ label: 'archived', tone: 'neutral', hint: null })
  })

  it('never derives a status itself: a stale lastActiveAt does not turn Active into Dormant', () => {
    // The server said active; the console repeats it even though the timestamp
    // alone would read as months of disuse.
    const badge = statusBadge('active', '2025-01-01T00:00:00Z', NOW)
    expect(badge.label).toBe('Active')
    expect(badge.hint).toBeNull()
  })
})

describe('dormantDays', () => {
  it('is null when the profile was never used', () => {
    expect(dormantDays(null, NOW)).toBeNull()
  })

  it('is null when the timestamp cannot be read, rather than inventing a number', () => {
    expect(dormantDays('not-a-date', NOW)).toBeNull()
  })

  it('counts whole days since last use', () => {
    expect(dormantDays('2026-10-10T12:00:00Z', NOW)).toBe(14)
    expect(dormantDays('2026-10-23T12:00:00Z', NOW)).toBe(1)
    // A partial day is not yet a day.
    expect(dormantDays('2026-10-23T13:00:00Z', NOW)).toBe(0)
  })

  it('clamps a device clock slightly ahead to 0, never a negative day count', () => {
    expect(dormantDays('2026-10-24T13:00:00Z', NOW)).toBe(0)
  })
})

describe('dormantHint', () => {
  it('reads "never used" for a profile with no last-active date', () => {
    expect(dormantHint(null, NOW)).toBe('never used')
  })

  it('pluralizes the unused-days wording', () => {
    expect(dormantHint('2026-10-23T12:00:00Z', NOW)).toBe('unused 1 day')
    expect(dormantHint('2026-10-10T12:00:00Z', NOW)).toBe('unused 14 days')
  })
})

describe('canRemove', () => {
  it('refuses the built-in Default profile', () => {
    expect(canRemove('Default')).toBe(false)
  })

  it('refuses case and whitespace variants too — the key names a directory on a case-insensitive disk', () => {
    expect(canRemove('default')).toBe(false)
    expect(canRemove('DEFAULT')).toBe(false)
    expect(canRemove(' Default ')).toBe(false)
  })

  it('allows every other profile', () => {
    expect(canRemove('Profile 1')).toBe(true)
    expect(canRemove('Profile 12')).toBe(true)
    // A profile the person happened to NAME "Default" is keyed "Profile N";
    // only the key decides.
    expect(canRemove('Profile 2')).toBe(true)
  })
})

describe('removeTitle', () => {
  it('explains why Default cannot be removed', () => {
    expect(removeTitle('Default')).toMatch(/built-in Default profile cannot be removed/)
  })

  it('says what the button does otherwise', () => {
    expect(removeTitle('Profile 1')).toBe('Removes this profile from Chrome on the device')
  })
})

describe('removeSuggested', () => {
  it('suggests Remove only on dormant profiles — the cleanup target', () => {
    expect(removeSuggested('dormant')).toBe(true)
    expect(removeSuggested('active')).toBe(false)
    expect(removeSuggested('signInBroken')).toBe(false)
    expect(removeSuggested('notSignedIn')).toBe(false)
  })
})

describe('removeProfileLabel', () => {
  it('names the task after what the operator knows the profile as', () => {
    expect(removeProfileLabel({ profileName: 'Work', profileKey: 'Profile 3' })).toBe('Remove Chrome profile "Work"')
    expect(removeProfileLabel({ profileName: null, profileKey: 'Profile 3' })).toBe('Remove Chrome profile "Profile 3"')
  })
})

describe('removeRestartLabel', () => {
  it('names the restart after the removal it finishes', () => {
    expect(removeRestartLabel({ profileName: 'Work', profileKey: 'Profile 3' }))
      .toBe('Restart device to finish removing "Work"')
    expect(removeRestartLabel({ profileName: null, profileKey: 'Profile 3' }))
      .toBe('Restart device to finish removing "Profile 3"')
  })
})

describe('canRemoveEverywhere', () => {
  it('offers the fleet action only where a Google account is signed in', () => {
    expect(canRemoveEverywhere({ profileKey: 'Profile 2', accountEmail: 'person@example.com' })).toBe(true)
    expect(canRemoveEverywhere({ profileKey: 'Profile 2', accountEmail: null })).toBe(false)
  })

  it('treats a whitespace email like no email — it cannot address an account', () => {
    expect(canRemoveEverywhere({ profileKey: 'Profile 2', accountEmail: '   ' })).toBe(false)
  })

  it('never offers it on the built-in Default profile, signed in or not', () => {
    expect(canRemoveEverywhere({ profileKey: 'Default', accountEmail: 'person@example.com' })).toBe(false)
    expect(canRemoveEverywhere({ profileKey: ' default ', accountEmail: 'person@example.com' })).toBe(false)
  })
})

describe('scheduled-removal copy', () => {
  it('explains completion on Chrome closing or the next startup, never a retry', () => {
    expect(ScheduledRemovalNote).toContain('the moment Chrome closes, or at the next startup')
    expect(ScheduledRemovalNote).toContain('does not fail')
    expect(ScheduledRemovalNote).not.toMatch(/try again/i)
    expect(RestartFinishesNote).toContain('when Chrome closes or at the next startup')
  })

  it('names the agent floor', () => {
    expect(AgentFloorNote).toContain('1.26.0')
  })
})

describe('tallyLabel', () => {
  it('turns a camelCased tally key into words', () => {
    expect(tallyLabel('alreadyQueued')).toBe('already queued')
    expect(tallyLabel('rejectedAgentTooOld')).toBe('rejected agent too old')
    expect(tallyLabel('skipped_no_profile')).toBe('skipped no profile')
    expect(tallyLabel('offline')).toBe('offline')
  })
})

describe('removalTallyBadges', () => {
  it('orders the known tallies and drops the zeros', () => {
    const badges = removalTallyBadges({
      targetedDevices: 9,
      queuedRemovals: 5,
      queuedRestarts: 0,
      skippedNoProfile: 2,
      skippedDefault: 0,
      rejectedAgentTooOld: 1,
      offline: 3,
    })

    expect(badges).toEqual([
      { label: 'removals queued', value: 5, tone: 'ok' },
      { label: 'offline — act on reconnect', value: 3, tone: 'info' },
      { label: 'skipped — no profile for this account', value: 2, tone: 'neutral' },
      { label: 'refused — agent too old', value: 1, tone: 'crit' },
    ])
  })

  it('treats a missing key as not reported, never as a zero to render', () => {
    expect(removalTallyBadges({ queuedRemovals: 2 })).toEqual([
      { label: 'removals queued', value: 2, tone: 'ok' },
    ])
  })

  it('renders a tally this build does not know, named from its key, after the known ones', () => {
    const badges = removalTallyBadges({ queuedRemovals: 2, alreadyQueued: 4 })

    expect(badges).toEqual([
      { label: 'removals queued', value: 2, tone: 'ok' },
      { label: 'already queued', value: 4, tone: 'neutral' },
    ])
  })

  it('keeps targetedDevices out — the summary sentence already carries it', () => {
    expect(removalTallyBadges({ targetedDevices: 12 })).toEqual([])
  })

  it('shows queued restarts beside queued removals', () => {
    expect(removalTallyBadges({ queuedRemovals: 3, queuedRestarts: 3 })).toEqual([
      { label: 'removals queued', value: 3, tone: 'ok' },
      { label: 'restarts queued', value: 3, tone: 'info' },
    ])
  })
})

describe('removeEverywhereSummary', () => {
  it('reads the full outcome back in one sentence, part by part', () => {
    expect(
      removeEverywhereSummary({
        targetedDevices: 9,
        queuedRemovals: 5,
        queuedRestarts: 5,
        offline: 2,
        skippedNoProfile: 2,
        skippedDefault: 1,
        rejectedAgentTooOld: 1,
      }),
    ).toBe(
      'Queued the removal on 5 devices of 9 targeted · 5 restarts queued to finish immediately · '
      + '2 offline — they act on reconnect · 2 skipped — no profile for this account · '
      + '1 skipped — Default profile · 1 refused — agent too old.',
    )
  })

  it('does not restate the target when every targeted device got the removal', () => {
    expect(removeEverywhereSummary({ targetedDevices: 3, queuedRemovals: 3 })).toBe(
      'Queued the removal on 3 devices.',
    )
    expect(removeEverywhereSummary({ targetedDevices: 1, queuedRemovals: 1 })).toBe(
      'Queued the removal on 1 device.',
    )
  })

  it('says why nothing happened when every device lacked a profile for the account', () => {
    expect(removeEverywhereSummary({ targetedDevices: 4, queuedRemovals: 0, skippedNoProfile: 4 })).toBe(
      'Nothing was removed — no targeted device has a Chrome profile signed in to this account.',
    )
  })

  it('names the agent floor when old agents alone explain the refusals', () => {
    expect(removeEverywhereSummary({ targetedDevices: 2, rejectedAgentTooOld: 2 })).toBe(
      'Nothing was removed — 2 devices need an agent with scheduled removal (1.26.0 or newer).',
    )
  })

  it('uses the honest umbrella when the skips are mixed', () => {
    expect(
      removeEverywhereSummary({ targetedDevices: 3, skippedNoProfile: 1, skippedDefault: 1, rejectedAgentTooOld: 1 }),
    ).toBe('Nothing was removed — all 3 targeted devices were skipped or refused.')
  })

  it('still reads sensibly when the server sent no targeted count', () => {
    expect(removeEverywhereSummary({ skippedDefault: 2 })).toBe(
      'Nothing was removed — every targeted device was skipped or refused.',
    )
  })

  it('reports an empty target as such — an empty tally object breaks nothing', () => {
    expect(removeEverywhereSummary({})).toBe('Nothing was done — the target resolved to no devices.')
  })
})
