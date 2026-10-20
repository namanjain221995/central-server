import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  ApiError,
  configureRemoteSupport,
  deviceName,
  getRemoteSupportDevices,
  type RemoteSupportDeviceRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { RemoteSessionDialog } from './RemoteSessionDialog'
import { stateBadgeTone, stateLabel } from './remoteSupportView'

/** Device state changes as agents report in; refresh every 30s, like Devices. */
const POLL_MS = 30_000

/**
 * IT Remote Support: a trimmed device list for authorized help-desk remote
 * control over RustDesk.
 *
 * Every capability is gated. The whole page is behind `remotesupport.view`;
 * enabling a device is behind `remotesupport.configure`; starting a session is
 * behind `remotesupport.connect`. These gates hide controls — the server
 * enforces each one independently.
 */
export function RemoteSupportPage() {
  const { hasPermission } = useAuth()
  const canView = hasPermission('remotesupport.view')
  const canConfigure = hasPermission('remotesupport.configure')
  const canConnect = hasPermission('remotesupport.connect')

  const [rows, setRows] = useState<RemoteSupportDeviceRow[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  // The device whose configure call is in flight, so one row's button can say
  // "Enabling…" without freezing the rest.
  const [configuring, setConfiguring] = useState<string | null>(null)
  const [connectTarget, setConnectTarget] = useState<RemoteSupportDeviceRow | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setRows(await getRemoteSupportDevices())
      setError(null)
    } catch {
      setError('Could not load devices from the Admin API.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    // Not polled at all without permission: the request would 403 every 30s.
    if (!canView) {
      setLoading(false)
      return
    }
    void load()
    const timer = setInterval(() => void load(), POLL_MS)
    return () => clearInterval(timer)
  }, [canView, load])

  async function onConfigure(device: RemoteSupportDeviceRow) {
    setConfiguring(device.deviceId)
    setNotice(null)
    setError(null)
    try {
      await configureRemoteSupport(device.deviceId)
      setNotice(
        `Enabling remote support on ${deviceName(device)}. It becomes ready to connect once the ` +
          'device reports its RustDesk ID.',
      )
      void load()
    } catch (e) {
      setError(e instanceof ApiError ? (e.detail ?? e.message) : 'Remote support could not be enabled.')
    } finally {
      setConfiguring(null)
    }
  }

  // The whole-page gate, mirroring DeviceBitLockerPanel. Declared after the hooks
  // above so the hook order never changes between renders.
  if (!canView) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to view IT Remote Support.</p>
      </div>
    )
  }

  return (
    <>
      {error && (
        <div className="error-banner">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}
      {notice && (
        <div className="notice-banner" role="status">
          <Icon name="check" size={15} />
          <span style={{ flex: 1 }}>{notice}</span>
          <button
            type="button"
            className="btn-ghost btn-sm"
            style={{ color: 'inherit' }}
            onClick={() => setNotice(null)}
          >
            Dismiss
          </button>
        </div>
      )}

      <div className="page-header">
        <div className="lede">
          Authorized help-desk remote control of managed Windows devices over RustDesk. Enabling a
          device installs and enrols it; connecting reveals a one-time password and opens RustDesk.
          The device shows RustDesk&rsquo;s own connection indicator throughout, and every connection
          is recorded against your account.
        </div>
      </div>

      <div className="card">
        <div className="toolbar">
          <div className="spacer" />
          <button type="button" onClick={() => void load()} disabled={loading}>
            <Icon name="refresh" size={14} />
            {loading ? 'Refreshing…' : 'Refresh'}
          </button>
        </div>

        {loading && rows.length === 0 && <div className="loading">Loading devices&hellip;</div>}

        {!loading && rows.length === 0 && (
          <div className="empty-state">
            <Icon name="remote" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No devices</div>
            <div>Enrolled Windows devices appear here once the server reports them.</div>
          </div>
        )}

        {rows.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Status</th>
                  <th>Remote support</th>
                  <th>RustDesk ID</th>
                  <th aria-label="Remote support actions" />
                </tr>
              </thead>
              <tbody>
                {rows.map((d) => (
                  <tr key={d.deviceId}>
                    <td>
                      {deviceName(d)}
                      <div className="muted">{d.hostname}</div>
                    </td>
                    <td>
                      {d.isOnline ? (
                        <span className="badge ok">Online</span>
                      ) : (
                        <span className="badge crit">Offline</span>
                      )}
                    </td>
                    <td>
                      <span className={`badge ${stateBadgeTone(d.state)}`}>{stateLabel(d.state)}</span>
                    </td>
                    <td>
                      {d.rustDeskId ? <code>{d.rustDeskId}</code> : <span className="muted">&mdash;</span>}
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      {d.state === 'NotInstalled' && (
                        <span className="muted">
                          Install RustDesk from <Link to="/company-apps">Company Apps</Link>
                        </span>
                      )}

                      {(d.state === 'NotEnrolled' || d.state === 'Enrolling') && canConfigure && (
                        <button
                          type="button"
                          className="btn-sm"
                          disabled={configuring === d.deviceId}
                          onClick={() => void onConfigure(d)}
                        >
                          {configuring === d.deviceId ? 'Enabling…' : 'Enable remote support'}
                        </button>
                      )}

                      {d.state === 'Ready' && canConnect && (
                        <button
                          type="button"
                          className="btn-sm btn-primary"
                          onClick={() => setConnectTarget(d)}
                        >
                          Connect to client
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {connectTarget && (
        <RemoteSessionDialog device={connectTarget} onClose={() => setConnectTarget(null)} />
      )}
    </>
  )
}
