import { useCallback, useEffect, useMemo, useState } from 'react'
import { useAuth } from '../auth/AuthContext'
import { ApiError, getLocalUsers, startFileCollection, type LocalUserRow } from '../api/client'
import { Icon } from '../components/Icon'
import { ReleaseStepUpDialog } from './ReleaseStepUpDialog'
import {
  canStreamToDisk,
  downloadCollection,
  generateRecipientKeyPair,
  openDownloadSink,
  type CollectionOutcome,
  type DownloadSink,
} from './fileCollectionTransfer'

const FOLDERS = ['Desktop', 'Downloads', 'Documents'] as const

function sanitize(part: string): string {
  return part.replace(/[^A-Za-z0-9._-]+/g, '-').replace(/^-+|-+$/g, '') || 'files'
}

/**
 * Collect files from a device: download a Windows user's Desktop, Downloads or
 * Documents folder straight to the operator's own machine. The archive is encrypted
 * on the device to a one-time key generated here and decrypted only in this browser —
 * the server relays it without being able to read it. Every download re-confirms the
 * operator with a password step-up and is recorded in the audit log.
 */
export function DeviceFileCollectionPanel({ deviceId, deviceName }: { deviceId: string; deviceName: string }) {
  const { hasPermission } = useAuth()
  const canCollect = hasPermission('files.collect')

  const [users, setUsers] = useState<LocalUserRow[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [selectedSid, setSelectedSid] = useState<string>('')
  const [folders, setFolders] = useState<Set<string>>(new Set(FOLDERS))

  const [dialogOpen, setDialogOpen] = useState(false)
  const [sink, setSink] = useState<DownloadSink | null>(null)
  const [busy, setBusy] = useState(false)
  const [progress, setProgress] = useState(0)
  const [outcome, setOutcome] = useState<CollectionOutcome | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!canCollect) return
    let cancelled = false
    getLocalUsers(deviceId)
      .then((rows) => {
        if (cancelled) return
        setUsers(rows)
        if (rows.length > 0) setSelectedSid(rows[0].sid)
      })
      .catch((err) => {
        if (!cancelled) setLoadError(err instanceof ApiError ? err.message : 'Could not load this device’s users.')
      })
    return () => {
      cancelled = true
    }
  }, [canCollect, deviceId])

  const selectedUser = useMemo(
    () => users?.find((u) => u.sid === selectedSid) ?? null,
    [users, selectedSid],
  )

  const rootKeys = useMemo(() => FOLDERS.filter((f) => folders.has(f)), [folders])
  const canStart = selectedSid !== '' && rootKeys.length > 0 && !busy

  const toggleFolder = (folder: string) =>
    setFolders((prev) => {
      const next = new Set(prev)
      if (next.has(folder)) next.delete(folder)
      else next.add(folder)
      return next
    })

  // Opens the save destination inside the click gesture (the Save-File picker needs
  // it), then opens the step-up dialog. The sink is reused if the password is wrong
  // and the operator retries, so they are not asked where to save twice.
  const onCollectClick = useCallback(async () => {
    setError(null)
    setOutcome(null)
    const name = `${sanitize(deviceName)}-${sanitize(selectedUser?.name ?? 'user')}-files.zip`
    try {
      const opened = await openDownloadSink(name)
      setSink(opened)
      setDialogOpen(true)
    } catch (err) {
      // The viewer dismissed the save dialog: nothing to do.
      if (!(err instanceof DOMException && err.name === 'AbortError')) {
        setError('Could not open a download location.')
      }
    }
  }, [deviceName, selectedUser])

  const onConfirm = useCallback(
    async (stepUp: { currentPassword: string; justification: string }) => {
      if (!sink) throw new Error('No download location is open.')
      const keyPair = await generateRecipientKeyPair()
      // May throw ApiError (wrong password / agent too old): the dialog keeps it inline.
      const { transferId } = await startFileCollection(deviceId, {
        recipientPublicKey: keyPair.publicKeyBase64,
        userSid: selectedSid,
        rootKeys,
        currentPassword: stepUp.currentPassword,
        justification: stepUp.justification,
      })

      setDialogOpen(false)
      setBusy(true)
      setProgress(0)
      try {
        const result = await downloadCollection(deviceId, transferId, keyPair.privateKey, sink, setProgress)
        setOutcome(result)
      } catch (err) {
        setError(err instanceof ApiError ? err.message : 'The download failed.')
      } finally {
        setBusy(false)
        setSink(null)
      }
    },
    [deviceId, rootKeys, selectedSid, sink],
  )

  if (!canCollect) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to collect files from devices.</p>
      </div>
    )
  }

  return (
    <div className="card glass-card">
      <div className="lede" style={{ marginBottom: 14 }}>
        Download a Windows user’s Desktop, Downloads or Documents folder from this device straight to
        your machine. The archive is encrypted on the device and decrypted only in your browser; the
        server never sees its contents. Every download needs your password and is recorded in the audit log.
      </div>

      {loadError && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{loadError}</span>
        </div>
      )}

      <label className="field-label" htmlFor="fc-user">Windows user</label>
      <select
        id="fc-user"
        className="input"
        value={selectedSid}
        disabled={busy || !users || users.length === 0}
        onChange={(e) => setSelectedSid(e.target.value)}
      >
        {users?.length
          ? users.map((u) => (
              <option key={u.sid} value={u.sid}>
                {u.name}{u.isLocalAdministrator ? ' (administrator)' : ''}
              </option>
            ))
          : <option value="">No local users reported</option>}
      </select>

      <fieldset style={{ border: 'none', padding: '12px 0 4px', margin: 0 }}>
        <legend className="field-label" style={{ padding: 0 }}>Folders</legend>
        <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
          {FOLDERS.map((folder) => (
            <label key={folder} style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>
              <input
                type="checkbox"
                checked={folders.has(folder)}
                disabled={busy}
                onChange={() => toggleFolder(folder)}
              />
              {folder}
            </label>
          ))}
        </div>
      </fieldset>

      {!canStreamToDisk() && (
        <p className="muted" style={{ fontSize: 12.5 }}>
          This browser will download the archive after buffering it in memory. For a large collection,
          use a Chromium-based browser, which streams straight to disk.
        </p>
      )}

      <div style={{ marginTop: 14, display: 'flex', alignItems: 'center', gap: 12 }}>
        <button type="button" className="btn-primary" disabled={!canStart} onClick={() => void onCollectClick()}>
          <Icon name="download" size={14} /> Collect files…
        </button>
        {busy && (
          <span className="muted" role="status">
            Downloading… {(progress / (1024 * 1024)).toFixed(1)} MB
          </span>
        )}
      </div>

      {error && (
        <div className="error-banner" role="alert" style={{ marginTop: 12 }}>
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      {outcome && (
        <div className={`notice-banner${outcome.result && !outcome.result.succeeded ? '' : ''}`} role="status" style={{ marginTop: 12 }}>
          <Icon name="check" size={15} />
          <span>
            Saved {(outcome.bytesWritten / (1024 * 1024)).toFixed(1)} MB
            {outcome.result?.truncated ? ' (archive hit the size limit and is incomplete)' : ''}
            {!outcome.streamedToDisk ? ' — downloaded from memory' : ''}.
          </span>
        </div>
      )}

      {dialogOpen && (
        <ReleaseStepUpDialog
          title="Collect files from this device"
          confirmLabel="Start collection"
          busyLabel="Starting…"
          fallbackError="The collection could not be started."
          onCancel={() => {
            setDialogOpen(false)
            setSink(null)
          }}
          onConfirm={onConfirm}
        >
          You are about to download <strong>{selectedUser?.name ?? 'a user'}</strong>’s{' '}
          {rootKeys.join(', ')} from <strong>{deviceName}</strong>. The employee is not notified;
          this download is recorded in the audit log.
        </ReleaseStepUpDialog>
      )}
    </div>
  )
}
