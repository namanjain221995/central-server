import { useEffect, useId, useRef, useState, type FormEvent, type ReactNode } from 'react'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'
import { useDialogFocus } from '../components/dialogFocus'
import { apiFailureMessage, validateStepUpForm } from './agentReleaseView'

/**
 * Confirmation-with-step-up for the agent-release actions that put SYSTEM-level
 * code on the path to machines: publish, artifact replacement, and queueing a
 * device self-update.
 *
 * The pattern is the BitLocker recovery-key reveal's, for the same reason:
 * holding the permission proves what the account may do; re-typing the password
 * proves the account is still being driven by the person who signed in, and the
 * justification is recorded in the audit trail. The password lives in component
 * state alone -- never in storage, a URL or router state -- is sent once, and is
 * cleared the moment it has been used or the dialog unmounts.
 *
 * This joins the action's existing confirmation rather than replacing it: the
 * caller's copy (what will happen, to which machines, and any warnings) renders
 * above the step-up fields, and a refusal -- "That password is not correct.",
 * "Too many agent-release actions. Try again later." -- is shown inline, as the
 * server wrote it, so the operator can correct and retry without losing the
 * dialog.
 */
export function ReleaseStepUpDialog({
  title,
  children,
  fields,
  confirmLabel,
  busyLabel = 'Verifying…',
  fallbackError,
  ready = true,
  onCancel,
  onConfirm,
}: {
  title: string
  /** What this action does, said before the credentials are asked for. */
  children: ReactNode
  /** The action's own inputs, if any (e.g. a file picker), above the step-up fields. */
  fields?: ReactNode
  /** Verb for the confirming button. Say what will happen, never just "OK". */
  confirmLabel: string
  busyLabel?: string
  /** Shown when the server refused without giving a reason, or the call never reached it. */
  fallbackError: string
  /** False holds the confirm button off until the action's own inputs are complete. */
  ready?: boolean
  onCancel: () => void
  /** Performs the action. A rejection keeps the dialog open with the reason inline. */
  onConfirm: (stepUp: { currentPassword: string; justification: string }) => Promise<void>
}) {
  const [password, setPassword] = useState('')
  const [justification, setJustification] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const titleId = useId()
  const justificationId = useId()
  const passwordId = useId()
  const container = useRef<HTMLDivElement>(null)

  useDialogDismiss(onCancel)
  useDialogFocus(container)

  // Belt and braces: whatever ends this component -- cancel, success, a route
  // change -- the typed password goes with it rather than lingering in state.
  useEffect(() => () => setPassword(''), [])

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setBusy(true)

    try {
      await onConfirm({ currentPassword: password, justification: justification.trim() })
      // The password is cleared the instant it has been used. On success the
      // caller unmounts this dialog; this covers the path where it does not.
      setPassword('')
    } catch (e) {
      // The server's refusal, verbatim: it names the rule that failed -- wrong
      // password, rate limited, a publish gate -- and never what was typed.
      setError(apiFailureMessage(e, fallbackError))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div
      ref={container}
      className="overlay"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
      // Focusable by script only: the dialog holds focus while it is open
      // without adding a stop to the page's tab order.
      tabIndex={-1}
    >
      <div className="dialog" style={{ maxWidth: 520 }}>
        <div className="dialog-header">
          <h2 id={titleId}>{title}</h2>
          <div className="sub">
            This action is recorded against your account with the reason you give below.
          </div>
        </div>

        <form onSubmit={submit}>
          <div className="dialog-body">
            <p className="muted" style={{ margin: '0 0 12px', fontSize: 13.5 }}>
              {children}
            </p>

            {fields}

            <div className="field">
              <label className="field-label" htmlFor={justificationId}>
                Why are you doing this?
              </label>
              <input
                id={justificationId}
                value={justification}
                onChange={(e) => setJustification(e.target.value)}
                placeholder="e.g. rolling out the 1.21.1 driver-install fix"
                autoComplete="off"
                maxLength={500}
                required
              />
            </div>

            <div className="field">
              <label className="field-label" htmlFor={passwordId}>
                Your password
              </label>
              <input
                id={passwordId}
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="current-password"
                maxLength={256}
                required
              />
            </div>

            {error && (
              <div className="error-banner" role="alert">
                <Icon name="alert" size={15} />
                <span>{error}</span>
              </div>
            )}
          </div>

          <div className="dialog-footer">
            <button type="button" className="btn-ghost" onClick={onCancel}>
              Cancel
            </button>
            <button
              type="submit"
              className="btn-primary"
              disabled={busy || !ready || !validateStepUpForm({ currentPassword: password, justification })}
            >
              {busy ? busyLabel : confirmLabel}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
