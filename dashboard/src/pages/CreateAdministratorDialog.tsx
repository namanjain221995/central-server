import { useEffect, useId, useRef, useState } from 'react'
import {
  createPlatformUser,
  getAccessLevels,
  type AccessLevel,
  type GeneratedCredential,
} from '../api/client'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'
import { useDialogFocus } from '../components/dialogFocus'

interface Props {
  onClose: () => void
  onCreated: () => void
}

/**
 * Creates an administrator and shows their password exactly once.
 *
 * Two steps in one dialog, deliberately. The generated password only exists in
 * this response, so the form must not be dismissible into a state where the
 * account exists and nobody knows its password — the reveal step replaces the
 * form rather than appearing beside it, and closing is only offered once the
 * password has been acknowledged.
 */
export function CreateAdministratorDialog({ onClose, onCreated }: Props) {
  const titleId = useId()
  const container = useRef<HTMLDivElement>(null)

  const [levels, setLevels] = useState<AccessLevel[]>([])
  const [email, setEmail] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [roleKey, setRoleKey] = useState('')
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [credential, setCredential] = useState<GeneratedCredential | null>(null)
  const [copied, setCopied] = useState(false)

  // Escape closes only while the password is not on screen. Once it is, the only
  // way out is the Done button, which states that it cannot be shown again.
  useDialogDismiss(credential ? () => {} : onClose)
  useDialogFocus(container)

  useEffect(() => {
    let cancelled = false
    getAccessLevels()
      .then((response) => {
        if (cancelled) return
        setLevels(response.accessLevels)
        // Least privilege first: the lowest level is preselected so creating an
        // over-powered account is a deliberate choice rather than the default.
        setRoleKey((current) => current || response.accessLevels.at(-1)?.key || '')
      })
      .catch(() => {
        if (!cancelled) setError('Could not load access levels.')
      })
    return () => {
      cancelled = true
    }
  }, [])

  // The password dies with the dialog.
  useEffect(() => () => setCredential(null), [])

  async function submit(event: React.FormEvent) {
    event.preventDefault()
    setError(null)
    setSubmitting(true)

    try {
      const result = await createPlatformUser({
        email: email.trim(),
        displayName: displayName.trim(),
        roleKey,
      })
      setCredential(result)
      // The list refreshes now rather than on close, so the new row is already
      // there when the password is acknowledged.
      onCreated()
    } catch (cause: unknown) {
      setError(cause instanceof Error ? cause.message : 'The administrator could not be created.')
    } finally {
      setSubmitting(false)
    }
  }

  const selectedLevel = levels.find((level) => level.key === roleKey)

  return (
    <div className="overlay" tabIndex={-1}>
      <div
        className="dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        ref={container}
        style={{ maxWidth: 560 }}
      >
        {credential ? (
          <>
            <h2 id={titleId}>Administrator created</h2>

            <div className="warn-banner" role="alert">
              <strong>{credential.warning}</strong>
            </div>

            <div className="field">
              <span className="field-label">Temporary password</span>
              <code className="revealed-key">{credential.password}</code>
              <span className="field-hint">
                They will be asked to choose their own password the first time they sign in,
                and cannot use the console until they do.
              </span>
            </div>

            <div className="btn-row">
              <button
                type="button"
                className="btn-ghost"
                onClick={() => {
                  void navigator.clipboard?.writeText(credential.password).then(
                    () => setCopied(true),
                    // A refused clipboard is not an error worth a banner; the value
                    // is on screen and can be selected by hand.
                    () => setCopied(false),
                  )
                }}
              >
                <Icon name="key" size={14} />
                {copied ? 'Copied' : 'Copy password'}
              </button>

              <span className="spacer" />

              <button
                type="button"
                className="btn-primary"
                onClick={() => {
                  setCredential(null)
                  onClose()
                }}
              >
                I have saved it
              </button>
            </div>
          </>
        ) : (
          <form onSubmit={(event) => void submit(event)}>
            <h2 id={titleId}>New administrator</h2>

            {error && <div className="warn-banner" role="alert">{error}</div>}

            <div className="field">
              <label className="field-label" htmlFor={`${titleId}-email`}>E-mail address</label>
              <input
                id={`${titleId}-email`}
                type="email"
                required
                autoComplete="off"
                value={email}
                onChange={(event) => setEmail(event.target.value)}
              />
              <span className="field-hint">
                They sign in with this. It must not already belong to an administrator.
              </span>
            </div>

            <div className="field">
              <label className="field-label" htmlFor={`${titleId}-name`}>Display name</label>
              <input
                id={`${titleId}-name`}
                type="text"
                required
                value={displayName}
                onChange={(event) => setDisplayName(event.target.value)}
              />
            </div>

            <div className="field">
              <label className="field-label" htmlFor={`${titleId}-role`}>Access level</label>
              <select
                id={`${titleId}-role`}
                value={roleKey}
                onChange={(event) => setRoleKey(event.target.value)}
                required
              >
                {levels.map((level) => (
                  <option key={level.key} value={level.key}>{level.displayName}</option>
                ))}
              </select>
              {selectedLevel && (
                <span className="field-hint">{selectedLevel.description}</span>
              )}
            </div>

            {selectedLevel?.holdsEveryPermission && (
              <div className="warn-banner" role="status">
                This level holds <strong>every permission</strong>, including managing other
                administrators and reading BitLocker recovery keys.
              </div>
            )}

            <div className="field">
              <span className="field-hint">
                The server generates the first password and shows it once. You will not be
                able to retrieve it afterwards — only reset it.
              </span>
            </div>

            <div className="btn-row">
              <button type="button" className="btn-ghost" onClick={onClose} disabled={submitting}>
                Cancel
              </button>
              <span className="spacer" />
              <button
                type="submit"
                className={submitting ? 'btn-primary btn-loading' : 'btn-primary'}
                disabled={submitting || !roleKey}
              >
                Create administrator
              </button>
            </div>
          </form>
        )}
      </div>
    </div>
  )
}
