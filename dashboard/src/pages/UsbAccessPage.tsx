import { useCallback, useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import {
  getUsbAccessRequests,
  revokeUsbAccess,
  type UsbAccessRequestRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ConnectedUsbPanel, type ConnectedUsbFilter } from './ConnectedUsbPanel'

const STATUS_BADGE: Record<UsbAccessRequestRow['status'], string> = {
  Approved: 'warn',
  Pending: 'warn',
  Rejected: 'neutral',
  Expired: 'neutral',
  Revoked: 'neutral',
}

function remaining(iso: string | null): string {
  if (!iso) return '—'
  const ms = new Date(iso).getTime() - Date.now()
  if (ms <= 0) return 'expired'
  const minutes = Math.floor(ms / 60000)
  if (minutes < 60) return `${Math.max(1, minutes)}m left`
  const hours = Math.floor(minutes / 60)
  return hours < 24 ? `${hours}h ${minutes % 60}m left` : `${Math.floor(hours / 24)}d left`
}

/**
 * The fleet-wide USB access ledger.
 *
 * Two questions this page exists to answer: *what is open right now*, and *who
 * opened it*. The live view is the default because the first question is the
 * urgent one — an administrator arriving here during an incident wants the
 * currently-readable devices, not a chronology. The full history is one toggle
 * away and is never pruned, because "who granted access to that machine in
 * March" has to stay answerable.
 */
export function UsbAccessPage() {
  const { hasPermission } = useAuth()
  const canManage = hasPermission('usb.manage')

  // The dashboard's USB tiles open this page at ?show=connected or ?show=phones.
  const [searchParams] = useSearchParams()
  const show = searchParams.get('show')
  const connectedFilter: ConnectedUsbFilter = show === 'phones' ? 'phones' : show === 'storage' ? 'storage' : 'all'

  const [liveOnly, setLiveOnly] = useState(true)
  const [rows, setRows] = useState<UsbAccessRequestRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [revoking, setRevoking] = useState<UsbAccessRequestRow | null>(null)

  const load = useCallback(async () => {
    try {
      setRows(await getUsbAccessRequests(liveOnly, 200))
      setError(null)
    } catch {
      setError('Could not load USB access records.')
    }
  }, [liveOnly])

  useEffect(() => {
    void load()
  }, [load])

  async function revoke(row: UsbAccessRequestRow) {
    setRevoking(null)
    setError(null)
    try {
      await revokeUsbAccess(row.id, 'Revoked from the USB access ledger.')
      await load()
    } catch {
      setError('Could not revoke that grant.')
    }
  }

  const liveCount = rows?.filter((r) => r.isLive).length ?? 0

  return (
    <>
      <div className="page-header">
        <div className="lede">
          USB storage and phones plugged in across the fleet, and every grant of temporary access
          to them. Access is always time-boxed; a device with no live grant is restricted on its
          endpoint.
        </div>
        <button type="button" onClick={() => void load()}>
          <Icon name="refresh" size={14} />
          Refresh
        </button>
      </div>

      <ConnectedUsbPanel key={connectedFilter} initialFilter={connectedFilter} />

      <h2 style={{ margin: '8px 0 10px' }}>Access grants</h2>
      <div className="toolbar">
        <label htmlFor="usb-access-filter">Show</label>
        <select
          id="usb-access-filter"
          value={liveOnly ? 'live' : 'all'}
          onChange={(e) => setLiveOnly(e.target.value === 'live')}
        >
          <option value="live">Live access only</option>
          <option value="all">All history</option>
        </select>
        {liveOnly && rows !== null && (
          <span className="badge warn">{liveCount} open</span>
        )}
      </div>

      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      {rows === null && <p className="muted">Loading…</p>}

      {rows !== null && rows.length === 0 && (
        <div className="card">
          <div className="empty-state">
            <Icon name="shield-check" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">
              {liveOnly ? 'No USB storage access is open' : 'No USB access has been granted'}
            </div>
            <div>
              {liveOnly
                ? 'Every USB storage device across the fleet is currently restricted.'
                : 'Grant access from a device’s USB module when someone needs it.'}
            </div>
          </div>
        </div>
      )}

      {rows !== null && rows.length > 0 && (
        <div className="card">
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Device</th>
                  <th>USB device</th>
                  <th>Status</th>
                  <th>Granted by</th>
                  <th>Justification</th>
                  <th>Expires</th>
                  {canManage && <th style={{ textAlign: 'right' }}>Action</th>}
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <tr key={row.id}>
                    <td>
                      <Link to={`/devices/${row.deviceId}?m=usb`}>{row.deviceName}</Link>
                    </td>
                    <td>
                      <div>
                        <span>{row.product ?? 'USB storage'}</span>
                        <code className="row-sub mono-sub" title={row.instanceId}>
                          {row.instanceId.length > 42
                            ? `…${row.instanceId.slice(-40)}`
                            : row.instanceId}
                        </code>
                      </div>
                    </td>
                    <td>
                      {/* "Live" and "Approved" are different facts: a row can be
                          Approved on paper and already past its deadline if the
                          expiry sweep has not run yet. The badge reports which
                          one is actually true. */}
                      <span className={`badge ${row.isLive ? 'warn' : STATUS_BADGE[row.status]}`}>
                        {row.isLive ? 'Read-only now' : row.status}
                      </span>
                    </td>
                    <td>
                      <div>
                        <span>{row.decidedByDisplay ?? '—'}</span>
                        <span className="row-sub">
                          {row.decidedAt ? new Date(row.decidedAt).toLocaleString() : ''}
                        </span>
                      </div>
                    </td>
                    <td style={{ maxWidth: 260 }}>{row.justification}</td>
                    <td>
                      {row.isLive ? (
                        <span title={row.expiresAt ?? undefined}>{remaining(row.expiresAt)}</span>
                      ) : (
                        <span className="muted">
                          {row.expiresAt ? new Date(row.expiresAt).toLocaleString() : '—'}
                        </span>
                      )}
                    </td>
                    {canManage && (
                      <td style={{ textAlign: 'right' }}>
                        {row.isLive ? (
                          <button
                            type="button"
                            className="btn-danger btn-sm"
                            onClick={() => setRevoking(row)}
                          >
                            Revoke
                          </button>
                        ) : (
                          <span className="row-sub">—</span>
                        )}
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {revoking && (
        <ConfirmDialog
          title={`Revoke USB access on ${revoking.deviceName}?`}
          confirmLabel="Yes, revoke access"
          onCancel={() => setRevoking(null)}
          onConfirm={() => void revoke(revoking)}
        >
          <>
            <strong className="secondary">{revoking.product ?? 'The USB device'}</strong> will be
            restricted again on <strong className="secondary">{revoking.deviceName}</strong>. Files
            already copied from it are unaffected — revoking closes the path, it does not undo what
            was read.
          </>
        </ConfirmDialog>
      )}
    </>
  )
}
