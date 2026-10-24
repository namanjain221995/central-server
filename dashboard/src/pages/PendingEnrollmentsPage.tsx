import { useCallback, useEffect, useState } from 'react'
import {
  approveEnrollment,
  getPendingEnrollments,
  rejectEnrollment,
  ApiError,
  type PendingEnrollment,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import {
  approveConfirmation,
  isReEnrollment,
  matchLine,
  rejectConfirmation,
  showOnlineWarning,
} from './enrollmentsView'

/** How often the list refreshes while the page is open. */
const POLL_INTERVAL_MS = 12_000

/**
 * Windows agents waiting for administrator approval.
 *
 * A PC that has installed the MSI carries no credential and no token — it asks
 * to be managed and waits here. Approving is what admits it, so this page is the
 * human half of the enrollment authorization boundary.
 *
 * Re-enrollments wait here too: a machine presenting a valid token whose
 * identifier matches an ACTIVE device. Those rows carry a badge, the matched
 * device and its liveness, because approving one revokes that device's working
 * credential and hands its identity to the requester — which is also why
 * approval asks for confirmation there and nowhere else.
 *
 * Everything shown is identity and state. The proof secret, the sealed
 * enrollment token and the issued device credential never leave the server, so
 * there is nothing secret on this page to redact.
 */
export function PendingEnrollmentsPage() {
  const { hasPermission } = useAuth()
  const canDecide = hasPermission('device.enroll')

  const [requests, setRequests] = useState<PendingEnrollment[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  /** Request id currently being approved/rejected, so its buttons can disable. */
  const [busy, setBusy] = useState<string | null>(null)

  // Lets the render loop re-evaluate "expires in" without refetching.
  const [, setTick] = useState(0)

  const load = useCallback(async (showSpinner = false) => {
    if (showSpinner) setLoading(true)
    try {
      setRequests(await getPendingEnrollments())
      setError(null)
    } catch (e) {
      // Never clear the list on a failed refresh: a network blip must not make
      // machines look as though they withdrew their requests.
      setError(describe(e, 'Could not load pending enrollments.'))
    } finally {
      setLoading(false)
    }
  }, [])

  // Poll while mounted. The interval is cleared on unmount so a backgrounded
  // dashboard is not still calling the API.
  useEffect(() => {
    void load(true)
    const poll = setInterval(() => void load(), POLL_INTERVAL_MS)
    const clock = setInterval(() => setTick((t) => t + 1), 1_000)
    return () => {
      clearInterval(poll)
      clearInterval(clock)
    }
  }, [load])

  async function decide(request: PendingEnrollment, approve: boolean) {
    // Approving a plain enrollment stays one click (it takes nothing from
    // anyone); approving a RE-enrollment asks, because it revokes the matched
    // device's credential and hands its identity to the requester. Rejection
    // always asks, with kind-specific consequences.
    const confirmation = approve ? approveConfirmation(request) : rejectConfirmation(request)
    if (confirmation !== null && !window.confirm(confirmation)) return

    setBusy(request.requestId)
    setError(null)
    setNotice(null)

    try {
      const result = approve
        ? await approveEnrollment(request.requestId)
        : await rejectEnrollment(request.requestId)

      setNotice(
        approve
          ? `${result.hostname} approved. ${result.message}`
          : `${result.hostname} rejected. ${result.message}`,
      )
    } catch (e) {
      setError(describe(e, 'The decision could not be recorded.'))
    } finally {
      setBusy(null)
      // Refresh either way: on success to pick up the new state, and on failure
      // because the server's view is authoritative and ours may be stale.
      await load()
    }
  }

  const waiting = requests.filter(
    (r) => r.status === 'Pending' && new Date(r.expiresAt).getTime() > Date.now(),
  ).length

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
          Windows agents waiting for administrator approval. A machine appears here after the
          agent is installed and reaches this server; it receives no credential and cannot be
          managed until it is approved.
        </div>
        <button type="button" onClick={() => void load(true)} disabled={loading}>
          <Icon name="refresh" size={14} />
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>

      {!canDecide && (
        <div className="info-banner">
          You can see enrolment requests but not decide them — that needs the{' '}
          <code>device.enroll</code> permission.
        </div>
      )}

      <div className="card">
        {waiting > 0 && (
          <h2>
            {waiting} machine{waiting === 1 ? '' : 's'} awaiting a decision
          </h2>
        )}

        {loading && requests.length === 0 && <div className="loading">Loading…</div>}

        {!loading && requests.length === 0 && (
          <div className="empty-state">
            <Icon name="inbox" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No pending enrollment requests</div>
            <div>Install the Windows agent on a PC and it will appear here within a minute.</div>
          </div>
        )}

        {requests.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Computer</th>
                  <th>Operating system</th>
                  <th>Agent</th>
                  <th>Requested</th>
                  <th>Expires</th>
                  <th>Status</th>
                  <th style={{ textAlign: 'right' }}>Decision</th>
                </tr>
              </thead>
              <tbody>
                {requests.map((r) => {
                  const expired = new Date(r.expiresAt).getTime() <= Date.now()
                  const decided = r.status !== 'Pending'
                  const working = busy === r.requestId
                  const disabled = busy !== null || decided || expired || !canDecide

                  return (
                    <tr key={r.requestId}>
                      <td>
                        <div>
                          {r.hostname}
                          {isReEnrollment(r) && (
                            <>
                              {' '}
                              <span className="badge warn">Re-enrollment</span>
                              {showOnlineWarning(r) && (
                                <>
                                  {' '}
                                  {/* The matched device authenticated within the offline
                                      window: it is alive while someone asks to become it.
                                      That is the takeover shape, so it shouts. */}
                                  <span
                                    className="badge crit"
                                    title="The matched device is still reporting. A live machine does not normally need re-enrolling."
                                  >
                                    Device online
                                  </span>
                                </>
                              )}
                            </>
                          )}
                        </div>
                        {/* Which active device this request would take over, and whether
                            that device looks alive. Approval copy repeats it; this is the
                            at-a-glance version. */}
                        {matchLine(r, Date.now()) && (
                          <div className="row-sub">{matchLine(r, Date.now())}</div>
                        )}
                        {/* Not a secret: the SMBIOS UUID is how a re-enrolling machine
                            resolves to its existing device record instead of a duplicate. */}
                        <div className="row-sub mono-sub">{r.machineIdentifier}</div>
                      </td>
                      <td>{r.operatingSystem ?? '—'}</td>
                      <td>
                        <code>{r.agentVersion}</code>
                      </td>
                      <td title={r.requestedAt}>{formatTime(r.requestedAt)}</td>
                      <td style={{ color: expired ? 'var(--color-crit)' : undefined }}>
                        {expired ? 'Expired' : relativeTo(r.expiresAt)}
                      </td>
                      <td>
                        <span className={`badge ${statusClass(r.status, expired)}`}>
                          {expired && r.status === 'Pending' ? 'Expired' : r.status}
                        </span>
                        {r.approvedBy && <div className="row-sub">by {r.approvedBy}</div>}
                      </td>
                      <td style={{ textAlign: 'right' }}>
                        <div className="btn-row" style={{ justifyContent: 'flex-end' }}>
                          <button
                            type="button"
                            className={`btn-primary btn-sm${working ? ' btn-loading' : ''}`}
                            disabled={disabled}
                            onClick={() => void decide(r, true)}
                          >
                            {working ? 'Working…' : 'Approve'}
                          </button>
                          {/* Reject is styled as destructive because it is: no
                              credential is issued and the agent stops asking. */}
                          <button
                            type="button"
                            className="btn-danger btn-sm"
                            disabled={disabled}
                            onClick={() => void decide(r, false)}
                          >
                            Reject
                          </button>
                        </div>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <p className="muted" style={{ fontSize: 12.5, marginTop: 12 }}>
        After approval the agent collects its credential on its next poll, then appears under
        Devices and becomes Active once it starts reporting. New-machine requests expire 15
        minutes after they are made; an agent that is still waiting simply asks again.
        Re-enrollment requests stay up to 24 hours while the agent keeps retrying; after
        approval the agent completes on its own next retry, within about 5 minutes, and the
        matched device&apos;s old credential is revoked at that moment.
      </p>
    </>
  )
}

/**
 * Turns a failure into something an administrator can act on.
 *
 * 409 is the interesting one: it means the request was already decided, has
 * expired, or another administrator got there first. That is a stale view rather
 * than a fault, so it reads as information instead of an error.
 */
function describe(error: unknown, fallback: string): string {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 401:
        return 'Your session has expired. Sign in again.'
      case 403:
        return 'You do not have permission to decide enrolment requests (device.enroll).'
      case 409:
        return 'That request was already decided, or it expired. The list has been refreshed.'
      case 503:
        return 'The server is temporarily unavailable. It will be retried on the next refresh.'
      default:
        return `${fallback} (HTTP ${error.status}${error.correlationId ? `, ref ${error.correlationId}` : ''})`
    }
  }
  return fallback
}

function statusClass(status: string, expired: boolean): string {
  if (expired || status === 'Rejected') return 'crit'
  if (status === 'Approved') return 'ok'
  return 'warn'
}

function formatTime(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleTimeString()
}

/** "in 12m 30s" (or "in 23h 54m" for the long-lived re-enrollment requests), or "Expired". */
function relativeTo(iso: string): string {
  const ms = new Date(iso).getTime() - Date.now()
  if (Number.isNaN(ms) || ms <= 0) return 'Expired'
  const total = Math.floor(ms / 1000)
  const minutes = Math.floor(total / 60)
  const seconds = total % 60
  if (minutes >= 60) return `in ${Math.floor(minutes / 60)}h ${minutes % 60}m`
  return minutes > 0 ? `in ${minutes}m ${seconds}s` : `in ${seconds}s`
}
