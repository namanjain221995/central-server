import { useState } from 'react'

/**
 * Displays a set of single-use recovery codes, with the two things people
 * actually do with them: copy, and print.
 *
 * Shared by enrolment and by the Settings panel that regenerates them, so the
 * "shown exactly once" presentation is identical in both places rather than
 * drifting into two slightly different warnings.
 */
export function RecoveryCodes({ codes }: { codes: string[] }) {
  const [copied, setCopied] = useState(false)

  async function copy() {
    try {
      await navigator.clipboard.writeText(codes.join('\n'))
      setCopied(true)
      window.setTimeout(() => setCopied(false), 2000)
    } catch {
      // Clipboard access is refused in some browsers and over plain HTTP. The
      // codes are on screen and selectable, so this is not worth an error banner.
      setCopied(false)
    }
  }

  return (
    <div className="recovery-codes">
      <ol className="recovery-codes-list">
        {codes.map((code) => (
          <li key={code}>
            <code>{code}</code>
          </li>
        ))}
      </ol>

      <div className="recovery-codes-actions">
        <button type="button" className="btn-ghost" onClick={() => void copy()}>
          {copied ? 'Copied' : 'Copy all'}
        </button>
        <button type="button" className="btn-ghost" onClick={() => window.print()}>
          Print
        </button>
      </div>
    </div>
  )
}
