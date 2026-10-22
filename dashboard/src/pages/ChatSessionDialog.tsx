import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import {
  ApiError,
  deviceName,
  endChat,
  getChatMessages,
  sendChatMessage,
  type ChatDeviceRow,
  type ChatMessage,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'
import { advanceCursor, bubbleAlignment } from './liveChatView'

/** The transcript is short-polled while the panel is open; this is the cadence. */
const POLL_MS = 1500

/**
 * A live conversation with one device.
 *
 * Opens on an existing session id — Live Chat starts the session (or reopens an
 * active one) and hands it here. While open, the transcript is short-polled with
 * the highest seq already shown as the cursor, so each poll returns only new
 * lines; polling stops the moment the conversation ends or the panel closes.
 *
 * Sending and ending need `chat.start`; the gate hides the controls, and the
 * server enforces it. The operator's own lines sit to the right, the employee's
 * to the left, and system notes are centred.
 */
export function ChatSessionDialog({
  device,
  sessionId,
  onClose,
}: {
  device: ChatDeviceRow
  sessionId: string
  onClose: () => void
}) {
  const { hasPermission } = useAuth()
  const canSend = hasPermission('chat.start')

  const [messages, setMessages] = useState<ChatMessage[]>([])
  const [ended, setEnded] = useState(false)
  const [input, setInput] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  // The highest seq shown, held in a ref so each poll reads the live value
  // without re-subscribing the interval on every message.
  const cursor = useRef(0)
  const transcriptRef = useRef<HTMLDivElement>(null)

  useDialogDismiss(onClose)

  const poll = useCallback(async () => {
    try {
      const res = await getChatMessages(sessionId, cursor.current)
      if (res.messages.length > 0) {
        cursor.current = advanceCursor(res.messages, cursor.current)
        setMessages((cur) => [...cur, ...res.messages])
      }
      if (res.ended) setEnded(true)
      setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? (e.detail ?? e.message) : 'Could not load the conversation.')
    }
  }, [sessionId])

  // Poll while open and not ended. When `ended` flips true the effect re-runs,
  // clears the running interval and returns before starting another, so polling
  // stops; unmounting clears it the same way.
  useEffect(() => {
    if (ended) return
    void poll()
    const timer = setInterval(() => void poll(), POLL_MS)
    return () => clearInterval(timer)
  }, [poll, ended])

  // Keep the newest message in view as the transcript grows.
  useEffect(() => {
    const el = transcriptRef.current
    if (el) el.scrollTop = el.scrollHeight
  }, [messages, ended])

  async function onSend(event: FormEvent) {
    event.preventDefault()
    const body = input.trim()
    if (!body || busy || ended) return
    setBusy(true)
    setError(null)
    try {
      await sendChatMessage(sessionId, body)
      setInput('')
      // Pull it straight back rather than waiting for the next tick.
      await poll()
    } catch (e) {
      setError(e instanceof ApiError ? (e.detail ?? e.message) : 'The message could not be sent.')
    } finally {
      setBusy(false)
    }
  }

  async function onEnd() {
    setBusy(true)
    setError(null)
    try {
      await endChat(sessionId)
      setEnded(true)
    } catch (e) {
      setError(e instanceof ApiError ? (e.detail ?? e.message) : 'The chat could not be ended.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-labelledby="chat-session-title">
      <div className="dialog" style={{ maxWidth: 'min(560px, 100%)' }}>
        <div className="dialog-header">
          <h2 id="chat-session-title">Chat with {deviceName(device)}</h2>
          <div className="sub">
            Messages reach the signed-in employee&rsquo;s chat window. This conversation is recorded
            against your account.
          </div>
        </div>

        <div
          className="dialog-body"
          ref={transcriptRef}
          style={{ maxHeight: 360, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 8 }}
        >
          {messages.length === 0 && (
            <div className="muted" style={{ textAlign: 'center' }}>
              {ended ? 'This chat ended with no messages.' : 'No messages yet.'}
            </div>
          )}

          {messages.map((m) => {
            const align = bubbleAlignment(m.sender)
            if (m.sender === 'System') {
              return (
                <div key={m.seq} className="muted" style={{ alignSelf: 'center', fontSize: 12, textAlign: 'center' }}>
                  {m.body}
                </div>
              )
            }
            const isAdmin = m.sender === 'Admin'
            return (
              <div
                key={m.seq}
                style={{
                  alignSelf: align === 'right' ? 'flex-end' : 'flex-start',
                  maxWidth: '78%',
                  padding: '6px 10px',
                  borderRadius: 'var(--radius)',
                  border: '1px solid',
                  borderColor: isAdmin ? 'var(--color-info-border)' : 'var(--color-border)',
                  background: isAdmin ? 'var(--color-info-bg)' : 'var(--color-neutral-bg)',
                }}
              >
                <div className="muted" style={{ fontSize: 11, marginBottom: 2 }}>
                  {m.senderDisplay} · {new Date(m.sentAt).toLocaleTimeString()}
                </div>
                <div style={{ whiteSpace: 'pre-wrap', wordBreak: 'break-word' }}>{m.body}</div>
              </div>
            )
          })}
        </div>

        {error && (
          <div className="error-banner" role="alert" style={{ margin: '0 22px' }}>
            <Icon name="alert" size={15} />
            <span>{error}</span>
          </div>
        )}

        {(ended || !device.isOnline) && (
          <div className="notice-banner" role="status" style={{ margin: '12px 22px 0' }}>
            <Icon name="info" size={15} />
            <span>
              {ended
                ? 'This chat has ended.'
                : 'This device is offline; the employee will see your messages when it reconnects.'}
            </span>
          </div>
        )}

        <form onSubmit={onSend}>
          <div className="dialog-footer">
            {canSend && !ended && (
              <>
                <input
                  aria-label="Message"
                  value={input}
                  onChange={(e) => setInput(e.target.value)}
                  placeholder="Type a message…"
                  autoComplete="off"
                  disabled={busy}
                  style={{ flex: 1 }}
                />
                <button type="submit" className="btn-primary" disabled={busy || input.trim().length === 0}>
                  Send
                </button>
                <button type="button" className="btn-ghost" disabled={busy} onClick={() => void onEnd()}>
                  End chat
                </button>
              </>
            )}
            <button type="button" className="btn-ghost" onClick={onClose}>
              {ended || !canSend ? 'Close' : 'Done'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}
