import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  ApiError,
  deviceName,
  getDeviceDriverUpdates,
  getDeviceTasks,
  getDriverUpdateOverview,
  installAllDriverUpdates,
  installDriverUpdates,
  restartDevice,
  scanAllDriverUpdates,
  scanDriverUpdates,
  suppressDriverUpdate,
  unsuppressDriverUpdate,
  type DeviceDriverUpdates,
  type DriverUpdateDeviceRow,
  type DriverUpdateItem,
  type DriverUpdateOverview,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { Icon } from '../components/Icon'
import { TaskProgress } from '../components/TaskProgress'
import { useTaskTracker } from '../components/useTaskTracker'
import {
  activeInstallMessage,
  chipBadgeClass,
  detailNeedsFollowUp,
  followUpHidden,
  followUpPolled,
  followUpSignature,
  followUpTimerMs,
  followUpVisible,
  formatUpdateSize,
  hiddenUpdatesLabel,
  installRebootRequired,
  needsRestartText,
  nonPreviewUpdateIds,
  overviewCounts,
  pendingRebootCount,
  RE_OFFERED_EXPLANATION,
  recentInstallNote,
  reOfferedSelection,
  reOfferedWarning,
  settlingSelectionNotes,
  showRebootBanner,
  startFollowUp,
  statusChip,
  type FollowUpStop,
} from './driverUpdatesView'

/**
 * Windows Driver Updates.
 *
 * The devices whose last scan found driver updates Windows Update is offering,
 * with per-device counts. An administrator expands a device to see its pending
 * updates, ticks the ones to install (or "non-preview only" in one click),
 * re-scans, or starts an install — and the page follows each queued task to
 * what Windows actually did, never calling an install done merely because the
 * server accepted it. When an install leaves a reboot pending it offers a
 * restart there and then, and a route to Restart Management for a department.
 *
 * Every update carries the server's own verdict (Pending / Installing… /
 * Verifying… / Needs restart to finish / Re-offered by Windows / Failed: why),
 * built from install records the server keeps — so a reload, a closed tab or
 * an old agent never leaves a dead "installing". The server also queues every
 * follow-up rescan itself; this page only reads and words what it reports.
 * Updates Windows keeps re-offering can be hidden per device (and unhidden).
 *
 * Reading the page needs `driver.view`; scanning, installing, hiding and
 * unhiding need `driver.manage`. The gates hide controls; the server enforces
 * each one.
 */
export function WindowsDriverUpdatesPage() {
  const { hasPermission } = useAuth()
  const canView = hasPermission('driver.view')
  const canManage = hasPermission('driver.manage')

  const [overview, setOverview] = useState<DriverUpdateOverview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [busyBulk, setBusyBulk] = useState(false)
  const [confirmInstallAll, setConfirmInstallAll] = useState(false)

  const load = useCallback(async () => {
    try {
      setOverview(await getDriverUpdateOverview())
      setError(null)
    } catch {
      setError('Could not load driver updates.')
    }
  }, [])

  // Re-read on a short cadence while a device is expanded (an install may be in
  // flight below); slower otherwise. A pending reboot does NOT speed it up: a
  // restart can be hours away and the server resolves the verdict on its own,
  // so a 10s poll there would run for as long as the tab stays open. Nothing is
  // fetched while the tab is hidden. Per-task progress is the 3s useTaskTracker
  // inside each expanded panel, not this poll.
  const lively = expanded !== null
  useEffect(() => {
    if (!canView) return
    void load()
    const interval = lively ? 10_000 : 30_000
    const timer = setInterval(() => {
      if (!document.hidden) void load()
    }, interval)
    return () => clearInterval(timer)
  }, [canView, lively, load])

  async function onScanAll() {
    setBusyBulk(true)
    setNotice(null)
    setError(null)
    try {
      const { queued } = await scanAllDriverUpdates()
      setNotice(
        queued === 0
          ? 'No devices needed a scan.'
          : `Scan queued on ${queued} device${queued === 1 ? '' : 's'}. Results appear as each one reports back.`,
      )
      await load()
    } catch (e) {
      setError(e instanceof ApiError && e.detail ? e.detail : 'Could not queue the scan.')
    } finally {
      setBusyBulk(false)
    }
  }

  async function onInstallAll() {
    setConfirmInstallAll(false)
    setBusyBulk(true)
    setNotice(null)
    setError(null)
    try {
      const { queued } = await installAllDriverUpdates()
      setNotice(
        queued === 0
          ? 'No eligible non-preview driver updates to install.'
          : `Non-preview driver install queued on ${queued} device${queued === 1 ? '' : 's'}. Each device reports its own result.`,
      )
      await load()
    } catch (e) {
      setError(e instanceof ApiError && e.detail ? e.detail : 'Could not queue the install.')
    } finally {
      setBusyBulk(false)
    }
  }

  // Declared after the hooks above so the hook order never changes between
  // renders, mirroring RemoteSupportPage/DeviceBitLockerPanel.
  if (!canView) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to view Windows driver updates.</p>
      </div>
    )
  }

  const summary = overview?.summary
  const counts = overviewCounts(overview)
  const devices = overview?.devices ?? []

  return (
    <>
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span style={{ flex: 1 }}>{error}</span>
          <button type="button" className="btn-ghost btn-sm" style={{ color: 'inherit' }} onClick={() => setError(null)}>
            Dismiss
          </button>
        </div>
      )}
      {notice && (
        <div className="notice-banner" role="status">
          <Icon name="info" size={15} />
          <span style={{ flex: 1 }}>{notice}</span>
          <button type="button" className="btn-ghost btn-sm" style={{ color: 'inherit' }} onClick={() => setNotice(null)}>
            Dismiss
          </button>
        </div>
      )}

      <div className="page-header">
        <div className="lede">
          Driver updates Windows Update is offering your devices. Expand a device to choose which to
          install, re-scan it, or install the non-preview ones — the platform scans and installs on
          request and never silently reboots. When an install needs a restart, do it here or send a
          whole department through Restart Management.
        </div>
      </div>

      <div className="stat-grid">
        <div className="card stat-card">
          <div className="stat-label">Devices with pending</div>
          <div className="stat-value">{summary ? summary.devicesWithPending : '—'}</div>
        </div>
        {/* Actionable vs cannot-complete: the number the operator steers by
            excludes updates the platform cannot finish (re-offered or hidden). */}
        <div className="card stat-card">
          <div className="stat-label">Actionable updates</div>
          <div className="stat-value">{counts ? counts.actionable : '—'}</div>
          <div className="stat-note">Pending, not hidden or re-offered</div>
        </div>
        <div className={`card stat-card${counts && counts.cannotComplete > 0 ? ' tone-warn' : ''}`}>
          <div className="stat-label">Cannot complete</div>
          <div className="stat-value">{counts ? counts.cannotComplete : '—'}</div>
          <div className="stat-note">Hidden or re-offered by Windows</div>
        </div>
        <div className={`card stat-card${summary && summary.devicesRebootPending > 0 ? ' tone-warn' : ''}`}>
          <div className="stat-label">Reboot pending</div>
          <div className="stat-value">{summary ? summary.devicesRebootPending : '—'}</div>
          {counts && counts.needsRestart > 0 && (
            <div className="stat-note">{needsRestartText(counts.needsRestart)}</div>
          )}
        </div>
      </div>

      <div className="card">
        <div className="toolbar" style={{ gap: 12, flexWrap: 'wrap' }}>
          <span className="spacer" />
          <button type="button" className="btn-ghost btn-sm" onClick={() => void load()}>
            <Icon name="refresh" size={14} /> Refresh
          </button>
          {canManage && (
            <>
              <button type="button" disabled={busyBulk} onClick={() => void onScanAll()}>
                <Icon name="refresh" size={14} /> Scan all
              </button>
              <button
                type="button"
                className="btn-primary"
                disabled={busyBulk || summary?.devicesWithPending === 0}
                onClick={() => setConfirmInstallAll(true)}
              >
                <Icon name="download" size={14} /> Install all (non-preview)
              </button>
            </>
          )}
        </div>
      </div>

      <div className="card">
        <div className="section-head">
          <h2>Devices with pending driver updates</h2>
        </div>

        {!overview && <div className="loading">Loading driver updates…</div>}

        {overview && devices.length === 0 && (
          <div className="empty-state">
            <Icon name="updates" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No pending driver updates</div>
            <div>Every device that has reported is up to date on drivers. Use “Scan all” to re-check.</div>
          </div>
        )}

        {devices.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th style={{ width: 32 }} aria-label="Expand" />
                  <th>Device</th>
                  <th>Status</th>
                  <th>Actionable</th>
                  <th>Preview</th>
                  <th>Cannot complete</th>
                  <th>Last scan</th>
                  <th>Reboot pending</th>
                </tr>
              </thead>
              <tbody>
                {devices.map((d) => {
                  const isOpen = expanded === d.deviceId
                  return (
                    <DeviceRows
                      key={d.deviceId}
                      device={d}
                      open={isOpen}
                      canManage={canManage}
                      onToggle={() => setExpanded((cur) => (cur === d.deviceId ? null : d.deviceId))}
                      onChanged={load}
                    />
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {confirmInstallAll && (
        <ConfirmDialog
          title="Install non-preview driver updates on all pending devices?"
          confirmLabel="Queue install on all"
          onCancel={() => setConfirmInstallAll(false)}
          onConfirm={() => void onInstallAll()}
        >
          Every device with pending driver updates is queued to install its non-preview (not
          optional) updates. Preview drivers are left alone. Each device installs on its next
          check-in and reports its own result; some may ask for a reboot afterwards. This action is
          audited.
        </ConfirmDialog>
      )}
    </>
  )
}

// --------------------------------------------------------------- device rows

/** The device's summary row, plus a full-width detail row when it is expanded. */
function DeviceRows({
  device,
  open,
  canManage,
  onToggle,
  onChanged,
}: {
  device: DriverUpdateDeviceRow
  open: boolean
  canManage: boolean
  onToggle: () => void
  onChanged: () => Promise<void> | void
}) {
  return (
    <>
      <tr>
        <td>
          <button
            type="button"
            className="btn-ghost btn-sm"
            aria-label={open ? `Collapse ${deviceName(device)}` : `Expand ${deviceName(device)}`}
            aria-expanded={open}
            onClick={onToggle}
          >
            <Icon name={open ? 'chevron-down' : 'chevron-right'} size={14} />
          </button>
        </td>
        <td>
          <button type="button" className="link-button" onClick={onToggle}>
            {deviceName(device)}
          </button>
          {device.displayName && (
            <div className="muted" style={{ fontSize: 11.5 }}>
              {device.hostname}
            </div>
          )}
        </td>
        <td>
          {device.isOnline ? (
            <span className="badge ok">Online</span>
          ) : (
            <span className="badge neutral">Offline</span>
          )}
        </td>
        <td>
          <span className={`badge plain ${device.pendingCount > 0 ? 'warn' : 'neutral'}`}>{device.pendingCount}</span>
        </td>
        <td>
          <span className="badge plain neutral">{device.previewCount}</span>
        </td>
        <td>
          <span className={`badge plain ${(device.cannotCompleteCount ?? 0) > 0 ? 'warn' : 'neutral'}`}>
            {device.cannotCompleteCount ?? 0}
          </span>
        </td>
        <td className="muted">{device.lastScanAt ? new Date(device.lastScanAt).toLocaleString() : 'never'}</td>
        <td>
          {device.rebootPending ? (
            <span className="badge warn">Yes</span>
          ) : (
            <span className="badge neutral">No</span>
          )}
          {(device.needsRestartCount ?? 0) > 0 && (
            <div className="row-sub">{needsRestartText(device.needsRestartCount ?? 0)}</div>
          )}
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={8} style={{ background: 'transparent' }}>
            <DeviceDriverUpdatesPanel device={device} canManage={canManage} onChanged={onChanged} />
          </td>
        </tr>
      )}
    </>
  )
}

// ------------------------------------------------------------- device panel

/**
 * Re-reads one device's detail while the server still has work in flight for it
 * ({@link detailNeedsFollowUp}), on the schedule driverUpdatesView's FollowUpState describes:
 * 10s, backing off to a 60s cap while nothing changes, paused while the page is
 * hidden (and read straight away when it comes back), giving up after 15 minutes
 * of no change. `onStopped` fires once when polling ends on its own -- the detail
 * settled, or it gave up -- so the caller can refresh what sits above it.
 * Changing `restartKey` starts the schedule afresh (a manual Refresh).
 *
 * The timers live here; every scheduling decision is the pure helpers', which
 * is where they are tested.
 */
function useFollowUpPolling(
  following: boolean,
  restartKey: number,
  details: DeviceDriverUpdates | null,
  poll: () => Promise<DeviceDriverUpdates | null>,
  onStopped: (reason: FollowUpStop) => void,
) {
  const detailsRef = useRef(details)
  detailsRef.current = details
  const pollRef = useRef(poll)
  pollRef.current = poll
  const onStoppedRef = useRef(onStopped)
  onStoppedRef.current = onStopped

  useEffect(() => {
    if (!following) return

    let state = startFollowUp(Date.now(), document.hidden)
    let lastSignature = followUpSignature(detailsRef.current)
    let timer: ReturnType<typeof setTimeout> | undefined
    let cancelled = false
    let inFlight = false

    const schedule = () => {
      clearTimeout(timer)
      timer = undefined
      const ms = followUpTimerMs(state, Date.now())
      if (ms !== null) timer = setTimeout(() => void tick(), ms)
    }

    const tick = async () => {
      if (cancelled || inFlight || state.stopped) return
      inFlight = true
      let fresh: DeviceDriverUpdates | null = null
      try {
        fresh = await pollRef.current()
      } finally {
        inFlight = false
      }
      const signature = fresh ? followUpSignature(fresh) : lastSignature
      state = followUpPolled(state, {
        changed: signature !== lastSignature,
        // A failed read settles nothing.
        needsFollowUp: fresh ? detailNeedsFollowUp(fresh) : true,
        now: Date.now(),
      })
      lastSignature = signature
      if (state.stopped) {
        // Even when this very poll tore the effect down (the detail settled and
        // `following` flipped), the overview row above still needs its refresh.
        onStoppedRef.current(state.stopped)
        return
      }
      if (!cancelled) schedule()
    }

    const onVisibility = () => {
      if (cancelled) return
      const now = Date.now()
      if (document.hidden) {
        state = followUpHidden(state, now)
        clearTimeout(timer)
        timer = undefined
      } else {
        state = followUpVisible(state, now)
        // Back in view: read now rather than show a stale list for up to a minute.
        void tick()
      }
    }

    document.addEventListener('visibilitychange', onVisibility)
    schedule()
    return () => {
      cancelled = true
      clearTimeout(timer)
      document.removeEventListener('visibilitychange', onVisibility)
    }
  }, [following, restartKey])
}

/**
 * One device's pending driver updates: the selectable list with each update's
 * install state, the scan/install actions, live task progress, the reboot
 * handoff, recently confirmed installs, and the updates hidden on this device.
 *
 * Loads its own detail and refreshes it whenever a tracked task settles, and
 * keeps re-reading it while the server still has work in flight for the device
 * (an install, the verification scan after it) — that is how a rescan the server
 * queued on its own reaches this list without anyone clicking. A restart still
 * to happen is not polled for (it can be hours away); the panel's Refresh
 * button reads the detail on demand.
 */
function DeviceDriverUpdatesPanel({
  device,
  canManage,
  onChanged,
}: {
  device: DriverUpdateDeviceRow
  canManage: boolean
  onChanged: () => Promise<void> | void
}) {
  const [details, setDetails] = useState<DeviceDriverUpdates | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())
  const [busy, setBusy] = useState(false)
  // Whether the install we queued reported a reboot is now pending, read once
  // from the install task's own resultJson when it settles. It only bridges the
  // gap until the next detail read: from then on the detail's rebootPending (and
  // any "Needs restart to finish" update) is the truth, so the banner goes away
  // once the server has seen the restart.
  const [installReboot, setInstallReboot] = useState(false)
  const lastInstallTaskId = useRef<string | null>(null)
  // Follow-up polling gave up (15 minutes with no change); cleared by Refresh or
  // once the detail no longer needs following. `followRun` restarts the schedule.
  const [followGaveUp, setFollowGaveUp] = useState(false)
  const [followRun, setFollowRun] = useState(0)

  /** Reads the detail; returns it, or null when the read failed (the error banner says so). */
  const load = useCallback(async (): Promise<DeviceDriverUpdates | null> => {
    try {
      const d = await getDeviceDriverUpdates(device.deviceId)
      setDetails(d)
      setError(null)
      if (!detailNeedsFollowUp(d)) setFollowGaveUp(false)
      // The install's own reboot hint has been read; the durable detail wins now.
      if (!lastInstallTaskId.current) setInstallReboot(false)
      // An update that is no longer pending (installed, or hidden) leaves the
      // selection too, so an install names only updates the device still offers.
      const ids = new Set(d.updates.map((u) => u.updateId))
      setSelected((cur) => new Set([...cur].filter((id) => ids.has(id))))
      return d
    } catch {
      setError('Could not load this device’s driver updates.')
      return null
    }
  }, [device.deviceId])

  const onRefreshed = useCallback(async () => {
    await load()
    await onChanged()
    // The immediate reboot hint comes from the install task's structured result,
    // read once the task has settled rather than on every tick. There is no
    // browser-side re-scan: the server queues the verification scan itself (and
    // a newer agent verifies on the device), so the list moves on its own even
    // if this tab is closed.
    if (!lastInstallTaskId.current) return
    try {
      const tasks = await getDeviceTasks(device.deviceId)
      const t = tasks.find((x) => x.id === lastInstallTaskId.current)
      if (t && t.status !== 'Queued' && t.status !== 'Delivered') {
        // Read once: a later settle (the restart, a scan) must not raise it again.
        lastInstallTaskId.current = null
        setInstallReboot(t.status === 'Succeeded' && installRebootRequired(t.resultJson ?? null))
      }
    } catch {
      // The detail's rebootPending still covers this; a failed read is not fatal.
    }
  }, [load, onChanged, device.deviceId])

  const { tracked, track, dismiss } = useTaskTracker(device.deviceId, !device.isOnline, onRefreshed)

  useEffect(() => {
    void load()
  }, [load])

  // Follow the server's own follow-up work until it settles (or goes quiet for
  // 15 minutes); see detailNeedsFollowUp and useFollowUpPolling. When it stops
  // on its own, the overview row above is re-read so its counts catch up.
  const following = detailNeedsFollowUp(details)
  useFollowUpPolling(following, followRun, details, load, (reason) => {
    if (reason === 'gaveUp') setFollowGaveUp(true)
    void onChanged()
  })

  /** Follows afresh: new work (or a Refresh) makes a schedule that gave up current again. */
  function restartFollowUp() {
    setFollowGaveUp(false)
    setFollowRun((n) => n + 1)
  }

  async function onRefresh() {
    restartFollowUp()
    await load()
    await onChanged()
  }

  const updates = details?.updates ?? []
  const hidden = details?.hidden ?? []
  const recent = details?.recentInstalls ?? []
  const activeInstall = details?.activeInstall ?? null
  const allSelected = updates.length > 0 && updates.every((u) => selected.has(u.updateId))
  const rebootPending = showRebootBanner(details, device, installReboot)
  const restartCount = pendingRebootCount(updates)
  const installWarning = reOfferedWarning(reOfferedSelection(updates, selected))
  const settlingNotes = settlingSelectionNotes(updates, selected)

  function toggle(updateId: string) {
    setSelected((cur) => {
      const next = new Set(cur)
      if (next.has(updateId)) next.delete(updateId)
      else next.add(updateId)
      return next
    })
  }

  function toggleAll(on: boolean) {
    setSelected(on ? new Set(updates.map((u) => u.updateId)) : new Set())
  }

  function autoSelectNonPreview() {
    setSelected(new Set(nonPreviewUpdateIds(updates)))
  }

  async function onScan() {
    setBusy(true)
    setError(null)
    try {
      const { taskId } = await scanDriverUpdates(device.deviceId)
      track(taskId, 'Scan for driver updates')
      restartFollowUp()
      await load()
    } catch (e) {
      setError(e instanceof ApiError && e.detail ? e.detail : 'Could not queue the scan.')
    } finally {
      setBusy(false)
    }
  }

  async function onInstall() {
    if (selected.size === 0) return
    setBusy(true)
    setError(null)
    setInstallReboot(false)
    try {
      const { taskId } = await installDriverUpdates(device.deviceId, [...selected])
      lastInstallTaskId.current = taskId
      track(taskId, `Install ${selected.size} driver update${selected.size === 1 ? '' : 's'}`)
      restartFollowUp()
      await load()
    } catch (e) {
      // 409 problem+json when none are eligible or the agent is too old.
      setError(e instanceof ApiError && e.detail ? e.detail : 'Could not queue the install.')
    } finally {
      setBusy(false)
    }
  }

  async function onHide(update: DriverUpdateItem, reason: string) {
    setBusy(true)
    setError(null)
    try {
      await suppressDriverUpdate(device.deviceId, update.updateId, reason)
      // The reload moves it out of the list (and the selection) into Hidden.
      await load()
      await onChanged()
    } catch (e) {
      setError(e instanceof ApiError && e.detail ? e.detail : 'Could not hide this update.')
    } finally {
      setBusy(false)
    }
  }

  async function onUnhide(updateId: string) {
    setBusy(true)
    setError(null)
    try {
      await unsuppressDriverUpdate(device.deviceId, updateId)
      await load()
      await onChanged()
    } catch (e) {
      setError(e instanceof ApiError && e.detail ? e.detail : 'Could not unhide this update.')
    } finally {
      setBusy(false)
    }
  }

  async function onRestart() {
    setBusy(true)
    setError(null)
    try {
      const { taskId } = await restartDevice(device.deviceId, 0)
      track(taskId, 'Restart device')
      await onChanged()
    } catch (e) {
      setError(
        e instanceof ApiError && e.detail
          ? e.detail
          : e instanceof ApiError && e.status === 409
            ? 'A restart is already queued or in progress for this device.'
            : 'Could not queue the restart.',
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card card-section" style={{ marginBottom: 0 }}>
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span style={{ flex: 1 }}>{error}</span>
          <button type="button" className="btn-ghost btn-sm" style={{ color: 'inherit' }} onClick={() => setError(null)}>
            Dismiss
          </button>
        </div>
      )}

      {/* Live progress for tasks queued from this tab. */}
      <TaskProgress tasks={tracked} onDismiss={dismiss} />

      {/* The durable "installing": from the server, so it survives a reload. */}
      {activeInstall && (
        <div className="info-banner" role="status">
          <strong>{activeInstallMessage(activeInstall, device.isOnline)}</strong> Queued{' '}
          {new Date(activeInstall.queuedAt).toLocaleString()}.
        </div>
      )}

      {rebootPending && (
        <div className="warn-banner">
          <strong>Reboot pending.</strong> A driver install needs the device to restart before it
          takes effect.
          {restartCount > 0 && ` ${needsRestartText(restartCount)}.`}
          {canManage && (
            <>
              {' '}
              <button type="button" className="btn-warning btn-sm" disabled={busy} onClick={() => void onRestart()}>
                <Icon name="restart" size={13} /> Restart now
              </button>
            </>
          )}{' '}
          Or restart a whole department from <Link to="/restart-management">Restart Management</Link>.
          {restartCount > 0 &&
            ' Once the device has restarted and checked in, the platform confirms the install on its own — use Refresh to see the result.'}
        </div>
      )}

      {recent.length > 0 && (
        <div className="notice-banner" role="status">
          <Icon name="check" size={15} />
          <div style={{ flex: 1 }}>
            {recentInstallNote(recent.length)}
            <ul style={{ margin: '4px 0 0', paddingLeft: 18 }}>
              {recent.map((r) => (
                <li key={r.updateId}>
                  {r.title}
                  <span style={{ opacity: 0.8 }}> — confirmed {new Date(r.confirmedAt).toLocaleString()}</span>
                </li>
              ))}
            </ul>
          </div>
        </div>
      )}

      <div className="toolbar" style={{ gap: 12, flexWrap: 'wrap', marginBottom: 12 }}>
        <span className="muted">
          Last scan: {details?.lastScanAt ? new Date(details.lastScanAt).toLocaleString() : 'never'}
        </span>
        <span className="spacer" />
        <button
          type="button"
          className="btn-ghost btn-sm"
          aria-label={`Refresh ${deviceName(device)}’s driver updates`}
          onClick={() => void onRefresh()}
        >
          <Icon name="refresh" size={14} /> Refresh
        </button>
        {canManage && (
          <>
            <button type="button" className="btn-ghost btn-sm" disabled={busy} onClick={() => void onScan()}>
              <Icon name="refresh" size={14} /> Scan now
            </button>
            <button type="button" className="btn-ghost btn-sm" disabled={busy || updates.length === 0} onClick={autoSelectNonPreview}>
              Auto-select non-preview
            </button>
            <button
              type="button"
              className="btn-primary btn-sm"
              disabled={busy || selected.size === 0}
              onClick={() => void onInstall()}
            >
              <Icon name="download" size={14} /> Start update{selected.size > 0 ? ` (${selected.size})` : ''}
            </button>
          </>
        )}
      </div>

      {/* Warn before installing what Windows already re-offered; not a block —
          an operator may be retrying deliberately. */}
      {canManage && installWarning && (
        <div className="warn-banner" role="alert">
          <strong>Before you start:</strong> {installWarning}
        </div>
      )}
      {/* Same pattern for a hand-ticked update that is still settling (waiting
          for a restart, or being verified): auto-select leaves these unticked. */}
      {canManage && settlingNotes.length > 0 && (
        <div className="warn-banner" role="alert">
          <strong>Before you start:</strong> {settlingNotes.join(' ')}
        </div>
      )}

      {following && followGaveUp && (
        <p className="muted" role="status" style={{ fontSize: 12.5, margin: '0 0 12px' }}>
          Stopped checking automatically after 15 minutes without a change — use Refresh to check again.
        </p>
      )}

      {!details && <div className="loading">Loading pending updates…</div>}

      {details && updates.length === 0 && (
        <div className="empty-state">
          <div className="title">No pending driver updates</div>
          <div>
            {canManage
              ? 'Use “Scan now” to re-check this device against Windows Update.'
              : 'This device has no driver updates pending from its last scan.'}
          </div>
        </div>
      )}

      {details && updates.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th style={{ width: 32 }}>
                  <input
                    type="checkbox"
                    aria-label="Select every pending driver update"
                    checked={allSelected}
                    disabled={!canManage || busy}
                    onChange={(e) => toggleAll(e.target.checked)}
                  />
                </th>
                <th>Driver update</th>
                <th>Status</th>
                <th>Class</th>
                <th>Provider</th>
                <th>Driver date</th>
                <th>Size</th>
              </tr>
            </thead>
            <tbody>
              {updates.map((u) => (
                <tr key={`${u.updateId}:${u.revision}`}>
                  <td>
                    <input
                      type="checkbox"
                      aria-label={`Select ${u.title}`}
                      checked={selected.has(u.updateId)}
                      disabled={!canManage || busy}
                      onChange={() => toggle(u.updateId)}
                    />
                  </td>
                  <td>
                    {u.title}
                    {u.isPreview && (
                      <span className="badge neutral" style={{ marginLeft: 8 }}>
                        Preview
                      </span>
                    )}
                  </td>
                  <td>
                    <UpdateStatusCell
                      update={u}
                      canManage={canManage}
                      busy={busy}
                      onHide={(update, reason) => void onHide(update, reason)}
                    />
                  </td>
                  <td>{u.driverClass ?? '—'}</td>
                  <td>{u.driverProvider ?? '—'}</td>
                  <td className="muted">{u.driverVerDate ? new Date(u.driverVerDate).toLocaleDateString() : '—'}</td>
                  <td>{formatUpdateSize(u.sizeBytes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Hidden per device, platform-side only; collapsed so the list stays the list. */}
      {hidden.length > 0 && (
        <details className="evidence" style={{ fontSize: 'inherit', marginTop: 16 }}>
          <summary className="muted">{hiddenUpdatesLabel(hidden)}</summary>
          <p className="muted" style={{ fontSize: 12.5, margin: '6px 0 8px' }}>
            Hidden on this device by the platform, so they no longer count as actionable. The laptop’s
            own Windows Update settings are unchanged.
          </p>
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Driver update</th>
                  <th>Reason</th>
                  <th>Hidden by</th>
                  <th>Hidden</th>
                  {canManage && <th aria-label="Actions" />}
                </tr>
              </thead>
              <tbody>
                {hidden.map((h) => (
                  <tr key={h.updateId}>
                    <td>{h.title}</td>
                    <td>{h.reason ?? '—'}</td>
                    <td>{h.hiddenBy ?? '—'}</td>
                    <td className="muted">{new Date(h.hiddenAt).toLocaleString()}</td>
                    {canManage && (
                      <td>
                        <button
                          type="button"
                          className="btn-ghost btn-sm"
                          disabled={busy}
                          aria-label={`Unhide ${h.title}`}
                          onClick={() => void onUnhide(h.updateId)}
                        >
                          Unhide
                        </button>
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </details>
      )}
    </div>
  )
}

// --------------------------------------------------------------- status cell

/**
 * One update's Status cell: the server's verdict as a chip, and the attempt
 * count once the platform has tried it. A "Re-offered by Windows" chip expands
 * to the plain-words explanation and, for `driver.manage`, a Hide action with an
 * optional reason.
 */
function UpdateStatusCell({
  update,
  canManage,
  busy,
  onHide,
}: {
  update: DriverUpdateItem
  canManage: boolean
  busy: boolean
  onHide: (update: DriverUpdateItem, reason: string) => void
}) {
  const [reason, setReason] = useState('')
  const chip = statusChip(update)
  const status = update.installStatus ?? 'none'
  const attempts = update.attempts ?? 0
  const badge = (
    <span className={`badge ${chipBadgeClass(chip.tone)}`} style={{ whiteSpace: 'normal', maxWidth: 320 }}>
      {chip.label}
    </span>
  )

  return (
    <>
      {status === 'reOffered' ? (
        <details className="evidence" style={{ fontSize: 'inherit', marginTop: 0 }}>
          <summary aria-label={`${chip.label}: why, and what to do`}>{badge}</summary>
          <div style={{ maxWidth: 460, whiteSpace: 'normal', padding: '6px 0 8px', fontSize: 12.5 }}>
            <p style={{ margin: '0 0 8px' }}>{RE_OFFERED_EXPLANATION}</p>
            {canManage && (
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
                <input
                  type="text"
                  aria-label={`Reason for hiding ${update.title} (optional)`}
                  placeholder="Reason (optional)"
                  value={reason}
                  maxLength={200}
                  disabled={busy}
                  onChange={(e) => setReason(e.target.value)}
                  style={{ flex: '1 1 180px', minWidth: 0 }}
                />
                <button type="button" className="btn-sm" disabled={busy} onClick={() => onHide(update, reason)}>
                  Hide this update
                </button>
              </div>
            )}
          </div>
        </details>
      ) : (
        badge
      )}
      {attempts > 0 && status !== 'none' && (
        <div className="row-sub">
          Tried {attempts} time{attempts === 1 ? '' : 's'}
          {update.lastAttemptAt ? ` · last ${new Date(update.lastAttemptAt).toLocaleString()}` : ''}
        </div>
      )}
    </>
  )
}
