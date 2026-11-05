import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  ApiError,
  downloadWorkTimeReportCsv,
  getGroups,
  getWorkTimeReport,
  type DeviceGroupSummary,
  type WorkTimeReport,
  type WorkTimeReportRow,
} from '../api/client'
import { useWorkTime } from '../components/WorkTimeContext'
import { Icon } from '../components/Icon'
import {
  clockOfMinutes,
  decimalHours,
  hoursMinutes,
  isoDaysAgo,
  isoDate,
  offsetLabel,
  shiftTargets,
} from './workTimeView'

/** Sort group → name → date so the table reads as a group-wise timesheet. */
function sortRows(rows: readonly WorkTimeReportRow[]): WorkTimeReportRow[] {
  return [...rows].sort(
    (a, b) =>
      a.groupName.localeCompare(b.groupName) ||
      a.userName.localeCompare(b.userName) ||
      a.shiftDate.localeCompare(b.shiftDate),
  )
}

interface Totals {
  present: number
  active: number
  idle: number
}

function totalsOf(rows: readonly WorkTimeReportRow[]): Totals {
  return rows.reduce(
    (sum, r) => ({
      present: sum.present + r.presentMinutes,
      active: sum.active + r.activeMinutes,
      idle: sum.idle + r.idleMinutes,
    }),
    { present: 0, active: 0, idle: 0 },
  )
}

/**
 * Time Management: how long each person's device was present, actively working
 * and idle during the shift, from keyboard/mouse activity presence only — never
 * content, apps or screenshots. Grouped by department, filterable by date range
 * and group, and exportable as CSV.
 *
 * Reachable from the navigation only while the feature is enabled in Settings;
 * opened directly while it is off, it says so and points at Settings.
 */
export function WorkTimePage() {
  const { enabled, canView, settings: liveSettings } = useWorkTime()

  const today = useMemo(() => new Date(), [])
  const [from, setFrom] = useState(() => isoDaysAgo(6, today))
  const [to, setTo] = useState(() => isoDate(today))
  const [groupId, setGroupId] = useState('')

  const [groups, setGroups] = useState<DeviceGroupSummary[]>([])
  const [report, setReport] = useState<WorkTimeReport | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const query = useMemo(
    () => ({ from, to, groupId: groupId || undefined }),
    [from, to, groupId],
  )

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setReport(await getWorkTimeReport(query))
    } catch (err) {
      setError(err instanceof ApiError ? (err.detail ?? err.message) : 'Could not load the report.')
    } finally {
      setLoading(false)
    }
  }, [query])

  useEffect(() => {
    if (!canView || !enabled) return
    void load()
  }, [canView, enabled, load])

  // The group filter is a convenience; a failed read just leaves it empty.
  useEffect(() => {
    if (!canView || !enabled) return
    let cancelled = false
    getGroups()
      .then((g) => { if (!cancelled) setGroups(g) })
      .catch(() => { /* ignored */ })
    return () => { cancelled = true }
  }, [canView, enabled])

  if (!canView) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to view work time.</p>
      </div>
    )
  }

  if (!enabled) {
    return (
      <div className="card glass-card">
        <div className="empty-state">
          <div className="icon" aria-hidden="true"><Icon name="clock" size={22} /></div>
          <div className="title">Time Management is turned off</div>
          <p>
            Activity tracking is not recording. Turn it on under{' '}
            <Link to="/settings?tab=work-time">Settings → Work time</Link> to start measuring
            working and idle time and to see this report.
          </p>
        </div>
      </div>
    )
  }

  const rows = report ? sortRows(report.rows) : []
  const totals = totalsOf(rows)
  const settings = report?.settings ?? liveSettings
  const targets = settings ? shiftTargets(settings) : null
  const invalidRange = from > to

  return (
    <>
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span style={{ flex: 1 }}>{error}</span>
          <button type="button" className="btn-ghost btn-sm" style={{ color: 'inherit' }} onClick={() => void load()}>
            Retry
          </button>
        </div>
      )}

      <div className="page-header">
        <div className="lede">
          Working time from device activity — keyboard and mouse presence only, never content,
          apps or screenshots. <strong>Idle time counts as break</strong>, and actual working time
          is the gross present time minus idle. Grouped by department.
        </div>
      </div>

      {settings && targets && (
        <div className="stat-grid">
          <div className="card stat-card glass-card">
            <div className="stat-label">Shift</div>
            <div className="stat-value" style={{ fontSize: 22 }}>
              {clockOfMinutes(settings.shiftStartMinutes)}–{clockOfMinutes(settings.shiftEndMinutes)}
            </div>
            <div className="stat-note">{offsetLabel(settings.utcOffsetMinutes)}</div>
          </div>
          <div className="card stat-card glass-card">
            <div className="stat-label">Gross hours</div>
            <div className="stat-value" style={{ fontSize: 22 }}>{hoursMinutes(targets.grossMinutes)}</div>
            <div className="stat-note">full shift window</div>
          </div>
          <div className="card stat-card glass-card">
            <div className="stat-label">Break</div>
            <div className="stat-value" style={{ fontSize: 22 }}>{hoursMinutes(targets.breakMinutes)}</div>
            <div className="stat-note">allowance</div>
          </div>
          <div className="card stat-card glass-card">
            <div className="stat-label">Working target</div>
            <div className="stat-value" style={{ fontSize: 22 }}>{hoursMinutes(targets.workingTargetMinutes)}</div>
            <div className="stat-note">gross − break</div>
          </div>
          <div className="card stat-card glass-card">
            <div className="stat-label">Idle after</div>
            <div className="stat-value" style={{ fontSize: 22 }}>{settings.idleThresholdMinutes} min</div>
            <div className="stat-note">no activity → break</div>
          </div>
        </div>
      )}

      <div className="card glass-card">
        <div className="section-head" style={{ flexWrap: 'wrap', gap: 10 }}>
          <div className="field" style={{ margin: 0 }}>
            <label className="field-label" htmlFor="wt-from">From</label>
            <input id="wt-from" type="date" value={from} max={to}
              onChange={(e) => setFrom(e.target.value)} />
          </div>
          <div className="field" style={{ margin: 0 }}>
            <label className="field-label" htmlFor="wt-to">To</label>
            <input id="wt-to" type="date" value={to} min={from}
              onChange={(e) => setTo(e.target.value)} />
          </div>
          <div className="field" style={{ margin: 0, minWidth: 180 }}>
            <label className="field-label" htmlFor="wt-group">Group</label>
            <select id="wt-group" value={groupId} onChange={(e) => setGroupId(e.target.value)}>
              <option value="">All groups</option>
              {groups.map((g) => (
                <option key={g.id} value={g.id}>{g.name}</option>
              ))}
            </select>
          </div>
          <span className="spacer" style={{ flex: 1 }} />
          <button type="button" className="btn-ghost btn-sm" onClick={() => void load()}
            disabled={loading || invalidRange}>
            <Icon name="refresh" size={14} /> Refresh
          </button>
          <button type="button" className="btn-primary btn-sm"
            onClick={() => downloadWorkTimeReportCsv(query)}
            disabled={invalidRange || rows.length === 0}
            title={rows.length === 0 ? 'Nothing to export' : undefined}>
            <Icon name="download" size={14} /> Download CSV
          </button>
        </div>

        {invalidRange && (
          <p className="field-message">The start date is after the end date.</p>
        )}

        {loading && !report && <div className="loading">Loading work time…</div>}

        {report && rows.length === 0 && !loading && (
          <div className="empty-state">
            <p>No activity recorded for this range. It can take a shift for data to appear after enabling.</p>
          </div>
        )}

        {rows.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <caption className="sr-only">Work time by person and shift date</caption>
              <thead>
                <tr>
                  <th scope="col">Date</th>
                  <th scope="col">Name</th>
                  <th scope="col">Group</th>
                  <th scope="col">Device</th>
                  <th scope="col" style={{ textAlign: 'right' }}>Gross</th>
                  <th scope="col" style={{ textAlign: 'right' }}>Working</th>
                  <th scope="col" style={{ textAlign: 'right' }}>Idle / break</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={`${r.deviceId}-${r.userName}-${r.shiftDate}`}>
                    <td style={{ whiteSpace: 'nowrap' }}>{r.shiftDate}</td>
                    <td>{r.userName}</td>
                    <td>{r.groupName}</td>
                    <td><Link to={`/devices/${r.deviceId}`}>{r.hostname}</Link></td>
                    <td style={{ textAlign: 'right' }} title={decimalHours(r.presentMinutes)}>
                      {hoursMinutes(r.presentMinutes)}
                    </td>
                    <td style={{ textAlign: 'right', fontWeight: 600 }} title={decimalHours(r.activeMinutes)}>
                      {hoursMinutes(r.activeMinutes)}
                    </td>
                    <td style={{ textAlign: 'right' }} title={decimalHours(r.idleMinutes)}>
                      {hoursMinutes(r.idleMinutes)}
                    </td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr>
                  <td colSpan={4} style={{ fontWeight: 600 }}>
                    Total · {rows.length} {rows.length === 1 ? 'row' : 'rows'}
                  </td>
                  <td style={{ textAlign: 'right', fontWeight: 600 }}>{hoursMinutes(totals.present)}</td>
                  <td style={{ textAlign: 'right', fontWeight: 600 }}>{hoursMinutes(totals.active)}</td>
                  <td style={{ textAlign: 'right', fontWeight: 600 }}>{hoursMinutes(totals.idle)}</td>
                </tr>
              </tfoot>
            </table>
          </div>
        )}

        {report?.truncated && (
          <p className="muted" style={{ fontSize: 12.5, marginTop: 10 }}>
            The range hit the row cap and was truncated. Narrow the dates or pick a group for a complete view.
          </p>
        )}
      </div>
    </>
  )
}
