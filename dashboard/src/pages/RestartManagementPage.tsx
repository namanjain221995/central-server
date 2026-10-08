import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  ApiError,
  cancelRestartSchedule,
  createRestartSchedule,
  getGroupRestartSchedules,
  getGroups,
  type DeviceGroupSummary,
  type GroupRestartSchedules,
  type RestartSchedule,
  type RestartScheduleDevice,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { Icon } from '../components/Icon'
import { formatDateTime } from './chromeView'
import { GroupDialog } from './GroupDialogs'
import {
  DEVICE_STATE_LABELS,
  SCHEDULE_DELAY_PRESETS,
  customDelaySeconds,
  describeCancelResult,
  describeRelative,
  describeScheduleDelay,
  describeSchedulePlan,
  deviceStateTone,
  isAcceptedScheduleDelay,
  isCancellable,
  isInFlight,
  scheduleStage,
  scheduleStageTone,
  summarizeDevices,
  type CustomDelayUnit,
} from './restartScheduleView'

/**
 * Restart Management: restart a department at a chosen moment, and call it
 * off -- for the whole department or for chosen devices -- before or after
 * the restart has gone out.
 *
 * A department is a device group, with the Groups page's membership. The
 * server holds the schedule and sends the ordinary restart task to each online
 * member shortly before the moment; until then nothing has reached a device,
 * and cancelling is just deleting the plan. After it has gone out, cancelling
 * goes through each device, and this page shows, per device, how that went.
 * Nothing here decides membership, online state or what a device did: the
 * server names every state, and this page re-reads it on a short cadence
 * while anything is still in flight.
 */
export function RestartManagementPage() {
  const { hasPermission } = useAuth()
  const canRestart = hasPermission('device.restart')

  const [groups, setGroups] = useState<DeviceGroupSummary[]>([])
  const [selectedGroupId, setSelectedGroupId] = useState<string | null>(null)
  const [overview, setOverview] = useState<GroupRestartSchedules | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [dialog, setDialog] = useState<'schedule' | 'cancel-all' | 'cancel-selected' | null>(null)
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set())
  const [busy, setBusy] = useState(false)
  const [now, setNow] = useState(() => new Date())

  const loadGroups = useCallback(async () => {
    try {
      const list = await getGroups()
      setGroups(list)
      setSelectedGroupId((current) =>
        current && list.some((g) => g.id === current) ? current : (list.find((g) => g.isBuiltIn)?.id ?? list[0]?.id ?? null),
      )
    } catch {
      setError('Could not load departments.')
    }
  }, [])

  // The department whose answer is still wanted. A poll that resolves after
  // the selector moved on is dropped, so a card never shows one department's
  // schedule under another's name.
  const wantedGroupId = useRef<string | null>(null)
  wantedGroupId.current = selectedGroupId

  const loadOverview = useCallback(async (groupId: string) => {
    try {
      const result = await getGroupRestartSchedules(groupId)
      if (wantedGroupId.current !== groupId) return
      setOverview(result)
      setError(null)
    } catch {
      if (wantedGroupId.current !== groupId) return
      setOverview(null)
      setError('Could not load the restart schedule for this department.')
    }
  }, [])

  useEffect(() => {
    if (!canRestart) return
    void loadGroups()
  }, [canRestart, loadGroups])

  // Re-read on a short cadence while a restart is scheduled or in flight, so
  // "counting down", "cancel requested" and "restarted" appear without a
  // refresh; slower when nothing is happening. The clock ticks with it, so the
  // relative times stay honest.
  const active = overview?.active ?? null
  const latest = overview?.recent[0] ?? null
  const lively = !!active || (latest !== null && isInFlight(latest))
  useEffect(() => {
    if (!canRestart || !selectedGroupId) return
    void loadOverview(selectedGroupId)
    const interval = lively ? 10_000 : 30_000
    const timer = setInterval(() => {
      setNow(new Date())
      void loadOverview(selectedGroupId)
    }, interval)
    return () => clearInterval(timer)
  }, [canRestart, selectedGroupId, lively, loadOverview])

  // A device that left the table -- moved, retired, or the schedule changed --
  // leaves the selection too; a cancel must name only devices still shown.
  const shown: RestartSchedule | null = active ?? (latest && latest.status === 'Dispatched' ? latest : null)
  useEffect(() => {
    if (!shown) {
      setSelected(new Set())
      return
    }
    const ids = new Set(shown.devices.filter(isCancellable).map((d) => d.deviceId))
    setSelected((current) => new Set([...current].filter((id) => ids.has(id))))
  }, [shown])

  const group = overview?.group ?? null
  const cancellable = useMemo(() => (shown ? shown.devices.filter(isCancellable) : []), [shown])

  async function refresh() {
    setNow(new Date())
    await loadGroups()
    if (selectedGroupId) await loadOverview(selectedGroupId)
  }

  async function onSchedule(delaySeconds: number) {
    if (!selectedGroupId) return
    setBusy(true)
    try {
      await createRestartSchedule(selectedGroupId, delaySeconds)
      setDialog(null)
      setNotice(`Restart scheduled in ${describeScheduleDelay(delaySeconds)}.`)
      await loadOverview(selectedGroupId)
    } catch (e) {
      setError(e instanceof ApiError && e.detail ? e.detail : 'Could not schedule the restart.')
      setDialog(null)
    } finally {
      setBusy(false)
    }
  }

  async function onCancel(deviceIds: string[] | null) {
    if (!shown || !selectedGroupId) return
    setBusy(true)
    try {
      const result = await cancelRestartSchedule(shown.id, deviceIds ?? undefined)
      setDialog(null)
      setSelected(new Set())
      setNotice(describeCancelResult(result.devices, deviceIds === null))
      await loadOverview(selectedGroupId)
    } catch (e) {
      setError(e instanceof ApiError && e.detail ? e.detail : 'Could not cancel the restart.')
      setDialog(null)
    } finally {
      setBusy(false)
    }
  }

  if (!canRestart) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to restart devices.</p>
      </div>
    )
  }

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
          Restart a whole department at a chosen time, or call it off. Departments are your device groups; only devices online
          at the moment are restarted, and each one shows a warning first.
        </div>
      </div>

      <div className="card">
        <div className="toolbar" style={{ gap: 12, flexWrap: 'wrap' }}>
          <label style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <span>Department</span>
            <select
              aria-label="Select department"
              value={selectedGroupId ?? ''}
              onChange={(e) => {
                setSelectedGroupId(e.target.value || null)
                setOverview(null)
                setNotice(null)
              }}
              disabled={groups.length === 0}
            >
              {groups.length === 0 && <option value="">No departments you can see</option>}
              {groups.map((g) => (
                <option key={g.id} value={g.id}>
                  {g.name}
                  {g.isBuiltIn ? ' (devices in no other group)' : ''} · {g.deviceCount} device{g.deviceCount === 1 ? '' : 's'},{' '}
                  {g.onlineCount} online
                </option>
              ))}
            </select>
          </label>
          <span className="spacer" />
          <button type="button" className="btn-ghost btn-sm" onClick={() => void refresh()}>
            <Icon name="refresh" size={14} /> Refresh
          </button>
          {group && !active && (
            <button type="button" className="btn-primary" disabled={group.deviceCount === 0 || busy} onClick={() => setDialog('schedule')}>
              <Icon name="restart" size={15} /> Schedule restart
            </button>
          )}
        </div>
      </div>

      {group && !shown && (
        <div className="card">
          <div className="empty-state">
            <Icon name="restart" size={36} strokeWidth={1.25} className="icon" />
            <div className="title">No restart scheduled for {group.name}</div>
            <div>
              {group.deviceCount === 0
                ? 'This department has no devices. Add some on the Groups page first.'
                : `Schedule one for all ${group.deviceCount} device${group.deviceCount === 1 ? '' : 's'}, with a warning on each device before it restarts.`}
            </div>
          </div>
        </div>
      )}

      {group && shown && (
        <ScheduleCard
          schedule={shown}
          group={group}
          now={now}
          busy={busy}
          selected={selected}
          onToggle={(id) =>
            setSelected((current) => {
              const next = new Set(current)
              if (next.has(id)) next.delete(id)
              else next.add(id)
              return next
            })
          }
          onToggleAll={(on) => setSelected(on ? new Set(cancellable.map((d) => d.deviceId)) : new Set())}
          onCancelAll={() => setDialog('cancel-all')}
          onCancelSelected={() => setDialog('cancel-selected')}
        />
      )}

      {overview && overview.recent.length > 0 && (
        <div className="card">
          <div className="card-header">
            <h2>Recent restarts</h2>
            <div className="muted">The last {overview.recent.length} for {overview.group.name}</div>
          </div>
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Restart at</th>
                  <th>Delay</th>
                  <th>Result</th>
                  <th>Devices</th>
                  <th>Scheduled by</th>
                </tr>
              </thead>
              <tbody>
                {overview.recent.map((s) => {
                  const stage = scheduleStage(s)
                  return (
                    <tr key={s.id}>
                      <td>{formatDateTime(s.restartAt)}</td>
                      <td>{describeScheduleDelay(s.requestedDelaySeconds)}</td>
                      <td>
                        <span className={`badge ${scheduleStageTone(stage)}`}>{stage}</span>
                        {s.status === 'Cancelled' && s.cancelledByDisplay && (
                          <div className="muted" style={{ fontSize: 11.5 }}>
                            by {s.cancelledByDisplay}
                          </div>
                        )}
                        {s.status === 'Missed' && (
                          <div className="muted" style={{ fontSize: 11.5 }}>
                            The server could not send it at the time; nothing was sent.
                          </div>
                        )}
                      </td>
                      <td>{s.status === 'Dispatched' ? summarizeDevices(s.devices) : '—'}</td>
                      <td>{s.createdByDisplay}</td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {dialog === 'schedule' && group && (
        <ScheduleRestartDialog group={group} busy={busy} onCancel={() => setDialog(null)} onConfirm={(delay) => void onSchedule(delay)} />
      )}

      {dialog === 'cancel-all' && shown && (
        <ConfirmDialog
          title={`Cancel the restart for ${shown.groupName}?`}
          confirmLabel="Cancel the restart"
          onCancel={() => setDialog(null)}
          onConfirm={() => void onCancel(null)}
        >
          {shown.canCancelCleanly
            ? 'Nothing has reached any device yet. The restart is simply called off.'
            : 'The restart has already gone out. Each device that still has it pending is told to abort its countdown; devices whose moment has passed are left as they are, and the table shows what each one did.'}
        </ConfirmDialog>
      )}

      {dialog === 'cancel-selected' && shown && (
        <ConfirmDialog
          title={`Cancel the restart for ${selected.size} device${selected.size === 1 ? '' : 's'}?`}
          confirmLabel={`Cancel for ${selected.size}`}
          onCancel={() => setDialog(null)}
          onConfirm={() => void onCancel([...selected])}
        >
          {shown.canCancelCleanly
            ? 'These devices are taken out of the restart. The rest of the department still restarts as scheduled.'
            : 'These devices are told to abort their countdown. The rest of the department still restarts as scheduled.'}
        </ConfirmDialog>
      )}
    </>
  )
}

// ------------------------------------------------------------- schedule card

function ScheduleCard({
  schedule,
  group,
  now,
  busy,
  selected,
  onToggle,
  onToggleAll,
  onCancelAll,
  onCancelSelected,
}: {
  schedule: RestartSchedule
  group: { name: string; deviceCount: number; onlineCount: number }
  now: Date
  busy: boolean
  selected: ReadonlySet<string>
  onToggle: (deviceId: string) => void
  onToggleAll: (on: boolean) => void
  onCancelAll: () => void
  onCancelSelected: () => void
}) {
  const stage = scheduleStage(schedule)
  const pending = schedule.status === 'Pending'
  const cancellable = schedule.devices.filter(isCancellable)
  const anyCancellable = cancellable.length > 0
  const allSelected = anyCancellable && cancellable.every((d) => selected.has(d.deviceId))

  return (
    <div className="card">
      <div className="card-header" style={{ flexWrap: 'wrap', gap: 8 }}>
        <div>
          <h2 style={{ margin: 0 }}>
            {pending ? 'Restart scheduled' : stage === 'In progress' ? 'Restart in progress' : 'Last restart'} · {schedule.groupName}
          </h2>
          <div className="muted">{summarizeDevices(schedule.devices)}</div>
        </div>
        <span className={`badge ${scheduleStageTone(stage)}`}>{stage}</span>
      </div>

      <dl className="kv" style={{ margin: '0 0 12px' }}>
        <dt>Restart at</dt>
        <dd>
          {formatDateTime(schedule.restartAt)} <span className="muted">({describeRelative(schedule.restartAt, now)})</span>
        </dd>
        <dt>Warning</dt>
        <dd>
          Each device shows a {describeScheduleDelay(schedule.warningSeconds)} warning, from{' '}
          {new Date(schedule.dispatchAt).toLocaleTimeString()}
        </dd>
        <dt>Scheduled by</dt>
        <dd>
          {schedule.createdByDisplay} <span className="muted">· {formatDateTime(schedule.createdAt)}</span>
        </dd>
        {schedule.cancelledAt && (
          <>
            <dt>Cancel requested</dt>
            <dd>
              {schedule.cancelledByDisplay} <span className="muted">· {formatDateTime(schedule.cancelledAt)}</span>
            </dd>
          </>
        )}
      </dl>

      {pending && (
        <p className="muted" style={{ margin: '0 0 12px' }}>
          {schedule.canCancelCleanly
            ? `Nothing has reached any device yet. Cancelling before ${new Date(schedule.dispatchAt).toLocaleTimeString()} sends nothing at all.`
            : 'The restart is about to go out to the devices.'}
        </p>
      )}

      <div className="btn-row" style={{ marginBottom: 12 }}>
        <button
          type="button"
          className="btn-danger"
          disabled={busy || (!pending && !anyCancellable)}
          onClick={onCancelAll}
        >
          Cancel for department
        </button>
        <button type="button" className="btn-ghost" disabled={busy || selected.size === 0} onClick={onCancelSelected}>
          Cancel for selected{selected.size > 0 ? ` (${selected.size})` : ''}
        </button>
        {!pending && !anyCancellable && <span className="muted">Nothing left to cancel on any device.</span>}
      </div>

      <div className="table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th style={{ width: 32 }}>
                <input
                  type="checkbox"
                  aria-label="Select every device that can still be cancelled"
                  checked={allSelected}
                  disabled={!anyCancellable || busy}
                  onChange={(e) => onToggleAll(e.target.checked)}
                />
              </th>
              <th>Device</th>
              <th>Status</th>
              <th>Detail</th>
              <th>Agent</th>
            </tr>
          </thead>
          <tbody>
            {schedule.devices.length === 0 && (
              <tr>
                <td colSpan={5} className="muted">
                  No devices in {group.name}.
                </td>
              </tr>
            )}
            {schedule.devices.map((d) => (
              <DeviceRow key={d.deviceId} device={d} now={now} selected={selected.has(d.deviceId)} busy={busy} onToggle={onToggle} />
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function DeviceRow({
  device,
  now,
  selected,
  busy,
  onToggle,
}: {
  device: RestartScheduleDevice
  now: Date
  selected: boolean
  busy: boolean
  onToggle: (deviceId: string) => void
}) {
  const can = isCancellable(device)
  const label = DEVICE_STATE_LABELS[device.state]
  const detail =
    device.state === 'Scheduled' && device.restartAt
      ? `Windows restarts it ${describeRelative(device.restartAt, now)}.`
      : device.state === 'Restarted' && device.restartAt
        ? `Windows restarted it ${describeRelative(device.restartAt, now)}.`
        : (device.detail ?? '')

  return (
    <tr>
      <td>
        <input
          type="checkbox"
          aria-label={`Select ${device.displayName ?? device.hostname}`}
          checked={selected}
          disabled={!can || busy}
          onChange={() => onToggle(device.deviceId)}
        />
      </td>
      <td>
        <div>
          <Link to={`/devices/${device.deviceId}`}>{device.displayName ?? device.hostname}</Link>
        </div>
        {device.displayName && (
          <div className="muted" style={{ fontSize: 11.5 }}>
            {device.hostname}
          </div>
        )}
      </td>
      <td>
        <span className={`badge ${deviceStateTone(device.state)}`}>{label}</span>
        <div className="muted" style={{ fontSize: 11.5 }}>
          {device.isOnline ? 'Online' : 'Offline'}
        </div>
      </td>
      <td style={{ maxWidth: 420 }}>{detail}</td>
      <td>
        {device.agentVersion || '—'}
        {device.supportsOfflineRestart ? (
          <div className="muted" style={{ fontSize: 11.5 }}>
            Offline-safe: restarts on time even without the network
          </div>
        ) : (
          <div className="muted" style={{ fontSize: 11.5 }}>
            Must be online shortly before the restart; update to 1.15.0 for offline-safe restarts
          </div>
        )}
        {!device.supportsCancel && (
          <div className="muted" style={{ fontSize: 11.5 }}>
            Cannot abort a started countdown; update to 1.14.0 or later
          </div>
        )}
      </td>
    </tr>
  )
}

// ---------------------------------------------------------- schedule dialog

function ScheduleRestartDialog({
  group,
  busy,
  onCancel,
  onConfirm,
}: {
  group: { name: string; deviceCount: number; onlineCount: number; warningSeconds: number }
  busy: boolean
  onCancel: () => void
  onConfirm: (delaySeconds: number) => void
}) {
  const [preset, setPreset] = useState<number | 'other'>(SCHEDULE_DELAY_PRESETS[0].seconds)
  const [customValue, setCustomValue] = useState('')
  const [customUnit, setCustomUnit] = useState<CustomDelayUnit>('minutes')

  const delay = preset === 'other' ? customDelaySeconds(customValue, customUnit) : preset
  const valid = delay !== null && isAcceptedScheduleDelay(delay)
  // The server's configured lead time, not a client guess: what the dialog
  // promises about the warning and the clean-cancel deadline is what happens.
  const plan = valid ? describeSchedulePlan(delay, group.onlineCount, group.deviceCount, new Date(), group.warningSeconds) : null

  return (
    <GroupDialog
      title={`Schedule a restart for ${group.name}?`}
      onCancel={onCancel}
      wide
      footer={
        <>
          <button type="button" onClick={onCancel} disabled={busy}>
            Back
          </button>
          <button type="button" className="btn-danger" disabled={!valid || busy} onClick={() => valid && onConfirm(delay)}>
            Schedule restart
          </button>
        </>
      }
    >
      <fieldset className="choice-group" style={{ border: 0, padding: 0, margin: '0 0 10px' }}>
        <legend className="muted" style={{ marginBottom: 6 }}>
          Restart after
        </legend>
        {SCHEDULE_DELAY_PRESETS.map((o) => (
          <label key={o.seconds} style={{ display: 'inline-flex', alignItems: 'center', gap: 6, marginRight: 14, marginBottom: 6 }}>
            <input type="radio" name="schedule-delay" checked={preset === o.seconds} onChange={() => setPreset(o.seconds)} />
            {o.label}
          </label>
        ))}
        <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6, marginRight: 14, marginBottom: 6 }}>
          <input type="radio" name="schedule-delay" checked={preset === 'other'} onChange={() => setPreset('other')} />
          Other
        </label>
      </fieldset>

      {preset === 'other' && (
        <div className="btn-row" style={{ marginBottom: 10 }}>
          <input
            type="number"
            min={1}
            step={1}
            inputMode="numeric"
            aria-label="Amount"
            placeholder="e.g. 90"
            value={customValue}
            onChange={(e) => setCustomValue(e.target.value)}
            style={{ width: 110 }}
          />
          <select aria-label="Unit" value={customUnit} onChange={(e) => setCustomUnit(e.target.value as CustomDelayUnit)}>
            <option value="minutes">minutes</option>
            <option value="hours">hours</option>
          </select>
          {delay !== null && !valid && <span className="muted">Between 1 minute and 7 days.</span>}
          {delay === null && customValue.trim() !== '' && <span className="muted">Whole numbers only.</span>}
        </div>
      )}

      {plan && (
        <div style={{ margin: '0 0 4px' }}>
          {plan.map((line, i) => (
            <p key={i} style={{ margin: '0 0 6px' }} className={i === 0 ? undefined : 'muted'}>
              {i === 0 ? <strong className="secondary">{line}</strong> : line}
            </p>
          ))}
        </div>
      )}
    </GroupDialog>
  )
}
