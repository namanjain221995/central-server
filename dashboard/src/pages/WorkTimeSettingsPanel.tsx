import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { ApiError, updateWorkTimeSettings, type WorkTimeSettings } from '../api/client'
import { useWorkTime } from '../components/WorkTimeContext'
import { Icon } from '../components/Icon'
import { clockOfMinutes, hoursMinutes, minutesOfClock, offsetLabel } from './workTimeView'

/** The editable draft — clock strings for the times, numbers for the rest. */
interface Draft {
  enabled: boolean
  shiftStart: string
  shiftEnd: string
  idleThresholdMinutes: number
  breakMinutes: number
  utcOffsetMinutes: number
}

function draftOf(s: WorkTimeSettings): Draft {
  return {
    enabled: s.enabled,
    shiftStart: clockOfMinutes(s.shiftStartMinutes),
    shiftEnd: clockOfMinutes(s.shiftEndMinutes),
    idleThresholdMinutes: s.idleThresholdMinutes,
    breakMinutes: s.breakMinutes,
    utcOffsetMinutes: s.utcOffsetMinutes,
  }
}

/**
 * Work-time configuration: the master on/off switch, the one shift everyone is
 * measured against, the idle threshold, the break allowance and the time zone.
 *
 * The switch is the one the whole feature hangs on — off, the server records
 * nothing and the Time Management page and its navigation entry disappear; on,
 * they return. Saving re-reads the shared flag so the navigation updates without
 * a refresh. Changing anything needs activity.configure; activity.view alone
 * sees the values read-only.
 */
export function WorkTimeSettingsPanel() {
  const { settings, loading, canConfigure, reload } = useWorkTime()

  const [draft, setDraft] = useState<Draft | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  // Seed (and re-seed) the draft from the live settings, so "dirty" is always
  // relative to what the server holds.
  useEffect(() => {
    if (settings) setDraft(draftOf(settings))
  }, [settings])

  if (loading && !settings) return <div className="loading">Loading work-time settings…</div>
  if (!settings || !draft) {
    return (
      <div className="card">
        <p className="muted">Work-time settings are unavailable.</p>
      </div>
    )
  }

  const startMin = minutesOfClock(draft.shiftStart)
  const endMin = minutesOfClock(draft.shiftEnd)
  const timesValid = startMin !== null && endMin !== null && startMin !== endMin
  const idleValid = draft.idleThresholdMinutes >= 1 && draft.idleThresholdMinutes <= 480
  const breakValid = draft.breakMinutes >= 0 && draft.breakMinutes <= 480
  const offsetValid = draft.utcOffsetMinutes >= -720 && draft.utcOffsetMinutes <= 840
  const valid = timesValid && idleValid && breakValid && offsetValid

  const dirty =
    draft.enabled !== settings.enabled ||
    startMin !== settings.shiftStartMinutes ||
    endMin !== settings.shiftEndMinutes ||
    draft.idleThresholdMinutes !== settings.idleThresholdMinutes ||
    draft.breakMinutes !== settings.breakMinutes ||
    draft.utcOffsetMinutes !== settings.utcOffsetMinutes

  const crossesMidnight = startMin !== null && endMin !== null && endMin <= startMin
  const lengthMinutes =
    startMin === null || endMin === null
      ? 0
      : crossesMidnight
        ? 1440 - startMin + endMin
        : endMin - startMin

  const saveProblem = !canConfigure
    ? 'You do not have permission to change these settings.'
    : !valid
      ? 'Check the highlighted values.'
      : !dirty
        ? 'No changes to save.'
        : null

  function set<K extends keyof Draft>(key: K, value: Draft[K]) {
    setDraft((d) => (d ? { ...d, [key]: value } : d))
    setNotice(null)
  }

  async function save() {
    if (startMin === null || endMin === null) return
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const wasEnabled = settings!.enabled
      await updateWorkTimeSettings({
        enabled: draft!.enabled,
        shiftStartMinutes: startMin,
        shiftEndMinutes: endMin,
        idleThresholdMinutes: draft!.idleThresholdMinutes,
        breakMinutes: draft!.breakMinutes,
        utcOffsetMinutes: draft!.utcOffsetMinutes,
      })
      await reload()
      setNotice(
        draft!.enabled === wasEnabled
          ? 'Saved.'
          : draft!.enabled
            ? 'Saved. Time Management is now on and recording — it appears in the navigation.'
            : 'Saved. Time Management is now off; recording has stopped and it is hidden from the navigation.',
      )
    } catch (cause) {
      setError(cause instanceof ApiError ? (cause.detail ?? cause.message) : 'The settings could not be saved.')
    } finally {
      setBusy(false)
    }
  }

  const readOnly = !canConfigure

  return (
    <>
      {error && (
        <div className="error-banner" role="alert"><Icon name="alert" size={15} /><span>{error}</span></div>
      )}
      {notice && (
        <div className="notice-banner" role="status"><Icon name="check" size={15} /><span>{notice}</span></div>
      )}

      <div className="page-header">
        <div className="lede">
          Measure working time from device activity — keyboard and mouse presence only, never
          content, apps or screenshots. One shift applies to everyone. Results appear under{' '}
          <Link to="/work-time">Time Management</Link> when this is on.
        </div>
      </div>

      <div className="card card-section glass-card">
        <div className="field full">
          <label className="check-row">
            <input
              type="checkbox"
              checked={draft.enabled}
              disabled={readOnly}
              onChange={(e) => set('enabled', e.target.checked)}
            />
            {' '}Enable Time Management
          </label>
          <span className="field-hint">
            {draft.enabled
              ? 'On: devices record activity presence and the Time Management report is available.'
              : 'Off: nothing is recorded, and Time Management is hidden from the navigation and its page.'}
          </span>
        </div>

        <div className="form-grid" style={{ opacity: draft.enabled ? 1 : 0.65 }}>
          <div className="field">
            <label className="field-label" htmlFor="wt-start">Shift start</label>
            <input id="wt-start" type="time" value={draft.shiftStart} disabled={readOnly}
              onChange={(e) => set('shiftStart', e.target.value)} />
          </div>
          <div className="field">
            <label className="field-label" htmlFor="wt-end">Shift end</label>
            <input id="wt-end" type="time" value={draft.shiftEnd} disabled={readOnly}
              onChange={(e) => set('shiftEnd', e.target.value)} />
            {!timesValid && <span className="field-message">Start and end must differ.</span>}
          </div>
          <div className="field">
            <label className="field-label" htmlFor="wt-idle">Idle threshold (minutes)</label>
            <input id="wt-idle" type="number" min={1} max={480} value={draft.idleThresholdMinutes}
              disabled={readOnly}
              onChange={(e) => set('idleThresholdMinutes', Number(e.target.value))} />
            {!idleValid && <span className="field-message">1 to 480 minutes.</span>}
            <span className="field-hint">No keyboard or mouse for this long counts as break.</span>
          </div>
          <div className="field">
            <label className="field-label" htmlFor="wt-break">Break allowance (minutes)</label>
            <input id="wt-break" type="number" min={0} max={480} value={draft.breakMinutes}
              disabled={readOnly}
              onChange={(e) => set('breakMinutes', Number(e.target.value))} />
            {!breakValid && <span className="field-message">0 to 480 minutes.</span>}
            <span className="field-hint">{hoursMinutes(draft.breakMinutes)} — subtracted from gross for the working target.</span>
          </div>
          <div className="field">
            <label className="field-label" htmlFor="wt-offset">Time zone (minutes from UTC)</label>
            <input id="wt-offset" type="number" min={-720} max={840} step={30} value={draft.utcOffsetMinutes}
              disabled={readOnly}
              onChange={(e) => set('utcOffsetMinutes', Number(e.target.value))} />
            {!offsetValid && <span className="field-message">−720 to 840.</span>}
            <span className="field-hint">{offsetLabel(draft.utcOffsetMinutes)} — 330 is IST. Shift times are in this zone.</span>
          </div>
        </div>

        {timesValid && (
          <p className="muted" style={{ fontSize: 12.5 }}>
            <Icon name="info" size={14} />{' '}
            Shift {draft.shiftStart}–{draft.shiftEnd}{crossesMidnight ? ' (crosses midnight)' : ''} ·
            gross {hoursMinutes(lengthMinutes)} · working target {hoursMinutes(Math.max(0, lengthMinutes - draft.breakMinutes))}
          </p>
        )}

        {!readOnly && (
          <div className="toolbar" style={{ marginTop: 12 }}>
            <span className="spacer" style={{ flex: 1 }} />
            <button
              type="button"
              className={`btn-accent${busy ? ' btn-loading' : ''}`}
              disabled={busy || saveProblem !== null}
              title={saveProblem ?? undefined}
              onClick={() => void save()}
            >
              Save
            </button>
          </div>
        )}
        {readOnly && (
          <p className="muted" style={{ marginTop: 12 }}>You can view these settings but not change them.</p>
        )}
      </div>
    </>
  )
}
