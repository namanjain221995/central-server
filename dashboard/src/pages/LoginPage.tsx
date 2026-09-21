import { useState, type FormEvent } from 'react'
import { ApiError, type MfaChallenge } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'

export function LoginPage() {
  const { login } = useAuth()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [challenge, setChallenge] = useState<MfaChallenge | null>(null)

  async function onSubmit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const pending = await login(email, password)

      if (pending) {
        // The password was right and a code is owed. The password is dropped
        // here: it is not needed again, and keeping it in state for the length
        // of the code prompt is a needless second copy.
        setPassword('')
        setChallenge(pending)
      }
    } catch (e) {
      // Deliberately the same message for a wrong address and a wrong password:
      // distinguishing them would confirm which accounts exist.
      setError(
        e instanceof ApiError && e.status === 429
          ? 'Too many sign-in attempts. Wait a minute and try again.'
          : 'Sign-in failed. Check your email address and password.',
      )
    } finally {
      setBusy(false)
    }
  }

  if (challenge) {
    return (
      <MfaChallengeStep
        challenge={challenge}
        onStartOver={() => {
          setChallenge(null)
          setError(null)
        }}
      />
    )
  }

  return (
    <div className="login-shell">
      <form onSubmit={(e) => void onSubmit(e)} className="card login-card">
        <div className="login-brand">
          <div className="mark">
            <Icon name="shield-check" size={22} strokeWidth={2} />
          </div>
          <div className="name">Endpoint Platform</div>
          <div className="sub">Sign in to continue</div>
        </div>

        {error && (
          // Announced on appearance: a keyboard user who submitted and stayed on
          // the button would otherwise get no indication the attempt failed.
          <div className="error-banner" role="alert">
            <Icon name="alert" size={15} />
            <span>{error}</span>
          </div>
        )}

        <div className="field">
          <label className="field-label" htmlFor="login-email">
            Email
          </label>
          <input
            id="login-email"
            type="email"
            autoComplete="username"
            autoFocus
            required
            value={email}
            onChange={(e) => setEmail(e.target.value)}
          />
        </div>

        <div className="field">
          <label className="field-label" htmlFor="login-password">
            Password
          </label>
          <input
            id="login-password"
            type="password"
            autoComplete="current-password"
            required
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </div>

        <button
          type="submit"
          className={`btn-primary${busy ? ' btn-loading' : ''}`}
          disabled={busy}
        >
          {busy ? 'Signing in…' : 'Sign in'}
        </button>

        <div className="login-foot">Authorized administrators only. Access is audited.</div>
      </form>
    </div>
  )
}

/**
 * The second step of sign-in: the code from the authenticator app.
 *
 * A separate component with its own state, so the password form's values cannot
 * linger behind it. It accepts a recovery code in the same box — people reach
 * for one when their phone is missing, and a separate "use a recovery code"
 * screen is one more thing to find at the worst moment. The server decides which
 * kind it is, and only tries a recovery code when the input is not six digits,
 * so a mistyped code never burns one.
 */
function MfaChallengeStep({
  challenge,
  onStartOver,
}: {
  challenge: MfaChallenge
  onStartOver: () => void
}) {
  const { verifyMfa } = useAuth()
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function onSubmit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await verifyMfa(challenge.challengeToken, code)
    } catch (e) {
      // One message for every failure. The server does not say whether the code
      // was wrong, already used, or the challenge expired, and neither does this.
      setError(
        e instanceof ApiError && e.status === 429
          ? 'Too many attempts. Wait a minute and try again.'
          : 'That code was not accepted. Try the current code, or a recovery code.',
      )
      setCode('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-shell">
      <form onSubmit={(e) => void onSubmit(e)} className="card login-card">
        <div className="login-brand">
          <div className="mark">
            <Icon name="shield-check" size={22} strokeWidth={2} />
          </div>
          <div className="name">Two-factor authentication</div>
          <div className="sub">Enter the code from your authenticator app</div>
        </div>

        {error && (
          <div className="error-banner" role="alert">
            <Icon name="alert" size={15} />
            <span>{error}</span>
          </div>
        )}

        <div className="field">
          <label className="field-label" htmlFor="login-mfa-code">
            Code
          </label>
          <input
            id="login-mfa-code"
            type="text"
            inputMode="numeric"
            autoComplete="one-time-code"
            autoFocus
            required
            maxLength={20}
            placeholder="000000"
            value={code}
            onChange={(e) => setCode(e.target.value)}
          />
          <p className="field-hint">Lost your phone? Enter one of your recovery codes instead.</p>
        </div>

        <button
          type="submit"
          className={`btn-primary${busy ? ' btn-loading' : ''}`}
          disabled={busy || code.trim().length === 0}
        >
          {busy ? 'Checking…' : 'Verify'}
        </button>

        <button type="button" className="btn-link" onClick={onStartOver}>
          Start over
        </button>

        <div className="login-foot">Authorized administrators only. Access is audited.</div>
      </form>
    </div>
  )
}
