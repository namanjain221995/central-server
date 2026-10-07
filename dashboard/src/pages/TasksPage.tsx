import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  ApiError,
  cancelDeviceTask,
  getRecentTasks,
  type FleetTaskItem,
  type FleetTaskPage,
} from '../api/client'
import { Icon } from '../components/Icon'
import { isCancellableTask } from './restartView'

const PAGE_SIZE = 50
const POLL_INTERVAL_MS = 15_000

/** Every state a task can be in, in lifecycle order, for the filter control. */
const STATUS_FILTERS = ['', 'Queued', 'Delivered', 'Succeeded', 'Failed', 'Expired', 'Cancelled'] as const

/**
 * Fleet-wide task history — what the platform has been asked to do across every
 * device, and what actually came of it.
 *
 * The per-device Tasks module answers "what happened to this machine"; this page
 * answers "what is the platform doing right now". Rows link to their device, and
 * a task still Queued can be cancelled from here — the same rule as everywhere
 * else: you may cancel what you are permitted to queue, and never anything the
 * agent already holds.
 */
export function TasksPage() {
  const [data, setData] = useState<FleetTaskPage | null>(null)
  const [page, setPage] = useState(1)
  const [status, setStatus] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(
    async (showSpinner = false) => {
      if (showSpinner) setLoading(true)
      try {
        setData(await getRecentTasks(page, PAGE_SIZE, status || undefined))
        setError(null)
      } catch {
        // Keep the last known list on a failed refresh rather than blanking it.
        setError('Could not load tasks from the Admin API.')
      } finally {
        setLoading(false)
      }
    },
    [page, status],
  )

  useEffect(() => {
    void load(true)
    const timer = setInterval(() => void load(), POLL_INTERVAL_MS)
    return () => clearInterval(timer)
  }, [load])

  async function onCancel(task: FleetTaskItem) {
    setNotice(null)
    setError(null)
    try {
      const requested = await cancelDeviceTask(task.deviceId, task.id)
      setNotice(
        requested
          ? `Cancellation sent to ${task.deviceDisplayName ?? task.deviceHostname}; it confirms on its next check-in.`
          : `${task.type} for ${task.deviceDisplayName ?? task.deviceHostname} cancelled.`,
      )
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 409
          ? (e.detail ?? 'Too late to cancel — the task was already delivered to the agent or has finished.')
          : e instanceof ApiError && e.status === 403
            ? 'You do not have permission to cancel this type of task.'
            : 'The task could not be cancelled.',
      )
    } finally {
      await load()
    }
  }

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / PAGE_SIZE)) : 1

  return (
    <>
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}
      {notice && (
        <div className="notice-banner" role="status">
          <Icon name="check" size={15} />
          <span>{notice}</span>
        </div>
      )}

      <div className="page-header">
        <div className="lede">
          Every remote action queued across the fleet, with what actually happened on each machine.
          A task is only Succeeded once the device itself reported the Windows operation done.
        </div>
        <button type="button" onClick={() => void load(true)} disabled={loading}>
          <Icon name="refresh" size={14} />
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>

      <div className="card">
        <div className="toolbar">
          <select
            aria-label="Filter by task status"
            style={{ width: 'auto', minWidth: 160 }}
            value={status}
            onChange={(e) => {
              setPage(1)
              setStatus(e.target.value)
            }}
          >
            {STATUS_FILTERS.map((s) => (
              <option key={s} value={s}>
                {s === '' ? 'All statuses' : s}
              </option>
            ))}
          </select>
        </div>

        {loading && !data && <div className="loading">Loading tasks…</div>}

        {data && data.items.length === 0 && (
          <div className="empty-state">
            <Icon name="tasks" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">
              {status ? `No ${status.toLowerCase()} tasks` : 'No tasks yet'}
            </div>
            <div>Queue an action from a device's Actions module to see it here.</div>
          </div>
        )}

        {data && data.items.length > 0 && (
          <>
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Device</th>
                    <th>Task</th>
                    <th>Status</th>
                    <th>Queued by</th>
                    <th>Queued</th>
                    <th>Result</th>
                    <th style={{ textAlign: 'right' }}></th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((t) => (
                    <tr key={t.id}>
                      <td>
                        <Link to={`/devices/${t.deviceId}?m=tasks`}>
                          {t.deviceDisplayName ?? t.deviceHostname}
                        </Link>
                        {t.deviceDisplayName && <div className="row-sub">{t.deviceHostname}</div>}
                      </td>
                      <td>{t.type}</td>
                      <td>
                        <span className={`badge ${statusClass(t.status)}`}>{t.status}</span>
                      </td>
                      <td>{t.createdByDisplay}</td>
                      <td title={t.createdAt}>{new Date(t.createdAt).toLocaleString()}</td>
                      <td className="muted" style={{ maxWidth: 320 }}>
                        <span className="truncate" title={t.resultMessage ?? undefined}>
                          {t.resultMessage ?? '—'}
                        </span>
                      </td>
                      <td style={{ textAlign: 'right' }}>
                        {isCancellableTask(t, new Date()) && (
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            onClick={() => void onCancel(t)}
                          >
                            Cancel
                          </button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <div className="pagination">
              <span>
                {data.totalCount} task{data.totalCount === 1 ? '' : 's'}
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
    </>
  )
}

function statusClass(status: string): string {
  switch (status) {
    case 'Succeeded':
      return 'ok'
    case 'Failed':
    case 'Expired':
      return 'crit'
    case 'Cancelled':
      return 'neutral'
    // Queued and Delivered are both "not done yet" — amber, never green.
    default:
      return 'warn'
  }
}
