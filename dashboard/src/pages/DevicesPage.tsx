import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import {
  ApiError,
  deviceName,
  getAgentReleases,
  getDevices,
  releaseScreenLock,
  updateDeviceAgent,
  type AgentReleaseRow,
  type DeviceListItem,
  type DevicePage,
  type ScreenLockResult,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { RowActionsMenu } from '../components/RowActionsMenu'
import { ScreenLockDialog } from './ScreenLockDialog'
import { deriveLockedBadge, lockSummary } from './screenLocksView'
import { ConfirmDialog } from '../components/ConfirmDialog'
import {
  connectivityFilters,
  connectivityFromParam,
  deviceListViews,
  statusFilterFor,
  viewFromParam,
  type ConnectivityFilter,
  type DeviceListView,
} from './deviceListView'
import { Icon } from '../components/Icon'
import {
  describeIneligibility,
  describeSelection,
  eligibleDevices,
  ineligibilityReason,
  isOperatorRefusal,
  publishedReleases,
  refusalText,
  signingLabel,
  summariseResults,
  toCandidate,
  type UpdateOutcome,
} from './agentUpdateView'
import { ReleaseStepUpDialog } from './ReleaseStepUpDialog'

const PAGE_SIZE = 25

function formatLastSeen(lastSeenAt: string | null): string {
  if (!lastSeenAt) {
    return 'never'
  }

  const seconds = Math.floor((Date.now() - new Date(lastSeenAt).getTime()) / 1000)
  if (seconds < 5) return 'just now'
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return new Date(lastSeenAt).toLocaleString()
}

export function DevicesPage() {
  const [data, setData] = useState<DevicePage | null>(null)
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  // Active is the working view; removed devices keep their history under
  // Retired instead of padding the day-to-day estate list. A dashboard tile
  // opens the page with its own filter (?view=, ?connectivity=).
  const [searchParams] = useSearchParams()
  const [view, setView] = useState<DeviceListView>(() => viewFromParam(searchParams.get('view')))
  const [connectivity, setConnectivity] = useState<ConnectivityFilter>(() =>
    connectivityFromParam(searchParams.get('connectivity')),
  )
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  // ---- agent updates ----
  // Selection is by explicit device id and nothing else. There is no "every
  // device" action anywhere in this page: "all eligible" fills these checkboxes
  // from the rows currently on screen, so the operator sees exactly which
  // machines an installer would reach before confirming.
  const [releases, setReleases] = useState<AgentReleaseRow[]>([])
  const [targetReleaseId, setTargetReleaseId] = useState('')
  const [selected, setSelected] = useState<string[]>([])
  const [confirming, setConfirming] = useState(false)
  const [updating, setUpdating] = useState(false)
  const [outcome, setOutcome] = useState<string | null>(null)
  const [refusals, setRefusals] = useState<UpdateOutcome[]>([])

  // ---- full-screen lock ----
  const { hasPermission } = useAuth()
  const canLock = hasPermission('device.screenlock')
  const [lockTarget, setLockTarget] = useState<DeviceListItem | null>(null)
  const [unlockTarget, setUnlockTarget] = useState<DeviceListItem | null>(null)
  const [lockNotice, setLockNotice] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await getDevices(page, PAGE_SIZE, search, statusFilterFor(view), connectivity || undefined))
      setError(null)
    } catch {
      setError('Could not load devices from the Admin API.')
    } finally {
      setLoading(false)
    }
  }, [page, search, view, connectivity])

  useEffect(() => {
    void load()
    // Device status changes as heartbeats arrive; refresh every 30s.
    const timer = setInterval(() => void load(), 30_000)
    return () => clearInterval(timer)
  }, [load])

  useEffect(() => {
    // A failure here is not surfaced: the update controls simply do not appear,
    // which is the correct outcome when the release list cannot be read.
    getAgentReleases()
      .then((rows) => setReleases(rows))
      .catch(() => setReleases([]))
  }, [])

  // Any change to what is on screen drops the selection. Carrying ticks across
  // a page, a search or a filter change would let an operator confirm devices
  // they can no longer see.
  useEffect(() => {
    setSelected([])
    setOutcome(null)
    setRefusals([])
  }, [page, search, view, connectivity])

  const targets = useMemo(() => publishedReleases(releases), [releases])
  const target = targets.find((r) => r.id === targetReleaseId) ?? targets[0] ?? null

  const rows: DeviceListItem[] = useMemo(() => data?.items ?? [], [data])
  const candidates = useMemo(() => rows.map(toCandidate), [rows])

  const eligibleOnPage = useMemo(
    () => (target ? eligibleDevices(candidates, target) : []),
    [candidates, target],
  )

  const selectedCandidates = candidates.filter((c) => selected.includes(c.deviceId))

  function toggle(deviceId: string) {
    setSelected((current) =>
      current.includes(deviceId)
        ? current.filter((id) => id !== deviceId)
        : [...current, deviceId],
    )
  }

  /**
   * Queues the update one device at a time, with the operator's step-up riding
   * on every call.
   *
   * Sequential on purpose: each call is authorized, version-checked and audited
   * independently by the server, and a refusal on one device must not stop the
   * rest or be attributed to another. Every result is kept so the report can
   * name what was refused.
   *
   * The exception is a refusal about the operator rather than a device -- the
   * step-up gate (403) or the rate limit (429). Replaying one of those down the
   * list would refuse every remaining device for the same reason, and each
   * wrong-password attempt counts towards the operator's own sign-in lockout.
   * Before anything has been queued it is rethrown, so the dialog stays open
   * with the reason inline and the retry covers the whole selection; mid-run it
   * stops the loop, and the summary names exactly how far the run got.
   */
  async function runUpdate(stepUp: { currentPassword: string; justification: string }) {
    if (!target) return

    setUpdating(true)
    setOutcome(null)

    const results: UpdateOutcome[] = []

    try {
      for (const candidate of eligibleDevices(selectedCandidates, target)) {
        try {
          await updateDeviceAgent(
            candidate.deviceId, target.id, stepUp.currentPassword, stepUp.justification)
          results.push({ hostname: candidate.hostname, error: null })
        } catch (e) {
          results.push({ hostname: candidate.hostname, error: refusalText(e) })

          if (isOperatorRefusal(e)) {
            // Nothing queued yet: the whole selection is safe to retry once the
            // password (or the wait) is fixed, so the dialog keeps the attempt.
            if (!results.some((r) => r.error === null)) throw e
            break
          }
        }
      }
    } finally {
      setUpdating(false)
    }

    setConfirming(false)
    setOutcome(summariseResults(results))
    setRefusals(results.filter((r) => r.error !== null))
    setSelected([])
    void load()
  }

  function onLocked(result: ScreenLockResult) {
    setLockTarget(null)
    setLockNotice(lockSummary(result))
    void load()
  }

  async function onUnlock() {
    if (unlockTarget === null) return
    const target = unlockTarget
    setUnlockTarget(null)
    try {
      const { released } = await releaseScreenLock({ deviceIds: [target.id] })
      setLockNotice(`Unlocked ${released} device${released === 1 ? '' : 's'}.`)
      void load()
    } catch (e) {
      setError(e instanceof ApiError ? (e.detail ?? e.message) : 'The device could not be unlocked.')
    }
  }

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / PAGE_SIZE)) : 1

  return (
    <>
      {error && (
        <div className="error-banner">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}
      {lockNotice && (
        <div className="notice-banner" role="status">
          <Icon name="check" size={15} />
          <span style={{ flex: 1 }}>{lockNotice}</span>
          <button
            type="button"
            className="btn-ghost btn-sm"
            style={{ color: 'inherit' }}
            onClick={() => setLockNotice(null)}
          >
            Dismiss
          </button>
        </div>
      )}

      <div className="card card-section">
        <div className="toolbar">
          <div className="input-search">
            <Icon name="search" size={15} className="search-icon" />
            <input
              type="search"
              placeholder="Search name or hostname…"
              aria-label="Search devices by name or hostname"
              value={search}
              onChange={(event) => {
                setPage(1)
                setSearch(event.target.value)
              }}
            />
          </div>
          <select
            aria-label="Filter devices by lifecycle state"
            style={{ width: 'auto', minWidth: 130 }}
            value={view}
            onChange={(e) => {
              setPage(1)
              setView(e.target.value as DeviceListView)
            }}
          >
            {deviceListViews.map((v) => (
              <option key={v} value={v}>
                {v}
              </option>
            ))}
          </select>
          <select
            aria-label="Filter devices by online or offline"
            style={{ width: 'auto', minWidth: 150 }}
            value={connectivity}
            onChange={(e) => {
              setPage(1)
              setConnectivity(e.target.value as ConnectivityFilter)
            }}
          >
            {connectivityFilters.map((c) => (
              <option key={c.value} value={c.value}>
                {c.label}
              </option>
            ))}
          </select>
          <div className="spacer" />
          <button type="button" onClick={() => void load()} disabled={loading}>
            <Icon name="refresh" size={14} />
            {loading ? 'Refreshing…' : 'Refresh'}
          </button>
        </div>

        {/* Only shown once a published release exists. An unpublished build --
            1.4.1 today -- is not a target and must not be offerable here. */}
        {target && rows.length > 0 && (
          <div className="toolbar">
            <label htmlFor="bulk-release" className="muted" style={{ fontSize: 13 }}>
              Update agent to
            </label>
            <select
              id="bulk-release"
              style={{ width: 'auto', minWidth: 110 }}
              value={target.id}
              onChange={(e) => {
                setTargetReleaseId(e.target.value)
                setSelected([])
              }}
            >
              {targets.map((r) => (
                <option key={r.id} value={r.id}>
                  {r.version}
                </option>
              ))}
            </select>

            <button
              type="button"
              className="btn-sm"
              disabled={eligibleOnPage.length === 0}
              onClick={() => setSelected(eligibleOnPage.map((d) => d.deviceId))}
            >
              Select all eligible on this page ({eligibleOnPage.length})
            </button>

            {selected.length > 0 && (
              <button type="button" className="btn-sm" onClick={() => setSelected([])}>
                Clear
              </button>
            )}

            <div className="spacer" />

            <span className="muted" style={{ fontSize: 12.5 }}>{signingLabel(target)}</span>

            <button
              type="button"
              className="btn-primary"
              disabled={updating || eligibleDevices(selectedCandidates, target).length === 0}
              onClick={() => setConfirming(true)}
            >
              {updating
                ? 'Queueing…'
                : `Update selected (${eligibleDevices(selectedCandidates, target).length})`}
            </button>
          </div>
        )}

        {outcome && (
          <div className={refusals.length > 0 ? 'warn-banner' : 'info-banner'}>
            {outcome}
            {refusals.length > 0 && (
              <ul style={{ margin: '6px 0 0', paddingLeft: 18 }}>
                {refusals.map((r) => (
                  <li key={r.hostname}>
                    {r.hostname}: {r.error}
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}

        {loading && !data && <div className="loading">Loading devices…</div>}

        {data && data.items.length === 0 && (
          <div className="empty-state">
            <Icon name="devices" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">
              {view === 'Retired' ? 'No retired devices' : 'No devices found'}
            </div>
            <div>
              {view === 'Retired' ? (
                'Devices removed from management appear here with their full history.'
              ) : search ? (
                'No device name or hostname matches this search.'
              ) : (
                <>
                  Install the Windows agent on a PC, then approve it under{' '}
                  <Link to="/enrollments">Pending Enrollments</Link>.
                </>
              )}
            </div>
          </div>
        )}

        {data && data.items.length > 0 && (
          <>
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    {target && <th style={{ width: 28 }} aria-label="Select for agent update" />}
                    <th>Name</th>
                    <th>Hostname</th>
                    <th>Status</th>
                    <th>Operating system</th>
                    <th>Agent</th>
                    <th>Last seen</th>
                    <th>Enrolled</th>
                    {canLock && <th aria-label="Screen lock actions" />}
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((device) => {
                    const lockedBadge = deriveLockedBadge(device)
                    return (
                    <tr key={device.id}>
                      {target && (
                        <td>
                          {/* Ineligible rows have no checkbox at all rather than a
                              disabled one: an operator cannot tick a machine the
                              update would be refused on, and hovering says why. */}
                          {ineligibilityReason(toCandidate(device), target) === null ? (
                            <input
                              type="checkbox"
                              checked={selected.includes(device.id)}
                              onChange={() => toggle(device.id)}
                              aria-label={`Select ${deviceName(device)} for agent update`}
                            />
                          ) : (
                            <span
                              className="muted"
                              title={describeIneligibility(
                                ineligibilityReason(toCandidate(device), target)!,
                              )}
                            >
                              —
                            </span>
                          )}
                        </td>
                      )}
                      {/* The administrator's label leads. The Windows hostname
                          keeps its own column so the row still says which
                          physical machine this is. */}
                      <td>
                        <Link to={`/devices/${device.id}`}>{deviceName(device)}</Link>
                      </td>
                      <td className="muted">{device.hostname}</td>
                      <td>
                        {/* Retired is neutral rather than critical: it is an
                            intended end state, not a fault to be investigated. */}
                        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', alignItems: 'center' }}>
                          {device.status === 'Retired' ? (
                            <span className="badge neutral">Retired</span>
                          ) : device.isOnline ? (
                            <span className="badge ok">Online</span>
                          ) : (
                            <span className="badge crit">Offline</span>
                          )}
                          {/* A held machine, from the server's own flag. */}
                          {lockedBadge && <span className={`badge ${lockedBadge.tone}`}>{lockedBadge.label}</span>}
                        </div>
                      </td>
                      <td>{device.operatingSystem ?? '—'}</td>
                      <td>
                        <code>{device.agentVersion}</code>
                        {device.agentUpdateAvailable && (
                          <div className="row-sub">
                            <span className="badge warn">
                              Update available: {device.latestAgentVersion}
                            </span>
                          </div>
                        )}
                      </td>
                      <td>{formatLastSeen(device.lastSeenAt)}</td>
                      <td>{new Date(device.enrolledAt).toLocaleDateString()}</td>
                      {canLock && (
                        <td style={{ textAlign: 'right' }}>
                          {/* Both actions always present so the menu's shape is
                              steady row to row; each says why when it cannot run. */}
                          <RowActionsMenu
                            label={`Screen lock actions for ${deviceName(device)}`}
                            items={[
                              {
                                key: 'lock',
                                label: 'Full-screen lock…',
                                enabled: device.status === 'Active',
                                reason: device.status === 'Active' ? null : 'A retired device cannot be locked',
                              },
                              {
                                key: 'unlock',
                                label: 'Unlock',
                                enabled: device.screenLocked,
                                reason: device.screenLocked ? null : 'This device is not locked',
                              },
                            ]}
                            onSelect={(key) => (key === 'lock' ? setLockTarget(device) : setUnlockTarget(device))}
                          />
                        </td>
                      )}
                    </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>

            <div className="pagination">
              <span>
                {data.totalCount} {view === 'All' ? '' : view.toLowerCase() + ' '}device
                {data.totalCount === 1 ? '' : 's'}
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

      {confirming && target && (
        <ReleaseStepUpDialog
          title="Update agent on selected devices"
          confirmLabel="Queue update"
          busyLabel="Queueing…"
          fallbackError="The update could not be queued."
          onCancel={() => setConfirming(false)}
          onConfirm={runUpdate}
        >
          {describeSelection(selectedCandidates, target)} Each device downloads the installer and
          runs it as SYSTEM, then restarts its own service.{' '}
          {eligibleDevices(selectedCandidates, target)
            .map((d) => d.hostname)
            .join(', ')}
        </ReleaseStepUpDialog>
      )}

      {lockTarget && (
        <ScreenLockDialog
          preset={{ kind: 'device', deviceId: lockTarget.id, name: deviceName(lockTarget) }}
          onCancel={() => setLockTarget(null)}
          onLocked={onLocked}
        />
      )}

      {unlockTarget && (
        <ConfirmDialog
          title={`Unlock ${deviceName(unlockTarget)}?`}
          confirmLabel="Unlock"
          onCancel={() => setUnlockTarget(null)}
          onConfirm={() => void onUnlock()}
        >
          The full-screen lock is lifted and the device returns to the Windows sign-in screen. If it is
          offline, it unlocks when it next connects.
        </ConfirmDialog>
      )}
    </>
  )
}
