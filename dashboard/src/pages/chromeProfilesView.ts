import type { ChromeAccountRemovalResult, ChromeProfileRow } from '../api/client'
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

/** The tracked restart task's label when a removal asked for one. */
export function removeRestartLabel(profile: Pick<ChromeProfileRow, 'profileName' | 'profileKey'>): string {
  return `Restart device to finish removing "${profileTitle(profile)}"`
}

// ------------------------------------------------------- scheduled semantics
//
// Agent 1.26.0 changed what an open Chrome means: the removal no longer fails,
// it is scheduled. The copy lives here, once, so the single-device dialog, the
// fleet dialog and the tests all say the same thing — three retellings of one
// behavior is how wording starts contradicting itself.

/**
 * The scheduled-removal behavior, in the operator's words. Shown in both remove
 * dialogs in place of the old "have them close Chrome and try again" advice.
 */
export const ScheduledRemovalNote =
  'Where Chrome is open, the removal does not fail: the device reports it as “Scheduled” and completes it the moment Chrome closes, or at the next startup.'

/** Why some targeted devices refuse: they predate scheduled removal. */
export const AgentFloorNote = 'Devices on agents older than 1.26.0 cannot schedule a removal and are refused.'

/** The restart checkbox. Shown only to a viewer holding the device-restart permission. */
export const RestartCheckboxLabel = 'Restart the device to finish immediately'

/** Under the restart checkbox: what happens without one. */
export const RestartFinishesNote =
  'Without it, the profile is removed automatically when Chrome closes or at the next startup.'

// ------------------------------------------------------------ fleet removal

/**
 * Whether the row offers "Remove on all devices…". The fleet action addresses a
 * Google account, so the profile needs one signed in; and Chrome's built-in
 * Default profile is refused everywhere, exactly as it is on this device. The
 * server re-checks both — this only keeps the console from offering a request
 * the server would refuse.
 */
export function canRemoveEverywhere(profile: Pick<ChromeProfileRow, 'profileKey' | 'accountEmail'>): boolean {
  return canRemove(profile.profileKey) && (profile.accountEmail?.trim().length ?? 0) > 0
}

/** A tally that is not a number — or was never sent — counts as 0 and is not shown. */
function tallyCount(value: number | undefined): number {
  return typeof value === 'number' && Number.isFinite(value) ? value : 0
}

export interface RemovalTallyBadge {
  label: string
  value: number
  tone: 'ok' | 'info' | 'neutral' | 'crit'
}

/**
 * The known tallies' wording and tone, in the order they are shown.
 * `targetedDevices` stays out: the summary sentence already carries it.
 */
const KNOWN_TALLIES: readonly { key: string; label: string; tone: RemovalTallyBadge['tone'] }[] = [
  { key: 'queuedRemovals', label: 'removals queued', tone: 'ok' },
  { key: 'queuedRestarts', label: 'restarts queued', tone: 'info' },
  { key: 'offline', label: 'offline — act on reconnect', tone: 'info' },
  { key: 'skippedNoProfile', label: 'skipped — no profile for this account', tone: 'neutral' },
  { key: 'skippedDefault', label: 'skipped — Default profile', tone: 'neutral' },
  { key: 'rejectedAgentTooOld', label: 'refused — agent too old', tone: 'crit' },
]

/** "rejectedAgentTooOld" → "rejected agent too old": words for a tally this build does not know. */
export function tallyLabel(key: string): string {
  return key
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .toLowerCase()
}

/**
 * The server's non-zero tallies as badges (mirrors the bulk Accounts page's
 * resultBadges): known keys first, in a fixed order with their wording, then
 * whatever else the server sent, named from its key — a count a newer server
 * adds is rendered, never dropped. Zero and missing tallies are simply absent;
 * nothing here invents a number the server did not give.
 */
export function removalTallyBadges(result: ChromeAccountRemovalResult): RemovalTallyBadge[] {
  const known = KNOWN_TALLIES.map(({ key, label, tone }) => ({ label, tone, value: tallyCount(result[key]) }))
  const extras = Object.keys(result)
    .filter((key) => key !== 'targetedDevices' && !KNOWN_TALLIES.some((k) => k.key === key))
    .map((key) => ({ label: tallyLabel(key), tone: 'neutral' as const, value: tallyCount(result[key]) }))
  return [...known, ...extras].filter((badge) => badge.value > 0)
}

/**
 * The server's tallies turned into one sentence, mirroring the bulk Accounts
 * page's provisionSummary: the server's own counts, never a local guess, and
 * tolerant of keys it did not send — a missing count is simply not mentioned.
 */
export function removeEverywhereSummary(result: ChromeAccountRemovalResult): string {
  const queued = tallyCount(result.queuedRemovals)
  const targeted = tallyCount(result.targetedDevices)
  const noProfile = tallyCount(result.skippedNoProfile)
  const skippedDefault = tallyCount(result.skippedDefault)
  const tooOld = tallyCount(result.rejectedAgentTooOld)
  const offline = tallyCount(result.offline)
  const restarts = tallyCount(result.queuedRestarts)

  if (queued <= 0) {
    // One cause deserves its real name; a mixture gets the honest umbrella.
    if (tooOld > 0 && noProfile <= 0 && skippedDefault <= 0) {
      return `Nothing was removed — ${countLabel(tooOld, 'device needs', 'devices need')} an agent with scheduled removal (1.26.0 or newer).`
    }
    if (noProfile > 0 && tooOld <= 0 && skippedDefault <= 0) {
      return 'Nothing was removed — no targeted device has a Chrome profile signed in to this account.'
    }
    if (noProfile > 0 || skippedDefault > 0 || tooOld > 0) {
      const where = targeted > 0
        ? `all ${targeted} targeted device${targeted === 1 ? ' was' : 's were'}`
        : 'every targeted device was'
      return `Nothing was removed — ${where} skipped or refused.`
    }
    return 'Nothing was done — the target resolved to no devices.'
  }

  const parts = [
    `Queued the removal on ${countLabel(queued, 'device')}${targeted > queued ? ` of ${targeted} targeted` : ''}`,
  ]
  if (restarts > 0) parts.push(`${countLabel(restarts, 'restart')} queued to finish immediately`)
  if (offline > 0) parts.push(`${offline} offline — they act on reconnect`)
  if (noProfile > 0) parts.push(`${noProfile} skipped — no profile for this account`)
  if (skippedDefault > 0) parts.push(`${skippedDefault} skipped — Default profile`)
  if (tooOld > 0) parts.push(`${tooOld} refused — agent too old`)

  return `${parts.join(' · ')}.`
}
