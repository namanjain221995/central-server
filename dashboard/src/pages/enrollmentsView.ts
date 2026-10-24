import type { PendingEnrollment } from '../api/client'

/**
 * Presentation logic for the Pending Enrollments page.
 *
 * Two kinds of request share that page, and the difference is the whole point.
 * A plain enrollment admits an unknown machine; a RE-ENROLLMENT hands an
 * existing, active device's identity — its queued tasks and device-bound
 * secrets — to the requester and revokes the credential the real machine is
 * holding. Everything here exists so that difference is impossible to miss at
 * the moment of decision: the badge, the matched-device line, the ONLINE
 * warning, and an approve confirmation that spells out what is being handed
 * over. Pure functions, so the stakes-bearing copy is pinned by tests rather
 * than living only in JSX.
 */

/** The fields these helpers read; the page passes full rows. */
export type EnrollmentRow = Pick<
  PendingEnrollment,
  'kind' | 'hostname' | 'matchedDeviceHostname' | 'credentialLastUsedAt' | 'online'
>

export function isReEnrollment(row: Pick<PendingEnrollment, 'kind'>): boolean {
  return row.kind === 're-enrollment'
}

/**
 * "3m ago" — how long since the matched device's credential last authenticated.
 *
 * Coarse on purpose: the administrator is judging "is this machine still
 * alive", not reading a log. Clamped at zero so clock skew between browser and
 * server can never render a contact "in the future", which would read as a bug
 * exactly when trust in the page matters most.
 */
export function relativeAgo(iso: string, now: number): string {
  const then = new Date(iso).getTime()
  if (Number.isNaN(then)) return 'unknown'

  const seconds = Math.max(0, Math.floor((now - then) / 1000))
  if (seconds < 60) return `${seconds}s ago`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 48) return `${hours}h ago`
  return `${Math.floor(hours / 24)}d ago`
}

/**
 * "matches OM-PC-07 — last agent contact 3m ago", under a re-enrollment row.
 *
 * Null for plain enrollments: there is no matched device to describe. A device
 * that has never authenticated says so explicitly rather than showing nothing,
 * because "no agent contact recorded" (a machine that enrolled but never
 * reported) reads very differently from a device alive minutes ago.
 */
export function matchLine(row: EnrollmentRow, now: number): string | null {
  if (!isReEnrollment(row) || !row.matchedDeviceHostname) return null

  const contact = row.credentialLastUsedAt
    ? `last agent contact ${relativeAgo(row.credentialLastUsedAt, now)}`
    : 'no agent contact recorded'

  return `matches ${row.matchedDeviceHostname} — ${contact}`
}

/**
 * Whether to show the ONLINE warning chip.
 *
 * True only for a re-enrollment whose matched device authenticated within the
 * fleet's offline window — the server computes that from the credential's own
 * use, which enrollment cannot bump. An online device being re-enrolled is THE
 * suspicious case: the real machine is still reporting while someone else asks
 * to become it. A wiped or dead machine shows no chip and reads as routine.
 */
export function showOnlineWarning(row: EnrollmentRow): boolean {
  return isReEnrollment(row) && row.online === true
}

/**
 * The confirmation an approver must read, or null when no dialog is needed.
 *
 * Plain enrollments keep their one-click approve: admitting a new machine
 * takes nothing from anyone. A re-enrollment names exactly what approval does
 * — revokes the existing device's credential and hands its identity over — and
 * escalates in plain words when that device looks alive right now.
 */
export function approveConfirmation(row: EnrollmentRow): string | null {
  if (!isReEnrollment(row)) return null

  const device = row.matchedDeviceHostname ?? 'the matched device'
  const lines = [
    `Approve re-enrollment of "${row.hostname}"?`,
    '',
    `This hands the identity of "${device}" to the requesting machine. ` +
      `${device}'s current credential is revoked the moment the agent completes ` +
      '(within about 5 minutes), and its queued tasks and pending secrets go to the requester.',
  ]

  if (showOnlineWarning(row)) {
    lines.push(
      '',
      `WARNING: ${device} is ONLINE right now. A machine that is still reporting does ` +
        'not normally need re-enrolling — approve only if you know who is asking.',
    )
  }

  return lines.join('\n')
}

/** The confirmation a rejecting administrator reads; kind-specific consequences. */
export function rejectConfirmation(row: EnrollmentRow): string {
  if (isReEnrollment(row)) {
    const device = row.matchedDeviceHostname ?? 'the matched device'
    return (
      `Reject the re-enrollment request from "${row.hostname}"?\n\n` +
      `${device} keeps its current credential and nothing changes on it. ` +
      'The requesting machine keeps being refused; if it is still asking after an hour, ' +
      'the request will reappear here.'
    )
  }

  return (
    `Reject the enrollment request from "${row.hostname}"?\n\n` +
    'No credential will be issued and the agent will stop asking. ' +
    'The machine can request enrollment again later.'
  )
}
