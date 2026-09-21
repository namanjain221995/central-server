import { useId, useState } from 'react'
import { changeAdminPassword } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'

/**
 * The only screen a new administrator sees until they replace the password the
 * server generated for them.
 *
 * Rendered INSTEAD of the whole application shell, not as a dialog over it, so
 * there is no route to navigate to and no page rendered behind it. The server
 * refuses every other endpoint for this account anyway — this screen exists so
 * that refusal reads as an instruction rather than as the console being broken.
 *
 * Changing the password ends the session: it rotates the security stamp and
 * revokes every session row, which is the documented contract of that endpoint.
 * So this signs out and returns to the sign-in screen rather than pretending the
 * session survived.
 */
export function ForcedPasswordChangePage() {
  const { user, logout } = useAuth()
  const fieldId = useId()

  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [done, setDone] = useState(false)

  const mismatch = confirmPassword.length > 0 && newPassword !== confirmPassword

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setError(null)

    if (newPassword !== confirmPassword) {
      setError('The two new passwords do not match.')
      return
    }

    setSubmitting(true)
    try {
      await changeAdminPassword({ currentPassword, newPassword, confirmPassword })
      setDone(true)
    } catch (cause: unknown) {
      setError(cause instanceof Error ? cause.message : 'The password could not be changed.')
      setSubmitting(false)
    }
  }

  if (done) {
    return (
      <div className="login-shell">
        <div className="card" style={{ maxWidth: 420 }}>
          <h2>Password changed</h2>
          <p className="lede">
            Every session was signed out, including this one. Sign in again with your new
            password to continue.
          </p>
          <button type="button" className="btn-primary" onClick={() => void logout()}>
            Go to sign in
          </button>
        </div>
      </div>
    )
  }

  return (
    <div className="login-shell">
      <form className="card" style={{ maxWidth: 420 }} onSubmit={(event) => void submit(event)}>
        <h2>Choose your password</h2>

        <p className="lede">
          {user?.email} was created with a temporary password. Choose your own before
          continuing — until you do, the platform will refuse every other request from
          this account.
        </p>

        {error && <div className="warn-banner" role="alert">{error}</div>}

        <div className="field">
          <label className="field-label" htmlFor={`${fieldId}-current`}>Temporary password</label>
          <input
            id={`${fieldId}-current`}
            type="password"
            required
            autoComplete="current-password"
            value={currentPassword}
            onChange={(event) => setCurrentPassword(event.target.value)}
          />
          <span className="field-hint">The one you were given. Paste it if you copied it.</span>
        </div>

        <div className="field">
          <label className="field-label" htmlFor={`${fieldId}-new`}>New password</label>
          <input
            id={`${fieldId}-new`}
            type="password"
            required
            minLength={12}
            autoComplete="new-password"
            value={newPassword}
            onChange={(event) => setNewPassword(event.target.value)}
          />
          <span className="field-hint">At least 12 characters.</span>
        </div>

        <div className="field">
          <label className="field-label" htmlFor={`${fieldId}-confirm`}>Confirm new password</label>
          <input
            id={`${fieldId}-confirm`}
            type="password"
            required
            autoComplete="new-password"
            aria-invalid={mismatch}
            aria-describedby={mismatch ? `${fieldId}-mismatch` : undefined}
            value={confirmPassword}
            onChange={(event) => setConfirmPassword(event.target.value)}
          />
          {mismatch && (
            <span className="field-message" id={`${fieldId}-mismatch`} role="alert">
              The two new passwords do not match.
            </span>
          )}
        </div>

        <div className="btn-row">
          <button type="button" className="btn-ghost" onClick={() => void logout()} disabled={submitting}>
            <Icon name="logout" size={14} />
            Sign out
          </button>
          <span className="spacer" />
          <button
            type="submit"
            className={submitting ? 'btn-primary btn-loading' : 'btn-primary'}
            disabled={submitting || mismatch}
          >
            Change password
          </button>
        </div>
      </form>
    </div>
  )
}
