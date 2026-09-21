import { useEffect, useId, useRef, useState } from 'react'
import type { GeneratedCredential } from '../api/client'
import { Icon } from '../components/Icon'
import { useDialogFocus } from '../components/dialogFocus'

interface Props {
  email: string
  credential: GeneratedCredential
  onClose: () => void
}

/**
 * Shows a reset password exactly once.
 *
 * Deliberately NOT dismissible with Escape or by clicking away: the account's
 * password has already been changed by the time this renders, so losing the value
 * here means the administrator is locked out until somebody resets it again.
 * Acknowledging is the only exit.
 */
export function ResetPasswordResultDialog({ email, credential, onClose }: Props) {
  const titleId = useId()
  const container = useRef<HTMLDivElement>(null)
  const [copied, setCopied] = useState(false)

  useDialogFocus(container)

  // Belt and braces: the value leaves memory when this unmounts, whatever route
  // the caller took to get rid of it.
  useEffect(() => () => setCopied(false), [])

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
        <h2 id={titleId}>New password for {email}</h2>

        <div className="warn-banner" role="alert">
          <strong>{credential.warning}</strong>
        </div>

        <div className="field">
          <span className="field-label">Temporary password</span>
          <code className="revealed-key">{credential.password}</code>
          <span className="field-hint">
            Their previous password no longer works and every session they held has been
            signed out. They must choose a new password the first time they sign in.
          </span>
        </div>

        <div className="btn-row">
          <button
            type="button"
            className="btn-ghost"
            onClick={() => {
              void navigator.clipboard?.writeText(credential.password).then(
                () => setCopied(true),
                () => setCopied(false),
              )
            }}
          >
            <Icon name="key" size={14} />
            {copied ? 'Copied' : 'Copy password'}
          </button>

          <span className="spacer" />

          <button type="button" className="btn-primary" onClick={onClose}>
            I have saved it
          </button>
        </div>
      </div>
    </div>
  )
}
