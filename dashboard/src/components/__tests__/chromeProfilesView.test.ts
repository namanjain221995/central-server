import { describe, expect, it } from 'vitest'
import {
  canRemove,
  dormantDays,
  dormantHint,
  removeProfileLabel,
  removeSuggested,
  removeTitle,
  statusBadge,
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
