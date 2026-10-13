import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { getConnectedUsbDevices, type ConnectedUsbDevice } from '../api/client'
import { Icon } from '../components/Icon'

/** Which connected devices to list: both kinds, phones only, or storage only. */
export type ConnectedUsbFilter = 'all' | 'phones' | 'storage'

const FILTERS: { value: ConnectedUsbFilter; label: string }[] = [
  { value: 'all', label: 'Phones and storage' },
  { value: 'phones', label: 'Phones only' },
  { value: 'storage', label: 'Storage only' },
]

/** What the device may do, in the words the grant dialog uses. */
const POLICY_LABEL: Record<string, string> = {
  Restricted: 'Blocked',
  ReadOnly: 'Read-only',
  Enabled: 'Read & write',
}

function policyBadge(policy: string): string {
  return policy === 'Restricted' ? 'ok' : 'warn'
}

function matches(device: ConnectedUsbDevice, filter: ConnectedUsbFilter): boolean {
  if (filter === 'phones') return device.isPortableDevice
  if (filter === 'storage') return device.isStorage
  return true
}

/**
 * USB storage devices and phones plugged into an endpoint right now, across the
 * fleet — the list behind the dashboard's "USB devices connected" and "Phones
 * connected" tiles.
 *
 * "Right now" is each endpoint's latest report. A machine that has gone offline
 * still lists what was plugged into it, so its last report time is shown next to
 * its name rather than implying a live view of a machine nobody can reach.
 */
export function ConnectedUsbPanel({ initialFilter }: { initialFilter: ConnectedUsbFilter }) {
  const [filter, setFilter] = useState<ConnectedUsbFilter>(initialFilter)
  const [rows, setRows] = useState<ConnectedUsbDevice[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setRows(await getConnectedUsbDevices())
      setError(null)
    } catch {
      setError('Could not load connected USB devices.')
    }
  }, [])

  useEffect(() => {
    void load()
    const timer = setInterval(() => void load(), 30_000)
    return () => clearInterval(timer)
  }, [load])

  const shown = rows?.filter((r) => matches(r, filter)) ?? []
  const phones = rows?.filter((r) => r.isPortableDevice).length ?? 0
  const storage = rows?.filter((r) => r.isStorage).length ?? 0

  return (
    <div className="card card-section" id="connected">
      <div className="section-head">
        <h2>Connected now</h2>
        <div className="toolbar" style={{ margin: 0 }}>
          {rows !== null && (
            <span className="muted" style={{ fontSize: 13 }}>
              {phones} {phones === 1 ? 'phone' : 'phones'} · {storage} storage
            </span>
          )}
          <select
            aria-label="Show connected phones, storage or both"
            style={{ width: 'auto', minWidth: 170 }}
            value={filter}
            onChange={(e) => setFilter(e.target.value as ConnectedUsbFilter)}
          >
            {FILTERS.map((f) => (
              <option key={f.value} value={f.value}>
                {f.label}
              </option>
            ))}
          </select>
        </div>
      </div>

      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      {rows === null && !error && <p className="muted">Loading…</p>}

      {rows !== null && shown.length === 0 && (
        <div className="empty-state">
          <Icon name="usb" size={40} strokeWidth={1.25} className="icon" />
          <div className="title">
            {filter === 'phones'
              ? 'No phones are connected'
              : filter === 'storage'
                ? 'No USB storage is connected'
                : 'No USB storage or phones are connected'}
          </div>
          <div>Devices appear here when an endpoint reports them plugged in.</div>
        </div>
      )}

      {shown.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Computer</th>
                <th>Device</th>
                <th>Type</th>
                <th>Access</th>
                <th>On the endpoint</th>
              </tr>
            </thead>
            <tbody>
              {shown.map((row) => (
                <tr key={row.id}>
                  <td>
                    <div>
                      <Link to={`/devices/${row.deviceId}?m=usb`}>{row.deviceName}</Link>
                      <span className="row-sub">
                        {row.deviceLastSeenAt
                          ? `reported ${new Date(row.deviceLastSeenAt).toLocaleString()}`
                          : 'never reported'}
                      </span>
                    </div>
                  </td>
                  <td>
                    <div>
                      <span>{row.product ?? (row.isPortableDevice ? 'Phone' : 'USB storage')}</span>
                      {row.manufacturer && <span className="row-sub">{row.manufacturer}</span>}
                    </div>
                  </td>
                  <td>{row.isPortableDevice ? 'Phone' : 'Storage'}</td>
                  <td>
                    <span className={`badge ${policyBadge(row.policy)}`}>
                      {POLICY_LABEL[row.policy] ?? row.policy}
                    </span>
                  </td>
                  <td className="muted">{row.enforcementState}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
