import { useCallback, useEffect, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { signingLabel, upgradeTargets } from './agentUpdateView'
import {
  RUNNING_FILTERS,
  categoryLabel,
  confidenceLabel,
  confidenceTone,
  emptySoftwareListMessage,
  evidenceSourceLabel,
  forceStopMessage,
  isApplicationRow,
  matchesRunningFilter,
  registryViewLabel,
  reportsRunningState,
  rowActions,
  scopeLabel,
  signerLabel,
  softwareRowKey,
  type RunningFilter,
} from './softwareView'
import { forceStopApplication, restartDevice } from '../api/client'
import {
  RESTART_DELAY_OPTIONS,
  describeRestartTiming,
  restartStage,
} from './restartView'
import { useAuth } from '../auth/AuthContext'
import { RowActionsMenu } from '../components/RowActionsMenu'
import { DeviceUsersPanel } from './DeviceUsersPanel'
import { DeviceGroupsPanel } from './DeviceGroupsPanel'
import { DeviceServicesPanel } from './DeviceServicesPanel'
import { DeviceProcessesPanel } from './DeviceProcessesPanel'
import { isModule, type DeviceModule } from './deviceModules'
import { DeviceUsbPanel } from './DeviceUsbPanel'
import { DeviceDriversPanel } from './DeviceDriversPanel'
import { DeviceBitLockerPanel } from './DeviceBitLockerPanel'
import { TaskProgress } from '../components/TaskProgress'
import { useTaskTracker } from '../components/useTaskTracker'
import {
  ApiError,
  cancelDeviceTask,
  getAgentReleases,
  getLatestAgentRelease,
  updateDeviceAgent,
  type AgentReleaseRow,
  type LatestAgentRelease,
  getDevice,
  getDeviceTasks,
  deviceName,
  offboardDevice,
  queueDeviceAction,
  reactivateDevice,
  requestInventoryRefresh,
  type DeviceDetail,
  type DeviceTaskItem,
} from '../api/client'

import { Icon, type IconName } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { EditDeviceNameDialog } from './EditDeviceNameDialog'

/** One navigation card in the device feature grid. */
interface FeatureSpec {
  key: Tab
  label: string
  icon: IconName
  desc: string
  /** Omitted where the device has not reported anything to count. */
  count?: number
}

/**
 * The module list and this alias now come from one place, so a module cannot
 * exist as a type without also being accepted by the URL guard. See
 * deviceModules.ts for why that mattered.
 */
type Tab = DeviceModule

type ActionKey = 'restart' | 'shutdown' | 'lock' | 'signout'

const ACTION_LABELS: Record<ActionKey, string> = {
  restart: 'Restart device',
  shutdown: 'Shut down device',
  lock: 'Lock screen',
  signout: 'Sign out user',
}

interface ActionSpec {
  key: ActionKey
  label: string
  perm: string
  /** Whether the action interrupts someone, and so warrants a confirmation. */
  confirm: boolean
}

function formatBytes(bytes: number | null): string {
  if (bytes == null) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`
}

function formatTimestamp(value: string | null): string {
  return value ? new Date(value).toLocaleString() : '—'
}

/** "20 seconds ago". Falls back to an absolute date once it stops being useful. */
function relativeSince(value: string | null): string {
  if (!value) return 'never'
  const seconds = Math.floor((Date.now() - new Date(value).getTime()) / 1000)
  if (seconds < 5) return 'just now'
  if (seconds < 60) return `${seconds} seconds ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} minutes ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} hours ago`
  return new Date(value).toLocaleDateString()
}

/**
 * The "we have not heard from the machine yet" state, which every inventory tab
 * needs. Says what to do about it rather than only reporting absence.
 */
function InventoryEmpty({ title, detail }: { title: string; detail?: string }) {
  return (
    <div className="card">
      <div className="empty-state">
        <Icon name="inbox" size={40} strokeWidth={1.25} className="icon" />
        <div className="title">{title}</div>
        <div>{detail ?? 'Use "Refresh inventory" and wait for the agent’s next heartbeat.'}</div>
      </div>
    </div>
  )
}

export function DeviceDetailPage() {
  const { deviceId } = useParams<{ deviceId: string }>()
  const { hasPermission } = useAuth()
  const [device, setDevice] = useState<DeviceDetail | null>(null)
  const [tasks, setTasks] = useState<DeviceTaskItem[]>([])
  const [confirm, setConfirm] = useState<null | 'restart' | 'shutdown' | 'lock' | 'signout'>(null)
  // The delay chosen in the restart dialog, in seconds; 0 is "now". Reset each
  // time the dialog opens so a timer picked for one restart never carries over
  // silently to the next.
  const [restartDelay, setRestartDelay] = useState(0)
  const [actionMsg, setActionMsg] = useState<string | null>(null)
  const [editingName, setEditingName] = useState(false)
  const [latestAgent, setLatestAgent] = useState<LatestAgentRelease | null>(null)
  const [releases, setReleases] = useState<AgentReleaseRow[]>([])
  const [targetReleaseId, setTargetReleaseId] = useState<string>('')
  const [softwareSearch, setSoftwareSearch] = useState('')
  // Frameworks, inbox apps and system components are reported and categorised
  // by the endpoint, and hidden here by default: the count in the tab is the
  // number the device reported, the list is what an operator manages.
  const [showSystemSoftware, setShowSystemSoftware] = useState(false)
  // Running state is what the last inventory saw, so "Not running" is a claim
  // about that inventory and not about now — the footnote by the control says so.
  const [runningFilter, setRunningFilter] = useState<RunningFilter>('all')
  // The row whose request is in flight, by row key rather than name: the same
  // application installed for two users is two rows, and only one is busy.
  const [busy, setBusy] = useState<{ key: string; label: string } | null>(null)
  // Advanced by the page's existing reload tick. Only the Tasks tab reads it,
  // to decide whether a scheduled restart's moment has passed.
  const [now, setNow] = useState(() => new Date())

  // The published agent release, for the "update agent" affordance. Fetched
  // once per page visit: releases change rarely, and the compare is cheap.
  useEffect(() => {
    getLatestAgentRelease().then(setLatestAgent).catch(() => setLatestAgent(null))

    // The full list too, so the operator can choose a target rather than only
    // taking whatever happens to be latest. Both are cheap and change rarely.
    // A failure here simply leaves no targets, which the panel states plainly.
    getAgentReleases().then(setReleases).catch(() => setReleases([]))
  }, [])

  // The open module lives in the query string rather than component state, so
  // Back returns to the feature grid instead of leaving the device entirely,
  // and a link to a specific module can be shared. No module in the URL means
  // the grid — which is the landing view, not a tab that happens to be first.
  const [searchParams, setSearchParams] = useSearchParams()
  const moduleParam = searchParams.get('m')
  const tab: Tab | null = isModule(moduleParam) ? moduleParam : null

  const openModule = useCallback(
    (next: Tab) => setSearchParams({ m: next }),
    [setSearchParams],
  )
  const closeModule = useCallback(() => setSearchParams({}), [setSearchParams])

  const [error, setError] = useState<string | null>(null)
  const [refreshRequested, setRefreshRequested] = useState(false)

  const load = useCallback(async () => {
    if (!deviceId) return
    try {
      const [d, t] = await Promise.all([
        getDevice(deviceId),
        getDeviceTasks(deviceId).catch(() => [] as DeviceTaskItem[]),
      ])
      setDevice(d)
      setTasks(t)
      setError(null)
    } catch {
      setError('Could not load this device.')
    }
  }, [deviceId])

  // Tracks queued actions to their terminal state, so the page reports what the
  // machine actually did rather than that the server accepted a request.
  const { tracked, track, dismiss } = useTaskTracker(
    deviceId ?? '',
    device ? !device.isOnline : false,
    load,
  )

  async function runAction(action: 'restart' | 'shutdown' | 'lock' | 'signout') {
    if (!deviceId) return
    setConfirm(null)
    setActionMsg(null)
    try {
      // Restart carries the chosen delay; the other three carry nothing. The
      // task is tracked from the id either way, so the banner reports what
      // Windows did, never merely that the server accepted a request.
      const { taskId } = action === 'restart'
        ? await restartDevice(deviceId, restartDelay)
        : await queueDeviceAction(deviceId, action)
      track(taskId, ACTION_LABELS[action])
      await load()
    } catch (e) {
      // The server's own reason when it gave one: a restart already queued
      // for this device is a 409 that names it, and a refused delay is a 400
      // that says what is accepted. Neither is "could not queue".
      setActionMsg(e instanceof ApiError && e.detail
        ? `Could not queue "${action}": ${e.detail}`
        : `Could not queue "${action}".`)
    }
  }

  async function onCancelTask(taskId: string) {
    if (!deviceId) return
    setActionMsg(null)
    try {
      await cancelDeviceTask(deviceId, taskId)
      setActionMsg('Task cancelled.')
    } catch (e) {
      setActionMsg(
        e instanceof ApiError && e.status === 409
          ? 'Too late to cancel — the task was already delivered to the agent or has finished.'
          : e instanceof ApiError && e.status === 403
            ? 'You do not have permission to cancel this type of task.'
            : 'The task could not be cancelled.',
      )
    } finally {
      await load()
    }
  }

  const [confirmRemoval, setConfirmRemoval] = useState(false)

  async function onRemoveDevice() {
    setConfirmRemoval(false)
    if (!deviceId) return
    try {
      await offboardDevice(deviceId)
      setActionMsg(
        'Device retired: credentials revoked and it is no longer under active management. '
        + 'Its record and history remain under the Retired view.',
      )
      await load()
    } catch {
      setActionMsg('Could not retire the device.')
    }
  }

  async function onReactivate() {
    if (!deviceId) return
    try {
      await reactivateDevice(deviceId)
      setActionMsg('Device reactivated. The machine must re-enroll to obtain a fresh credential.')
      await load()
    } catch {
      setActionMsg('Could not reactivate the device.')
    }
  }

  useEffect(() => {
    void load()
    const timer = setInterval(() => {
      // The same tick drives both the reload and the clock the Tasks tab reads,
      // so a scheduled restart's badge advances without a second timer.
      setNow(new Date())
      void load()
    }, 30_000)
    return () => clearInterval(timer)
  }, [load])

  async function onRefreshInventory() {
    if (!deviceId) return
    try {
      await requestInventoryRefresh(deviceId)
      setRefreshRequested(true)
      await load()
    } catch {
      setError('Inventory refresh request failed.')
    }
  }

  if (error && !device) {
    return (
      <div className="error-banner" role="alert">
        <Icon name="alert" size={15} />
        <span>{error}</span>
      </div>
    )
  }

  if (!device) {
    return <div className="loading">Loading device…</div>
  }

  // Counts come from the loaded inventory only. A module whose count the device
  // has not reported shows a description instead of a fabricated zero — "0
  // services" would be a claim about the machine, not an absence of data.
  const features: FeatureSpec[] = [
    { key: 'overview', label: 'Overview', icon: 'devices', desc: 'device status' },
    { key: 'hardware', label: 'Hardware', icon: 'software', desc: 'hardware info' },
    { key: 'network', label: 'Network', icon: 'updates', desc: 'interfaces',
      count: device.networkInterfaces.length },
    { key: 'users', label: 'Users', icon: 'users', desc: 'local accounts',
      count: device.localUsers.length },
    { key: 'groups', label: 'Groups', icon: 'groups', desc: 'local groups',
      count: device.localGroups.length },
    { key: 'software', label: 'Software', icon: 'software', desc: 'installed SW',
      count: device.software.length },
    { key: 'security', label: 'Security', icon: 'security', desc: 'posture' },
    // No count: the number of attached USB devices is loaded by the module
    // itself, and a stale count on the card would be worse than none.
    { key: 'usb', label: 'USB', icon: 'usb', desc: 'peripherals & storage' },
    // No counts: both are loaded by their own module, and a count here would
    // either force an extra request on every device page or show a stale number.
    { key: 'drivers', label: 'Drivers', icon: 'settings', desc: 'driver health' },
    { key: 'bitlocker', label: 'BitLocker', icon: 'shield-check', desc: 'disk encryption' },
    { key: 'updates', label: 'Updates', icon: 'updates', desc: 'Windows Update',
      count: device.windowsUpdate?.history.length },
    { key: 'services', label: 'Services', icon: 'settings', desc: 'services',
      count: device.services.length },
    { key: 'processes', label: 'Processes', icon: 'tasks', desc: 'running procs',
      count: device.processes.length },
    { key: 'actions', label: 'Actions', icon: 'key', desc: 'device actions' },
    { key: 'tasks', label: 'Tasks', icon: 'audit', desc: 'task history', count: tasks.length },
  ]

  const openFeature = tab ? features.find((f) => f.key === tab) : undefined

  // Grouped by consequence. "Lock screen" is recoverable in a second; "Shut
  // down" needs someone physically present to undo. Presenting them as one flat
  // row of identical buttons is what makes the wrong one easy to click.
  const sessionActions: ActionSpec[] = [
    { key: 'lock', label: 'Lock screen', perm: 'device.lock', confirm: false },
    { key: 'signout', label: 'Sign out user', perm: 'device.sign_out_user', confirm: true },
  ]
  const powerActions: ActionSpec[] = [
    { key: 'restart', label: 'Restart', perm: 'device.restart', confirm: true },
    { key: 'shutdown', label: 'Shut down', perm: 'device.shutdown', confirm: true },
  ]

  const canRename = hasPermission('device.rename')
  const canDeploy = hasPermission('software.deploy')
  // Force Stop terminates a process, so it needs the same permission as doing
  // that directly rather than a weaker software-view one. It is the only action
  // the software table offers, so it alone decides whether the column exists.
  const canExecuteTasks = hasPermission('task.execute')
  const showSoftwareActions = canExecuteTasks

  // The software table's rows, filtered once. Doing it inside the <tbody> left
  // nothing else able to tell whether any row survived, so the three controls
  // could empty the table and still render a header over nothing; a second copy
  // of these predicates elsewhere is how a table and its empty state start
  // disagreeing about what exists.
  const softwareTerm = softwareSearch.trim().toLowerCase()
  const visibleSoftware = device.software
    .filter((sw) => showSystemSoftware || isApplicationRow(sw.category))
    .filter((sw) => matchesRunningFilter(sw, runningFilter))
    .filter((sw) =>
      softwareTerm === ''
      || sw.name.toLowerCase().includes(softwareTerm)
      || (sw.publisher ?? '').toLowerCase().includes(softwareTerm))
  // Asked of the whole inventory, not the filtered rows: whether this agent
  // reports running state at all is what separates "nothing is running" from
  // "nothing here can ever be shown as running".
  const softwareEmpty = emptySoftwareListMessage({
    filter: runningFilter,
    searching: softwareTerm !== '',
    hidingSystem: !showSystemSoftware,
    reportsRunningState: reportsRunningState(device.software),
  })

  /**
   * Asks the server to stop an application by name.
   *
   * The name and publisher are all that is sent; the server resolves them to
   * process ids from its own inventory, so this cannot ask for an arbitrary
   * process to be terminated.
   */
  async function onForceStop(rowKey: string, name: string, publisher: string | null) {
    if (!device) return

    setBusy({ key: rowKey, label: 'Stopping…' })
    setActionMsg(null)
    try {
      const result = await forceStopApplication([device.id], name, publisher)
      const outcome = result.devices[0]

      setActionMsg(outcome
        ? forceStopMessage(name, outcome.outcome)
        : `${name} could not be stopped.`)
    } catch {
      setActionMsg(`${name} could not be stopped.`)
    } finally {
      setBusy(null)
    }
  }

  // Every published release this device could legitimately move to, newest
  // first. The rules -- retired, not-newer, unpublished, unreadable version --
  // live in agentUpdateView and are unit tested there; this page had its own
  // inline copy of the comparison, which is now gone.
  const targets = device
    ? upgradeTargets(
        {
          deviceId: device.id,
          hostname: device.hostname,
          agentVersion: device.agentVersion,
          status: device.status,
        },
        releases,
      )
    : []

  const chosen = targets.find((r) => r.id === targetReleaseId) ?? targets[0]

  // The server re-checks all of this and returns 409 regardless; offering only
  // reachable targets just avoids inviting a guaranteed refusal.
  const agentOutdated = targets.length > 0

  async function onUpdateAgent() {
    if (!deviceId || !chosen) return
    setActionMsg(null)
    try {
      const { taskId } = await updateDeviceAgent(deviceId, chosen.id)
      track(taskId, `Update agent to ${chosen.version}`)
      await load()
    } catch (e) {
      setActionMsg(
        e instanceof ApiError && e.status === 409
          ? 'The update was refused — the release is not newer than the installed agent, or is no longer published.'
          : 'The agent update could not be queued.',
      )
    }
  }
  const visibleSession = sessionActions.filter((a) => hasPermission(a.perm))
  const visiblePower = powerActions.filter((a) => hasPermission(a.perm))

  function actionButton(a: ActionSpec) {
    return (
      <button
        key={a.key}
        type="button"
        className={a.confirm ? 'btn-warning' : undefined}
        onClick={() => {
          if (!a.confirm) {
            void runAction(a.key)
            return
          }
          // Every restart starts from "now"; a timer is chosen deliberately each time.
          if (a.key === 'restart') setRestartDelay(0)
          setConfirm(a.key)
        }}
      >
        {a.label}
      </button>
    )
  }

  return (
    <>
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}
      {actionMsg && (
        <div className="notice-banner" role="status">
          <Icon name="check" size={15} />
          <span>{actionMsg}</span>
        </div>
      )}

      {editingName && (
        <EditDeviceNameDialog
          deviceId={device.id}
          hostname={device.hostname}
          currentDisplayName={device.displayName}
          onCancel={() => setEditingName(false)}
          onSaved={async () => {
            setEditingName(false)
            // Reload rather than patching local state: the server is what decides
            // whether a blank cleared the label, so its answer is the truth.
            await load()
          }}
        />
      )}

      {confirm === 'restart' && (
        <ConfirmDialog
          title={`Restart ${deviceName(device)}?`}
          confirmLabel="Yes, restart"
          onCancel={() => setConfirm(null)}
          onConfirm={() => void runAction('restart')}
        >
          <>
            {/* The timer is chosen here, in the open, and repeated back in a
                sentence before the destructive button. What is chosen becomes
                the grace period the device hands to Windows; nothing about the
                timing is decided anywhere else. */}
            <fieldset className="choice-group" style={{ border: 0, padding: 0, margin: '0 0 12px' }}>
              <legend className="muted" style={{ marginBottom: 6 }}>When</legend>
              {RESTART_DELAY_OPTIONS.map((o) => (
                <label key={o.seconds} style={{ display: 'inline-flex', alignItems: 'center', gap: 6, marginRight: 14 }}>
                  <input
                    type="radio"
                    name="restart-delay"
                    value={o.seconds}
                    checked={restartDelay === o.seconds}
                    onChange={() => setRestartDelay(o.seconds)}
                  />
                  {o.label}
                </label>
              ))}
            </fieldset>
            <p style={{ margin: '0 0 8px' }}>
              <strong className="secondary">{describeRestartTiming(restartDelay)}</strong>
            </p>
            The device performs this on its next check-in; if it has not checked in within 15
            minutes the task expires and nothing restarts. Unsaved work on the device is lost.
            This action is audited.
          </>
        </ConfirmDialog>
      )}

      {confirm && confirm !== 'restart' && (
        <ConfirmDialog
          title={`Confirm ${confirm}`}
          confirmLabel={`Yes, ${confirm}`}
          onCancel={() => setConfirm(null)}
          onConfirm={() => void runAction(confirm)}
        >
          <>
            This queues a <strong className="secondary">{confirm}</strong> task for{' '}
            <strong className="secondary">{device.hostname}</strong>. The device performs it on its
            next check-in. This action is audited.
          </>
        </ConfirmDialog>
      )}

      <div className="detail-header">
        <div className="breadcrumb">
          <Link to="/devices">Devices</Link>
          <Icon name="chevron-right" size={12} />
          <span>{deviceName(device)}</span>
        </div>
        <div className="detail-title">
          {/* The administrator's label is the headline. The real hostname sits
              in the facts strip below, so the machine is still identifiable. */}
          <h1>{deviceName(device)}</h1>
          {device.status === 'Retired' ? (
            <span className="badge neutral">Retired</span>
          ) : (
            <span className="badge ok">Active</span>
          )}
          <span className="spacer" />
          {canRename && (
            <button type="button" className="btn-sm" onClick={() => setEditingName(true)}>
              <Icon name="edit" size={14} />
              Edit Name
            </button>
          )}
          <button
            type="button"
            onClick={() => void onRefreshInventory()}
            disabled={device.inventoryRefreshPending}
          >
            <Icon name="refresh" size={14} />
            {device.inventoryRefreshPending
              ? 'Inventory refresh pending…'
              : refreshRequested
                ? 'Refresh again'
                : 'Refresh inventory'}
          </button>
        </div>
        <div className="detail-facts">
          {/* Shown only when a label is set: without one the heading already
              is the hostname, and repeating it says nothing. */}
          {device.displayName && (
            <span className="fact">
              <span className="fact-label">Hostname</span>
              <span className="fact-value">{device.hostname}</span>
            </span>
          )}
          <span className="fact">
            <span className="fact-label">OS</span>
            <span className="fact-value">{device.operatingSystem ?? '—'}</span>
          </span>
          <span className="fact">
            <span className="fact-label">Agent</span>
            <span className="fact-value">{device.agentVersion}</span>
          </span>
          <span className="fact">
            <span className="fact-label">Signed in</span>
            <span className="fact-value">{device.loggedOnUser ?? '—'}</span>
          </span>
          <span className="fact">
            <span className="fact-label">Last seen</span>
            {/* Relative, because "is this machine reachable right now" is the
                question being asked. The exact timestamp is on the tooltip and
                in Overview for when it matters. */}
            <span className="fact-value" title={formatTimestamp(device.lastSeenAt)}>
              {relativeSince(device.lastSeenAt)}
            </span>
          </span>
        </div>
      </div>

      {/* The feature grid IS the device navigation. When a module is open the
          grid is replaced rather than kept above it, so the module gets the
          whole content area and there is exactly one way back. */}
      {!tab && (
        <>
          <h2 className="section-label">Device features</h2>
          <div className="feature-grid">
            {features.map((f) => (
              <button
                key={f.key}
                type="button"
                className="feature-card"
                onClick={() => openModule(f.key)}
              >
                <span className="feature-icon">
                  <Icon name={f.icon} size={17} />
                </span>
                <span className="feature-body">
                  <span className="feature-name">{f.label}</span>
                  {f.count !== undefined && <span className="feature-count">{f.count}</span>}
                  <span className="feature-desc">{f.desc}</span>
                </span>
              </button>
            ))}
          </div>
        </>
      )}

      {openFeature && (
        <div className="module-header">
          <button type="button" className="btn-ghost btn-sm" onClick={closeModule}>
            <Icon name="chevron-left" size={14} />
            Device Menu
          </button>
          <span className="back-divider" />
          <h2>{openFeature.label}</h2>
          {openFeature.count !== undefined && (
            <span className="badge neutral plain">{openFeature.count}</span>
          )}
        </div>
      )}

      {tab === 'overview' && (
        <div className="card">
          <h2>Identity</h2>
          <dl className="kv">
            {/* Both, always, and labelled — this is the one place where an
                administrator should be able to see exactly which name is the
                console's and which one is the machine's. */}
            <dt>Display name</dt>
            <dd>
              {device.displayName ?? <span className="muted">Not set — showing hostname</span>}
            </dd>
            <dt>Hostname</dt>
            <dd>{device.hostname}</dd>
            <dt>Operating system</dt>
            <dd>{device.operatingSystem ?? '—'}</dd>
            <dt>Logged-on user</dt>
            <dd>{device.loggedOnUser ?? '—'}</dd>
            <dt>Agent version</dt>
            <dd>
              <code>{device.agentVersion}</code>
            </dd>
            <dt>Last seen</dt>
            <dd>{formatTimestamp(device.lastSeenAt)}</dd>
            <dt>Enrolled</dt>
            <dd>{formatTimestamp(device.enrolledAt)}</dd>
            <dt>Inventory collected</dt>
            <dd>{formatTimestamp(device.inventoryCollectedAt)}</dd>
            {/* The SMBIOS UUID: how this record survives a rename or a rebuild. */}
            <dt>Machine identifier</dt>
            <dd>
              <code>{device.machineIdentifier}</code>
            </dd>
          </dl>
        </div>
      )}

      {tab === 'hardware' && (
        <>
          {!device.hardware && <InventoryEmpty title="No hardware inventory yet" />}
          {device.hardware && (
            <>
              <div className="card card-section">
                <h2>System</h2>
                <dl className="kv">
                  <dt>Manufacturer</dt>
                  <dd>{device.hardware.manufacturer ?? '—'}</dd>
                  <dt>Model</dt>
                  <dd>{device.hardware.model ?? '—'}</dd>
                  <dt>Serial number</dt>
                  <dd>{device.hardware.serialNumber ?? '—'}</dd>
                  <dt>CPU</dt>
                  <dd>{device.hardware.cpuName ?? '—'}</dd>
                  <dt>Cores</dt>
                  <dd>
                    {device.hardware.cpuPhysicalCores ?? '—'} physical /{' '}
                    {device.hardware.cpuLogicalProcessors ?? '—'} logical
                  </dd>
                  <dt>Memory</dt>
                  <dd>{formatBytes(device.hardware.totalMemoryBytes)}</dd>
                </dl>
              </div>

              <div className="card card-section">
                <h2>Disks</h2>
                {!device.hardware.disks?.length && (
                  <div className="empty-state">
                    <div className="title">No fixed volumes reported</div>
                  </div>
                )}
                {!!device.hardware.disks?.length && (
                  <div className="table-wrap">
                    <table className="table">
                      <thead>
                        <tr>
                          <th>Volume</th>
                          <th>File system</th>
                          <th>Size</th>
                          <th>Free</th>
                          <th>Used</th>
                        </tr>
                      </thead>
                      <tbody>
                        {device.hardware.disks.map((disk) => {
                          const usedPct =
                            disk.sizeBytes > 0
                              ? Math.round(((disk.sizeBytes - disk.freeBytes) / disk.sizeBytes) * 100)
                              : 0
                          return (
                            <tr key={disk.name}>
                              <td>{disk.name}</td>
                              <td>{disk.fileSystem ?? '—'}</td>
                              <td>{formatBytes(disk.sizeBytes)}</td>
                              <td>{formatBytes(disk.freeBytes)}</td>
                              <td>
                                {/* A full disk is an operational problem, so the
                                    thresholds escalate rather than just reporting. */}
                                <span
                                  className={`badge plain ${
                                    usedPct >= 90 ? 'crit' : usedPct >= 75 ? 'warn' : 'ok'
                                  }`}
                                >
                                  {usedPct}%
                                </span>
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
          )}
        </>
      )}

      {tab === 'users' && <DeviceUsersPanel deviceId={deviceId!} deviceName={device.hostname} />}

      {tab === 'groups' && <DeviceGroupsPanel deviceId={deviceId!} />}

      {tab === 'software' && (
        <>
          {!device.software.length && <InventoryEmpty title="No software inventory yet" />}
          {!!device.software.length && (
            <div className="card">
              {/* A Remove is a task like a service stop: the banner stays in its
                  pending style until the device has reported fresh inventory,
                  so a green Succeeded never sits above a row that is still there. */}
              <TaskProgress tasks={tracked} onDismiss={dismiss} />
              <div className="card-header">
                <h2>Installed applications</h2>
                <div style={{ display: 'flex', gap: 12, alignItems: 'center' }}>
                  <label className="checkbox-inline">
                    <input
                      type="checkbox"
                      checked={showSystemSoftware}
                      onChange={(e) => setShowSystemSoftware(e.target.checked)}
                    />
                    Show Windows components
                  </label>
                  <div className="input-search" style={{ maxWidth: 260 }}>
                    <Icon name="search" size={15} className="search-icon" />
                    <input
                      type="search"
                      placeholder="Search applications…"
                      aria-label="Search installed applications"
                      value={softwareSearch}
                      onChange={(e) => setSoftwareSearch(e.target.value)}
                    />
                  </div>
                </div>
              </div>
              {/* Running is what the last inventory saw, not what is true now,
                  and the control says so next to the choice rather than in a
                  tooltip: "Not running" is otherwise read as a live fact. Rows
                  the inventory could not judge appear only under All. */}
              <div style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap', marginBottom: 12 }}>
                <div
                  className="segmented"
                  role="tablist"
                  aria-label="Filter by running state"
                  style={{ marginBottom: 0 }}
                >
                  {RUNNING_FILTERS.map((f) => (
                    <button
                      key={f.key}
                      type="button"
                      role="tab"
                      aria-selected={runningFilter === f.key}
                      className={runningFilter === f.key ? 'active' : undefined}
                      onClick={() => setRunningFilter(f.key)}
                    >
                      {f.label}
                    </button>
                  ))}
                </div>
                <span className="muted" style={{ fontSize: 12 }}>
                  Running state is as of the last inventory ({formatTimestamp(device.inventoryCollectedAt)}).
                </span>
              </div>
              {/* Either the table or a reason it is empty, never a header over
                  nothing: on a device whose agent predates process evidence,
                  both filtered views match no row at all, and a bare header
                  reads as a page that failed to load. */}
              {visibleSoftware.length === 0 ? (
                <div className="empty-state">
                  <Icon name="inbox" size={40} strokeWidth={1.25} className="icon" />
                  <div className="title">{softwareEmpty.title}</div>
                  <div>{softwareEmpty.detail}</div>
                </div>
              ) : (
                <div className="scroll-y table-wrap">
                  <table className="table">
                    <thead>
                      <tr>
                        <th>Application</th>
                        <th>Version</th>
                        <th>Publisher</th>
                        <th>Type</th>
                        <th>Installed for</th>
                        <th>Signer</th>
                        <th>Found in</th>
                        {showSoftwareActions && <th style={{ textAlign: 'right' }}>Actions</th>}
                      </tr>
                    </thead>
                    <tbody>
                      {visibleSoftware.map((sw) => (
                        // Keyed by user as well as name and version: one machine
                        // legitimately holds the same application once per user,
                        // and keying without the user collides on exactly those
                        // rows.
                        <tr key={softwareRowKey(sw)}>
                          <td>
                            {sw.name}
                            {sw.confidence === 'Observed' && (
                              <span className={`badge ${confidenceTone(sw.confidence)}`} style={{ marginLeft: 8 }}>
                                {confidenceLabel(sw.confidence)}
                              </span>
                            )}
                            {/* Why the endpoint believes this application exists,
                                in the order it reported: installation records
                                first, then what attached to them. Collapsed, so
                                the list stays a list. */}
                            {sw.evidence && sw.evidence.length > 0 && (
                              <details className="evidence">
                                <summary className="muted">
                                  {sw.evidence.length} piece{sw.evidence.length === 1 ? '' : 's'} of evidence
                                </summary>
                                <ul className="evidence-list">
                                  {sw.evidence.map((e, index) => (
                                    <li key={`${e.source}|${e.detail ?? ''}|${index}`}>
                                      <strong>{evidenceSourceLabel(e.source)}</strong>
                                      {e.name ? ` — ${e.name}` : ''}
                                      {e.detail ? <span className="muted"> ({e.detail})</span> : null}
                                    </li>
                                  ))}
                                </ul>
                              </details>
                            )}
                          </td>
                          <td>{sw.version ?? '—'}</td>
                          <td>{sw.publisher ?? '—'}</td>
                          <td>{categoryLabel(sw.category)}</td>
                          <td>{scopeLabel(sw)}</td>
                          {/* Presence of a signature, not trust in it. */}
                          <td title={sw.signerSubject ?? undefined}>{signerLabel(sw.signerSubject, sw.signatureStatus)}</td>
                          {/* Not the binary's architecture: 64-bit products
                              routinely register under WOW6432Node. */}
                          <td>{registryViewLabel(sw.architecture)}</td>
                          {showSoftwareActions && (
                            <td style={{ textAlign: 'right' }}>
                              {/* Which actions the row supports, and why not, is
                                  decided in softwareView: Force Stop needs an
                                  install path as its only link to a process. An
                                  unsupported action stays in the menu, disabled,
                                  with its reason. */}
                              <RowActionsMenu
                                label={`Actions for ${sw.name}`}
                                items={rowActions(sw, { canExecuteTasks })}
                                triggerLabel={busy?.key === softwareRowKey(sw) ? busy.label : 'Actions'}
                                disabled={busy !== null}
                                onSelect={(key) => {
                                  if (key === 'force-stop') {
                                    void onForceStop(softwareRowKey(sw), sw.name, sw.publisher)
                                  }
                                }}
                              />
                            </td>
                          )}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          )}
        </>
      )}

      {tab === 'usb' && <DeviceUsbPanel deviceId={device.id} offline={!device.isOnline} />}

      {tab === 'drivers' && <DeviceDriversPanel deviceId={device.id} />}

      {tab === 'bitlocker' && <DeviceBitLockerPanel deviceId={device.id} />}

      {tab === 'security' && (
        <>
          {!device.securityPosture && (
            <InventoryEmpty
              title="No security posture yet"
              detail="Use “Refresh inventory” and wait for the agent’s next heartbeat. Some checks (BitLocker, TPM) require the agent to run elevated."
            />
          )}
          {device.securityPosture &&
            (() => {
              const p = device.securityPosture
              // "Unknown" is kept visually distinct from "Fail". An unelevated
              // agent that cannot read TPM state has not found a problem, and
              // showing it as a failure would send someone chasing nothing.
              const check = (label: string, v: boolean | null, extra?: string) => (
                <>
                  <dt>{label}</dt>
                  <dd>
                    {v == null ? (
                      <span className="badge neutral">Unknown — agent may need elevation</span>
                    ) : v ? (
                      <span className="badge ok">Pass</span>
                    ) : (
                      <span className="badge crit">Fail</span>
                    )}
                    {extra ? <span className="muted" style={{ marginLeft: 8, fontSize: 12.5 }}>{extra}</span> : null}
                  </dd>
                </>
              )
              return (
                <div className="card">
                  <div className="card-header">
                    <h2>Compliance</h2>
                    {p.complianceScore == null ? (
                      <span className="badge neutral">Score unknown</span>
                    ) : (
                      <span
                        className={`badge ${
                          p.complianceScore >= 80 ? 'ok' : p.complianceScore >= 50 ? 'warn' : 'crit'
                        }`}
                        style={{ fontSize: 13 }}
                      >
                        {p.complianceScore}% compliant
                      </span>
                    )}
                  </div>
                  <dl className="kv">
                    {check('Microsoft Defender antivirus', p.defenderAntivirusEnabled)}
                    {check('Defender real-time protection', p.defenderRealtimeProtectionEnabled)}
                    {check(
                      'Defender signatures fresh (≤7d)',
                      p.defenderSignatureAgeDays == null ? null : p.defenderSignatureAgeDays <= 7,
                      p.defenderSignatureAgeDays == null ? undefined : `${p.defenderSignatureAgeDays}d old`,
                    )}
                    {check('Firewall (Domain)', p.firewallDomainEnabled)}
                    {check('Firewall (Private)', p.firewallPrivateEnabled)}
                    {check('Firewall (Public)', p.firewallPublicEnabled)}
                    {check('Secure Boot', p.secureBootEnabled)}
                    {check('TPM enabled', p.tpmEnabled, p.tpmSpecVersion ? `v${p.tpmSpecVersion}` : undefined)}
                    {check(
                      'BitLocker (system drive)',
                      p.bitLockerSystemDriveStatus == null
                        ? null
                        : p.bitLockerSystemDriveStatus === 'On',
                      p.bitLockerSystemDriveStatus ?? undefined,
                    )}
                    <dt>Local administrators</dt>
                    <dd>{p.localAdministratorCount ?? '—'}</dd>
                  </dl>
                </div>
              )
            })()}
        </>
      )}

      {tab === 'updates' && (
        <>
          {!device.windowsUpdate && (
            <InventoryEmpty
              title="No update data yet"
              detail="Refresh inventory and wait for the agent’s next heartbeat. History is read from the local Windows Update store."
            />
          )}
          {device.windowsUpdate &&
            (() => {
              const u = device.windowsUpdate
              return (
                <div className="card">
                  <div className="detail-facts" style={{ marginTop: 0, paddingTop: 0, borderTop: 'none' }}>
                    <span className="fact">
                      <span className="fact-label">Reboot pending</span>
                      {u.rebootRequired ? (
                        <span className="badge warn">Yes</span>
                      ) : (
                        <span className="badge ok">No</span>
                      )}
                    </span>
                    <span className="fact">
                      <span className="fact-label">Failed updates</span>
                      <span className={`badge plain ${u.failedUpdateCount > 0 ? 'crit' : 'neutral'}`}>
                        {u.failedUpdateCount}
                      </span>
                    </span>
                    <span className="fact">
                      <span className="fact-label">Reported</span>
                      <span className="fact-value">{new Date(u.collectedAt).toLocaleString()}</span>
                    </span>
                  </div>
                  <hr className="divider" />
                  {u.history.length === 0 ? (
                    <div className="empty-state">
                      <div className="title">No update history recorded</div>
                    </div>
                  ) : (
                    <div className="scroll-y table-wrap">
                      <table className="table">
                        <thead>
                          <tr>
                            <th>Update</th>
                            <th>Operation</th>
                            <th>Result</th>
                            <th>Date</th>
                          </tr>
                        </thead>
                        <tbody>
                          {u.history.map((h, i) => (
                            <tr key={i}>
                              <td>{h.title}</td>
                              <td>{h.operation}</td>
                              <td>
                                {h.result === 'Succeeded' ? (
                                  <span className="badge ok">Succeeded</span>
                                ) : h.result === 'Failed' || h.result === 'Aborted' ? (
                                  <span className="badge crit">{h.result}</span>
                                ) : (
                                  <span className="badge neutral">{h.result}</span>
                                )}
                              </td>
                              <td className="muted">
                                {h.date ? new Date(h.date).toLocaleString() : '—'}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </div>
              )
            })()}
        </>
      )}

      {tab === 'services' && (
        <>
          {!device.services.length && (
            <InventoryEmpty
              title="No service inventory yet"
              detail="Refresh inventory and wait for the next heartbeat."
            />
          )}
          {!!device.services.length && (
            <DeviceServicesPanel
              deviceId={device.id}
              services={device.services}
              offline={!device.isOnline}
              onInventoryChanged={load}
            />
          )}
        </>
      )}

      {tab === 'processes' && (
        <>
          {!device.processes.length && (
            <InventoryEmpty
              title="No process snapshot yet"
              detail="Refresh inventory and wait for the next heartbeat."
            />
          )}
          {!!device.processes.length && (
            <DeviceProcessesPanel
              deviceId={device.id}
              processes={device.processes}
              offline={!device.isOnline}
              onInventoryChanged={load}
            />
          )}
        </>
      )}

      {tab === 'actions' && (
        <div className="card">
          <h2>Device actions</h2>
          <p className="muted" style={{ fontSize: 13, marginTop: 0, maxWidth: 620 }}>
            Each action is delivered as a typed task the device pulls on its next check-in. Actions
            you lack permission for are hidden; the server enforces this regardless.
          </p>

          {/* An offline machine accepts nothing right now. Saying so up front
              beats letting someone queue a restart and wonder why nothing
              happened — the task waits, and that is exactly what this says. */}
          {!device.isOnline && device.status !== 'Retired' && (
            <div className="warn-banner">
              <strong>Device offline.</strong> Actions queued now will wait and run when the agent
              reconnects — last seen {relativeSince(device.lastSeenAt)}.
            </div>
          )}

          <TaskProgress tasks={tracked} onDismiss={dismiss} />

          {visibleSession.length === 0 && visiblePower.length === 0 && !hasPermission('device.retire') && (
            <div className="empty-state">
              <Icon name="key" size={36} strokeWidth={1.25} className="icon" />
              <div className="title">No actions available</div>
              <div>Your role grants no device actions on this machine.</div>
            </div>
          )}

          {visibleSession.length > 0 && (
            <div className="action-group">
              <h3>Session</h3>
              <p className="group-note">
                Affects whoever is signed in right now. Locking is immediate and harmless; signing a
                user out closes their applications and can lose unsaved work.
              </p>
              <div className="btn-row">{visibleSession.map(actionButton)}</div>
            </div>
          )}

          {visiblePower.length > 0 && (
            <div className="action-group">
              <h3>Power</h3>
              <p className="group-note">
                Interrupts the machine. A shut-down device cannot be restarted remotely — someone
                has to be physically present to power it back on.
              </p>
              <div className="btn-row">{visiblePower.map(actionButton)}</div>
            </div>
          )}

          {canDeploy && agentOutdated && chosen && (
            <div className="action-group">
              <h3>Agent</h3>
              <p className="group-note">
                This device runs agent <code>{device.agentVersion}</code>. The agent downloads the
                MSI itself, verifies its SHA-256 against the hash the server recorded — and its
                signature when the release carries one — then restarts into the new version;
                identity, enrollment and credential are preserved.
              </p>

              <div className="field" style={{ maxWidth: 360 }}>
                <label htmlFor="agent-target">Target version</label>
                <select
                  id="agent-target"
                  value={chosen.id}
                  onChange={(e) => setTargetReleaseId(e.target.value)}
                >
                  {targets.map((r) => (
                    <option key={r.id} value={r.id}>
                      {r.version}
                    </option>
                  ))}
                </select>
                {/* Reported, not enforced here. The agent always re-checks the
                    SHA-256 over the bytes it downloaded; it checks a signature
                    only when the release declares a signer. Saying exactly that
                    beats implying a refusal that will not happen -- under the
                    Internal trust model no release declares a signer, and the
                    endpoint installs on hash verification alone. */}
                <span className="muted">{signingLabel(chosen)}</span>
              </div>

              <div className="btn-row">
                <button type="button" className="btn-primary" onClick={() => void onUpdateAgent()}>
                  Update agent to {chosen.version}
                </button>
              </div>
            </div>
          )}

          {/* A device with nothing newer available should say so rather than
              showing an empty section or implying an update is pending. */}
          {canDeploy && !agentOutdated && device.status !== 'Retired' && (
            <div className="action-group">
              <h3>Agent</h3>
              <p className="group-note">
                Running agent <code>{device.agentVersion}</code>.
                {latestAgent?.version
                  ? ` No newer published release is available (latest is ${latestAgent.version}).`
                  : ' No published release is available.'}
              </p>
            </div>
          )}

          {hasPermission('device.retire') && (
            <div className="action-group destructive">
              <h3>Retire Device</h3>
              <p className="group-note">
                Retires this machine from active management: its credentials are revoked and it can
                no longer check in, receive tasks, or take agent updates. The device record and its
                full history are kept under the Retired view — nothing is wiped on the machine
                itself, and this is not the same as uninstalling the agent. Reinstalling the agent
                later produces a new pending enrollment and, once approved, a separate new device.
              </p>
              {device.status === 'Retired' ? (
                <button type="button" onClick={() => void onReactivate()}>
                  Reactivate device
                </button>
              ) : (
                <button type="button" className="btn-danger" onClick={() => setConfirmRemoval(true)}>
                  <Icon name="trash" size={14} />
                  Retire device
                </button>
              )}
            </div>
          )}

          {confirmRemoval && (
            <ConfirmDialog
              title={`Retire ${deviceName(device)}?`}
              confirmLabel="Yes, retire device"
              onCancel={() => setConfirmRemoval(false)}
              onConfirm={() => void onRemoveDevice()}
            >
              <>
                <strong className="secondary">{deviceName(device)}</strong>
                {device.hostname && device.hostname !== deviceName(device)
                  ? ` (${device.hostname})`
                  : ''}{' '}
                will be retired from active management. Its credentials are revoked immediately and
                it stops appearing in the Active devices view, though its record, inventory, tasks,
                audit history and any escrowed recovery keys are all kept and stay readable under
                the Retired view. It will no longer be eligible for management actions or agent
                updates.{' '}
                <strong className="secondary">
                  This is not the same as uninstalling the Windows agent
                </strong>
                {' '}— nothing on the machine is changed or wiped. If the agent is later
                reinstalled there, it will appear as a new pending enrollment and must be approved
                in the normal way, which creates a separate new device record rather than reviving
                this one. This action is audited.
              </>
            </ConfirmDialog>
          )}
        </div>
      )}

      {tab === 'tasks' && (
        <div className="card">
          <h2>Queued and completed tasks</h2>
          {!tasks.length && (
            <div className="empty-state">
              <Icon name="tasks" size={40} strokeWidth={1.25} className="icon" />
              <div className="title">No tasks yet</div>
              <div>Queue an action from the Actions tab to see it here.</div>
            </div>
          )}
          {!!tasks.length && (
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Task</th>
                    <th>Status</th>
                    <th>Queued by</th>
                    <th>Queued</th>
                    <th>Result</th>
                    <th style={{ textAlign: 'right' }}></th>
                  </tr>
                </thead>
                <tbody>
                  {tasks.map((t) => {
                    // A restart has one stage the generic model lacks: Windows
                    // accepted it and the machine is still up. That is
                    // "Scheduled", amber, not a green "Succeeded". Every other
                    // type shows the server's status as it is.
                    //
                    // `now` comes from the page's own 30-second reload rather
                    // than a timer of its own: the badge flips from Scheduled to
                    // Succeeded on the first refresh after Windows was due to
                    // act, which is as prompt as this page is about anything.
                    const stage = t.type === 'RestartDevice' ? restartStage(t, now) : t.status
                    return (
                    <tr key={t.id}>
                      <td>{t.type}</td>
                      <td>
                        {/* Anything still in flight is amber, not green: a queued
                            task has not happened on the machine yet. */}
                        <span
                          className={`badge ${
                            stage === 'Succeeded'
                              ? 'ok'
                              : stage === 'Failed' || stage === 'Expired'
                                ? 'crit'
                                : stage === 'Cancelled'
                                  ? 'neutral'
                                  : 'warn'
                          }`}
                        >
                          {stage}
                        </span>
                      </td>
                      <td>{t.createdByDisplay}</td>
                      <td>{new Date(t.createdAt).toLocaleString()}</td>
                      <td className="muted">{t.resultMessage ?? '—'}</td>
                      <td style={{ textAlign: 'right' }}>
                        {/* Only while Queued: once delivered, the agent may be
                            mid-operation and the server refuses anyway. */}
                        {t.status === 'Queued' && (
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            onClick={() => void onCancelTask(t.id)}
                          >
                            Cancel
                          </button>
                        )}
                      </td>
                    </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {tab === 'network' && (
        <>
          {!device.networkInterfaces.length && <InventoryEmpty title="No network inventory yet" />}
          {!!device.networkInterfaces.length && (
            <div className="card">
              <h2>Network interfaces</h2>
              <div className="scroll-y table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Interface</th>
                      <th>Status</th>
                      <th>MAC address</th>
                      <th>IP addresses</th>
                    </tr>
                  </thead>
                  <tbody>
                    {device.networkInterfaces.map((nic) => (
                      <tr key={nic.name}>
                        <td>{nic.name}</td>
                        <td>
                          {nic.isUp ? (
                            <span className="badge ok">Up</span>
                          ) : (
                            <span className="badge neutral">Down</span>
                          )}
                        </td>
                        <td>
                          <code>{nic.macAddress ?? '—'}</code>
                        </td>
                        <td>{nic.ipAddresses?.length ? nic.ipAddresses.join(', ') : '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </>
      )}
    </>
  )
}
