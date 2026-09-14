import { useCallback, useEffect, useState } from 'react'
import {
  addGroupDevices,
  cancelGroupRestart,
  createGroup,
  deleteGroup,
  forceStopGroup,
  getGroup,
  getGroups,
  removeGroupDevices,
  renameGroup,
  runGroupAction,
  type DeviceGroupDetail,
  type DeviceGroupSummary,
  type GroupAction,
  type GroupActionResult,
  type GroupForceStopResult,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { RowActionsMenu } from '../components/RowActionsMenu'
import {
  AddDevicesDialog,
  CreateGroupDialog,
  DeleteGroupDialog,
  ForceStopGroupDialog,
  GroupActionDialog,
  GroupActionProgress,
  GroupDialog,
  RenameGroupDialog,
} from './GroupDialogs'
import { errorText, groupActionItems, membershipOutcomeLabel } from './groupsView'

type Dialog =
  | { kind: 'create' }
  | { kind: 'add' }
  | { kind: 'rename' }
  | { kind: 'delete' }
  | { kind: 'remove'; deviceId: string; hostname: string }
  | { kind: 'action'; action: GroupAction }
  | { kind: 'force-stop' }
  | { kind: 'cancel-restart' }

/**
 * Device groups: the partitions a device lives in, and the actions a group runs
 * on its online devices.
 *
 * A device belongs to exactly one group. "All Devices" is always first and is
 * where a device lives until it is placed somewhere, and where it returns when
 * removed or when its group is deleted; it cannot be renamed or deleted.
 *
 * Everything shown here is the server's current view, and every change is
 * decided there: the page names a group, and the server resolves its devices,
 * their online state and the caller's authority over each.
 */
export function GroupsPage() {
  const { hasPermission } = useAuth()
  const canManage = hasPermission('group.manage')
  const actionItems = groupActionItems({
    restart: hasPermission('device.restart'),
    shutdown: hasPermission('device.shutdown'),
    lock: hasPermission('device.lock'),
    signOut: hasPermission('device.sign_out_user'),
    executeTasks: hasPermission('task.execute'),
  })

  const [groups, setGroups] = useState<DeviceGroupSummary[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [detail, setDetail] = useState<DeviceGroupDetail | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [run, setRun] = useState<{ action: GroupAction | 'cancel-restart'; result: GroupActionResult } | null>(null)
  const [forceStop, setForceStop] = useState<GroupForceStopResult | null>(null)

  const allDevices = groups.find((g) => g.isBuiltIn) ?? null

  const loadGroups = useCallback(async () => {
    try {
      const list = await getGroups()
      setGroups(list)
      setError(null)
      // Keep the selection if it still exists; otherwise fall back to All Devices.
      setSelectedId((current) =>
        current && list.some((g) => g.id === current) ? current : (list.find((g) => g.isBuiltIn)?.id ?? list[0]?.id ?? null),
      )
    } catch {
      setError('Could not load groups.')
    }
  }, [])

  const loadDetail = useCallback(async (id: string) => {
    try {
      setDetail(await getGroup(id))
    } catch {
      setDetail(null)
    }
  }, [])

  useEffect(() => {
    void loadGroups()
  }, [loadGroups])

  // The device table is refreshed on the same 30-second cadence as the device
  // page, so online state is never older than that when an action is confirmed
  // -- and the server re-reads it anyway when the action is queued.
  useEffect(() => {
    if (!selectedId) return
    void loadDetail(selectedId)
    const timer = setInterval(() => void loadDetail(selectedId), 30_000)
    return () => clearInterval(timer)
  }, [selectedId, loadDetail])

  async function refresh() {
    await loadGroups()
    if (selectedId) await loadDetail(selectedId)
  }

  // ------------------------------------------------------------ membership

  async function onCreate(name: string, deviceIds: string[]): Promise<string | null> {
    try {
      const created = await createGroup(name, deviceIds)
      setDialog(null)
      setSelectedId(created.id)
      await loadGroups()
      await loadDetail(created.id)
      reportMembership(created.devices)
      return null
    } catch (e) {
      return errorText(e, 'Could not create the group.')
    }
  }

  async function onAdd(deviceIds: string[]): Promise<string | null> {
    if (!detail) return null
    try {
      const { devices } = await addGroupDevices(detail.id, deviceIds)
      setDialog(null)
      await refresh()
      reportMembership(devices)
      return null
    } catch (e) {
      return errorText(e, 'Could not add the devices.')
    }
  }

  async function onRemove(deviceId: string) {
    if (!detail) return
    try {
      const { devices } = await removeGroupDevices(detail.id, [deviceId])
      setDialog(null)
      await refresh()
      reportMembership(devices, 'All Devices')
    } catch (e) {
      setDialog(null)
      setError(errorText(e, 'Could not remove the device.'))
    }
  }

  async function onRename(name: string): Promise<string | null> {
    if (!detail) return null
    try {
      await renameGroup(detail.id, name)
      setDialog(null)
      await refresh()
      return null
    } catch (e) {
      return errorText(e, 'Could not rename the group.')
    }
  }

  async function onDelete(): Promise<string | null> {
    if (!detail) return null
    try {
      const { devicesMoved } = await deleteGroup(detail.id)
      setDialog(null)
      setSelectedId(allDevices?.id ?? null)
      setDetail(null)
      await loadGroups()
      setNotice(`Deleted ${detail.name}. ${devicesMoved} device${devicesMoved === 1 ? '' : 's'} moved to All Devices.`)
      return null
    } catch (e) {
      return errorText(e, 'Could not delete the group.')
    }
  }

  /** Says what happened when not every device moved, instead of implying they all did. */
  function reportMembership(devices: { hostname: string | null; outcome: Parameters<typeof membershipOutcomeLabel>[0] }[], to?: string) {
    const moved = devices.filter((d) => d.outcome === 'Moved').length
    const issues = devices.filter((d) => d.outcome !== 'Moved' && d.outcome !== 'AlreadyInGroup')
    if (issues.length === 0) {
      setNotice(moved > 0 ? `${moved} device${moved === 1 ? '' : 's'} moved${to ? ` to ${to}` : ''}.` : null)
      return
    }
    setError(
      `${moved} moved. Not moved: ` +
        issues.map((d) => `${d.hostname ?? 'a device'} (${membershipOutcomeLabel(d.outcome).toLowerCase()})`).join(', '),
    )
  }

  // --------------------------------------------------------------- actions

  function onActionSelected(key: (typeof actionItems)[number]['key']) {
    if (key === 'force-stop') setDialog({ kind: 'force-stop' })
    else if (key === 'cancel-restart') setDialog({ kind: 'cancel-restart' })
    else setDialog({ kind: 'action', action: key })
  }

  async function onRunAction(action: GroupAction, delaySeconds: number) {
    if (!detail) return
    setDialog(null)
    setError(null)
    try {
      const result = await runGroupAction(detail.id, action, delaySeconds)
      setForceStop(null)
      setRun({ action, result })
    } catch (e) {
      setError(errorText(e, `Could not ${action} the group.`))
    }
  }

  async function onForceStop(applicationName: string, publisher: string | null) {
    if (!detail) return
    setDialog(null)
    try {
      setRun(null)
      setForceStop(await forceStopGroup(detail.id, applicationName, publisher))
    } catch (e) {
      setError(errorText(e, 'Could not force stop the application.'))
    }
  }

  async function onCancelRestart() {
    if (!detail) return
    setDialog(null)
    try {
      setForceStop(null)
      setRun({ action: 'cancel-restart', result: await cancelGroupRestart(detail.id) })
    } catch (e) {
      setError(errorText(e, 'Could not cancel the restarts.'))
    }
  }

  // ---------------------------------------------------------------- render

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
          <Icon name="check" size={15} />
          <span style={{ flex: 1 }}>{notice}</span>
          <button type="button" className="btn-ghost btn-sm" style={{ color: 'inherit' }} onClick={() => setNotice(null)}>
            Dismiss
          </button>
        </div>
      )}

      <div className="page-header">
        <div className="lede">
          Every device belongs to exactly one group. Groups are how policies, software and device actions reach a set of
          machines at once.
        </div>
        {canManage && allDevices && (
          <button type="button" className="btn-primary" onClick={() => setDialog({ kind: 'create' })}>
            <Icon name="plus" size={14} />
            New group
          </button>
        )}
      </div>

      {run && <GroupActionProgress key={JSON.stringify(run.result.devices.map((d) => d.taskId))} run={run} onDismiss={() => setRun(null)} />}
      {forceStop && <ForceStopSummary result={forceStop} onDismiss={() => setForceStop(null)} />}

      <div className="split">
        <div className="card split-aside">
          <h2>Groups</h2>
          {groups.length === 0 && (
            <div className="empty-state">
              <Icon name="groups" size={36} strokeWidth={1.25} className="icon" />
              <div className="title">No groups you can manage</div>
              <div>Groups outside your scope are not shown.</div>
            </div>
          )}
          {groups.map((g) => (
            <button
              key={g.id}
              type="button"
              className="list-item"
              aria-selected={selectedId === g.id}
              onClick={() => setSelectedId(g.id)}
            >
              <div className="list-item-title">
                {g.name}
                {g.isBuiltIn && <span className="muted" style={{ fontWeight: 400 }}> · built-in</span>}
              </div>
              <div className="list-item-sub">
                {g.deviceCount} device{g.deviceCount === 1 ? '' : 's'} · {g.onlineCount} online
              </div>
            </button>
          ))}
        </div>

        <div className="card split-main">
          {!detail && (
            <div className="empty-state">
              <Icon name="chevron-right" size={36} strokeWidth={1.25} className="icon" />
              <div className="title">Select a group</div>
              <div>Choose a group to see its devices and run actions on them.</div>
            </div>
          )}

          {detail && (
            <>
              <div className="card-header" style={{ flexWrap: 'wrap', gap: 8 }}>
                <div>
                  <h2 style={{ margin: 0 }}>{detail.name}</h2>
                  <div className="muted">
                    {detail.deviceCount} device{detail.deviceCount === 1 ? '' : 's'} · {detail.onlineCount} online
                    {detail.isBuiltIn && ' · every device not in another group'}
                  </div>
                </div>
                <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                  {actionItems.length > 0 && (
                    <RowActionsMenu
                      label={`Actions for ${detail.name}`}
                      triggerLabel="Actions"
                      disabled={detail.deviceCount === 0}
                      items={actionItems.map((i) => ({
                        key: i.key,
                        label: i.label,
                        destructive: i.destructive,
                        // Offered, but disabled with the reason, when there is no one to send it to.
                        enabled: i.key === 'cancel-restart' || detail.onlineCount > 0,
                        reason: i.key !== 'cancel-restart' && detail.onlineCount === 0 ? 'No device in this group is online' : null,
                      }))}
                      onSelect={onActionSelected}
                    />
                  )}
                  {canManage && !detail.isBuiltIn && (
                    <>
                      <button type="button" className="btn-sm" onClick={() => setDialog({ kind: 'rename' })}>
                        <Icon name="edit" size={14} />
                        Rename
                      </button>
                      <button type="button" className="btn-sm" onClick={() => setDialog({ kind: 'delete' })}>
                        <Icon name="trash" size={14} />
                        Delete
                      </button>
                    </>
                  )}
                </div>
              </div>

              {detail.devices.length === 0 && (
                <div className="empty-state">
                  <Icon name="devices" size={36} strokeWidth={1.25} className="icon" />
                  <div className="title">No devices in this group</div>
                  <div>
                    {detail.isBuiltIn
                      ? 'Every device has been placed in another group.'
                      : 'Add devices to target them with policies, software and actions together.'}
                  </div>
                </div>
              )}

              {detail.devices.length > 0 && (
                <div className="table-wrap">
                  <table className="table">
                    <thead>
                      <tr>
                        <th>Device</th>
                        <th>Status</th>
                        <th>Agent</th>
                        {canManage && !detail.isBuiltIn && <th />}
                      </tr>
                    </thead>
                    <tbody>
                      {detail.devices.map((d) => (
                        <tr key={d.id}>
                          <td>
                            <a href={`/devices/${d.id}`}>{d.displayName ?? d.hostname}</a>
                          </td>
                          <td>
                            <span className={`badge ${d.isOnline ? 'ok' : 'neutral'}`}>{d.isOnline ? 'Online' : 'Offline'}</span>
                          </td>
                          <td>{d.agentVersion}</td>
                          {canManage && !detail.isBuiltIn && (
                            <td style={{ textAlign: 'right' }}>
                              <button
                                type="button"
                                className="btn-ghost btn-sm"
                                onClick={() => setDialog({ kind: 'remove', deviceId: d.id, hostname: d.displayName ?? d.hostname })}
                              >
                                Remove from group
                              </button>
                            </td>
                          )}
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}

              {canManage && (
                <div style={{ marginTop: 12 }}>
                  <button type="button" className="btn-sm" onClick={() => setDialog({ kind: 'add' })}>
                    <Icon name="plus" size={14} />
                    Add devices
                  </button>
                </div>
              )}
            </>
          )}
        </div>
      </div>

      {dialog?.kind === 'create' && allDevices && (
        <CreateGroupDialog allDevicesGroupId={allDevices.id} onCancel={() => setDialog(null)} onCreate={onCreate} />
      )}
      {dialog?.kind === 'add' && detail && <AddDevicesDialog group={detail} onCancel={() => setDialog(null)} onAdd={onAdd} />}
      {dialog?.kind === 'rename' && detail && <RenameGroupDialog group={detail} onCancel={() => setDialog(null)} onRename={onRename} />}
      {dialog?.kind === 'delete' && detail && <DeleteGroupDialog group={detail} onCancel={() => setDialog(null)} onDelete={onDelete} />}
      {dialog?.kind === 'remove' && detail && (
        <GroupDialog
          title={`Remove ${dialog.hostname} from ${detail.name}?`}
          onCancel={() => setDialog(null)}
          footer={
            <>
              <button type="button" onClick={() => setDialog(null)}>
                Cancel
              </button>
              <button type="button" className="btn-danger" onClick={() => void onRemove(dialog.deviceId)}>
                Remove from group
              </button>
            </>
          }
        >
          <p style={{ margin: 0 }}>
            The device moves to <strong className="secondary">All Devices</strong>. It is not deleted, retired or offboarded,
            and it keeps reporting as before. Policies attached to {detail.name} stop applying to it. This action is audited.
          </p>
        </GroupDialog>
      )}
      {dialog?.kind === 'action' && detail && (
        <GroupActionDialog
          group={detail}
          action={dialog.action}
          onCancel={() => setDialog(null)}
          onConfirm={(delay) => void onRunAction(dialog.action, delay)}
        />
      )}
      {dialog?.kind === 'force-stop' && detail && (
        <ForceStopGroupDialog group={detail} onCancel={() => setDialog(null)} onConfirm={(app, pub) => void onForceStop(app, pub)} />
      )}
      {dialog?.kind === 'cancel-restart' && detail && (
        <GroupDialog
          title={`Cancel pending restarts in ${detail.name}?`}
          onCancel={() => setDialog(null)}
          footer={
            <>
              <button type="button" onClick={() => setDialog(null)}>
                Keep them
              </button>
              <button type="button" className="btn-primary" onClick={() => void onCancelRestart()}>
                Cancel restarts
              </button>
            </>
          }
        >
          <p style={{ margin: 0 }}>
            Restarts that have not yet reached their device are cancelled. A restart already delivered cannot be taken back
            &mdash; Windows may already be counting down &mdash; and is reported as too late rather than as cancelled.
          </p>
        </GroupDialog>
      )}
    </>
  )
}

/** Force Stop's own per-device answer, which comes from each device's inventory. */
function ForceStopSummary({ result, onDismiss }: { result: GroupForceStopResult; onDismiss: () => void }) {
  return (
    <div className="card card-section" style={{ marginBottom: 16 }}>
      <div className="info-banner" role="status" style={{ marginBottom: 10 }}>
        <Icon name="clock" size={15} />
        <span style={{ flex: 1 }}>
          <strong>Force Stop {result.applicationName} — {result.groupName}</strong>: {result.processesQueued} process
          {result.processesQueued === 1 ? '' : 'es'} queued for termination
        </span>
        <button type="button" className="btn-ghost btn-sm" style={{ color: 'inherit' }} onClick={onDismiss}>
          Dismiss
        </button>
      </div>
      <div className="table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Device</th>
              <th>Result</th>
            </tr>
          </thead>
          <tbody>
            {result.devices.map((d) => (
              <tr key={d.deviceId}>
                <td>{d.hostname}</td>
                <td>
                  <span className={`badge ${d.outcome === 'Queued' ? 'warn' : 'neutral'}`}>
                    {d.outcome === 'Queued'
                      ? `${d.processesQueued} process${d.processesQueued === 1 ? '' : 'es'} queued`
                      : d.outcome === 'Offline'
                        ? 'Offline — not sent'
                        : d.outcome === 'NotInstalled'
                          ? 'Not installed'
                          : d.outcome}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
