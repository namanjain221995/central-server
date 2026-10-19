import { useState } from 'react'
import { Icon } from '../components/Icon'
import { useTheme } from '../theme/ThemeContext'
import type { ThemePreference } from '../theme/theme'

interface Option {
  value: ThemePreference
  label: string
  description: string
}

const OPTIONS: Option[] = [
  { value: 'light', label: 'Light', description: 'Bright backgrounds with dark text.' },
  { value: 'dark', label: 'Dark', description: 'The navy console, easy on the eyes in low light.' },
  { value: 'system', label: 'System', description: 'Matches your device and switches when it does.' },
]

/** A miniature console drawn with one theme's real tokens (see .theme-scope-* in index.css). */
function MiniConsole({ theme }: { theme: 'light' | 'dark' }) {
  return (
    <div className={`theme-preview-pane theme-scope-${theme}`} aria-hidden="true">
      <div className="theme-preview-sidebar">
        <span className="theme-preview-nav active" />
        <span className="theme-preview-nav" />
        <span className="theme-preview-nav" />
      </div>
      <div className="theme-preview-main">
        <span className="theme-preview-bar" />
        <div className="theme-preview-card">
          <span className="theme-preview-line strong" />
          <span className="theme-preview-line" />
          <span className="theme-preview-dot" />
        </div>
      </div>
    </div>
  )
}

function Preview({ value }: { value: ThemePreference }) {
  if (value === 'system') {
    return (
      <div className="theme-preview split">
        <MiniConsole theme="light" />
        <MiniConsole theme="dark" />
      </div>
    )
  }
  return (
    <div className="theme-preview">
      <MiniConsole theme={value} />
    </div>
  )
}

/**
 * How the console looks for the signed-in administrator, on every device they
 * use. A choice applies the moment it is made and is saved to their account in
 * the background; there is no Save button to forget.
 *
 * Built on native radio buttons, so arrow keys move between the options and
 * screen readers announce a group of three with the current one selected.
 */
export function AppearancePanel() {
  const { preference, resolved, setPreference } = useTheme()
  const [status, setStatus] = useState<{ kind: 'saved' | 'error'; text: string } | null>(null)

  async function choose(next: ThemePreference) {
    if (next === preference) return
    setStatus(null)
    try {
      await setPreference(next)
      setStatus({ kind: 'saved', text: 'Saved to your account.' })
    } catch {
      setStatus({
        kind: 'error',
        text: 'Your choice could not be saved, so the previous theme was restored. Check your connection and try again.',
      })
    }
  }

  return (
    <div className="card card-section appearance-card">
      <h2 id="appearance-heading">Appearance</h2>
      <p className="muted appearance-lede">Customize how Techsara looks on your device.</p>

      <div className="appearance-options" role="radiogroup" aria-labelledby="appearance-heading">
        {OPTIONS.map((option) => {
          const selected = preference === option.value
          return (
            <label key={option.value} className={`appearance-option${selected ? ' selected' : ''}`}>
              <input
                type="radio"
                name="theme-preference"
                id={`theme-${option.value}`}
                value={option.value}
                checked={selected}
                onChange={() => void choose(option.value)}
                aria-describedby={`theme-${option.value}-description`}
              />
              <Preview value={option.value} />
              <span className="appearance-option-head">
                <span className="appearance-option-label">{option.label}</span>
                {selected && (
                  <span className="appearance-selected">
                    <Icon name="check" size={13} />
                    Selected
                  </span>
                )}
              </span>
              <span className="appearance-option-description" id={`theme-${option.value}-description`}>
                {option.description}
                {option.value === 'system' && selected && (
                  <> Your device is using {resolved === 'dark' ? 'dark' : 'light'} mode now.</>
                )}
              </span>
            </label>
          )
        })}
      </div>

      <p className={`appearance-status ${status?.kind ?? ''}`} role="status" aria-live="polite">
        {status && (
          <>
            <Icon name={status.kind === 'saved' ? 'check' : 'alert'} size={14} />
            {status.text}
          </>
        )}
      </p>
    </div>
  )
}
