import { useCallback, useEffect, useMemo, useState } from 'react'
import { useAuth } from '../auth/AuthContext'
import {
  ApiError,
  getLocalUsers,
  startFileCollection,
  startFileListing,
  type LocalUserRow,
} from '../api/client'
import { Icon } from '../components/Icon'
import { ReleaseStepUpDialog } from './ReleaseStepUpDialog'
import { FileTree } from './FileTreeView'
import { buildTree, selectionStats, type TreeNode } from './fileTree'
import {
  browseListing,
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

function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value >= 100 || unit === 0 ? 0 : 1)} ${units[unit]}`
}

/**
 * Collect files from a device: browse a Windows user's Desktop, Downloads or Documents,
 * tick exactly which files or folders you want, and download them straight to your
 * machine. The archive is encrypted on the device and decrypted only in this browser —
 * the server never sees its contents. Every download needs your password and is audited.
 */
export function DeviceFileCollectionPanel({ deviceId, deviceName }: { deviceId: string; deviceName: string }) {
  const { hasPermission } = useAuth()
  const canCollect = hasPermission('files.collect')

  const [users, setUsers] = useState<LocalUserRow[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [selectedSid, setSelectedSid] = useState<string>('')
  const [folders, setFolders] = useState<Set<string>>(new Set(FOLDERS))

  const [tree, setTree] = useState<TreeNode[] | null>(null)
  const [truncated, setTruncated] = useState(false)
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [browsing, setBrowsing] = useState(false)

  const [dialogOpen, setDialogOpen] = useState(false)
  const [sink, setSink] = useState<DownloadSink | null>(null)
  const [pendingSelection, setPendingSelection] = useState<string[] | null>(null)
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

  const selectedUser = useMemo(() => users?.find((u) => u.sid === selectedSid) ?? null, [users, selectedSid])
  const rootKeys = useMemo(() => FOLDERS.filter((f) => folders.has(f)), [folders])
  const stats = useMemo(() => (tree ? selectionStats(tree, selected) : { files: 0, bytes: 0 }), [tree, selected])

  const suggestedName = `${sanitize(deviceName)}-${sanitize(selectedUser?.name ?? 'user')}-files.zip`

  // Reset the browse tree when the user or folder set changes — it no longer matches.
  useEffect(() => {
    setTree(null)
    setSelected(new Set())
  }, [selectedSid, folders])

  const toggleFolder = (folder: string) =>
    setFolders((prev) => {
      const next = new Set(prev)
      if (next.has(folder)) next.delete(folder)
      else next.add(folder)
      return next
    })

  const toggleExpand = useCallback((path: string) => {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(path)) next.delete(path)
      else next.add(path)
      return next
    })
  }, [])

  const toggleSelect = useCallback((path: string) => {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(path)) {
        next.delete(path)
      } else {
        next.add(path)
        // Ticking a folder covers its contents, so drop any now-redundant descendants.
        for (const p of [...next]) {
          if (p !== path && p.startsWith(path + '/')) next.delete(p)
        }
      }
      return next
    })
  }, [])

  const onBrowse = useCallback(async () => {
    setError(null)
    setOutcome(null)
    setBrowsing(true)
    setTree(null)
    setSelected(new Set())
    try {
      const keyPair = await generateRecipientKeyPair()
      const { transferId } = await startFileListing(deviceId, {
        recipientPublicKey: keyPair.publicKeyBase64,
        userSid: selectedSid,
        rootKeys,
      })
      const listing = await browseListing(deviceId, transferId, keyPair.privateKey)
      const roots = buildTree(listing.entries)
      setTree(roots)
      setTruncated(listing.truncated)
      setExpanded(new Set(roots.map((r) => r.path)))
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not list this device’s files.')
    } finally {
      setBrowsing(false)
    }
  }, [deviceId, selectedSid, rootKeys])

  // Opens the save destination inside the click gesture (the Save-File picker needs it),
  // then opens the step-up dialog. selection = null means the whole chosen folders.
  const openCollect = useCallback(
    async (selection: string[] | null) => {
      setError(null)
      setOutcome(null)
      try {
        const opened = await openDownloadSink(suggestedName)
        setSink(opened)
        setPendingSelection(selection)
        setDialogOpen(true)
      } catch (err) {
        if (!(err instanceof DOMException && err.name === 'AbortError')) {
          setError('Could not open a download location.')
        }
      }
    },
    [suggestedName],
  )

  const onConfirm = useCallback(
    async (stepUp: { currentPassword: string; justification: string }) => {
      if (!sink) throw new Error('No download location is open.')
      const keyPair = await generateRecipientKeyPair()
      const { transferId } = await startFileCollection(deviceId, {
        recipientPublicKey: keyPair.publicKeyBase64,
        userSid: selectedSid,
        rootKeys,
        currentPassword: stepUp.currentPassword,
        justification: stepUp.justification,
        selectedPaths: pendingSelection ?? undefined,
      })

      setDialogOpen(false)
      setBusy(true)
      setProgress(0)
      try {
        setOutcome(await downloadCollection(deviceId, transferId, keyPair.privateKey, sink, setProgress))
      } catch (err) {
        setError(err instanceof ApiError ? err.message : 'The download failed.')
      } finally {
        setBusy(false)
        setSink(null)
      }
    },
    [deviceId, rootKeys, selectedSid, sink, pendingSelection],
  )

  if (!canCollect) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to collect files from devices.</p>
      </div>
    )
  }

  const canAct = selectedSid !== '' && rootKeys.length > 0 && !busy && !browsing

  return (
    <div className="card glass-card">
      <div className="lede" style={{ marginBottom: 14 }}>
        Browse a Windows user’s Desktop, Downloads or Documents and pick exactly which files to download
        to your machine, or take the whole folders. The archive is encrypted on the device and decrypted
        only in your browser; the server never sees its contents. Every download needs your password and
        is recorded in the audit log.
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
        disabled={busy || browsing || !users || users.length === 0}
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
                disabled={busy || browsing}
                onChange={() => toggleFolder(folder)}
              />
              {folder}
            </label>
          ))}
        </div>
      </fieldset>

      <div style={{ marginTop: 14, display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
        <button type="button" className="btn-secondary" disabled={!canAct} onClick={() => void onBrowse()}>
          <Icon name="search" size={14} /> {browsing ? 'Listing…' : 'Browse & pick files'}
        </button>
        <button type="button" className="btn-ghost btn-sm" disabled={!canAct} onClick={() => void openCollect(null)}>
          <Icon name="download" size={14} /> Collect whole folders…
        </button>
        {busy && (
          <span className="muted" role="status">Downloading… {formatBytes(progress)}</span>
        )}
      </div>

      {!canStreamToDisk() && (
        <p className="muted" style={{ fontSize: 12.5, marginTop: 8 }}>
          This browser will download the archive after buffering it in memory. For a large collection,
          use a Chromium-based browser, which streams straight to disk.
        </p>
      )}

      {tree && (
        <div style={{ marginTop: 14 }}>
          {truncated && (
            <p className="warn-banner" role="status" style={{ marginBottom: 8 }}>
              <Icon name="alert" size={14} /> The listing was very large and was cut short; some files are not shown.
            </p>
          )}
          <FileTree
            roots={tree}
            selected={selected}
            expanded={expanded}
            onToggleSelect={toggleSelect}
            onToggleExpand={toggleExpand}
            formatBytes={formatBytes}
          />
          <div style={{ marginTop: 10, display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
            <button
              type="button"
              className="btn-primary"
              disabled={busy || stats.files === 0}
              onClick={() => void openCollect([...selected])}
            >
              <Icon name="download" size={14} /> Collect selected
              {stats.files > 0 ? ` (${stats.files} file${stats.files === 1 ? '' : 's'}, ${formatBytes(stats.bytes)})` : ''}
            </button>
            {selected.size > 0 && (
              <button type="button" className="btn-ghost btn-sm" disabled={busy} onClick={() => setSelected(new Set())}>
                Clear selection
              </button>
            )}
          </div>
        </div>
      )}

      {error && (
        <div className="error-banner" role="alert" style={{ marginTop: 12 }}>
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      {outcome && (
        <div className="notice-banner" role="status" style={{ marginTop: 12 }}>
          <Icon name="check" size={15} />
          <span>
            Saved {formatBytes(outcome.bytesWritten)}
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
          You are about to download{' '}
          {pendingSelection === null
            ? <>all of <strong>{selectedUser?.name ?? 'a user'}</strong>’s {rootKeys.join(', ')}</>
            : <><strong>{stats.files}</strong> selected file{stats.files === 1 ? '' : 's'} ({formatBytes(stats.bytes)}) from <strong>{selectedUser?.name ?? 'a user'}</strong></>}{' '}
          on <strong>{deviceName}</strong>. The employee is not notified; this download is recorded in the audit log.
        </ReleaseStepUpDialog>
      )}
    </div>
  )
}
