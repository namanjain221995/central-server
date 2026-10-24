import type { ChromeProfileRow } from '../api/client'
import { countLabel, profileTitle, type Tone } from './chromeView'

/**
 * The rules behind the profile lifecycle column and its Remove action, kept out
 * of the components so every branch is unit-tested without rendering (mirrors
 * accountsView / chromeExtensionRulesView). The status itself is the server's:
 * nothing here derives one from `signedIn`/`signInBroken` or a timestamp —
 * these only turn the server's word into a badge, say which profiles the
 * console may offer to remove, and name the queued task.
 */

/**
 * How long the server lets a profile sit unused before calling it dormant.
 * Stated here for wording only — the judgement is made on the server.
 */
export const DormantAfterDays = 7

export interface ProfileStatusBadge {
  label: string
  tone: Tone
  /** The line under the badge explaining it, or null when the label says it all. */
  hint: string | null
}

/**
 * How a profile's status reads. The four known values map to words; anything
 * else is rendered as-is in a neutral badge, because the server is the single
 * source of truth for status and a value this build does not know is still its
 * verdict — never something to re-derive locally.
 */
export function statusBadge(status: string, lastActiveAt: string | null, now: Date = new Date()): ProfileStatusBadge {
  switch (status) {
    case 'active':
      return { label: 'Active', tone: 'ok', hint: null }
    case 'signInBroken':
      return { label: 'Sign-in broken', tone: 'warn', hint: 'Account attached but sign-in is no longer working.' }
    case 'dormant':
      return { label: 'Dormant', tone: 'neutral', hint: dormantHint(lastActiveAt, now) }
    case 'notSignedIn':
      return { label: 'Not signed in', tone: 'neutral', hint: null }
    default:
      return { label: status, tone: 'neutral', hint: null }
  }
}

/**
 * Whole days since the profile was last used: 0 for earlier today, never
 * negative (a device clock slightly ahead is not "used in the future"). Null
 * when it never was — or when the timestamp cannot be read, which is shown the
 * same way rather than inventing a number.
 */
export function dormantDays(lastActiveAt: string | null, now: Date = new Date()): number | null {
  if (!lastActiveAt) return null
  const lastActive = new Date(lastActiveAt).getTime()
  if (Number.isNaN(lastActive)) return null
  return Math.max(0, Math.floor((now.getTime() - lastActive) / 86_400_000))
}

/** The line under a Dormant badge: "unused 12 days", "unused 1 day", or "never used". */
export function dormantHint(lastActiveAt: string | null, now: Date = new Date()): string {
  const days = dormantDays(lastActiveAt, now)
  return days === null ? 'never used' : `unused ${countLabel(days, 'day')}`
}

/**
 * Whether the console offers Remove for this profile. Chrome's built-in
 * "Default" profile cannot be removed — Chrome itself recreates it on next
 * launch — and the match ignores case because the key names a directory on a
 * case-insensitive Windows disk. The server refuses it regardless; this only
 * keeps the console from offering a request the server would refuse.
 */
export function canRemove(profileKey: string): boolean {
  return profileKey.trim().toLowerCase() !== 'default'
}

/** The Remove button's title: what it does, or why it is disabled. */
export function removeTitle(profileKey: string): string {
  return canRemove(profileKey)
    ? 'Removes this profile from Chrome on the device'
    : 'Chrome’s built-in Default profile cannot be removed — Chrome recreates it on its next launch.'
}

/**
 * Whether Remove is the row's suggested (visually primary) action. Dormant
 * profiles — unused for 7 days or more, or never — are the operator's cleanup
 * target; Remove stays available on every non-Default profile either way, this
 * only picks the button's weight.
 */
export function removeSuggested(status: string): boolean {
  return status === 'dormant'
}

/** The tracked task's label, e.g. `Remove Chrome profile "Work"`. */
export function removeProfileLabel(profile: Pick<ChromeProfileRow, 'profileName' | 'profileKey'>): string {
  return `Remove Chrome profile "${profileTitle(profile)}"`
}
