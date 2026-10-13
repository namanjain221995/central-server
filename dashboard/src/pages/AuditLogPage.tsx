import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { getAuditLog, type AuditLogEntry } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { auditResultTone, describeAuditAction, describeAuditSubject } from './auditView'

const PAGE_SIZE = 50

/**
 * The audit log, newest first: who did what, to which machine, and whether it
 * worked. "Load older" pages back by time, so new events arriving at the top
 * never shift or repeat the rows already on screen.
 */
export function AuditLogPage() {
  const { hasPermission } = useAuth()
  const canView = hasPermission('audit.view')

  const [rows, setRows] = useState<AuditLogEntry[] | null>(null)
  const [hasMore, setHasMore] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const loadNewest = useCallback(async () => {
    try {
      const page = await getAuditLog(PAGE_SIZE)
      setRows(page)
      setHasMore(page.length === PAGE_SIZE)
      setError(null)
    } catch {
      setError('Could not load the audit log.')
    }
  }, [])

  useEffect(() => {
    if (canView) void loadNewest()
  }, [canView, loadNewest])

  async function loadOlder() {
    if (!rows || rows.length === 0) return
    setLoadingMore(true)
    try {
      const page = await getAuditLog(PAGE_SIZE, rows[rows.length - 1].occurredAt)
      setRows([...rows, ...page])
      setHasMore(page.length === PAGE_SIZE)
    } catch {
      setError('Could not load older entries.')
    } finally {
      setLoadingMore(false)
    }
  }

  if (!canView) {
    return (
      <div className="card">
        <div className="empty-state">
          <Icon name="audit" size={40} strokeWidth={1.25} className="icon" />
          <div className="title">The audit log is not available to your role</div>
          <div>Reading it needs the audit.view permission.</div>
        </div>
      </div>
    )
  }

  return (
    <>
      <div className="page-header">
        <div className="lede">
          Every administrator action and every security-relevant event reported by a device, as
          recorded. The log is append-only: nothing here can be edited or deleted.
        </div>
        <button type="button" onClick={() => void loadNewest()}>
          <Icon name="refresh" size={14} />
          Refresh
        </button>
      </div>

      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      {rows === null && !error && <p className="muted">Loading…</p>}

      {rows !== null && rows.length === 0 && (
        <div className="card">
          <div className="empty-state">
            <Icon name="audit" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No activity yet</div>
            <div>Audit events will appear here once devices enroll and administrators act.</div>
          </div>
        </div>
      )}

      {rows !== null && rows.length > 0 && (
        <div className="card">
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Action</th>
                  <th>On</th>
                  <th>By</th>
                  <th>Result</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => {
                  const subject = describeAuditSubject(row)
                  return (
                    <tr key={row.id}>
                      <td className="muted" style={{ whiteSpace: 'nowrap' }}>
                        {new Date(row.occurredAt).toLocaleString()}
                      </td>
                      <td>
                        <div>
                          <span>{describeAuditAction(row.action)}</span>
                          <code className="row-sub mono-sub">{row.action}</code>
                        </div>
                      </td>
                      <td>
                        {subject === null ? (
                          <span className="muted">—</span>
                        ) : row.deviceId ? (
                          <Link to={`/devices/${row.deviceId}`}>{subject}</Link>
                        ) : (
                          subject
                        )}
                      </td>
                      <td>
                        <div>
                          <span>{row.actorDisplay}</span>
                          <span className="row-sub">{row.actorType}</span>
                        </div>
                      </td>
                      <td>
                        <span className={`badge ${auditResultTone(row.result)}`} title={row.failureReason ?? undefined}>
                          {row.result}
                        </span>
                        {row.failureReason && <span className="row-sub">{row.failureReason}</span>}
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
          {hasMore && (
            <div style={{ textAlign: 'center', marginTop: 12 }}>
              <button type="button" onClick={() => void loadOlder()} disabled={loadingMore}>
                {loadingMore ? 'Loading…' : 'Load older entries'}
              </button>
            </div>
          )}
        </div>
      )}
    </>
  )
}
