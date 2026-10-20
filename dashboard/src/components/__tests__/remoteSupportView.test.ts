import { describe, expect, it } from 'vitest'
import {
  beginSession,
  endSession,
  isRevealed,
  noSession,
  stateBadgeTone,
  stateLabel,
  validateConnectForm,
} from '../../pages/remoteSupportView'
import type { RemoteSupportState } from '../../api/client'

/**
 * The console's half of IT Remote Support.
 *
 * Two things carry weight here, both mirrored from recovery-key reveal. **A
 * revealed session lives in component state and nowhere else** — the value
 * object takes no clock and exposes no deadline, so no passage of time can
 * transition it back to hidden; it leaves the screen on Done and on unmount.
 * And **nothing here derives a connection from a secret**: the label and the
 * badge tone are computed from the server's state alone, so the list describes a
 * device completely without any credential existing in the page.
 */

const ALL_STATES: RemoteSupportState[] = ['NotInstalled', 'NotEnrolled', 'Enrolling', 'Ready']

describe('remote-support state presentation', () => {
  it('labels every state in plain words', () => {
    expect(stateLabel('NotInstalled')).toBe('Not installed')
    expect(stateLabel('NotEnrolled')).toBe('Not enrolled')
    expect(stateLabel('Enrolling')).toBe('Enrolling')
    expect(stateLabel('Ready')).toBe('Ready')
  })

  /**
   * Ready is the only state a session can be started from, so it is the only
   * green one. The others must not read as "good to go".
   */
  it('colours only Ready as good', () => {
    expect(stateBadgeTone('Ready')).toBe('ok')

    expect(stateBadgeTone('NotInstalled')).not.toBe('ok')
    expect(stateBadgeTone('NotEnrolled')).not.toBe('ok')
    expect(stateBadgeTone('Enrolling')).not.toBe('ok')
  })

  it('reads an in-flight enrolment as progress, not a fault', () => {
    expect(stateBadgeTone('Enrolling')).toBe('info')
  })

  it('reads a device that needs enabling as a warning, and a fresh one as neutral', () => {
    expect(stateBadgeTone('NotEnrolled')).toBe('warn')
    expect(stateBadgeTone('NotInstalled')).toBe('neutral')
  })

  it('has a label and an allowed tone for every state', () => {
    for (const state of ALL_STATES) {
      expect(stateLabel(state).length).toBeGreaterThan(0)
      expect(['ok', 'warn', 'info', 'neutral']).toContain(stateBadgeTone(state))
    }
  })
})

describe('connect form validation', () => {
  it('accepts a password with a real justification', () => {
    expect(validateConnectForm({ currentPassword: 'hunter2', justification: 'printer help' })).toBe(true)
  })

  it('rejects a missing password', () => {
    expect(validateConnectForm({ currentPassword: '', justification: 'printer help' })).toBe(false)
  })

  /** A stray keystroke is not a reason; mirrors the recovery-key reveal gate. */
  it('rejects a justification under three characters', () => {
    expect(validateConnectForm({ currentPassword: 'hunter2', justification: 'hi' })).toBe(false)
  })

  it('does not count surrounding whitespace as justification', () => {
    // Whitespace-only is nothing; and the length rule is applied to the trimmed
    // value, so " ok " ("ok", two characters) is still too short.
    expect(validateConnectForm({ currentPassword: 'hunter2', justification: '   ' })).toBe(false)
    expect(validateConnectForm({ currentPassword: 'hunter2', justification: '  ok ' })).toBe(false)
    expect(validateConnectForm({ currentPassword: 'hunter2', justification: '  fix ' })).toBe(true)
  })
})

/**
 * A revealed session stays until the operator dismisses it.
 *
 * The property under test is the absence of a mechanism, which is harder to
 * assert than a behaviour. `RemoteSessionReveal` therefore takes no clock and
 * exposes no deadline, so these tests can show that no passage of time changes
 * anything.
 */
describe('revealed session visibility', () => {
  const ID = '123 456 789'
  const PASSWORD = 's3cr3t-once'

  it('starts with nothing revealed', () => {
    expect(isRevealed(noSession)).toBe(false)
    expect(noSession.value).toBeNull()
  })

  it('holds the id and password once revealed', () => {
    const session = beginSession(ID, PASSWORD)

    expect(isRevealed(session)).toBe(true)
    expect(session.value).toEqual({ rustDeskId: ID, password: PASSWORD })
  })

  /** The removed behaviour: no timer, so a day's worth of "elapsed" changes nothing. */
  it('remains visible after sixty seconds, and after a day', () => {
    const session = beginSession(ID, PASSWORD)

    // There is no clock to advance: the session is the same value however much
    // time passes, which is the point.
    for (const _elapsedMs of [60_000, 60_001, 3_600_000, 86_400_000]) {
      expect(isRevealed(session)).toBe(true)
      expect(session.value?.password).toBe(PASSWORD)
    }
  })

  it('pressing Done clears the credentials at once', () => {
    const session = endSession()

    expect(isRevealed(session)).toBe(false)
    expect(session.value).toBeNull()
  })

  /** Nothing of the password survives the transition. */
  it('the password is not present after Done', () => {
    const after = endSession()

    expect(after.value).toBeNull()
    expect(JSON.stringify(after)).not.toContain(PASSWORD)
  })

  /**
   * The session carries the one value and nothing else, so there is no second
   * field a copy of the password could linger in after it is cleared.
   */
  it('carries the value and nothing else', () => {
    expect(Object.keys(beginSession(ID, PASSWORD))).toEqual(['value'])
  })

  it('a later reveal starts a fresh session', () => {
    const first = beginSession(ID, PASSWORD)
    const cleared = endSession()
    const second = beginSession(ID, PASSWORD)

    expect(isRevealed(first)).toBe(true)
    expect(isRevealed(cleared)).toBe(false)
    expect(isRevealed(second)).toBe(true)
  })
})
