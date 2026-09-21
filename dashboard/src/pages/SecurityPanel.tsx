import { useState } from 'react'
import { regenerateRecoveryCodes } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { RecoveryCodes } from '../components/RecoveryCodes'

/**
 * The signed-in administrator's own security settings.
 *
 * Only their own — resetting somebody else's second factor lives in the
 * Administrators panel behind <code>platform.user.manage</code>, because it is an
 * administrative act over another person and this panel is not gated at all.
 *
 * Two-factor authentication cannot be turned off here, and that is the point of
 * making it mandatory. The only actions are replacing the recovery codes and
 * reading how many are left.
 */
export function SecurityPanel() {
  const { user } = useAuth()

  const [codes, setCodes] = useState<string[] | null>(null)
  const [confirming, setConfirming] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function regenerate() {
    setBusy(true)
    setError(null)
    try {
      const result = await regenerateRecoveryCodes()
      setCodes(result.recoveryCodes)
      setConfirming(false)
    } catch (cause: unknown) {
      setError(
        cause instanceof Error ? cause.message : 'The recovery codes could not be replaced.',
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="panel">
      <h3>Two-factor authentication</h3>

      <p className="lede">
        <Icon name="shield-check" size={15} /> On for {user?.email}. It is required for
        every administrator and cannot be switched off.
      </p>

      <h4 style={{ marginTop: 24 }}>Recovery codes</h4>

      <p className="field-hint">
        Single-use codes for signing in when you do not have your phone. Replacing them
        invalidates every code issued before — do it if you think the old set has been
        seen, or if you are running low.
      </p>

      {error && <div className="warn-banner" role="alert">{error}</div>}

      {codes ? (
        <>
          <RecoveryCodes codes={codes} />
          <p className="field-hint">
            Your previous codes no longer work. This is the only time the new ones are
            shown.
          </p>
        </>
      ) : confirming ? (
        <div className="warn-banner">
          <p>
            Replacing the codes invalidates all of your existing ones immediately. If you
            have them written down somewhere, that copy will stop working.
          </p>
          <div className="btn-row">
            <button
              type="button"
              className="btn-primary"
              disabled={busy}
              onClick={() => void regenerate()}
            >
              {busy ? 'Replacing…' : 'Replace them'}
            </button>
            <button type="button" className="btn-ghost" onClick={() => setConfirming(false)}>
              Cancel
            </button>
          </div>
        </div>
      ) : (
        <button type="button" className="btn-ghost" onClick={() => setConfirming(true)}>
          Generate new recovery codes
        </button>
      )}
    </div>
  )
}
