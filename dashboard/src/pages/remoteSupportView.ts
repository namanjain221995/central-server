import type { RemoteSupportState } from '../api/client'

/**
 * Presentation logic for IT Remote Support, kept out of the component so the
 * rules that protect the session credentials can be asserted directly.
 *
 * The one that matters most mirrors recovery-key reveal: a revealed session —
 * the device's RustDesk id and its one-time password — lives in component state
 * and nowhere else, never in storage, a URL or router state, and is dropped when
 * the operator dismisses it or the view unmounts. Everything else here derives a
 * label or a tone from metadata the fleet reported.
 */

/**
 * A revealed remote session, held for exactly as long as the operator keeps the
 * dialog open.
 *
 * Modelled as a value rather than left implicit in component state so the one
 * property that matters can be asserted directly: **nothing here is a function
 * of time.** There is no deadline, no timer and no clock input, so no amount of
 * elapsed time can transition a revealed session back to hidden. It leaves the
 * screen when the operator presses Done, and on unmount.
 */
export interface RemoteSessionReveal {
  /** The RustDesk id and one-time password, or null when nothing is revealed. */
  value: { rustDeskId: string; password: string } | null
}

/** No session on screen. */
export const noSession: RemoteSessionReveal = { value: null }

export function beginSession(rustDeskId: string, password: string): RemoteSessionReveal {
  return { value: { rustDeskId, password } }
}

/** What "Done" does: drops the credentials immediately. */
export function endSession(): RemoteSessionReveal {
  return noSession
}

export function isRevealed(session: RemoteSessionReveal): boolean {
  return session.value !== null
}

/** How a device's remote-support state reads to an operator. */
export function stateLabel(state: RemoteSupportState): string {
  switch (state) {
    case 'Ready':
      return 'Ready'
    case 'Enrolling':
      return 'Enrolling'
    case 'NotEnrolled':
      return 'Not enrolled'
    default:
      return 'Not installed'
  }
}

/**
 * The badge colour for a state.
 *
 * `Ready` is the only "good" state, so it is the only green one. `Enrolling` is
 * in-flight, not a problem (info). `NotEnrolled` is the one that wants an
 * operator to act, so it reads as a warning. `NotInstalled` is simply the
 * starting point — nothing is wrong — so it stays neutral.
 */
export function stateBadgeTone(state: RemoteSupportState): 'ok' | 'warn' | 'info' | 'neutral' {
  switch (state) {
    case 'Ready':
      return 'ok'
    case 'Enrolling':
      return 'info'
    case 'NotEnrolled':
      return 'warn'
    default:
      return 'neutral'
  }
}

/**
 * Whether the connect form may be submitted.
 *
 * Mirrors the recovery-key reveal gate: a step-up password must be present and a
 * justification must be more than a stray keystroke. Client-side only, for
 * immediate feedback — the server authorizes and audits independently.
 */
export function validateConnectForm({
  currentPassword,
  justification,
}: {
  currentPassword: string
  justification: string
}): boolean {
  return currentPassword.length > 0 && justification.trim().length >= 3
}
