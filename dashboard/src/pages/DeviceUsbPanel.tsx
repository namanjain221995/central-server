import { useCallback, useEffect, useState, type FormEvent } from 'react'
import {
  getDeviceUsbDevices,
  grantUsbAccess,
  reapplyUsbPolicy,
  revokeUsbAccess,
  type UsbDeviceRow,
  type UsbGrantablePolicy,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { useDialogDismiss } from '../components/useDialogDismiss'
import { TaskProgress } from '../components/TaskProgress'
import { useTaskTracker } from '../components/useTaskTracker'
import {
  ENFORCEMENT,
  defaultGrantPolicy,
  describeDevice,
  describeUsbClass,
  grantButtonLabel,
  grantablePolicies,
  hasLiveGrant,
} from './usbView'

/** Offered durations. Bounded server-side too; these are the sensible ones. */
const DURATIONS = [
  { minutes: 30, label: '30 minutes' },
  { minutes: 60, label: '1 hour' },
  { minutes: 240, label: '4 hours' },
  { minutes: 480, label: '8 hours' },
  { minutes: 1440, label: '24 hours' },
]

function relative(iso: string | null): string {
  if (!iso) return '—'
  const delta = Date.now() - new Date(iso).getTime()
  const minutes = Math.round(delta / 60000)
  if (minutes < 1) return 'just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.round(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.round(hours / 24)}d ago`
}

/** Time remaining, floored — a grant showing "1m left" has at least a minute. */
function remaining(iso: string | null): string {
  if (!iso) return '—'
  const ms = new Date(iso).getTime() - Date.now()
  if (ms <= 0) return 'expired'
  const minutes = Math.floor(ms / 60000)
  if (minutes < 60) return `${Math.max(1, minutes)}m left`
  const hours = Math.floor(minutes / 60)
  return hours < 24 ? `${hours}h ${minutes % 60}m left` : `${Math.floor(hours / 24)}d ${hours % 24}h left`
}

/**
 * Device → USB: the peripheral inventory, and control over removable storage
 * and portable devices (phones, tablets, cameras).
 *
 * Two things are kept visibly separate throughout. The **policy** column is what
 * an administrator has decided; the **enforcement** column is what the endpoint
 * has confirmed it is actually doing, and how far it got confirming it. They
 * usually agree, and when they do not, that difference is the most useful
 * thing on the screen — so it is never collapsed into a single reassuring word.
 */
export function DeviceUsbPanel({
  deviceId,
  offline,
}: {
  deviceId: string
  offline: boolean
}) {
  const { hasPermission } = useAuth()
  const canManage = hasPermission('usb.manage')

  const [devices, setDevices] = useState<UsbDeviceRow[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [granting, setGranting] = useState<UsbDeviceRow | null>(null)
  const [revoking, setRevoking] = useState<UsbDeviceRow | null>(null)

  const load = useCallback(async () => {
    try {
      setDevices(await getDeviceUsbDevices(deviceId))
      setError(null)
    } catch {
      setError('Could not load USB devices for this endpoint.')
    }
  }, [deviceId])

  const { tracked, track, dismiss } = useTaskTracker(deviceId, offline, load)

  useEffect(() => {
    void load()
  }, [load])

  async function grant(
    device: UsbDeviceRow,
    minutes: number,
    justification: string,
    policy: UsbGrantablePolicy,
  ) {
    setGranting(null)
    setBusy(true)
    setError(null)
    try {
      await grantUsbAccess(deviceId, device.id, minutes, justification, policy)
      await load()
    } catch {
      setError(`Could not grant access to "${describeDevice(device)}".`)
    } finally {
      setBusy(false)
    }
  }

  async function revoke(device: UsbDeviceRow) {
    setRevoking(null)
    if (!device.liveRequestId) return

    setBusy(true)
    setError(null)
    try {
      await revokeUsbAccess(device.liveRequestId, 'Revoked from the device console.')
      await load()
    } catch {
      setError(`Could not revoke access to "${describeDevice(device)}".`)
    } finally {
      setBusy(false)
    }
  }

  async function reapply() {
    setBusy(true)
    setError(null)
    try {
      const { taskId } = await reapplyUsbPolicy(deviceId)
      track(taskId, 'Re-apply USB policy')
    } catch {
      setError('Could not queue the policy for this endpoint.')
    } finally {
      setBusy(false)
    }
  }

  if (devices === null) {
    return <p className="muted">Loading USB devices…</p>
  }

  // Policy applies to storage and portable devices alike; the server says
  // which rows that is, so a class added later lands in the right table
  // without the console having to know its name.
  const restrictable = devices.filter((d) => d.isRestrictable)
  const peripherals = devices.filter((d) => !d.isRestrictable)

  return (
    <>
      <TaskProgress tasks={tracked} onDismiss={dismiss} />

      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      <div className="card">
        <div className="card-head">
          <h2>USB storage and portable devices</h2>
          <span className="spacer" />
          {canManage && (
            <button type="button" className="btn-sm" onClick={() => void reapply()} disabled={busy}>
              <Icon name="refresh" size={14} />
              Re-apply policy
            </button>
          )}
        </div>

        {/* Stated once, plainly, where the decisions are made. An operator
            should not have to read the docs to know what "read-only" buys, or
            why a phone cannot have it. */}
        <p className="lede">
          USB storage and phones, tablets and cameras are <strong>restricted by default</strong>: the
          agent disables the device, so a stick gets no drive letter and a phone never appears under
          This PC — nothing can be copied in either direction. Granting access opens the device{' '}
          <strong>for a fixed period</strong>. Storage can be opened read-only, so files can be copied
          off it but not onto it; a phone connects through MTP/PTP, which has no read-only mode, so for
          a phone access is read/write or nothing. Neither level scans or blocks what is on the device.
        </p>

        {restrictable.length === 0 && (
          <p className="muted">No USB storage or portable device has been seen on this endpoint.</p>
        )}

        {restrictable.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Device</th>
                  <th>Type</th>
                  <th>Serial</th>
                  <th>Policy</th>
                  <th>Enforcement</th>
                  <th>Last seen</th>
                  <th style={{ textAlign: 'right' }}>Access</th>
                </tr>
              </thead>
              <tbody>
                {restrictable.map((device) => {
                  const state = ENFORCEMENT[device.enforcementState]
                  const live = hasLiveGrant(device)

                  return (
                    <tr key={device.id}>
                      <td>
                        <div>
                          <span>{describeDevice(device)}</span>
                          <code className="row-sub mono-sub" title={device.instanceId}>
                            {device.vendorId && device.productId
                              ? `VID_${device.vendorId} PID_${device.productId}`
                              : device.instanceId}
                          </code>
                        </div>
                      </td>
                      <td>
                        <div>
                          <span>{describeUsbClass(device.deviceClass)}</span>
                          {device.manufacturer && device.product && (
                            <span className="row-sub">{device.manufacturer}</span>
                          )}
                        </div>
                      </td>
                      <td>
                        {device.serialNumber ? (
                          <code className="row-sub mono-sub">{device.serialNumber}</code>
                        ) : (
                          // Said explicitly. A blank cell reads as missing data;
                          // this device genuinely has no serial to show, which
                          // is worth an administrator knowing before granting.
                          <span className="row-sub">No serial exposed</span>
                        )}
                      </td>
                      <td>
                        {live ? (
                          <div>
                            {/* Read/write is the wider grant, so it gets the
                                louder badge. An administrator scanning this
                                column should be able to spot writable media
                                without reading the label. */}
                            <span
                              className={`badge ${device.policy === 'Enabled' ? 'crit' : 'warn'}`}
                            >
                              {device.policy === 'Enabled' ? 'Read/write' : 'Read-only'}
                            </span>
                            <span className="row-sub">{remaining(device.policyExpiresAt)}</span>
                          </div>
                        ) : (
                          <span className="badge ok">Restricted</span>
                        )}
                      </td>
                      <td>
                        <span className={`badge ${state.badge}`} title={state.hint}>
                          {state.label}
                        </span>
                        {device.enforcementError && (
                          <div className="row-sub">{device.enforcementError}</div>
                        )}
                        {device.enforcedAt && (
                          <div className="row-sub" title={device.enforcedAt}>
                            Reported {relative(device.enforcedAt)}
                          </div>
                        )}
                      </td>
                      <td>
                        <div>
                          <span title={device.lastSeenAt}>{relative(device.lastSeenAt)}</span>
                          <span className="row-sub">
                            {device.isConnected ? 'Attached' : 'Not attached'}
                          </span>
                        </div>
                      </td>
                      <td style={{ textAlign: 'right' }}>
                        {!canManage && <span className="row-sub">View only</span>}
                        {canManage && live && (
                          <button
                            type="button"
                            className="btn-danger btn-sm"
                            onClick={() => setRevoking(device)}
                            disabled={busy}
                          >
                            Revoke
                          </button>
                        )}
                        {canManage && !live && (
                          <button
                            type="button"
                            className="btn-sm"
                            onClick={() => setGranting(device)}
                            disabled={busy}
                          >
                            Grant access…
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

      <div className="card">
        <h2>Other peripherals</h2>
        <p className="lede">
          Inventory only. Keyboards, mice, hubs, webcams, audio and other peripherals are never
          restricted — disabling an input device would lock the user out of their own machine. A
          phone that appears here was reported by an agent older than 1.16.0, which cannot classify
          or restrict it; update the agent.
        </p>

        {peripherals.length === 0 && <p className="muted">No other USB peripherals reported.</p>}

        {peripherals.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Device</th>
                  <th>Class</th>
                  <th>Vendor / product</th>
                  <th>Status</th>
                  <th>Last seen</th>
                </tr>
              </thead>
              <tbody>
                {peripherals.map((device) => (
                  <tr key={device.id}>
                    <td>{describeDevice(device)}</td>
                    <td>{describeUsbClass(device.deviceClass)}</td>
                    <td>
                      <code className="row-sub mono-sub">
                        {device.vendorId && device.productId
                          ? `VID_${device.vendorId} PID_${device.productId}`
                          : '—'}
                      </code>
                    </td>
                    <td>
                      {device.isConnected ? (
                        <span className="badge ok">Attached</span>
                      ) : (
                        <span className="badge neutral">Removed</span>
                      )}
                    </td>
                    <td title={device.lastSeenAt}>{relative(device.lastSeenAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {granting && (
        <GrantDialog
          device={granting}
          onCancel={() => setGranting(null)}
          onConfirm={(minutes, justification, policy) =>
            void grant(granting, minutes, justification, policy)
          }
        />
      )}

      {revoking && (
        <ConfirmDialog
          title={`Revoke access to ${describeDevice(revoking)}?`}
          confirmLabel="Yes, revoke access"
          onCancel={() => setRevoking(null)}
          onConfirm={() => void revoke(revoking)}
        >
          <>
            <strong className="secondary">{describeDevice(revoking)}</strong> will be restricted again
            on this endpoint: a stick loses its drive letter, a phone disappears from This PC. Files
            already copied are unaffected — revoking closes the path, it does not undo what was
            transferred.
          </>
        </ConfirmDialog>
      )}
    </>
  )
}

/**
 * The grant dialog.
 *
 * A justification is required rather than optional: a grant nobody can explain
 * three months later is not an auditable one, and the moment of granting is the
 * only moment when the reason is actually known.
 */
function GrantDialog({
  device,
  onCancel,
  onConfirm,
}: {
  device: UsbDeviceRow
  onCancel: () => void
  onConfirm: (minutes: number, justification: string, policy: UsbGrantablePolicy) => void
}) {
  useDialogDismiss(onCancel)

  const [minutes, setMinutes] = useState(DURATIONS[1].minutes)
  const [justification, setJustification] = useState('')

  // The narrowest available level is preselected, so that read/write is
  // something an administrator chooses, never something they arrive at by
  // leaving a control alone. For a phone the only level is read/write, and
  // the dialog says why.
  const levels = grantablePolicies(device)
  const [policy, setPolicy] = useState<UsbGrantablePolicy>(defaultGrantPolicy(device))

  const trimmed = justification.trim()
  const ready = trimmed.length >= 3

  function onSubmit(event: FormEvent) {
    event.preventDefault()
    if (!ready) return
    onConfirm(minutes, trimmed, policy)
  }

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-labelledby="grant-usb-title">
      <form className="dialog" style={{ maxWidth: 520 }} onSubmit={onSubmit}>
        <div className="dialog-header">
          <h2 id="grant-usb-title">Grant USB access</h2>
          <div className="sub">Temporary, time-boxed, and limited to this one device.</div>
        </div>

        <div className="dialog-body">
          <dl className="kv">
            <dt>Device</dt>
            <dd>{describeDevice(device)}</dd>
            <dt>Type</dt>
            <dd>{describeUsbClass(device.deviceClass)}</dd>
            <dt>Instance</dt>
            <dd>
              <code className="row-sub mono-sub">{device.instanceId}</code>
            </dd>
            <dt>Serial</dt>
            <dd>
              {device.serialNumber ?? (
                <span className="muted">
                  None exposed — the grant is keyed to this device instance
                </span>
              )}
            </dd>
          </dl>

          <div className="form-section">Access level</div>

          {levels.length > 1 ? (
            // Radios rather than a dropdown, deliberately. Both levels stay on
            // screen at once, so choosing read/write is a visible decision
            // rather than the result of not opening a menu.
            <div style={{ display: 'flex', gap: 20, flexWrap: 'wrap' }}>
              <label className="check-row">
                <input
                  type="radio"
                  name="usb-grant-policy"
                  checked={policy === 'ReadOnly'}
                  onChange={() => setPolicy('ReadOnly')}
                />
                Read-only
              </label>
              <label className="check-row">
                <input
                  type="radio"
                  name="usb-grant-policy"
                  checked={policy === 'Enabled'}
                  onChange={() => setPolicy('Enabled')}
                />
                Read/write
              </label>
            </div>
          ) : (
            <div className="field-hint">
              A phone, tablet or camera connects through MTP/PTP, which has no read-only mode.
              Access to it is read/write or nothing.
            </div>
          )}

          <div className="field-hint">
            {policy === 'Enabled'
              ? 'Ordinary Windows access, including copying files onto the device.'
              : 'Files can be opened and copied off the device. Windows itself refuses writes, renames and deletes.'}
          </div>

          {policy === 'Enabled' && (
            <div className="warn-banner" style={{ marginTop: 10, marginBottom: 0 }}>
              This is the <strong>widest access this platform grants</strong>. Data can be
              copied off this machine onto the device until the grant expires or is
              revoked. The grant and its justification are recorded in the audit log.
            </div>
          )}

          <div className="field">
            <label className="field-label" htmlFor="usb-grant-duration">
              Access expires after
            </label>
            <select
              id="usb-grant-duration"
              value={minutes}
              onChange={(e) => setMinutes(Number(e.target.value))}
            >
              {DURATIONS.map((d) => (
                <option key={d.minutes} value={d.minutes}>
                  {d.label}
                </option>
              ))}
            </select>
            <div className="field-hint">
              Access ends automatically when the period elapses, even if the machine is offline.
              It can be revoked sooner at any time.
            </div>
          </div>

          <div className="field">
            <label className="field-label" htmlFor="usb-grant-justification">
              Justification
            </label>
            <textarea
              id="usb-grant-justification"
              rows={3}
              value={justification}
              maxLength={1000}
              autoFocus
              placeholder={
                policy === 'Enabled'
                  ? 'Why does this user need to transfer files to and from this device?'
                  : 'Why does this user need to read this device?'
              }
              onChange={(e) => setJustification(e.target.value)}
              aria-describedby="usb-grant-justification-hint"
            />
            <div className="field-hint" id="usb-grant-justification-hint">
              Recorded in the audit log alongside who granted the access and when.
            </div>
          </div>
        </div>

        <div className="dialog-footer">
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button type="submit" className="btn-primary" disabled={!ready}>
            {grantButtonLabel(policy)}
          </button>
        </div>
      </form>
    </div>
  )
}
