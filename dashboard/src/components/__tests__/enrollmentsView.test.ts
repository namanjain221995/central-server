import { describe, expect, it } from 'vitest'
import {
  approveConfirmation,
  isReEnrollment,
  matchLine,
  rejectConfirmation,
  relativeAgo,
  showOnlineWarning,
  type EnrollmentRow,
} from '../../pages/enrollmentsView'

/**
 * The console's half of the re-enrollment approval gate.
 *
 * What carries weight here is the copy an administrator reads at the moment of
 * decision: approving a re-enrollment revokes the matched device's working
 * credential and hands its identity to the requester, and the one place that is
 * said is these functions. Plain enrollments must keep their one-click approve
 * — making every approval ask would train people to click through the dialog
 * that matters.
 */

const NOW = Date.parse('2026-10-24T12:00:00Z')

function reEnrollment(overrides: Partial<EnrollmentRow> = {}): EnrollmentRow {
  return {
    kind: 're-enrollment',
    hostname: 'OM-PC-07-REINSTALL',
    matchedDeviceHostname: 'OM-PC-07',
    credentialLastUsedAt: '2026-10-24T11:57:00Z',
    online: true,
    ...overrides,
  }
}

function plainEnrollment(): EnrollmentRow {
  return {
    kind: 'enrollment',
    hostname: 'NEW-PC-01',
    matchedDeviceHostname: null,
    credentialLastUsedAt: null,
    online: null,
  }
}

describe('row kind', () => {
  it('recognises a re-enrollment', () => {
    expect(isReEnrollment(reEnrollment())).toBe(true)
    expect(isReEnrollment(plainEnrollment())).toBe(false)
  })
})

describe('matched-device line', () => {
  it('names the matched device and its last agent contact', () => {
    expect(matchLine(reEnrollment(), NOW)).toBe('matches OM-PC-07 — last agent contact 3m ago')
  })

  /**
   * "Never authenticated" reads very differently from "alive minutes ago", so
   * it is said outright instead of showing nothing.
   */
  it('says when the device has no recorded agent contact', () => {
    expect(matchLine(reEnrollment({ credentialLastUsedAt: null }), NOW)).toBe(
      'matches OM-PC-07 — no agent contact recorded',
    )
  })

  it('shows nothing for a plain enrollment, which matches no device', () => {
    expect(matchLine(plainEnrollment(), NOW)).toBeNull()
  })
})

describe('relative last contact', () => {
  it('scales from seconds to days', () => {
    expect(relativeAgo('2026-10-24T11:59:30Z', NOW)).toBe('30s ago')
    expect(relativeAgo('2026-10-24T11:15:00Z', NOW)).toBe('45m ago')
    expect(relativeAgo('2026-10-24T02:00:00Z', NOW)).toBe('10h ago')
    expect(relativeAgo('2026-10-20T12:00:00Z', NOW)).toBe('4d ago')
  })

  /** Clock skew must never render a contact "in the future". */
  it('clamps a future timestamp to the present', () => {
    expect(relativeAgo('2026-10-24T12:00:05Z', NOW)).toBe('0s ago')
  })

  it('does not pretend to know an unparseable time', () => {
    expect(relativeAgo('not-a-date', NOW)).toBe('unknown')
  })
})

describe('online warning', () => {
  /**
   * The suspicious case, and only it: a re-enrollment whose matched device is
   * still authenticating. The server computes `online` from the credential's
   * own use, which the request being listed cannot bump.
   */
  it('shows only for a re-enrollment whose device is online', () => {
    expect(showOnlineWarning(reEnrollment())).toBe(true)
    expect(showOnlineWarning(reEnrollment({ online: false }))).toBe(false)
    expect(showOnlineWarning(reEnrollment({ online: null }))).toBe(false)
    expect(showOnlineWarning(plainEnrollment())).toBe(false)
  })
})

describe('approve confirmation', () => {
  /** One-click approve for new machines: admitting one takes nothing from anyone. */
  it('asks nothing for a plain enrollment', () => {
    expect(approveConfirmation(plainEnrollment())).toBeNull()
  })

  it('states the handover: credential revoked, identity to the requester', () => {
    const text = approveConfirmation(reEnrollment({ online: false }))!

    expect(text).toContain('Approve re-enrollment of "OM-PC-07-REINSTALL"?')
    expect(text).toContain('hands the identity of "OM-PC-07" to the requesting machine')
    expect(text).toContain('credential is revoked')
    expect(text).toContain('queued tasks and pending secrets go to the requester')
    // Sets the expectation that nothing is pushed: the agent's retry completes it.
    expect(text).toContain('within about 5 minutes')
  })

  it('escalates when the matched device is online right now', () => {
    expect(approveConfirmation(reEnrollment())).toContain('OM-PC-07 is ONLINE right now')
    expect(approveConfirmation(reEnrollment({ online: false }))).not.toContain('ONLINE')
  })

  it('still reads sensibly when the matched hostname is unknown', () => {
    const text = approveConfirmation(
      reEnrollment({ matchedDeviceHostname: null, online: false }),
    )!
    expect(text).toContain('hands the identity of "the matched device"')
  })
})

describe('reject confirmation', () => {
  it('keeps the existing copy for a plain enrollment', () => {
    const text = rejectConfirmation(plainEnrollment())
    expect(text).toContain('Reject the enrollment request from "NEW-PC-01"?')
    expect(text).toContain('No credential will be issued')
    expect(text).toContain('The machine can request enrollment again later.')
  })

  /**
   * A re-enrollment rejection reassures about the device that stays, and is
   * honest that a still-knocking machine resurfaces after the cooling-off.
   */
  it('says the device keeps its credential and the request may resurface', () => {
    const text = rejectConfirmation(reEnrollment())
    expect(text).toContain('Reject the re-enrollment request from "OM-PC-07-REINSTALL"?')
    expect(text).toContain('OM-PC-07 keeps its current credential')
    expect(text).toContain('after an hour')
  })
})
