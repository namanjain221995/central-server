import { useEffect, useId, useMemo, useRef, useState, type ReactNode } from 'react'
import {
  getDeviceTasks,
  getGroupCandidates,
  type DeviceGroupCandidate,
  type DeviceGroupDetail,
  type DeviceTaskItem,
  type GroupAction,
  type GroupActionResult,
} from '../api/client'
import { useDialogFocus } from '../components/dialogFocus'
import { useDialogDismiss } from '../components/useDialogDismiss'
import { Icon } from '../components/Icon'
import { RESTART_DELAY_OPTIONS, describeDuration, RESTART_IMMEDIATE_WARNING_SECONDS } from './restartView'
import {
  DEVICE_STATE_LABELS,
  GROUP_ACTION_VERBS,
  GROUP_AGGREGATE_LABELS,
  actionConfirmTitle,
  aggregate,
  describeCandidate,
  deviceState,
  deviceStateTone,
  isSettled,
  tally,
} from './groupsView'

/**
 * The dialog shell every group dialog uses: the same overlay, focus handling and
 * Escape-to-dismiss as ConfirmDialog, but with a body that may hold a form or a
 * table (ConfirmDialog wraps its body in a paragraph).
 */
export function GroupDialog({
  title,
  children,
  footer,
  onCancel,
  wide = false,
}: {
  title: string
  children: ReactNode
  footer: ReactNode
  onCancel: () => void
  wide?: boolean
}) {
  const titleId = useId()
  const container = useRef<HTMLDivElement>(null)
  useDialogDismiss(onCancel)
  useDialogFocus(container)

  return (
    <div ref={container} className="overlay" role="dialog" aria-modal="true" aria-labelledby={titleId} tabIndex={-1}>
      <div className="dialog" style={{ maxWidth: wide ? 640 : 480 }}>
        <div className="dialog-header">
          <h2 id={titleId}>{title}</h2>
        </div>
        <div className="dialog-body" style={{ fontSize: 13.5 }}>
          {children}
        </div>
        <div className="dialog-footer">{footer}</div>
      </div>
    </div>
  )
}

// ------------------------------------------------------------- device picker

/**
 * Chooses devices for a group. Each row says where the device is now, because
 * a device belongs to exactly one group: picking it here moves it.
 */
function DeviceChecklist({
  candidates,
  targetGroupName,
  selected,
  onToggle,
}: {
  candidates: DeviceGroupCandidate[]
  targetGroupName: string
  selected: ReadonlySet<string>
  onToggle: (id: string) => void
}) {
  const [search, setSearch] = useState('')
  const visible = candidates.filter((c) =>
    `${c.displayName ?? ''} ${c.hostname} ${c.currentGroupName}`.toLowerCase().includes(search.trim().toLowerCase()),
  )

  return (
    <>
      <input
        type="search"
        placeholder="Search devices or groups"
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        style={{ width: '100%', marginBottom: 8 }}
        aria-label="Search devices"
      />
      <div className="table-wrap" style={{ maxHeight: 280, overflowY: 'auto' }}>
        <table className="table">
          <thead>
            <tr>
              <th style={{ width: 32 }} />
              <th>Device</th>
              <th>Status</th>
              <th>Current group</th>
            </tr>
          </thead>
          <tbody>
            {visible.map((c) => (
              <tr key={c.id}>
                <td>
                  <input
                    type="checkbox"
                    aria-label={`Select ${c.displayName ?? c.hostname}`}
                    disabled={c.inThisGroup}
                    checked={c.inThisGroup || selected.has(c.id)}
                    onChange={() => onToggle(c.id)}
                  />
                </td>
                <td>{c.displayName ?? c.hostname}</td>
                <td>
                  <span className={`badge ${c.isOnline ? 'ok' : 'neutral'}`}>{c.isOnline ? 'Online' : 'Offline'}</span>
                </td>
                <td className={c.inThisGroup || c.currentGroupIsBuiltIn ? 'muted' : undefined}>
                  {describeCandidate(c, targetGroupName)}
                </td>
              </tr>
            ))}
            {visible.length === 0 && (
              <tr>
                <td colSpan={4} className="muted">
                  No devices match.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </>
  )
}

function useCandidates(groupId: string) {
  const [candidates, setCandidates] = useState<DeviceGroupCandidate[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    getGroupCandidates(groupId).then(setCandidates).catch(() => setError('Could not load devices.'))
  }, [groupId])
  return { candidates, error }
}

// ------------------------------------------------------------------ create

export function CreateGroupDialog({
  allDevicesGroupId,
  onCancel,
  onCreate,
}: {
  /** Candidates are read against All Devices, which every creating administrator can see. */
  allDevicesGroupId: string
  onCancel: () => void
  onCreate: (name: string, deviceIds: string[]) => Promise<string | null>
}) {
  const { candidates: loaded, error: loadError } = useCandidates(allDevicesGroupId)
  // Read against All Devices, so every device in it comes back "in this group".
  // For a group that does not exist yet no device is already in it, and without
  // this every device in All Devices would be listed but impossible to pick.
  const candidates = useMemo(() => loaded?.map((c) => ({ ...c, inThisGroup: false })) ?? null, [loaded])
  const [name, setName] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const moving = (candidates ?? []).filter((c) => selected.has(c.id) && !c.currentGroupIsBuiltIn)

  async function submit() {
    setSaving(true)
    setError(await onCreate(name.trim(), [...selected]))
    setSaving(false)
  }

  return (
    <GroupDialog
      title="New group"
      wide
      onCancel={onCancel}
      footer={
        <>
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button type="button" className="btn-primary" disabled={!name.trim() || saving} onClick={() => void submit()}>
            Create
          </button>
        </>
      }
    >
      {(error ?? loadError) && (
        <div className="error-banner" role="alert" style={{ marginBottom: 12 }}>
          <Icon name="alert" size={15} />
          <span>{error ?? loadError}</span>
        </div>
      )}
      <div className="field">
        <label className="field-label" htmlFor="new-group-name">
          Group name
        </label>
        <input id="new-group-name" value={name} maxLength={200} onChange={(e) => setName(e.target.value)} placeholder="Developers" />
      </div>
      <div className="field-label" style={{ marginBottom: 6 }}>
        Devices <span className="muted">(optional)</span>
      </div>
      {candidates === null && !loadError && <div className="muted">Loading devices…</div>}
      {candidates && (
        <DeviceChecklist
          candidates={candidates}
          targetGroupName={name.trim() || 'the new group'}
          selected={selected}
          onToggle={(id) => setSelected((s) => toggled(s, id))}
        />
      )}
      {moving.length > 0 && (
        <p className="muted" style={{ margin: '10px 0 0' }}>
          {moving.length} selected device{moving.length === 1 ? ' is' : 's are'} already in another group and will be moved.
          A device belongs to one group at a time.
        </p>
      )}
    </GroupDialog>
  )
}

function toggled(set: ReadonlySet<string>, id: string): Set<string> {
  const next = new Set(set)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  return next
}

// ------------------------------------------------------------- add devices

export function AddDevicesDialog({
  group,
  onCancel,
  onAdd,
}: {
  group: DeviceGroupDetail
  onCancel: () => void
  onAdd: (deviceIds: string[]) => Promise<string | null>
}) {
  const { candidates, error: loadError } = useCandidates(group.id)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  const moving = (candidates ?? []).filter((c) => selected.has(c.id) && !c.currentGroupIsBuiltIn)

  async function submit() {
    setSaving(true)
    setError(await onAdd([...selected]))
    setSaving(false)
  }

  return (
    <GroupDialog
      title={`Add devices to ${group.name}`}
      wide
      onCancel={onCancel}
      footer={
        <>
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button type="button" className="btn-primary" disabled={selected.size === 0 || saving} onClick={() => void submit()}>
            {moving.length > 0 ? `Add and move ${selected.size}` : `Add ${selected.size}`}
          </button>
        </>
      }
    >
      {(error ?? loadError) && (
        <div className="error-banner" role="alert" style={{ marginBottom: 12 }}>
          <Icon name="alert" size={15} />
          <span>{error ?? loadError}</span>
        </div>
      )}
      <p className="muted" style={{ margin: '0 0 10px' }}>
        A device belongs to exactly one group. Adding a device that is in another group moves it here and takes it out of
        that group, along with that group&rsquo;s policies.
      </p>
      {candidates === null && !loadError && <div className="muted">Loading devices…</div>}
      {candidates && (
        <DeviceChecklist
          candidates={candidates}
          targetGroupName={group.name}
          selected={selected}
          onToggle={(id) => setSelected((s) => toggled(s, id))}
        />
      )}
      {moving.length > 0 && (
        <p style={{ margin: '10px 0 0' }}>
          <strong className="secondary">
            {moving.length} device{moving.length === 1 ? '' : 's'} will be moved out of {moving.length === 1 ? 'its' : 'their'} current group.
          </strong>
        </p>
      )}
    </GroupDialog>
  )
}

// -------------------------------------------------------- rename, delete

export function RenameGroupDialog({
  group,
  onCancel,
  onRename,
}: {
  group: DeviceGroupDetail
  onCancel: () => void
  onRename: (name: string) => Promise<string | null>
}) {
  const [name, setName] = useState(group.name)
  const [error, setError] = useState<string | null>(null)

  return (
    <GroupDialog
      title={`Rename ${group.name}`}
      onCancel={onCancel}
      footer={
        <>
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button
            type="button"
            className="btn-primary"
            disabled={!name.trim() || name.trim() === group.name}
            onClick={async () => setError(await onRename(name.trim()))}
          >
            Rename
          </button>
        </>
      }
    >
      {error && (
        <div className="error-banner" role="alert" style={{ marginBottom: 12 }}>
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}
      <div className="field" style={{ marginBottom: 0 }}>
        <label className="field-label" htmlFor="rename-group">
          Group name
        </label>
        <input id="rename-group" value={name} maxLength={200} onChange={(e) => setName(e.target.value)} />
      </div>
    </GroupDialog>
  )
}

export function DeleteGroupDialog({
  group,
  onCancel,
  onDelete,
}: {
  group: DeviceGroupDetail
  onCancel: () => void
  onDelete: () => Promise<string | null>
}) {
  const [error, setError] = useState<string | null>(null)

  return (
    <GroupDialog
      title={`Delete ${group.name}?`}
      onCancel={onCancel}
      footer={
        <>
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button type="button" className="btn-danger" onClick={async () => setError(await onDelete())}>
            Delete group
          </button>
        </>
      }
    >
      {error && (
        <div className="error-banner" role="alert" style={{ marginBottom: 12 }}>
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}
      <p style={{ margin: 0 }}>
        {group.deviceCount === 0 ? (
          <>The group has no devices.</>
        ) : (
          <>
            Its <strong className="secondary">{group.deviceCount}</strong> device{group.deviceCount === 1 ? '' : 's'} will move
            to <strong className="secondary">All Devices</strong>.
          </>
        )}{' '}
        No device is deleted, retired or offboarded. Policies and administrator scope attached to this group stop applying to
        those devices. This action is audited.
      </p>
    </GroupDialog>
  )
}

// ----------------------------------------------------------------- actions

/**
 * Confirms a group action with the real counts. Restart offers exactly the
 * single-device timer choices, and says the one thing an administrator needs
 * to know about a group restart: each device counts down from when it receives
 * its own task, so machines that check in later restart later.
 */
export function GroupActionDialog({
  group,
  action,
  onCancel,
  onConfirm,
}: {
  group: DeviceGroupDetail
  action: GroupAction
  onCancel: () => void
  onConfirm: (delaySeconds: number) => void
}) {
  const [delay, setDelay] = useState(0)
  const offline = group.deviceCount - group.onlineCount
  const { verb, noun } = GROUP_ACTION_VERBS[action]
  const nothingToSend = group.onlineCount === 0

  return (
    <GroupDialog
      title={actionConfirmTitle(action, group.onlineCount)}
      onCancel={onCancel}
      footer={
        <>
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button
            type="button"
            className={action === 'lock' ? 'btn-primary' : 'btn-danger'}
            disabled={nothingToSend}
            onClick={() => onConfirm(action === 'restart' ? delay : 0)}
          >
            {verb} {group.onlineCount} device{group.onlineCount === 1 ? '' : 's'}
          </button>
        </>
      }
    >
      <dl className="kv" style={{ margin: '0 0 12px' }}>
        <dt>Group</dt>
        <dd>{group.name}</dd>
        <dt>Online devices</dt>
        <dd>{group.onlineCount}</dd>
        <dt>Offline devices</dt>
        <dd>{offline}</dd>
      </dl>

      {action === 'restart' && (
        <>
          <fieldset className="choice-group" style={{ border: 0, padding: 0, margin: '0 0 10px' }}>
            <legend className="muted" style={{ marginBottom: 6 }}>
              Restart after
            </legend>
            {RESTART_DELAY_OPTIONS.map((o) => (
              <label key={o.seconds} style={{ display: 'inline-flex', alignItems: 'center', gap: 6, marginRight: 14 }}>
                <input type="radio" name="group-restart-delay" checked={delay === o.seconds} onChange={() => setDelay(o.seconds)} />
                {o.label}
              </label>
            ))}
          </fieldset>
          <p style={{ margin: '0 0 8px' }}>
            <strong className="secondary">
              {delay === 0
                ? `Each device restarts immediately after a ${RESTART_IMMEDIATE_WARNING_SECONDS}-second warning.`
                : `Each device restarts ${describeDuration(delay)} after it receives the restart.`}
            </strong>
          </p>
          <p className="muted" style={{ margin: '0 0 8px' }}>
            The countdown starts on each device when that device receives its task, not when you press Restart, so a device
            that checks in later restarts later. Signed-in users see a notice that their IT administrator has scheduled a
            restart. A restart can be cancelled here until it has been delivered to the device.
          </p>
        </>
      )}

      <p className="muted" style={{ margin: 0 }}>
        {nothingToSend
          ? `No device in this group is online, so there is nothing to ${noun}.`
          : `The ${noun} will be sent only to currently online devices. Offline devices are skipped and will not receive it later.`}{' '}
        This action is audited.
      </p>
    </GroupDialog>
  )
}

export function ForceStopGroupDialog({
  group,
  onCancel,
  onConfirm,
}: {
  group: DeviceGroupDetail
  onCancel: () => void
  onConfirm: (applicationName: string, publisher: string | null) => void
}) {
  const [application, setApplication] = useState('')
  const [publisher, setPublisher] = useState('')

  return (
    <GroupDialog
      title={`Force Stop an application in ${group.name}?`}
      onCancel={onCancel}
      footer={
        <>
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button
            type="button"
            className="btn-danger"
            disabled={!application.trim() || group.onlineCount === 0}
            onClick={() => onConfirm(application.trim(), publisher.trim() || null)}
          >
            Force Stop on {group.onlineCount} device{group.onlineCount === 1 ? '' : 's'}
          </button>
        </>
      }
    >
      <div className="field">
        <label className="field-label" htmlFor="fs-app">
          Application name
        </label>
        <input id="fs-app" value={application} maxLength={384} onChange={(e) => setApplication(e.target.value)} placeholder="Google Chrome" />
      </div>
      <div className="field">
        <label className="field-label" htmlFor="fs-pub">
          Publisher <span className="muted">(optional)</span>
        </label>
        <input id="fs-pub" value={publisher} maxLength={256} onChange={(e) => setPublisher(e.target.value)} placeholder="Google LLC" />
      </div>
      <p className="muted" style={{ margin: 0 }}>
        Each online device stops the application only if its own inventory shows it installed there. Unsaved work in it is
        lost. Offline devices are skipped. This action is audited.
      </p>
    </GroupDialog>
  )
}

// ---------------------------------------------------------------- progress

/**
 * A group action's per-device results, followed to the end.
 *
 * Each device keeps its own state. Nothing is collapsed into one success or
 * failure: the header gives the group's overall result and the counts, and the
 * table says what happened on every device -- including the ones that were
 * offline and never sent anything.
 */
export function GroupActionProgress({
  run,
  onDismiss,
}: {
  run: { action: GroupAction | 'cancel-restart'; result: GroupActionResult }
  onDismiss: () => void
}) {
  const [tasks, setTasks] = useState<Record<string, DeviceTaskItem>>({})
  const [now, setNow] = useState(() => new Date())

  const queued = useMemo(() => run.result.devices.filter((d) => d.outcome === 'Queued' && d.taskId), [run.result])

  const states = run.result.devices.map((d) =>
    deviceState(run.action, d, d.taskId ? tasks[d.taskId] : undefined, now),
  )
  const settled = isSettled(states)

  // Poll each queued device's own task list until none is still waiting. A
  // restart Windows accepted stops being polled -- its task is finished -- but
  // the clock keeps ticking so "scheduled" turns into "restarted" on time.
  useEffect(() => {
    if (settled || queued.length === 0) return
    let cancelled = false
    const poll = async () => {
      const lists = await Promise.all(queued.map((d) => getDeviceTasks(d.deviceId).catch(() => [] as DeviceTaskItem[])))
      if (cancelled) return
      const byId: Record<string, DeviceTaskItem> = {}
      for (const list of lists) for (const t of list) byId[t.id] = t
      setTasks((current) => ({ ...current, ...byId }))
    }
    void poll()
    const timer = setInterval(() => void poll(), 3000)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [queued, settled])

  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 5000)
    return () => clearInterval(timer)
  }, [])

  const overall = aggregate(run.action, states)
  const counts = tally(states)
  const title = run.action === 'cancel-restart' ? 'Cancel pending restarts' : GROUP_ACTION_VERBS[run.action].verb
  const bannerClass =
    overall === 'AllSucceeded' ? 'notice-banner' : overall === 'Failed' ? 'error-banner' : 'info-banner'

  return (
    <div className="card card-section" style={{ marginBottom: 16 }}>
      <div className={bannerClass} role="status" style={{ marginBottom: 10 }}>
        <Icon name={overall === 'AllSucceeded' ? 'check' : overall === 'Failed' ? 'alert' : 'clock'} size={15} />
        <span style={{ flex: 1 }}>
          <strong>
            {title} — {run.result.groupName}
          </strong>
          {': '}
          {GROUP_AGGREGATE_LABELS[overall]}
          {run.result.graceSeconds != null && run.action === 'restart' && (
            <span className="muted"> · {run.result.graceSeconds === RESTART_IMMEDIATE_WARNING_SECONDS ? 'immediate' : describeDuration(run.result.graceSeconds)}</span>
          )}
        </span>
        <button type="button" className="btn-ghost btn-sm" style={{ color: 'inherit' }} onClick={onDismiss}>
          Dismiss
        </button>
      </div>

      {counts.length > 0 && (
        <p style={{ margin: '0 0 8px' }}>
          {counts.map((c, i) => (
            <span key={c.state}>
              {i > 0 && ' · '}
              <strong className="secondary">{c.count}</strong> {c.label}
            </span>
          ))}
        </p>
      )}

      {run.result.devices.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Device</th>
                <th>Result</th>
              </tr>
            </thead>
            <tbody>
              {run.result.devices.map((d, i) => (
                <tr key={d.deviceId}>
                  <td>{d.hostname}</td>
                  <td>
                    <span className={`badge ${deviceStateTone(states[i], run.action)}`}>{DEVICE_STATE_LABELS[states[i]]}</span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

