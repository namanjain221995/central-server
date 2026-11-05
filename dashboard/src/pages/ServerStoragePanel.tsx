import { useCallback, useEffect, useState } from 'react'
import { ApiError, getServerStorage, type ServerStorageSummary } from '../api/client'
import { Icon } from '../components/Icon'
import { formatBytes, storagePercent } from './workTimeView'

/**
 * How much disk the server's own database is using, broken down by table so an
 * administrator can see what is growing — the activity samples that Time
 * Management produces above all. Read-only; it rides the platform-settings
 * permission, so a 403 here means the client's idea of that permission is stale
 * and the panel simply hides, like the network-access panel.
 */
export function ServerStoragePanel() {
  const [summary, setSummary] = useState<ServerStorageSummary | null>(null)
  const [forbidden, setForbidden] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      setSummary(await getServerStorage())
    } catch (err) {
      if (err instanceof ApiError && err.status === 403) {
        setForbidden(true)
        return
      }
      setError(err instanceof ApiError ? (err.detail ?? err.message) : 'Could not load server storage.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  if (forbidden) return null

  const tables = summary?.tables ?? []

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
          How much disk the server database is using, by table, biggest first. Use it to see what
          is growing — for example the activity samples Time Management records.
        </div>
      </div>

      <div className="stat-grid">
        <div className="card stat-card glass-card">
          <div className="stat-label">Database size</div>
          <div className="stat-value">{summary ? formatBytes(summary.databaseBytes) : '—'}</div>
          <div className="stat-note">total on disk</div>
        </div>
        <div className="card stat-card glass-card">
          <div className="stat-label">Tables</div>
          <div className="stat-value">{summary ? tables.length : '—'}</div>
          <div className="stat-note">largest shown first</div>
        </div>
      </div>

      <div className="card glass-card">
        <div className="section-head">
          <h2 style={{ margin: 0 }}>Storage by table</h2>
          <span className="spacer" style={{ flex: 1 }} />
          <button type="button" className="btn-ghost btn-sm" onClick={() => void load()} disabled={loading}>
            <Icon name="refresh" size={14} /> Refresh
          </button>
        </div>

        {loading && !summary && <div className="loading">Loading server storage…</div>}

        {summary && tables.length === 0 && (
          <div className="empty-state"><p>No tables reported.</p></div>
        )}

        {tables.length > 0 && summary && (
          <div className="table-wrap">
            <table className="table">
              <caption className="sr-only">Database storage by table</caption>
              <thead>
                <tr>
                  <th scope="col">Table</th>
                  <th scope="col" style={{ textAlign: 'right' }}>Size</th>
                  <th scope="col" style={{ textAlign: 'right' }}>Rows (est.)</th>
                  <th scope="col" style={{ width: '28%' }}>Share</th>
                </tr>
              </thead>
              <tbody>
                {tables.map((t) => {
                  const pct = storagePercent(t.totalBytes, summary.databaseBytes)
                  return (
                    <tr key={t.name}>
                      <td><code>{t.name}</code></td>
                      <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>{formatBytes(t.totalBytes)}</td>
                      <td style={{ textAlign: 'right' }}>{t.rowEstimate.toLocaleString()}</td>
                      <td>
                        <div
                          aria-hidden="true"
                          style={{ height: 6, borderRadius: 3, background: 'var(--color-inset)', overflow: 'hidden' }}
                        >
                          <div style={{ width: `${pct}%`, height: '100%', background: 'var(--color-accent)' }} />
                        </div>
                        <span className="sr-only">{pct}% of the database</span>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  )
}
