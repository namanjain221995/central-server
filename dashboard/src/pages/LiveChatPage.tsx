import { useCallback, useEffect, useState } from 'react'
import {
  ApiError,
  deviceName,
  getChatDevices,
  startChat,
  type ChatDeviceRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { ChatSessionDialog } from './ChatSessionDialog'

/** Device presence changes as agents report in; refresh every 30s, like the other support lists. */
const POLL_MS = 30_000

/** The device whose chat panel is open, with the session it is bound to. */
interface OpenChat {
  device: ChatDeviceRow
  sessionId: string
}

/**
 * IT Support — Live Chat.
 *
 * A trimmed device list for an authorized operator to open a conversation with
 * the signed-in employee on a managed device. Starting a chat needs the device
 * to be online; a device that already has a session open offers "Open chat" to
 * rejoin it rather than starting a second.
 *
 * The whole page is behind `chat.view`; starting and sending are behind
 * `chat.start`. The gates hide controls — the server enforces each one.
 */
export function LiveChatPage() {
  const { hasPermission } = useAuth()
  const canView = hasPermission('chat.view')
  const canStart = hasPermission('chat.start')

  const [rows, setRows] = useState<ChatDeviceRow[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  // The device whose startChat is in flight, so one row's button can say
  // "Starting…" without freezing the rest.
  const [starting, setStarting] = useState<string | null>(null)
  const [open, setOpen] = useState<OpenChat | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setRows(await getChatDevices())
      setError(null)
    } catch {
      setError('Could not load devices from the Admin API.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    // Not polled at all without permission: the request would 403 every 30s.
    if (!canView) {
      setLoading(false)
      return
    }
    void load()
    const timer = setInterval(() => void load(), POLL_MS)
    return () => clearInterval(timer)
  }, [canView, load])

  async function onStart(device: ChatDeviceRow) {
    setStarting(device.deviceId)
    setError(null)
    try {
      const { sessionId } = await startChat(device.deviceId)
      setOpen({ device, sessionId })
    } catch (e) {
      // 409 problem+json when the agent is too old or a session is already active.
      setError(e instanceof ApiError ? (e.detail ?? e.message) : 'The chat could not be started.')
    } finally {
      setStarting(null)
    }
  }

  function onCloseChat() {
    setOpen(null)
    // The session may have ended; refresh so activeSessionId and the buttons catch up.
    void load()
  }

  // Declared after the hooks above so the hook order never changes between
  // renders, mirroring RemoteSupportPage/DeviceBitLockerPanel.
  if (!canView) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to view Live Chat.</p>
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

      <div className="page-header">
        <div className="lede">
          Open a live chat with the signed-in employee on a managed device. A device must be online
          to start a conversation; one that already has a chat open can be rejoined. Every
          conversation is recorded against your account.
        </div>
      </div>

      <div className="card">
        <div className="toolbar">
          <div className="spacer" />
          <button type="button" onClick={() => void load()} disabled={loading}>
            <Icon name="refresh" size={14} />
            {loading ? 'Refreshing…' : 'Refresh'}
          </button>
        </div>

        {loading && rows.length === 0 && <div className="loading">Loading devices&hellip;</div>}

        {!loading && rows.length === 0 && (
          <div className="empty-state">
            <Icon name="inbox" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No devices</div>
            <div>Enrolled Windows devices appear here once the server reports them.</div>
          </div>
        )}

        {rows.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Device</th>
                  <th>Status</th>
                  <th>Logged-on user</th>
                  <th aria-label="Chat actions" />
                </tr>
              </thead>
              <tbody>
                {rows.map((d) => (
                  <tr key={d.deviceId}>
                    <td>
                      {deviceName(d)}
                      <div className="muted">{d.hostname}</div>
                    </td>
                    <td>
                      {d.isOnline ? (
                        <span className="badge ok">Online</span>
                      ) : (
                        <span className="badge neutral">Offline</span>
                      )}
                    </td>
                    <td>
                      {d.loggedOnUser ? d.loggedOnUser : <span className="muted">&mdash;</span>}
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      {canStart &&
                        (d.activeSessionId ? (
                          <button
                            type="button"
                            className="btn-sm btn-primary"
                            onClick={() => setOpen({ device: d, sessionId: d.activeSessionId as string })}
                          >
                            Open chat
                          </button>
                        ) : (
                          <button
                            type="button"
                            className="btn-sm btn-primary"
                            disabled={!d.isOnline || starting === d.deviceId}
                            onClick={() => void onStart(d)}
                          >
                            {starting === d.deviceId ? 'Starting…' : 'Start chat'}
                          </button>
                        ))}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {open && (
        <ChatSessionDialog device={open.device} sessionId={open.sessionId} onClose={onCloseChat} />
      )}
    </>
  )
}
