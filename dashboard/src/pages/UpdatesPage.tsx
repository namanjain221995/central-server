import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { getUpdateOverview, type DeviceUpdateSummaryRow, type UpdateOverview } from '../api/client'
import { Icon } from '../components/Icon'

/** The summary's counts as filters (?show=), so each tile opens the devices it counts. */
type UpdateFilter = '' | 'reboot' | 'failed'

const UPDATE_FILTERS: { value: UpdateFilter; label: string }[] = [
  { value: '', label: 'All devices' },
  { value: 'reboot', label: 'Reboot pending' },
  { value: 'failed', label: 'With failed updates' },
]

function updateFilterFrom(value: string | null): UpdateFilter {
  return UPDATE_FILTERS.find((f) => f.value !== '' && f.value === value)?.value ?? ''
}

function matchesUpdateFilter(device: DeviceUpdateSummaryRow, filter: UpdateFilter): boolean {
  switch (filter) {
    case 'reboot':
      return device.rebootRequired
    case 'failed':
      return device.failedUpdateCount > 0
    default:
      return true
  }
}

export function UpdatesPage() {
  const [data, setData] = useState<UpdateOverview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [searchParams, setSearchParams] = useSearchParams()
  const filter = updateFilterFrom(searchParams.get('show'))
  const showOnly = (next: UpdateFilter) => setSearchParams(next ? { show: next } : {}, { replace: true })

  useEffect(() => {
    getUpdateOverview()
      .then(setData)
      .catch(() => setError('Could not load update status.'))
  }, [])

  if (error) {
    return (
      <div className="error-banner" role="alert">
        <Icon name="alert" size={15} />
        <span>{error}</span>
      </div>
    )
  }
  if (!data) return <div className="loading">Loading update status…</div>

  const { summary } = data
  const devices = data.devices.filter((d) => matchesUpdateFilter(d, filter))

  return (
    <>
      <div className="page-header">
        <div className="lede">
          Windows Update visibility across the fleet. The platform reads each device's local update
          history and reboot-pending state during inventory — it reports, and never silently
          installs or reboots.
        </div>
      </div>

      <div className="stat-grid">
        <Link to="/updates" className="card stat-card" replace>
          <div className="stat-label">Devices reporting</div>
          <div className="stat-value">{summary.devicesReporting}</div>
        </Link>
        <Link
          to="/updates?show=reboot"
          className={`card stat-card${summary.rebootPending > 0 ? ' tone-warn' : ''}`}
          replace
        >
          <div className="stat-label">Reboot pending</div>
          <div className="stat-value">{summary.rebootPending}</div>
        </Link>
        <Link
          to="/updates?show=failed"
          className={`card stat-card${summary.withFailedUpdates > 0 ? ' tone-crit' : ''}`}
          replace
        >
          <div className="stat-label">With failed updates</div>
          <div className="stat-value">{summary.withFailedUpdates}</div>
        </Link>
      </div>

      <div className="card">
        <div className="section-head">
          <h2>Per-device status</h2>
          <select
            aria-label="Filter devices by update state"
            style={{ width: 'auto', minWidth: 170 }}
            value={filter}
            onChange={(e) => showOnly(e.target.value as UpdateFilter)}
          >
            {UPDATE_FILTERS.map((f) => (
              <option key={f.value} value={f.value}>
                {f.label}
              </option>
            ))}
          </select>
        </div>
        {data.devices.length === 0 && (
          <div className="empty-state">
            <Icon name="updates" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No update data yet</div>
            <div>Devices report Windows Update history on their next inventory cycle.</div>
          </div>
        )}
        {data.devices.length > 0 && devices.length === 0 && (
          <div className="empty-state">
            <div className="title">No devices match this filter</div>
            <div>
              <button type="button" className="link-button" onClick={() => showOnly('')}>
                Show all devices
              </button>
            </div>
          </div>
        )}
        {devices.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Device</th>
                  <th>Reboot pending</th>
                  <th>Failed updates</th>
                  <th>Reported</th>
                </tr>
              </thead>
              <tbody>
                {devices.map((d) => (
                  <tr key={d.deviceId}>
                    <td>
                      <Link to={`/devices/${d.deviceId}`}>{d.hostname}</Link>
                    </td>
                    <td>
                      {d.rebootRequired ? (
                        <span className="badge warn">Yes</span>
                      ) : (
                        <span className="badge neutral">No</span>
                      )}
                    </td>
                    <td>
                      <span
                        className={`badge plain ${d.failedUpdateCount > 0 ? 'crit' : 'neutral'}`}
                      >
                        {d.failedUpdateCount}
                      </span>
                    </td>
                    <td className="muted">{new Date(d.collectedAt).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  )
}
