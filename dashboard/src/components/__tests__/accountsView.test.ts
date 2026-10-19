import { describe, expect, it } from 'vitest'
import type { BulkLocalAccountResult } from '../../api/client'
import {
  provisionSummary,
  resultBadges,
  validateCreateForm,
  validateResetForm,
  validateTarget,
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
})

describe('resultBadges', () => {
  it('keeps only the non-zero counts, in order', () => {
    expect(resultBadges(outcome({ queued: 4, offline: 1, skipped: 2 })).map((b) => b.label))
      .toEqual(['queued', 'offline', 'skipped'])
    expect(resultBadges(outcome())).toEqual([])
  })
})
