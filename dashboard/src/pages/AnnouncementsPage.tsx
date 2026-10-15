import { useCallback, useEffect, useState } from 'react'
import {
  ApiError,
  deviceName,
  getAnnouncements,
  getDevices,
  getGroups,
  sendAnnouncement,
  type AnnouncementInput,
  type AnnouncementRow,
  type DeviceGroupSummary,
  type DeviceListItem,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import {
  MESSAGE_MAX,
  TITLE_MAX,
  deliveryCounts,
  deriveOnline,
  messagePreview,
  sendSummary,
  sortNewestFirst,
  summarizeTarget,
  validateAnnouncementForm,
  type AnnouncementTargetMode,
} from './announcementsView'

const DEVICE_PAGE_SIZE = 100

/**
 * Announcements: broadcast a short message to device screens, by group or by
 * named device.
 *
 * The server resolves the target, queues per-device delivery and reports the
 * counts; nothing here claims a device saw anything. The success banner and the
 * delivery badges are the server's numbers verbatim, because a console that
 * says "delivered" from local state is a console that lies about the fleet.
 */
export function AnnouncementsPage() {
  const { hasPermission } = useAuth()
  const canSend = hasPermission('announcement.send')

  // ---- the form ----
  const [title, setTitle] = useState('')
  const [message, setMessage] = useState('')
  const [mode, setMode] = useState<AnnouncementTargetMode>('group')
  const [groupId, setGroupId] = useState<string | null>(null)
  const [deviceIds, setDeviceIds] = useState<string[]>([])
  const [sending, setSending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  // ---- the pickers ----
  const [groups, setGroups] = useState<DeviceGroupSummary[] | null>(null)
  const [devices, setDevices] = useState<DeviceListItem[] | null>(null)
  const [deviceTotal, setDeviceTotal] = useState(0)
  const [search, setSearch] = useState('')

  // ---- recent announcements ----
  const [rows, setRows] = useState<AnnouncementRow[] | null>(null)
  const [listError, setListError] = useState<string | null>(null)
  const [loadingRows, setLoadingRows] = useState(false)

  useEffect(() => {
    if (!canSend) return
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
  }, [canSend])

  // Searched and paged server-side, like the deployment pickers: pulling the
  // fleet into the browser to filter here would get worse as the estate grows.
  // Retired devices are excluded at the query — they cannot show a message.
  useEffect(() => {
    if (!canSend) return
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
  }, [canSend, search])

  const loadRecent = useCallback(async () => {
    setLoadingRows(true)
    try {
      setRows(await getAnnouncements())
      setListError(null)
    } catch {
      setListError('Could not load recent announcements.')
    } finally {
      setLoadingRows(false)
    }
  }, [])

  useEffect(() => {
    if (!canSend) return
    void loadRecent()
  }, [canSend, loadRecent])

  const problem = validateAnnouncementForm({ title, message, mode, groupId, deviceIds })

  function toggleDevice(id: string) {
    setDeviceIds((current) =>
      current.includes(id) ? current.filter((x) => x !== id) : [...current, id],
    )
  }

  async function onSend() {
    // The busy guard is what stops a double click sending the broadcast twice.
    if (sending || problem !== null) return

    const input: AnnouncementInput | null =
      mode === 'group'
        ? groupId !== null
          ? { title: title.trim(), message: message.trim(), groupId }
          : null
        : { title: title.trim(), message: message.trim(), deviceIds }
    if (input === null) return

    setSending(true)
    setError(null)
    setNotice(null)
    try {
      const result = await sendAnnouncement(input)
      setNotice(sendSummary(result))
      // Cleared only on success: a refused broadcast keeps the operator's text
      // so the fix is an edit, not a rewrite.
      setTitle('')
      setMessage('')
      setMode('group')
      setGroupId(null)
      setDeviceIds([])
      await loadRecent()
    } catch (e) {
      // The server's own reason — an unknown group, a validation refusal —
      // shown as written; the generic message only when it gave none.
      setError(
        e instanceof ApiError
          ? (e.detail ?? e.message)
          : 'The announcement could not be sent.',
      )
    } finally {
      setSending(false)
    }
  }

  if (!canSend) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to send announcements.</p>
      </div>
    )
  }

  // One clock per render: every row in the picker is judged against the same
  // moment, so two devices with the same heartbeat cannot disagree on screen.
  const now = new Date()

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
          Broadcast a short message to device screens. Online devices show it within seconds;
          offline devices show it when they next connect.
        </div>
      </div>

      <div className="card card-section">
        <h2>Broadcast a message</h2>
        <div className="form-grid">
          <div className="field full">
            <label className="field-label" htmlFor="ann-title">
              Title
            </label>
            <input
              id="ann-title"
              value={title}
              maxLength={TITLE_MAX}
              placeholder="e.g. Scheduled maintenance"
              onChange={(e) => setTitle(e.target.value)}
            />
          </div>

          <div className="field full">
            <label className="field-label" htmlFor="ann-message">
              Message
            </label>
            <textarea
              id="ann-message"
              rows={4}
              required
              value={message}
              maxLength={MESSAGE_MAX}
              placeholder="Your announcement to appear on device screens..."
              onChange={(e) => setMessage(e.target.value)}
            />
            <div className="field-hint">At most {MESSAGE_MAX} characters.</div>
          </div>

          <div className="field full">
            <span className="field-label">Send to</span>
            <div className="radio-row">
              <label>
                <input
                  type="radio"
                  name="ann-target"
                  checked={mode === 'group'}
                  onChange={() => setMode('group')}
                />
                To group
              </label>
              <label>
                <input
                  type="radio"
                  name="ann-target"
                  checked={mode === 'devices'}
                  onChange={() => setMode('devices')}
                />
                Specific devices
              </label>
            </div>
          </div>

          {mode === 'group' && (
            <div className="field full">
              {groups === null && <div className="loading">Loading groups…</div>}

              {groups !== null && groups.length === 0 && (
                <div className="empty-state">
                  <Icon name="groups" size={36} strokeWidth={1.25} className="icon" />
                  <div className="title">No groups you can see</div>
                  <div>Groups outside your scope are not shown.</div>
                </div>
              )}

              {groups !== null && groups.length > 0 && (
                <div className="table-wrap" style={{ maxHeight: 240, overflowY: 'auto' }}>
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
                              name="ann-group"
                              aria-label={`Send to ${g.name}`}
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
            </div>
          )}

          {mode === 'devices' && (
            <div className="field full">
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
                  <div>Retired devices are not shown: they cannot display a message.</div>
                </div>
              )}

              {devices !== null && devices.length > 0 && (
                <div className="table-wrap" style={{ maxHeight: 280, overflowY: 'auto' }}>
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
                  Showing {devices.length} of {deviceTotal} active devices. Narrow the search to
                  reach the rest, or send to a group.
                </p>
              )}
            </div>
          )}

          <div className="full btn-row" style={{ marginBottom: 4 }}>
            <button
              type="button"
              className={`btn-accent${sending ? ' btn-loading' : ''}`}
              disabled={sending || problem !== null}
              title={problem ?? undefined}
              onClick={() => void onSend()}
            >
              {sending ? 'Sending…' : 'Send announcement'}
            </button>
            {mode === 'devices' && deviceIds.length > 0 && (
              <span className="muted" style={{ fontSize: 12.5 }}>
                {deviceIds.length} device{deviceIds.length === 1 ? '' : 's'} selected
              </span>
            )}
          </div>
          <div className="full muted" style={{ fontSize: 12 }}>
            Online devices see it within seconds; offline devices receive it when they reconnect
            (within 24 hours).
          </div>
        </div>
      </div>

      <div className="card">
        <div className="card-header">
          <h2>Recent announcements</h2>
          <button type="button" className="btn-sm" onClick={() => void loadRecent()} disabled={loadingRows}>
            <Icon name="refresh" size={14} />
            {loadingRows ? 'Refreshing…' : 'Refresh'}
          </button>
        </div>

        {listError && (
          <div className="error-banner" role="alert">
            <Icon name="alert" size={15} />
            <span>{listError}</span>
          </div>
        )}

        {rows === null && !listError && <div className="loading">Loading announcements…</div>}

        {rows !== null && rows.length === 0 && (
          <div className="empty-state">
            <Icon name="megaphone" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No announcements yet</div>
            <div>Broadcast a message to see its delivery here.</div>
          </div>
        )}

        {rows !== null && rows.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Title</th>
                  <th>Sent by</th>
                  <th>Target</th>
                  <th>Delivery</th>
                </tr>
              </thead>
              <tbody>
                {sortNewestFirst(rows).map((r) => (
                  <tr key={r.id}>
                    <td>{new Date(r.createdAt).toLocaleString()}</td>
                    <td>
                      <div>{r.title}</div>
                      <div className="row-sub" title={r.message}>
                        {messagePreview(r.message)}
                      </div>
                    </td>
                    <td>{r.createdByDisplay}</td>
                    <td>{summarizeTarget(r)}</td>
                    <td>
                      {deliveryCounts(r).length === 0 ? (
                        <span className="muted">—</span>
                      ) : (
                        <div className="btn-row">
                          {deliveryCounts(r).map((c) => (
                            <span key={c.label} className={`badge ${c.tone}`}>
                              {c.value} {c.label}
                            </span>
                          ))}
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  )
}
