import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  ApiError,
  deviceName,
  engageScreenLock,
  getDevices,
  getGroups,
} from '../api/client'
import type {
  DeviceGroupSummary,
  DeviceListItem,
  ScreenLockInput,
  ScreenLockResult,
} from '../api/client'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'
import { deriveOnline } from './announcementsView'
import { LOCK_CONTACT_MAX, LOCK_MESSAGE_MAX, validateLockForm } from './screenLocksView'
import type { ScreenLockTargetMode } from './screenLocksView'

const DEVICE_PAGE_SIZE = 100

/** The approved defaults, shown pre-filled so a lock can be sent without typing. */
const DEFAULT_MESSAGE = 'This device has been locked by your IT department.'
const DEFAULT_CONTACT = 'Please contact your IT Department'

const HELPER_TEXT =
  'Shows a full-screen black lock with the Techsara logo. Unlock from the dashboard, or on the device with Ctrl + Alt + U.'

/**
 * Where a lock is already aimed when the dialog opens: a single device or a
 * group, from that page's action menu. Without one, the dialog shows the pickers
 * itself — the same group/device choosers the Announcements page uses.
 */
export type ScreenLockPreset =
  | { kind: 'group'; groupId: string; name: string }
  | { kind: 'device'; deviceId: string; name: string }

/**
 * Engage a full-screen lock on a device or a group.
 *
 * Nothing here is authoritative. The server re-resolves every id against the
 * caller's scope, excludes retired devices, and reports the counts; the success
 * banner is the server's own numbers, never a local guess. A 409 means the IT
 * unlock code has not been set — surfaced as the server's own detail, with the
 * way to fix it, rather than a dead end.
 */
export function ScreenLockDialog({
  preset,
  onCancel,
  onLocked,
}: {
  preset?: ScreenLockPreset
  onCancel: () => void
  onLocked: (result: ScreenLockResult) => void
}) {
  useDialogDismiss(onCancel)

  const [message, setMessage] = useState(DEFAULT_MESSAGE)
  const [contactLine, setContactLine] = useState(DEFAULT_CONTACT)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // Set on a 409 so the error adds a way to reach the settings control.
  const [needsCode, setNeedsCode] = useState(false)

  // ---- the pickers, only when the dialog chooses its own target ----
  const [mode, setMode] = useState<ScreenLockTargetMode>('group')
  const [groupId, setGroupId] = useState<string | null>(null)
  const [deviceIds, setDeviceIds] = useState<string[]>([])
  const [groups, setGroups] = useState<DeviceGroupSummary[] | null>(null)
  const [devices, setDevices] = useState<DeviceListItem[] | null>(null)
  const [deviceTotal, setDeviceTotal] = useState(0)
  const [search, setSearch] = useState('')

  const choosing = preset === undefined

  useEffect(() => {
    if (!choosing) return
    let cancelled = false
    getGroups()
      .then((list) => {
        if (!cancelled) setGroups(list)
      })
      .catch(() => {
        if (!cancelled) setError('Could not load groups.')
      })
    return () => {
      cancelled = true
    }
  }, [choosing])

  // Searched and paged server-side, like the Announcements picker; retired
  // devices are excluded at the query — a retired device cannot show a lock.
  useEffect(() => {
    if (!choosing || mode !== 'devices') return
    let cancelled = false
    getDevices(1, DEVICE_PAGE_SIZE, search, 'Active')
      .then((page) => {
        if (cancelled) return
        setDevices(page.items)
        setDeviceTotal(page.totalCount)
      })
      .catch(() => {
        if (!cancelled) setError('Could not load devices.')
      })
    return () => {
      cancelled = true
    }
  }, [choosing, mode, search])

  // The effective target: the preset, or whatever the pickers currently hold.
  const effectiveMode: ScreenLockTargetMode =
    preset === undefined ? mode : preset.kind === 'group' ? 'group' : 'devices'
  const effectiveGroupId = preset?.kind === 'group' ? preset.groupId : groupId
  const effectiveDeviceIds = preset?.kind === 'device' ? [preset.deviceId] : deviceIds

  const problem = validateLockForm({
    message,
    contactLine,
    mode: effectiveMode,
    groupId: effectiveGroupId,
    deviceIds: effectiveDeviceIds,
  })

  function toggleDevice(id: string) {
    setDeviceIds((current) =>
      current.includes(id) ? current.filter((x) => x !== id) : [...current, id],
    )
  }

  function buildInput(): ScreenLockInput | null {
    const trimmedMessage = message.trim()
    const trimmedContact = contactLine.trim()
    // Omitted when blank: the server falls back to its own default rather than
    // being handed an empty line to render.
    const contact = trimmedContact.length > 0 ? { contactLine: trimmedContact } : {}

    if (effectiveMode === 'group') {
      return effectiveGroupId !== null ? { message: trimmedMessage, ...contact, groupId: effectiveGroupId } : null
    }
    return effectiveDeviceIds.length > 0
      ? { message: trimmedMessage, ...contact, deviceIds: [...effectiveDeviceIds] }
      : null
  }

  async function onLock() {
    // The busy guard is what stops a double click locking twice.
    if (submitting || problem !== null) return
    const input = buildInput()
    if (input === null) return

    setSubmitting(true)
    setError(null)
    setNeedsCode(false)
    try {
      onLocked(await engageScreenLock(input))
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        setNeedsCode(true)
        setError(e.detail ?? 'Set the IT unlock code before locking a device.')
      } else {
        setError(e instanceof ApiError ? (e.detail ?? e.message) : 'The screen could not be locked.')
      }
      setSubmitting(false)
    }
  }

  const title = preset ? preset.name : 'Choose devices or a group'
  const now = new Date()

  return (
    <div className="overlay">
      <div className="dialog" role="dialog" aria-modal="true" aria-label="Full-Screen Lock">
        <div className="dialog-header">
          <h2>Full-Screen Lock</h2>
          <div className="sub">{title}</div>
        </div>

        <div className="dialog-body">
          {error && (
            <div className="error-banner" role="alert">
              <Icon name="alert" size={15} />
              <span>
                {error}
                {needsCode && (
                  <>
                    {' '}
                    <Link to="/screen-locks">Set it in Screen Locks.</Link>
                  </>
                )}
              </span>
            </div>
          )}

          <div className="field">
            <label className="field-label" htmlFor="lock-message">
              Message
            </label>
            <textarea
              id="lock-message"
              rows={3}
              required
              value={message}
              maxLength={LOCK_MESSAGE_MAX}
              onChange={(e) => setMessage(e.target.value)}
            />
            <div className="field-hint">At most {LOCK_MESSAGE_MAX} characters.</div>
          </div>

          <div className="field">
            <label className="field-label" htmlFor="lock-contact">
              Contact line
            </label>
            <input
              id="lock-contact"
              value={contactLine}
              maxLength={LOCK_CONTACT_MAX}
              onChange={(e) => setContactLine(e.target.value)}
            />
            <div className="field-hint">Optional. At most {LOCK_CONTACT_MAX} characters.</div>
          </div>

          {choosing && (
            <div className="field">
              <span className="field-label">Lock</span>
              <div className="radio-row">
                <label>
                  <input
                    type="radio"
                    name="lock-target"
                    checked={mode === 'group'}
                    onChange={() => setMode('group')}
                  />
                  A group
                </label>
                <label>
                  <input
                    type="radio"
                    name="lock-target"
                    checked={mode === 'devices'}
                    onChange={() => setMode('devices')}
                  />
                  Specific devices
                </label>
              </div>
            </div>
          )}

          {choosing && mode === 'group' && (
            <>
              {groups === null && <div className="loading">Loading groups…</div>}
              {groups !== null && groups.length === 0 && (
                <div className="empty-state">
                  <Icon name="groups" size={36} strokeWidth={1.25} className="icon" />
                  <div className="title">No groups you can see</div>
                  <div>Groups outside your scope are not shown.</div>
                </div>
              )}
              {groups !== null && groups.length > 0 && (
                <div className="table-wrap" style={{ maxHeight: 220, overflowY: 'auto' }}>
                  <table className="table">
                    <thead>
                      <tr>
                        <th style={{ width: 32 }}></th>
                        <th>Group</th>
                        <th>Devices</th>
                        <th>Online now</th>
                      </tr>
                    </thead>
                    <tbody>
                      {groups.map((g) => (
                        <tr key={g.id}>
                          <td>
                            <input
                              type="radio"
                              name="lock-group"
                              aria-label={`Lock ${g.name}`}
                              checked={groupId === g.id}
                              onChange={() => setGroupId(g.id)}
                            />
                          </td>
                          <td>{g.name}</td>
                          <td>{g.deviceCount}</td>
                          <td>{g.onlineCount}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}

          {choosing && mode === 'devices' && (
            <>
              <div className="input-search">
                <Icon name="search" size={15} className="search-icon" />
                <input
                  type="search"
                  placeholder="Search devices…"
                  aria-label="Search devices"
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                />
              </div>
              {devices === null && <div className="loading">Loading devices…</div>}
              {devices !== null && devices.length === 0 && (
                <div className="empty-state">
                  <div className="title">No devices match</div>
                  <div>Retired devices are not shown: they cannot display a lock.</div>
                </div>
              )}
              {devices !== null && devices.length > 0 && (
                <div className="table-wrap" style={{ maxHeight: 260, overflowY: 'auto' }}>
                  <table className="table">
                    <thead>
                      <tr>
                        <th style={{ width: 32 }}></th>
                        <th>Device</th>
                        <th>Logged-on user</th>
                        <th>Status</th>
                      </tr>
                    </thead>
                    <tbody>
                      {devices.map((d) => (
                        <tr key={d.id}>
                          <td>
                            <input
                              type="checkbox"
                              aria-label={`Select ${deviceName(d)}`}
                              checked={deviceIds.includes(d.id)}
                              onChange={() => toggleDevice(d.id)}
                            />
                          </td>
                          <td>{deviceName(d)}</td>
                          <td className="muted">{d.loggedOnUser ?? '—'}</td>
                          <td>
                            {deriveOnline(d.lastSeenAt, now) ? (
                              <span className="badge ok">Online</span>
                            ) : (
                              <span className="badge crit">Offline</span>
                            )}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              {devices !== null && deviceTotal > devices.length && (
                <p className="group-note">
                  Showing {devices.length} of {deviceTotal} active devices. Narrow the search to reach the
                  rest, or lock a group.
                </p>
              )}
            </>
          )}

          <p className="field-hint" style={{ marginTop: 4 }}>
            {HELPER_TEXT}
          </p>
        </div>

        <div className="dialog-footer">
          <button type="button" className="btn-sm" onClick={onCancel} disabled={submitting}>
            Back
          </button>
          <button
            type="button"
            className={`btn-accent${submitting ? ' btn-loading' : ''}`}
            disabled={submitting || problem !== null}
            title={problem ?? undefined}
            onClick={() => void onLock()}
          >
            {submitting ? 'Locking…' : 'Lock screen'}
          </button>
        </div>
      </div>
    </div>
  )
}
