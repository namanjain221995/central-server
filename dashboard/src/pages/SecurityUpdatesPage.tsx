import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  ApiError,
  deviceName,
  getDeviceSecurityUpdates,
  getDeviceTasks,
  getSecurityUpdateOverview,
  installAllSecurityUpdates,
  installSecurityUpdates,
  restartDevice,
  scanAllSecurityUpdates,
  scanSecurityUpdates,
  type DeviceSecurityUpdates,
  type SecurityUpdateDeviceRow,
  type SecurityUpdateOverview,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { Icon } from '../components/Icon'
import { TaskProgress } from '../components/TaskProgress'
import { useTaskTracker } from '../components/useTaskTracker'
import {
  formatKbIds,
  formatUpdateSize,
  installRebootRequired,
  nonPreviewUpdateIds,
  severityBadgeClass,
  shouldAutoRescanAfterInstall,
} from './securityUpdatesView'

/**
 * Windows Security Updates.
 *
 * The devices whose last scan found security updates Windows Update is
 * offering, with per-device counts. An administrator expands a device to see
 * its pending updates — each with its KB id(s) and MSRC severity — ticks the
 * ones to install (or "non-preview only" in one click), re-scans, or starts an
 * install — and the page follows each queued task to what Windows actually did,
 * never calling an install done merely because the server accepted it. When an
 * install leaves a reboot pending it offers a restart there and then, and a
 * route to Restart Management for a department.
 *
 * Reading the page needs `update.view`; scanning and installing need
 * `update.manage`. The gates hide controls; the server enforces each one.
 */
export function SecurityUpdatesPage() {
  const { hasPermission } = useAuth()
  const canView = hasPermission('update.view')
  const canManage = hasPermission('update.manage')

  const [overview, setOverview] = useState<SecurityUpdateOverview | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [busyBulk, setBusyBulk] = useState(false)
  const [confirmInstallAll, setConfirmInstallAll] = useState(false)

  const load = useCallback(async () => {
    try {
      setOverview(await getSecurityUpdateOverview())
      setError(null)
    } catch {
      setError('Could not load security updates.')
    }
  }, [])

  // Re-read on a short cadence while a device is expanded (an install may be in
  // flight below) or any device is reboot-pending, so counts and states move
  // without a manual refresh; slower when the fleet is quiet. Per-task progress
  // is the 3s useTaskTracker inside each expanded panel, not this poll.
  const lively = expanded !== null || (overview?.devices.some((d) => d.rebootPending) ?? false)
  useEffect(() => {
    if (!canView) return
    void load()
    const interval = lively ? 10_000 : 30_000
    const timer = setInterval(() => void load(), interval)
    return () => clearInterval(timer)
  }, [canView, lively, load])

  async function onScanAll() {
    setBusyBulk(true)
    setNotice(null)
    setError(null)
    try {
      const { queued } = await scanAllSecurityUpdates()
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
      const { queued } = await installAllSecurityUpdates()
      setNotice(
        queued === 0
          ? 'No eligible non-preview security updates to install.'
          : `Non-preview security update install queued on ${queued} device${queued === 1 ? '' : 's'}. Each device reports its own result.`,
      )
      await load()
    } catch (e) {
      setError(e instanceof ApiError && e.detail ? e.detail : 'Could not queue the install.')
    } finally {
      setBusyBulk(false)
    }
  }

  // Declared after the hooks above so the hook order never changes between
  // renders, mirroring WindowsDriverUpdatesPage.
  if (!canView) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to view Windows security updates.</p>
      </div>
    )
  }

  const summary = overview?.summary
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
          Security updates Windows Update is offering your devices. Expand a device to choose which
          to install, re-scan it, or install the non-preview ones — the platform scans and installs
          on request and never silently reboots. When an install needs a restart, do it here or send
          a whole department through Restart Management.
        </div>
      </div>

      <div className="stat-grid">
        <div className="card stat-card">
          <div className="stat-label">Devices with pending</div>
          <div className="stat-value">{summary ? summary.devicesWithPending : '—'}</div>
        </div>
        <div className="card stat-card">
          <div className="stat-label">Pending updates</div>
          <div className="stat-value">{summary ? summary.totalPending : '—'}</div>
        </div>
        <div className={`card stat-card${summary && summary.devicesRebootPending > 0 ? ' tone-warn' : ''}`}>
          <div className="stat-label">Reboot pending</div>
          <div className="stat-value">{summary ? summary.devicesRebootPending : '—'}</div>
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
          <h2>Devices with pending security updates</h2>
        </div>

        {!overview && <div className="loading">Loading security updates…</div>}

        {overview && devices.length === 0 && (
          <div className="empty-state">
            <Icon name="security" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No pending security updates</div>
            <div>Every device that has reported is up to date on security updates. Use “Scan all” to re-check.</div>
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
                  <th>Pending</th>
                  <th>Preview</th>
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
          title="Install non-preview security updates on all pending devices?"
          confirmLabel="Queue install on all"
          onCancel={() => setConfirmInstallAll(false)}
          onConfirm={() => void onInstallAll()}
        >
          Every device with pending security updates is queued to install its non-preview (not
          optional) updates. Preview updates are left alone. Each device installs on its next
          check-in and reports its own result. Security updates are large downloads and almost
          always need a restart to finish, so expect devices to ask for a reboot afterwards. This
          action is audited.
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
  device: SecurityUpdateDeviceRow
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
        <td className="muted">{device.lastScanAt ? new Date(device.lastScanAt).toLocaleString() : 'never'}</td>
        <td>
          {device.rebootPending ? (
            <span className="badge warn">Yes</span>
          ) : (
            <span className="badge neutral">No</span>
          )}
        </td>
      </tr>
      {open && (
        <tr>
          <td colSpan={7} style={{ background: 'transparent' }}>
            <DeviceSecurityUpdatesPanel device={device} canManage={canManage} onChanged={onChanged} />
          </td>
        </tr>
      )}
    </>
  )
}

// ------------------------------------------------------------- device panel

/**
 * One device's pending security updates: the selectable list, the scan/install
 * actions, live task progress, and the reboot handoff. Loads its own detail and
 * refreshes it whenever a tracked task settles, so the list and the counts above
 * never disagree with what the device has actually done.
 */
function DeviceSecurityUpdatesPanel({
  device,
  canManage,
  onChanged,
}: {
  device: SecurityUpdateDeviceRow
  canManage: boolean
  onChanged: () => Promise<void> | void
}) {
  const [details, setDetails] = useState<DeviceSecurityUpdates | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())
  const [busy, setBusy] = useState(false)
  // Whether the install we queued reported a reboot is now pending, read from
  // the install task's own resultJson. The device detail's rebootPending is the
  // durable source; this is the immediate one, before the detail catches up.
  const [installReboot, setInstallReboot] = useState(false)
  const lastInstallTaskId = useRef<string | null>(null)
  // The install task we have already queued an automatic re-scan for, so an
  // install success triggers exactly one scan and that scan — which settles
  // through onRefreshed like any other task — can never cascade into another.
  const autoRescannedInstallTaskId = useRef<string | null>(null)
  // track() comes from the hook below, which in turn takes onRefreshed, so the
  // callback reaches it through a ref to avoid the declaration cycle (the same
  // shape useTaskTracker itself uses for onRefreshed).
  const trackRef = useRef<((taskId: string, label: string) => void) | null>(null)

  const load = useCallback(async () => {
    try {
      const d = await getDeviceSecurityUpdates(device.deviceId)
      setDetails(d)
      setError(null)
      // An update that is no longer pending leaves the selection too, so an
      // install names only updates the device still offers.
      const ids = new Set(d.updates.map((u) => u.updateId))
      setSelected((cur) => new Set([...cur].filter((id) => ids.has(id))))
    } catch {
      setError('Could not load this device’s security updates.')
    }
  }, [device.deviceId])

  const onRefreshed = useCallback(async () => {
    await load()
    await onChanged()
    // The reboot handoff is driven by the install task's structured result; read
    // it once the task has settled rather than polling it on every tick.
    if (!lastInstallTaskId.current) return
    try {
      const tasks = await getDeviceTasks(device.deviceId)
      const t = tasks.find((x) => x.id === lastInstallTaskId.current)
      if (t && t.status === 'Succeeded') {
        setInstallReboot(installRebootRequired(t.resultJson ?? null))
        // The pending list is rebuilt only by a scan, so a successful install
        // would otherwise keep showing the just-installed updates as pending
        // until someone clicks "Scan now". Re-scan once (guarded to fire once
        // per install) so they drop off on their own; a scan never triggers an
        // install, so this cannot loop.
        if (shouldAutoRescanAfterInstall(t, autoRescannedInstallTaskId.current)) {
          autoRescannedInstallTaskId.current = t.id
          const { taskId } = await scanSecurityUpdates(device.deviceId)
          trackRef.current?.(taskId, 'Re-scan after install')
        }
      }
    } catch {
      // The detail's rebootPending still covers this; a failed read is not fatal.
    }
  }, [load, onChanged, device.deviceId])

  const { tracked, track, dismiss } = useTaskTracker(device.deviceId, !device.isOnline, onRefreshed)
  trackRef.current = track

  useEffect(() => {
    void load()
  }, [load])

  const updates = details?.updates ?? []
  const allSelected = updates.length > 0 && updates.every((u) => selected.has(u.updateId))
  const rebootPending = (details?.rebootPending ?? device.rebootPending) || installReboot

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
      const { taskId } = await scanSecurityUpdates(device.deviceId)
      track(taskId, 'Scan for security updates')
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
      const { taskId } = await installSecurityUpdates(device.deviceId, [...selected])
      lastInstallTaskId.current = taskId
      track(taskId, `Install ${selected.size} security update${selected.size === 1 ? '' : 's'}`)
      await load()
    } catch (e) {
      // 409 problem+json when none are eligible or the agent is too old.
      setError(e instanceof ApiError && e.detail ? e.detail : 'Could not queue the install.')
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

      <TaskProgress tasks={tracked} onDismiss={dismiss} />

      {rebootPending && (
        <div className="warn-banner">
          <strong>Reboot pending.</strong> A security update install needs the device to restart
          before it takes effect.
          {canManage && (
            <>
              {' '}
              <button type="button" className="btn-warning btn-sm" disabled={busy} onClick={() => void onRestart()}>
                <Icon name="restart" size={13} /> Restart now
              </button>
            </>
          )}{' '}
          Or restart a whole department from <Link to="/restart-management">Restart Management</Link>.
        </div>
      )}

      <div className="toolbar" style={{ gap: 12, flexWrap: 'wrap', marginBottom: 12 }}>
        <span className="muted">
          Last scan: {details?.lastScanAt ? new Date(details.lastScanAt).toLocaleString() : 'never'}
        </span>
        <span className="spacer" />
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

      {!details && <div className="loading">Loading pending updates…</div>}

      {details && updates.length === 0 && (
        <div className="empty-state">
          <div className="title">No pending security updates</div>
          <div>
            {canManage
              ? 'Use “Scan now” to re-check this device against Windows Update.'
              : 'This device has no security updates pending from its last scan.'}
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
                    aria-label="Select every pending security update"
                    checked={allSelected}
                    disabled={!canManage || busy}
                    onChange={(e) => toggleAll(e.target.checked)}
                  />
                </th>
                <th>Security update</th>
                <th>KB</th>
                <th>Severity</th>
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
                  <td className="muted">{formatKbIds(u.kbIds)}</td>
                  <td>
                    {u.severity ? (
                      <span className={`badge ${severityBadgeClass(u.severity)}`}>{u.severity}</span>
                    ) : (
                      '—'
                    )}
                  </td>
                  <td>{formatUpdateSize(u.sizeBytes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
