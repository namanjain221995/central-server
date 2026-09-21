import { useCallback, useEffect, useId, useState } from 'react'
import { beginMfaEnrolment, confirmMfaEnrolment, type MfaEnrolmentStart } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { RecoveryCodes } from '../components/RecoveryCodes'

/**
 * The only screen an administrator sees until they have set up a second factor.
 *
 * Rendered INSTEAD of the application shell, like the forced password change, so
 * there is no route to it and nothing rendered behind it. The server refuses
 * every other endpoint for an un-enrolled account, so without this screen that
 * refusal would read as the console being broken.
 *
 * Three steps, kept on one screen: scan, confirm, then save the recovery codes.
 * The codes step is deliberately not skippable by navigation — they are shown
 * exactly once, and somebody who clicks past them has no way back.
 */
export function MfaEnrolmentPage() {
  const { user, refresh } = useAuth()
  const fieldId = useId()

  const [enrolment, setEnrolment] = useState<MfaEnrolmentStart | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null)
  const [showSecret, setShowSecret] = useState(false)

  const start = useCallback(async () => {
    setLoadError(null)
    try {
      setEnrolment(await beginMfaEnrolment())
    } catch (cause: unknown) {
      setLoadError(
        cause instanceof Error ? cause.message : 'Two-factor setup could not be started.',
      )
    }
  }, [])

  useEffect(() => {
    void start()
  }, [start])

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setError(null)
    setSubmitting(true)

    try {
      const result = await confirmMfaEnrolment(code)
      setRecoveryCodes(result.recoveryCodes)
    } catch (cause: unknown) {
      setError(cause instanceof Error ? cause.message : 'That code was not correct.')
      setCode('')
    } finally {
      setSubmitting(false)
    }
  }

  // Only after the codes have been acknowledged. refresh() re-reads /auth/me,
  // which now reports mfaEnrolmentRequired: false, and the gate in App falls
  // through to the console.
  async function finish() {
    await refresh()
  }

  if (recoveryCodes) {
    return (
      <div className="login-shell">
        <div className="card" style={{ maxWidth: 520 }}>
          <h2>Save your recovery codes</h2>
          <p className="lede">
            Two-factor authentication is on. These codes are the way back in if you lose
            your phone. Each one works once.
          </p>

          <RecoveryCodes codes={recoveryCodes} />

          <p className="field-hint" style={{ marginTop: 16 }}>
            This is the only time they are shown. Store them somewhere you can reach
            without this console — a password manager, or printed and locked away.
          </p>

          <button type="button" className="btn-primary" onClick={() => void finish()}>
            I have saved them, continue
          </button>
        </div>
      </div>
    )
  }

  return (
    <div className="login-shell">
      <form className="card" style={{ maxWidth: 520 }} onSubmit={(event) => void submit(event)}>
        <h2>Set up two-factor authentication</h2>

        <p className="lede">
          {user?.email} needs a second factor before continuing. Scan this with an
          authenticator app — Google Authenticator, Microsoft Authenticator, 1Password
          or any other — then enter the six-digit code it shows.
        </p>

        {loadError && (
          <div className="warn-banner" role="alert">
            {loadError}{' '}
            <button type="button" className="btn-link" onClick={() => void start()}>
              Try again
            </button>
          </div>
        )}

        {!enrolment && !loadError && <div className="loading">Preparing…</div>}

        {enrolment && (
          <>
            {/*
              The SVG is rendered by the server and inlined here rather than fetched
              from a URL, so the secret never becomes a cacheable resource with its
              own access-control question. It is server-generated markup for this
              session's own account, not user input.
            */}
            <div
              className="qr-frame"
              aria-label="Two-factor setup QR code"
              // eslint-disable-next-line react/no-danger
              dangerouslySetInnerHTML={{ __html: enrolment.qrCodeSvg }}
            />

            <p className="field-hint">
              Cannot scan it?{' '}
              <button
                type="button"
                className="btn-link"
                onClick={() => setShowSecret((shown) => !shown)}
              >
                {showSecret ? 'Hide the setup key' : 'Enter the setup key by hand'}
              </button>
            </p>

            {showSecret && (
              <p className="mono-block" aria-label="Setup key">
                {enrolment.secret}
              </p>
            )}

            {error && <div className="warn-banner" role="alert">{error}</div>}

            <div className="field">
              <label className="field-label" htmlFor={`${fieldId}-code`}>
                Six-digit code
              </label>
              <input
                id={`${fieldId}-code`}
                type="text"
                required
                inputMode="numeric"
                autoComplete="one-time-code"
                pattern="[0-9 ]*"
                maxLength={7}
                placeholder="000000"
                value={code}
                onChange={(event) => setCode(event.target.value)}
              />
            </div>

            <button type="submit" className="btn-primary" disabled={submitting || code.length < 6}>
              {submitting ? 'Checking…' : 'Turn on two-factor authentication'}
            </button>
          </>
        )}
      </form>
    </div>
  )
}
