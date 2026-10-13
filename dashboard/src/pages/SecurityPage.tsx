import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { getSecurityOverview, type DeviceSecuritySummary, type SecurityOverview } from '../api/client'
import { Icon } from '../components/Icon'

/**
 * The bands the summary counts, as filters (?show=). "attention" is everything
 * below healthy — needs attention and critical together — which is what the
 * dashboard's Needs Attention tile adds up.
 */
type PostureFilter = '' | 'healthy' | 'attention' | 'critical'

const POSTURE_FILTERS: { value: PostureFilter; label: string }[] = [
  { value: '', label: 'All devices' },
  { value: 'attention', label: 'Needs attention (<80%)' },
  { value: 'critical', label: 'Critical (<50%)' },
  { value: 'healthy', label: 'Healthy (≥80%)' },
]

function postureFilterFrom(value: string | null): PostureFilter {
  return POSTURE_FILTERS.find((f) => f.value !== '' && f.value === value)?.value ?? ''
}

function matchesPosture(device: DeviceSecuritySummary, filter: PostureFilter): boolean {
  const score = device.complianceScore
  switch (filter) {
    case 'healthy':
      return score != null && score >= 80
    case 'attention':
      return score != null && score < 80
    case 'critical':
      return score != null && score < 50
    default:
      return true
  }
}

function scoreBadge(score: number | null) {
  if (score == null) return <span className="badge neutral">Unknown</span>
  const cls = score >= 80 ? 'badge ok' : score >= 50 ? 'badge warn' : 'badge crit'
  return <span className={cls}>{score}%</span>
}

/** Pass/Fail rather than a tick or a colour — readable in greyscale and aloud. */
function checkCell(value: boolean | null) {
  if (value == null) return <span className="muted">—</span>
  return value ? <span className="badge ok">Pass</span> : <span className="badge crit">Fail</span>
}

export function SecurityPage() {
  const [data, setData] = useState<SecurityOverview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [searchParams, setSearchParams] = useSearchParams()
  const filter = postureFilterFrom(searchParams.get('show'))
  const showOnly = (next: PostureFilter) => setSearchParams(next ? { show: next } : {}, { replace: true })

  useEffect(() => {
    getSecurityOverview()
      .then(setData)
      .catch(() => setError('Could not load security overview.'))
  }, [])

  if (error) {
    return (
      <div className="error-banner" role="alert">
        <Icon name="alert" size={15} />
        <span>{error}</span>
      </div>
    )
  }
  if (!data) return <div className="loading">Loading security overview…</div>

  const s = data.summary
  const devices = data.devices.filter((d) => matchesPosture(d, filter))

  return (
    <>
      <div className="stat-grid">
        <Link to="/security" className="card stat-card" replace>
          <div className="stat-label">Devices reporting</div>
          <div className="stat-value">{s.devicesReporting}</div>
        </Link>
        <Link to="/security" className="card stat-card" replace>
          <div className="stat-label">Average score</div>
          <div className={`stat-value${s.averageScore == null ? ' unknown' : ''}`}>
            {s.averageScore == null ? '—' : `${s.averageScore}%`}
          </div>
        </Link>
        <Link to="/security?show=healthy" className={`card stat-card${s.healthy > 0 ? ' tone-ok' : ''}`} replace>
          <div className="stat-label">Healthy (≥80%)</div>
          <div className="stat-value">{s.healthy}</div>
        </Link>
        <Link
          to="/security?show=attention"
          className={`card stat-card${s.needsAttention > 0 ? ' tone-warn' : ''}`}
          replace
        >
          <div className="stat-label">Needs attention</div>
          <div className="stat-value">{s.needsAttention}</div>
        </Link>
        <Link to="/security?show=critical" className={`card stat-card${s.critical > 0 ? ' tone-crit' : ''}`} replace>
          <div className="stat-label">Critical (&lt;50%)</div>
          <div className="stat-value">{s.critical}</div>
        </Link>
      </div>

      <div className="card">
        <div className="section-head">
          <h2>Device compliance</h2>
          <select
            aria-label="Filter devices by security score"
            style={{ width: 'auto', minWidth: 190 }}
            value={filter}
            onChange={(e) => showOnly(e.target.value as PostureFilter)}
          >
            {POSTURE_FILTERS.map((f) => (
              <option key={f.value} value={f.value}>
                {f.label}
              </option>
            ))}
          </select>
        </div>
        {data.devices.length > 0 && devices.length === 0 && (
          <div className="empty-state">
            <div className="title">No devices in this band</div>
            <div>
              <button type="button" className="link-button" onClick={() => showOnly('')}>
                Show all devices
              </button>
            </div>
          </div>
        )}
        {data.devices.length === 0 && (
          <div className="empty-state">
            <Icon name="security" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No security data yet</div>
            <div>Security posture appears once devices report an inventory that includes it.</div>
          </div>
        )}
        {devices.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Device</th>
                  <th>Score</th>
                  <th>Defender</th>
                  <th>Firewall</th>
                  <th>Secure Boot</th>
                  <th>TPM</th>
                  <th>BitLocker</th>
                  <th>Local admins</th>
                </tr>
              </thead>
              <tbody>
                {devices.map((d) => (
                  <tr key={d.deviceId}>
                    <td>
                      <Link to={`/devices/${d.deviceId}`}>{d.hostname}</Link>
                    </td>
                    <td>{scoreBadge(d.complianceScore)}</td>
                    <td>{checkCell(d.defenderEnabled)}</td>
                    <td>{checkCell(d.firewallEnabled)}</td>
                    <td>{checkCell(d.secureBootEnabled)}</td>
                    <td>{checkCell(d.tpmEnabled)}</td>
                    <td>
                      {d.bitLockerSystemDriveStatus == null ? (
                        <span className="muted">—</span>
                      ) : d.bitLockerSystemDriveStatus === 'On' ? (
                        <span className="badge ok">On</span>
                      ) : (
                        <span className="badge crit">{d.bitLockerSystemDriveStatus}</span>
                      )}
                    </td>
                    <td>
                      {/* More than three local administrators on one machine is
                          worth a look, not an alert — hence warn, never crit. */}
                      {d.localAdministratorCount == null ? (
                        <span className="muted">—</span>
                      ) : (
                        <span
                          className={`badge plain ${
                            d.localAdministratorCount > 3 ? 'warn' : 'neutral'
                          }`}
                        >
                          {d.localAdministratorCount}
                        </span>
                      )}
                    </td>
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
