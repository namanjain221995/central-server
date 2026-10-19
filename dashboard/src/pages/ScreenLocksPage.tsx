import { useCallback, useEffect, useState } from 'react'
import {
  ApiError,
  getScreenLocks,
  getScreenUnlockCodeStatus,
  releaseScreenLock,
  setScreenUnlockCode,
} from '../api/client'
import type { ScreenLockResult, ScreenLockRow, ScreenUnlockCodeStatus } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { messagePreview, summarizeTarget } from './announcementsView'
import { ScreenLockDialog } from './ScreenLockDialog'
import {
  UNLOCK_CODE_MIN,
  isReleasable,
  lockStatusCounts,
  lockSummary,
  sortLocksNewestFirst,
  validateUnlockCode,
} from './screenLocksView'

/**
 * Screen Locks: the hub for the Full-Screen Lock feature.
 *
 * It holds the record of what has been locked and by whom, the one control that
 * sets the IT unlock code a lock cannot be engaged without, and the way to lift
 * a lock from here. The lock itself is launched here or from a device's or a
 * group's action menu; every count shown is the server's own, never a guess.
 */
export function ScreenLocksPage() {
  const { hasPermission } = useAuth()
  const canLock = hasPermission('device.screenlock')

  const [rows, setRows] = useState<ScreenLockRow[] | null>(null)
  const [listError, setListError] = useState<string | null>(null)
  const [loadingRows, setLoadingRows] = useState(false)

  const [codeStatus, setCodeStatus] = useState<ScreenUnlockCodeStatus | null>(null)

  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const [locking, setLocking] = useState(false)
  const [releasing, setReleasing] = useState<ScreenLockRow | null>(null)

  const loadRows = useCallback(async () => {
    setLoadingRows(true)
    try {
      setRows(await getScreenLocks())
      setListError(null)
    } catch {
      setListError('Could not load recent locks.')
    } finally {
      setLoadingRows(false)
    }
  }, [])

  const loadCodeStatus = useCallback(async () => {
    try {
      setCodeStatus(await getScreenUnlockCodeStatus())
    } catch {
      // Left unknown on failure: the no-code callout appears only on a definite
      // "not set", never on a request that merely failed to answer.
      setCodeStatus(null)
    }
  }, [])

  useEffect(() => {
    if (!canLock) return
    void loadRows()
    void loadCodeStatus()
  }, [canLock, loadRows, loadCodeStatus])

  async function onRelease() {
    if (releasing === null) return
    const target = releasing
    setReleasing(null)
    setError(null)
    setNotice(null)
    try {
      const { released } = await releaseScreenLock({ lockId: target.id })
      setNotice(`Unlocked ${released} device${released === 1 ? '' : 's'}.`)
      await loadRows()
    } catch (e) {
      setError(e instanceof ApiError ? (e.detail ?? e.message) : 'The lock could not be released.')
    }
  }

  function onLocked(result: ScreenLockResult) {
    setLocking(false)
    // The shared summary, so an all-skipped or resolved-to-nothing send reads
    // correctly here instead of claiming "Locked 0 devices." with no reason.
    setNotice(lockSummary(result))
    void loadRows()
  }

  if (!canLock) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to manage screen locks.</p>
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
          <Icon name="check" size={15} />
          <span style={{ flex: 1 }}>{notice}</span>
          <button type="button" className="btn-ghost btn-sm" style={{ color: 'inherit' }} onClick={() => setNotice(null)}>
            Dismiss
          </button>
        </div>
      )}

      <div className="page-header">
        <div className="lede">
          Lock a device or a whole group behind a full-screen notice. Online devices lock within seconds;
          offline devices lock when they next connect. Unlock from here, or on the device with Ctrl + Alt + U.
        </div>
        <button type="button" className="btn-accent" onClick={() => setLocking(true)}>
          <Icon name="lock" size={14} />
          Lock a device or group
        </button>
      </div>

      {codeStatus?.isSet === false && (
        <div className="warn-banner" role="status">
          <strong>Set an IT unlock code before you can lock any device.</strong>{' '}
          <a href="#it-unlock-code">Set the code below.</a>
        </div>
      )}

      <div className="card">
        <div className="card-header">
          <h2>Recent locks</h2>
          <button type="button" className="btn-sm" onClick={() => void loadRows()} disabled={loadingRows}>
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

        {rows === null && !listError && <div className="loading">Loading locks…</div>}

        {rows !== null && rows.length === 0 && (
          <div className="empty-state">
            <Icon name="lock" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No locks yet</div>
            <div>Lock a device or group to see it here.</div>
          </div>
        )}

        {rows !== null && rows.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>When</th>
                  <th>Message</th>
                  <th>Sent by</th>
                  <th>Target</th>
                  <th>Status</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {sortLocksNewestFirst(rows).map((r) => (
                  <tr key={r.id}>
                    <td>{new Date(r.createdAt).toLocaleString()}</td>
                    <td>
                      <div className="row-sub" title={r.message}>
                        {messagePreview(r.message)}
                      </div>
                    </td>
                    <td>{r.createdByDisplay}</td>
                    <td>{summarizeTarget(r)}</td>
                    <td>
                      {lockStatusCounts(r).length === 0 ? (
                        <span className="muted">—</span>
                      ) : (
                        <div className="btn-row">
                          {lockStatusCounts(r).map((c) => (
                            <span key={c.label} className={`badge ${c.tone}`}>
                              {c.value} {c.label}
                            </span>
                          ))}
                        </div>
                      )}
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      {isReleasable(r) && (
                        <button type="button" className="btn-sm" onClick={() => setReleasing(r)}>
                          <Icon name="lock" size={13} />
                          Unlock
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <UnlockCodeControl status={codeStatus} onSaved={() => void loadCodeStatus()} onError={setError} onNotice={setNotice} />

      {locking && <ScreenLockDialog onCancel={() => setLocking(false)} onLocked={onLocked} />}

      {releasing && (
        <ConfirmDialog
          title="Unlock this lock?"
          confirmLabel="Unlock"
          onCancel={() => setReleasing(null)}
          onConfirm={() => void onRelease()}
        >
          Every device still held by this lock is released and returns to the Windows sign-in screen. Online
          devices unlock within seconds; offline devices unlock when they next connect.
        </ConfirmDialog>
      )}
    </>
  )
}

/**
 * The one control that sets the organisation's IT unlock code.
 *
 * The code is never displayed — not even masked — because the server never
 * returns it; only whether it is set, and when it last changed. A confirm field
 * catches a mistyped secret here rather than on a machine nobody can then open.
 */
function UnlockCodeControl({
  status,
  onSaved,
  onError,
  onNotice,
}: {
  status: ScreenUnlockCodeStatus | null
  onSaved: () => void
  onError: (message: string) => void
  onNotice: (message: string) => void
}) {
  const [code, setCode] = useState('')
  const [confirm, setConfirm] = useState('')
  const [saving, setSaving] = useState(false)

  const problem = validateUnlockCode(code, confirm)

  async function onSave() {
    if (saving || problem !== null) return
    setSaving(true)
    try {
      await setScreenUnlockCode(code)
      setCode('')
      setConfirm('')
      onNotice(status?.isSet ? 'IT unlock code replaced.' : 'IT unlock code set.')
      onSaved()
    } catch (e) {
      onError(e instanceof ApiError ? (e.detail ?? e.message) : 'The code could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="card" id="it-unlock-code">
      <div className="card-header">
        <h2>IT unlock code</h2>
        {status?.isSet && (
          <span className="badge ok">
            Set ✓{status.updatedAt ? ` (updated ${new Date(status.updatedAt).toLocaleDateString()})` : ''}
          </span>
        )}
      </div>

      <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
        A device can be unlocked on the spot by typing this code after Ctrl + Alt + U. It is stored only as a
        hash — this page can say whether it is set, never what it is.
      </p>

      <div className="form-grid">
        <div className="field">
          <label className="field-label" htmlFor="unlock-code">
            {status?.isSet ? 'New code' : 'Code'}
          </label>
          <input
            id="unlock-code"
            type="password"
            autoComplete="new-password"
            value={code}
            onChange={(e) => setCode(e.target.value)}
          />
          <div className="field-hint">At least {UNLOCK_CODE_MIN} characters.</div>
        </div>
        <div className="field">
          <label className="field-label" htmlFor="unlock-code-confirm">
            Confirm
          </label>
          <input
            id="unlock-code-confirm"
            type="password"
            autoComplete="new-password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
          />
        </div>
        <div className="full btn-row">
          <button
            type="button"
            className={`btn-primary${saving ? ' btn-loading' : ''}`}
            disabled={saving || problem !== null}
            title={problem ?? undefined}
            onClick={() => void onSave()}
          >
            {saving ? 'Saving…' : status?.isSet ? 'Replace code' : 'Set code'}
          </button>
          {code.length > 0 && problem !== null && (
            <span className="muted" style={{ fontSize: 12.5 }}>
              {problem}
            </span>
          )}
        </div>
      </div>
    </div>
  )
}
