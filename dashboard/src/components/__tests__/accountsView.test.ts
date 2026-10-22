import { describe, expect, it } from 'vitest'
import type { BulkAccountLifecycleResult, BulkLocalAccountResult } from '../../api/client'
import {
  actionLabel,
  actionVerb,
  provisionSummary,
  resultBadges,
  validateCreateForm,
  validateLifecycleForm,
  validateResetForm,
  validateTarget,
  type AccountsAction,
  type CreateFormState,
  type ResetFormState,
  type TargetState,
} from '../../pages/accountsView'

const allTarget: TargetState = { mode: 'all', groupId: null, deviceIds: [] }

function create(overrides: Partial<CreateFormState> = {}): CreateFormState {
  return { username: 'itadmin', password: 'hunter2hunter', confirmPassword: 'hunter2hunter', accountType: 'StandardUser', ...overrides }
}

function reset(overrides: Partial<ResetFormState> = {}): ResetFormState {
  return { username: 'itadmin', password: 'hunter2hunter', confirmPassword: 'hunter2hunter', ...overrides }
}

function outcome(overrides: Partial<BulkLocalAccountResult> = {}): BulkLocalAccountResult {
  return { targeted: 0, queued: 0, rejected: 0, offline: 0, skipped: 0, ...overrides }
}

function lifecycle(overrides: Partial<BulkAccountLifecycleResult> = {}): BulkAccountLifecycleResult {
  return { targeted: 0, queued: 0, rejected: 0, offline: 0, skippedNoAccount: 0, skippedOther: 0, ...overrides }
}

describe('validateTarget', () => {
  it('requires a group when the mode is group', () => {
    expect(validateTarget({ mode: 'group', groupId: null, deviceIds: [] })).toBe('Choose a group.')
    expect(validateTarget({ mode: 'group', groupId: 'g1', deviceIds: [] })).toBeNull()
  })

  it('requires at least one device when the mode is devices', () => {
    expect(validateTarget({ mode: 'devices', groupId: null, deviceIds: [] })).toBe('Select at least one device.')
    expect(validateTarget({ mode: 'devices', groupId: null, deviceIds: ['d1'] })).toBeNull()
  })

  it('needs nothing extra for all devices', () => {
    expect(validateTarget(allTarget)).toBeNull()
  })
})

describe('validateCreateForm', () => {
  it('accepts a complete form', () => {
    expect(validateCreateForm(create(), allTarget)).toBeNull()
  })

  it('reports problems one at a time, form before target', () => {
    expect(validateCreateForm(create({ username: '  ' }), allTarget)).toBe('Enter a username.')
    expect(validateCreateForm(create({ username: 'x'.repeat(21) }), allTarget)).toBe('Username must be 20 characters or fewer.')
    expect(validateCreateForm(create({ password: 'short', confirmPassword: 'short' }), allTarget))
      .toBe('Password must be at least 8 characters.')
    expect(validateCreateForm(create({ confirmPassword: 'different' }), allTarget)).toBe('Passwords do not match.')
    // A valid form but no group chosen falls through to the target check.
    expect(validateCreateForm(create(), { mode: 'group', groupId: null, deviceIds: [] })).toBe('Choose a group.')
  })
})

describe('validateResetForm', () => {
  it('accepts a complete form and catches each problem', () => {
    expect(validateResetForm(reset(), allTarget)).toBeNull()
    expect(validateResetForm(reset({ username: '' }), allTarget)).toBe('Enter the account username.')
    expect(validateResetForm(reset({ password: 'nope', confirmPassword: 'nope' }), allTarget))
      .toBe('Password must be at least 8 characters.')
    expect(validateResetForm(reset({ confirmPassword: 'other' }), allTarget)).toBe('Passwords do not match.')
  })
})

describe('validateLifecycleForm', () => {
  it('needs only an account name and a target', () => {
    expect(validateLifecycleForm({ username: 'itadmin' }, allTarget)).toBeNull()
    expect(validateLifecycleForm({ username: '  ' }, allTarget)).toBe('Enter the account username.')
    // A valid form but no group chosen falls through to the target check.
    expect(validateLifecycleForm({ username: 'itadmin' }, { mode: 'group', groupId: null, deviceIds: [] }))
      .toBe('Choose a group.')
  })
})

describe('action wording', () => {
  it('has a label and a past-tense verb for every action', () => {
    const actions: AccountsAction[] = ['create', 'reset', 'disable', 'enable', 'delete']
    for (const a of actions) {
      expect(actionLabel[a]).toBeTruthy()
      expect(actionVerb[a]).toBeTruthy()
    }
    expect(actionLabel.delete).toBe('Delete account')
    expect(actionVerb.delete).toBe('deleted')
  })
})

describe('provisionSummary', () => {
  it('summarises a normal queue, with offline called out', () => {
    expect(provisionSummary(outcome({ targeted: 5, queued: 5, offline: 2 }), 'created'))
      .toBe("Queued on 5 devices · 2 offline — they'll apply it on reconnect.")
  })

  it('reports when nothing happened', () => {
    expect(provisionSummary(outcome({ targeted: 3, skipped: 3 }), 'reset'))
      .toBe('Nothing was reset — all 3 targeted devices were skipped.')
    expect(provisionSummary(outcome({ targeted: 1, rejected: 1 }), 'created'))
      .toBe('Nothing was created — 1 device was refused.')
    expect(provisionSummary(outcome(), 'created')).toBe('Nothing was done — the target resolved to no devices.')
  })

  it('calls out devices skipped because the account is not present', () => {
    expect(provisionSummary(lifecycle({ targeted: 5, queued: 3, skippedNoAccount: 2 }), 'disabled'))
      .toBe('Queued on 3 devices · 2 devices skipped — account not present.')
    expect(provisionSummary(lifecycle({ targeted: 5, queued: 4, skippedNoAccount: 1 }), 'deleted'))
      .toBe('Queued on 4 devices · 1 device skipped — account not present.')
  })

  it('names the absent account as the reason when it explains every skip', () => {
    expect(provisionSummary(lifecycle({ targeted: 3, skippedNoAccount: 3 }), 'deleted'))
      .toBe('Nothing was deleted — the account is not present on any of the 3 targeted devices.')
    // Mixed skips fall back to the generic sentence rather than guessing a split.
    expect(provisionSummary(lifecycle({ targeted: 4, skippedOther: 2, skippedNoAccount: 2 }), 'enabled'))
      .toBe('Nothing was enabled — all 4 targeted devices were skipped.')
  })
})

describe('resultBadges', () => {
  it('keeps only the non-zero counts, in order', () => {
    expect(resultBadges(outcome({ queued: 4, offline: 1, skipped: 2 })).map((b) => b.label))
      .toEqual(['queued', 'offline', 'skipped'])
    expect(resultBadges(outcome())).toEqual([])
  })

  it('adds a badge for devices skipped because the account is not present', () => {
    expect(resultBadges(lifecycle({ queued: 3, skippedNoAccount: 2 })).map((b) => b.label))
      .toEqual(['queued', 'skipped — account not present'])
    // A lifecycle result's other skips land under the same plain label create/reset use.
    expect(resultBadges(lifecycle({ queued: 3, skippedOther: 1 })).map((b) => b.label))
      .toEqual(['queued', 'skipped'])
    // Create/reset results never carry the count, so the badge never appears for them.
    expect(resultBadges(outcome({ queued: 3 })).map((b) => b.label)).toEqual(['queued'])
  })
})
