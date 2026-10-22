// Pure view-model for the fleet-wide Accounts page: the two forms' validation and
// the way a server result is turned into words and badges. Kept out of the page so
// every branch is unit-tested without rendering. Mirrors announcementsView /
// screenLocksView.

import type { BulkAccountLifecycleResult, BulkLocalAccountResult } from '../api/client'

/**
 * Any bulk outcome this page can show. Create/reset report one `skipped` count;
 * disable/enable/delete split theirs into account-not-present and other.
 */
export type BulkOutcome = BulkLocalAccountResult | BulkAccountLifecycleResult

/** Where a bulk operation is aimed. Unlike announcements/screen-locks, this adds 'all'. */
export type AccountsTargetMode = 'all' | 'group' | 'devices'

/** Which operation the page is performing. */
export type AccountsAction = 'create' | 'reset' | 'disable' | 'enable' | 'delete'

/** The actions that take an existing account's name and nothing else. */
export type LifecycleAction = 'disable' | 'enable' | 'delete'

/** Button and radio wording, in one place so the two never drift apart. */
export const actionLabel: Record<AccountsAction, string> = {
  create: 'Create account',
  reset: 'Reset password',
  disable: 'Disable account',
  enable: 'Enable account',
  delete: 'Delete account',
}

/** Past tense for the result sentence: "Nothing was deleted — …". */
export const actionVerb: Record<AccountsAction, string> = {
  create: 'created',
  reset: 'reset',
  disable: 'disabled',
  enable: 'enabled',
  delete: 'deleted',
}

export interface TargetState {
  mode: AccountsTargetMode
  groupId: string | null
  deviceIds: string[]
}

export interface CreateFormState {
  username: string
  password: string
  confirmPassword: string
  accountType: 'StandardUser' | 'Administrator'
}

export interface ResetFormState {
  username: string
  password: string
  confirmPassword: string
}

/** Disable, enable and delete need only the account's name. */
export interface LifecycleFormState {
  username: string
}

/** Windows SAM account-name ceiling; the server enforces the same and more. */
export const MaxUsernameLength = 20

/** The server refuses anything shorter; stated here so the form fails before the round-trip. */
export const MinPasswordLength = 8

/** One message at a time, in the order the operator should fix them. */
export function validateTarget(target: TargetState): string | null {
  if (target.mode === 'group' && target.groupId === null) {
    return 'Choose a group.'
  }

  if (target.mode === 'devices' && target.deviceIds.length === 0) {
    return 'Select at least one device.'
  }

  return null
}

export function validateCreateForm(form: CreateFormState, target: TargetState): string | null {
  const username = form.username.trim()
  if (username.length === 0) {
    return 'Enter a username.'
  }

  if (username.length > MaxUsernameLength) {
    return `Username must be ${MaxUsernameLength} characters or fewer.`
  }

  if (form.password.length < MinPasswordLength) {
    return `Password must be at least ${MinPasswordLength} characters.`
  }

  if (form.password !== form.confirmPassword) {
    return 'Passwords do not match.'
  }

  return validateTarget(target)
}

export function validateResetForm(form: ResetFormState, target: TargetState): string | null {
  if (form.username.trim().length === 0) {
    return 'Enter the account username.'
  }

  if (form.password.length < MinPasswordLength) {
    return `Password must be at least ${MinPasswordLength} characters.`
  }

  if (form.password !== form.confirmPassword) {
    return 'Passwords do not match.'
  }

  return validateTarget(target)
}

/** Disable/enable/delete carry no secret, so only the name and the target can be wrong. */
export function validateLifecycleForm(form: LifecycleFormState, target: TargetState): string | null {
  if (form.username.trim().length === 0) {
    return 'Enter the account username.'
  }

  return validateTarget(target)
}

/**
 * The server's own counts turned into a sentence — never a local guess. `verb` is
 * the past tense of the action ('created', 'reset', 'disabled', 'enabled', 'deleted').
 */
export function provisionSummary(result: BulkOutcome, verb: string): string {
  const skipped = 'skipped' in result ? result.skipped : result.skippedOther
  const noAccount = 'skippedNoAccount' in result ? result.skippedNoAccount : 0

  if (result.queued <= 0) {
    if (result.rejected > 0) {
      return `Nothing was ${verb} — ${result.rejected} device${result.rejected === 1 ? ' was' : 's were'} refused.`
    }

    // All skips explained by the account's absence deserve the real reason, not
    // the generic word "skipped".
    if (noAccount > 0 && skipped <= 0) {
      return `Nothing was ${verb} — the account is not present on any of the ${result.targeted} targeted device${result.targeted === 1 ? '' : 's'}.`
    }

    if (skipped > 0 || noAccount > 0) {
      return `Nothing was ${verb} — all ${result.targeted} targeted device${result.targeted === 1 ? ' was' : 's were'} skipped.`
    }

    return 'Nothing was done — the target resolved to no devices.'
  }

  const parts = [`Queued on ${result.queued} device${result.queued === 1 ? '' : 's'}`]
  if (result.offline > 0) {
    parts.push(`${result.offline} offline — they'll apply it on reconnect`)
  }

  if (skipped > 0) {
    parts.push(`${skipped} skipped`)
  }

  if (noAccount > 0) {
    parts.push(`${noAccount} device${noAccount === 1 ? '' : 's'} skipped — account not present`)
  }

  if (result.rejected > 0) {
    parts.push(`${result.rejected} refused`)
  }

  return `${parts.join(' · ')}.`
}

export interface ResultBadge {
  label: string
  value: number
  tone: 'ok' | 'info' | 'neutral' | 'crit'
}

/** The non-zero counts as badges, in a fixed order; zeros are dropped. */
export function resultBadges(result: BulkOutcome): ResultBadge[] {
  const badges: ResultBadge[] = [
    { label: 'queued', value: result.queued, tone: 'ok' },
    { label: 'offline', value: result.offline, tone: 'info' },
    { label: 'skipped', value: 'skipped' in result ? result.skipped : result.skippedOther, tone: 'neutral' },
    {
      label: 'skipped — account not present',
      value: 'skippedNoAccount' in result ? result.skippedNoAccount : 0,
      tone: 'neutral',
    },
    { label: 'refused', value: result.rejected, tone: 'crit' },
  ]

  return badges.filter((badge) => badge.value > 0)
}
