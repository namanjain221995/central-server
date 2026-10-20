import { useCallback, useEffect, useState, type FormEvent } from 'react'
import {
  ApiError,
  deviceName,
  startRemoteSupportSession,
  type RemoteSupportDeviceRow,
} from '../api/client'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'
import {
  beginSession,
  endSession,
  noSession,
  validateConnectForm,
  type RemoteSessionReveal,
} from './remoteSupportView'

/**
 * Starts an authorized remote-support session after step-up authentication, then
 * reveals the device's RustDesk id and a one-time password.
 *
 * Nothing is fetched when this opens. The credentials are requested only when an
 * administrator submits their own password and a justification, and are held in
 * component state alone -- never in storage, a URL or router state.
 *
 * They stay on screen until the operator presses Done, and are dropped then, or
 * when the dialog unmounts for any other reason. There is deliberately no timer:
 * an operator typing an id and password into RustDesk, or reading them to a
 * colleague, takes longer than a countdown allows.
 *
 * The password is **never** placed in the rustdesk:// link. Opening RustDesk
 * carries only the id; the password is shown separately for the operator to
 * enter, so it never reaches browser history, a proxy log or a Referer header.
 */
export function RemoteSessionDialog({
  device,
  onClose,
}: {
  device: RemoteSupportDeviceRow
  onClose: () => void
}) {
  // The operator's own password, for step-up. Distinct from the one-time session
  // password the server returns, which the operator never types.
  const [password, setPassword] = useState('')
  const [justification, setJustification] = useState('')
  const [session, setSession] = useState<RemoteSessionReveal>(noSession)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [copied, setCopied] = useState<'id' | 'password' | null>(null)

  useDialogDismiss(onClose)

  const forget = useCallback(() => {
    setSession(endSession())
    setCopied(null)
  }, [])

  // Whatever ends this component -- closing, navigating, a route change -- the
  // credentials do not outlive it. There is deliberately no timer.
  useEffect(() => () => forget(), [forget])

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setBusy(true)

    try {
      const result = await startRemoteSupportSession(device.deviceId, password, justification.trim())

      // The step-up password is cleared the instant it has been used.
      setPassword('')
      setSession(beginSession(result.rustDeskId, result.password))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'The remote session could not be started.')
    } finally {
      setBusy(false)
    }
  }

  function copy(which: 'id' | 'password', value: string) {
    void navigator.clipboard?.writeText(value).then(() => setCopied(which))
  }

  const revealed = session.value

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-labelledby="remote-session-title">
      <div className="dialog" style={{ maxWidth: 'min(560px, 100%)' }}>
        <div className="dialog-header">
          <h2 id="remote-session-title">Connect to {deviceName(device)}</h2>
          <div className="sub">
            {revealed
              ? 'Open RustDesk with these one-time credentials. The device shows RustDesk’s own connection indicator while you are connected.'
              : 'Starting a remote session is recorded against your account with the reason you give below.'}
          </div>
        </div>

        {!revealed ? (
          <form onSubmit={submit}>
            <div className="dialog-body">
              <div className="field">
                <label className="field-label" htmlFor="remote-justification">
                  Why do you need to connect?
                </label>
                <input
                  id="remote-justification"
                  value={justification}
                  onChange={(e) => setJustification(e.target.value)}
                  placeholder="e.g. helping with a printer driver on this PC"
                  autoComplete="off"
                  required
                />
              </div>

              <div className="field">
                <label className="field-label" htmlFor="remote-password">
                  Your password
                </label>
                <input
                  id="remote-password"
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  autoComplete="current-password"
                  required
                />
              </div>

              {error && (
                <div className="error-banner">
                  <Icon name="alert" size={15} />
                  <span>{error}</span>
                </div>
              )}
            </div>

            <div className="dialog-footer">
              <button type="button" className="btn-ghost" onClick={onClose}>
                Cancel
              </button>
              <button
                type="submit"
                className="btn-primary"
                disabled={busy || !validateConnectForm({ currentPassword: password, justification })}
              >
                {busy ? 'Verifying…' : 'Connect'}
              </button>
            </div>
          </form>
        ) : (
          <>
            <div className="dialog-body">
              <div className="notice-banner" role="status">
                <Icon name="info" size={15} />
                <span>
                  These credentials stay on screen until you press Done. The password is one-time and
                  is never put in the RustDesk link.
                </span>
              </div>

              <div className="field">
                <span className="field-label">RustDesk ID</span>
                <code className="revealed-key">{revealed.rustDeskId}</code>
              </div>

              <div className="field">
                <span className="field-label">One-time password</span>
                <code className="revealed-key">{revealed.password}</code>
              </div>
            </div>

            <div className="dialog-footer">
              <button type="button" className="btn-ghost" onClick={() => copy('id', revealed.rustDeskId)}>
                {copied === 'id' ? 'Copied ID' : 'Copy ID'}
              </button>
              <button
                type="button"
                className="btn-ghost"
                onClick={() => copy('password', revealed.password)}
              >
                {copied === 'password' ? 'Copied password' : 'Copy password'}
              </button>
              {/* A real anchor, not a button: the browser hands rustdesk:// to the
                  OS protocol handler, which a button's onClick cannot do. Only the
                  id travels in the link; the password never does. The button
                  metrics are set inline because .btn-primary alone carries colour,
                  not layout, and this is an <a>, not a <button>. */}
              <a
                className="btn-primary"
                href={`rustdesk://connect/${encodeURIComponent(revealed.rustDeskId)}`}
                style={{
                  display: 'inline-flex',
                  alignItems: 'center',
                  justifyContent: 'center',
                  gap: 7,
                  height: 'var(--control-height)',
                  padding: '0 14px',
                  borderRadius: 'var(--radius)',
                  textDecoration: 'none',
                }}
              >
                <Icon name="remote" size={15} />
                Open RustDesk
              </a>
              <button
                type="button"
                className="btn-ghost"
                onClick={() => {
                  forget()
                  onClose()
                }}
              >
                Done
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}
