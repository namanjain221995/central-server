import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { getCatalogInstalls, type InstallRequestPage } from '../api/client'
import { Icon } from '../components/Icon'
import { installOutcomeLabel, outcomeTone } from './catalogView'

const PAGE_SIZE = 50

/**
 * What employees asked the Company Portal to install, newest first.
 *
 * Every outcome is the server's record of what it did with the request, and a
 * queued request's progress is the install task's own status. Nothing is
 * inferred here: a request reads as queued until a device says otherwise.
 */
export function CatalogInstallsPanel() {
  const [data, setData] = useState<InstallRequestPage | null>(null)
  const [page, setPage] = useState(1)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await getCatalogInstalls(page, PAGE_SIZE))
      setError(null)
    } catch {
      setError('Could not load install requests.')
    } finally {
      setLoading(false)
    }
  }, [page])

  useEffect(() => {
    void load()
  }, [load])

  const totalPages = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1

  return (
    <div className="card">
      <div className="card-header">
        <div>
          <h2>Install requests</h2>
          <div className="muted" style={{ fontSize: 12.5, marginTop: 3, maxWidth: 620 }}>
            Each time an employee installs an app from the Company Portal, and what happened to the
            request.
          </div>
        </div>
        <button type="button" className="btn-sm" disabled={loading} onClick={() => void load()}>
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

      {loading && !data && <div className="loading">Loading install requests…</div>}

      {data && data.items.length === 0 && !error && (
        <div className="empty-state">
          <Icon name="inbox" size={40} strokeWidth={1.25} className="icon" />
          <div className="title">No install requests yet</div>
          <div>Requests appear here once an employee installs an app from the Company Portal.</div>
        </div>
      )}

      {data && data.items.length > 0 && (
        <>
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>App</th>
                  <th>Device</th>
                  <th>Requested by</th>
                  <th>Outcome</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((r) => (
                  <tr key={r.id}>
                    <td>{new Date(r.requestedAt).toLocaleString()}</td>
                    <td>{r.appName}</td>
                    <td>
                      <Link to={`/devices/${r.deviceId}`}>{r.hostname}</Link>
                    </td>
                    <td>{r.requestedBy ?? <span className="muted">—</span>}</td>
                    <td>
                      <span className={`badge ${outcomeTone(r)}`}>{installOutcomeLabel(r)}</span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="pagination">
            <span>
              {data.total} request{data.total === 1 ? '' : 's'}
            </span>
            <span className="pager">
              <button
                type="button"
                className="btn-sm"
                disabled={page <= 1}
                onClick={() => setPage(page - 1)}
              >
                Previous
              </button>
              <span>
                Page {page} of {totalPages}
              </span>
              <button
                type="button"
                className="btn-sm"
                disabled={page >= totalPages}
                onClick={() => setPage(page + 1)}
              >
                Next
              </button>
            </span>
          </div>
        </>
      )}
    </div>
  )
}
