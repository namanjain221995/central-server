import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { ApiError, getFleetStorageSummary, type FleetStorageSummary } from '../api/client'
import { Icon } from '../components/Icon'
import {
  fleetHeadline,
  freeText,
  needsAttention,
  severityBadge,
  usedPercent,
} from './storageView'

function formatDateTime(value: string | null): string {
  if (!value) return 'never'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? 'never' : date.toLocaleString()
}

/** The fill tone follows the device severity so the bar and the badge agree. */
const BAR_COLOR: Record<string, string> = {
  crit: 'var(--color-crit)',
  warn: 'var(--color-warn)',
  ok: 'var(--color-ok)',
  muted: 'var(--color-border-strong)',
}

/**
 * Fleet storage overview: how full each managed device's fixed volumes are, read
 * from the hardware inventory the agent already uploads. Read-only — it adds no
 * capability over a single device's Users/Hardware view; it only gathers the disk
 * figures so a machine about to run out of space stands out without opening each
 * device in turn.
 */
export function StoragePage() {
  const { hasPermission } = useAuth()
  const canView = hasPermission('device.view')

  const [summary, setSummary] = useState<FleetStorageSummary | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setSummary(await getFleetStorageSummary())
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not load storage overview.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    if (!canView) return
    void load()
  }, [canView, load])

  if (!canView) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to view device storage.</p>
      </div>
    )
  }

  const devices = summary?.devices ?? []

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
          How full each managed device's fixed drives are, from the hardware inventory the agent
          already reports. Devices lowest on free space are listed first. A drive under{' '}
          {summary ? summary.lowFreePercent : 10}% free is flagged Low and under{' '}
          {summary ? summary.criticalFreePercent : 5}% Critical.
        </div>
      </div>

      <div className="stat-grid">
        <div className={`card stat-card glass-card${summary && summary.criticalDiskDeviceCount > 0 ? ' tone-warn' : ''}`}>
          <div className="stat-label">Critical</div>
          <div className="stat-value">{summary ? summary.criticalDiskDeviceCount : '—'}</div>
          <div className="stat-note">Below {summary ? summary.criticalFreePercent : 5}% free</div>
        </div>
        <div className={`card stat-card glass-card${summary && summary.lowDiskDeviceCount > 0 ? ' tone-warn' : ''}`}>
          <div className="stat-label">Low on space</div>
          <div className="stat-value">{summary ? summary.lowDiskDeviceCount : '—'}</div>
          <div className="stat-note">Below {summary ? summary.lowFreePercent : 10}% free</div>
        </div>
        <div className="card stat-card glass-card">
          <div className="stat-label">Reporting</div>
          <div className="stat-value">{summary ? summary.devicesReporting : '—'}</div>
          <div className="stat-note">of {summary ? summary.activeDevices : '—'} active devices</div>
        </div>
      </div>

      <div className="card glass-card">
        <div className="section-head">
          <h2 style={{ margin: 0 }}>Devices</h2>
          <span className="spacer" style={{ flex: 1 }} />
          {summary && (
            <span className="muted" style={{ fontSize: 12.5, marginRight: 12 }}>{fleetHeadline(summary)}</span>
          )}
          <button type="button" className="btn-ghost btn-sm" onClick={() => void load()} disabled={loading}>
            <Icon name="refresh" size={14} /> Refresh
          </button>
        </div>

        {loading && !summary && <div className="loading">Loading storage overview…</div>}

        {summary && devices.length === 0 && (
          <div className="empty-state">
            <p>No active devices in view.</p>
          </div>
        )}

        {devices.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Device</th>
                  <th>Status</th>
                  <th>Drives</th>
                  <th>Last reported</th>
                </tr>
              </thead>
              <tbody>
                {devices.map((device) => {
                  const badge = severityBadge(device.severity)
                  const rowClass =
                    device.severity === 'Critical'
                      ? 'row-attention row-critical'
                      : needsAttention(device)
                        ? 'row-attention'
                        : undefined
                  return (
                    <tr key={device.deviceId} className={rowClass}>
                      <td>
                        <Link to={`/devices/${device.deviceId}`}>{device.hostname}</Link>
                      </td>
                      <td>
                        <span className={`badge plain ${badge.tone === 'muted' ? 'neutral' : badge.tone}`}>
                          {badge.label}
                        </span>
                      </td>
                      <td>
                        {device.volumes.length === 0 ? (
                          <span className="muted">No inventory yet</span>
                        ) : (
                          <div style={{ display: 'flex', flexDirection: 'column', gap: 8, minWidth: 260 }}>
                            {device.volumes.map((volume) => {
                              const vb = severityBadge(volume.severity)
                              return (
                                <div key={volume.name}>
                                  <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 12.5 }}>
                                    <span>
                                      <strong>{volume.name}</strong>
                                      {volume.fileSystem ? <span className="muted"> · {volume.fileSystem}</span> : null}
                                    </span>
                                    <span className="muted">{freeText(volume)}</span>
                                  </div>
                                  <div
                                    aria-hidden="true"
                                    style={{
                                      marginTop: 3,
                                      height: 6,
                                      borderRadius: 3,
                                      background: 'var(--color-inset)',
                                      overflow: 'hidden',
                                    }}
                                  >
                                    <div
                                      style={{
                                        width: `${usedPercent(volume)}%`,
                                        height: '100%',
                                        background: BAR_COLOR[vb.tone],
                                      }}
                                    />
                                  </div>
                                </div>
                              )
                            })}
                          </div>
                        )}
                      </td>
                      <td className="muted" style={{ whiteSpace: 'nowrap' }}>
                        {formatDateTime(device.collectedAt)}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}

        {summary?.truncated && (
          <p className="muted" style={{ fontSize: 12.5, marginTop: 10 }}>
            The list was capped; the counts above still reflect the whole fleet.
          </p>
        )}
      </div>
    </>
  )
}
