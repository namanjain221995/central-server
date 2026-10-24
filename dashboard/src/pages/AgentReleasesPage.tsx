import { useCallback, useEffect, useState } from 'react'
import {
  createAgentRelease,
  downloadAgentRelease,
  getAgentReleasePolicy,
  getAgentReleases,
  publishAgentRelease,
  replaceAgentReleaseArtifact,
  revokeAgentRelease,
  type AgentReleaseRow,
  type AgentReleaseTrustMode,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ReleaseStepUpDialog } from './ReleaseStepUpDialog'
import {
  apiFailureMessage,
  deploymentWarning,
  describeReleaseGap,
  downloadHint,
  isDownloadable,
  newestPublished,
  newestUpdateEligible,
  newestUploaded,
  releaseGap,
  publishWillBeRefused,
  trustModeLabel,
} from './agentReleaseView'

/**
 * Windows Agent distribution: which build is current, downloading the MSI, and
 * the release lifecycle (upload as Draft → publish → revoke).
 *
 * The page never claims more than the platform knows. "Latest" is deliberately
 * three separate answers -- newest held, newest offered, newest a fleet update
 * would install -- because they can differ and the difference is the useful
 * part. Per-device installed versions live on the Devices page, and the trust
 * model a published build was verified under is named in the open rather than
 * left to be inferred from a blank signer column.
 */
export function AgentReleasesPage() {
  const { hasPermission } = useAuth()
  const canManage = hasPermission('software.deploy')

  const [releases, setReleases] = useState<AgentReleaseRow[]>([])
  // Internal until the server says otherwise: the safe assumption is the one
  // that promises least about signatures.
  const [trustMode, setTrustMode] = useState<AgentReleaseTrustMode>('Internal')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [showUpload, setShowUpload] = useState(false)
  const [confirmRevoke, setConfirmRevoke] = useState<AgentReleaseRow | null>(null)
  /** Release the publish step-up dialog is open for, or null. */
  const [publishTarget, setPublishTarget] = useState<AgentReleaseRow | null>(null)
  /** Draft whose MSI is being replaced, and the chosen replacement. */
  const [replaceTarget, setReplaceTarget] = useState<AgentReleaseRow | null>(null)
  const [replaceFile, setReplaceFile] = useState<File | null>(null)
  /** Release whose download just started; its buttons stay disabled briefly. */
  const [downloadingId, setDownloadingId] = useState<string | null>(null)

  const [file, setFile] = useState<File | null>(null)
  const [version, setVersion] = useState('')
  const [notes, setNotes] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      const [rows, policy] = await Promise.all([getAgentReleases(), getAgentReleasePolicy()])
      setReleases(rows)
      setTrustMode(policy.trustMode)
      setError(null)
    } catch {
      setError('Could not load agent releases.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  // Ordered by version, not by upload time. The list arrives newest-created
  // first, so the old `find(status === 'Published')` returned whichever
  // published release was uploaded most recently -- which is only the highest
  // version by coincidence, and stops being so the moment an older build is
  // published after a newer one.
  const latest = newestPublished(releases)
  const uploaded = newestUploaded(releases)
  const eligible = newestUpdateEligible(releases)
  const gap = releaseGap(releases)

  async function onUpload() {
    if (!file) return
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      await createAgentRelease(file, {
        version: version.trim(),
        releaseNotes: notes.trim() || undefined,
      })
      setNotice(`Release ${version.trim()} uploaded as a draft. Publish it to make it available.`)
      setFile(null)
      setVersion('')
      setNotes('')
      setShowUpload(false)
      await load()
    } catch (e) {
      // The server says why: a version that is not the MSI's ProductVersion, a
      // build already registered under another version, a file that is not an
      // MSI. Shown as written, so the fix is named rather than guessed at.
      setError(apiFailureMessage(
        e, 'Upload failed. Check the version is unique three-part (e.g. 1.1.0) and the file is the MSI.'))
    } finally {
      setBusy(false)
    }
  }

  function onPublish(release: AgentReleaseRow) {
    // The server is the gate; this is only a courtesy. Under the Public trust
    // model an unsigned build is refused server-side whatever is clicked here,
    // so the confirm explains that before the request rather than after it.
    // Under Internal nothing is refused on signature grounds -- no signature is
    // read at all -- so this is a no-op and Publish is simply offered.
    // publishWillBeRefused is the mode-aware answer, not a guess.
    if (publishWillBeRefused(release, trustMode)) {
      window.alert(`${release.version} cannot be published.\n\n${deploymentWarning(release, trustMode)}`)
      return
    }

    setError(null)
    setNotice(null)
    // Publishing now requires step-up, so the request is made from the dialog
    // where the operator has re-typed their password and given a reason.
    setPublishTarget(release)
  }

  /** The publish itself, from the step-up dialog. A rejection stays in the dialog. */
  async function onPublishConfirmed(
    release: AgentReleaseRow,
    stepUp: { currentPassword: string; justification: string },
  ) {
    try {
      await publishAgentRelease(release.id, stepUp.currentPassword, stepUp.justification)
    } catch (e) {
      // A refusal carries the server's reason -- a 422 names the requirement
      // that failed, a 403 the step-up, a 429 the rate limit -- and the dialog
      // shows it verbatim. Re-read the list first: the row is whatever the
      // server says it is, which after a refusal is still a Draft, and the page
      // must show that rather than assume.
      await load()
      throw e
    }
    setPublishTarget(null)
    setNotice(`Release ${release.version} is now published and available to devices.`)
    await load()
  }

  /**
   * Replaces a draft's MSI -- typically with the Authenticode-signed counterpart
   * of an unsigned upload -- from the step-up dialog. Changing the bytes a
   * publish would point at is as consequential as publishing, so the server
   * demands the same step-up, and a refusal stays in the dialog verbatim.
   */
  async function onReplaceConfirmed(
    release: AgentReleaseRow,
    file: File,
    stepUp: { currentPassword: string; justification: string },
  ) {
    const replaced = await replaceAgentReleaseArtifact(
      release.id, file, stepUp.currentPassword, stepUp.justification)
    setReplaceTarget(null)
    setReplaceFile(null)
    setNotice(
      `Artifact for ${release.version} replaced — `
      + `${replaced.signerSubject ? `signed by ${replaced.signerSubject}` : 'unsigned'}, `
      + 'SHA-256 recomputed by the server.')
    await load()
  }

  async function onRevoke(release: AgentReleaseRow) {
    setConfirmRevoke(null)
    setError(null)
    setNotice(null)
    try {
      await revokeAgentRelease(release.id)
      setNotice(`Release ${release.version} revoked. Nothing can download or install it any more.`)
      await load()
    } catch {
      setError('The release could not be revoked.')
    }
  }

  /**
   * Hands the download to the browser and holds the button disabled for a few
   * seconds. The cooldown is the second guard against the freeze this page
   * once caused: repeated clicks used to start parallel 29 MB fetches that
   * consumed the origin's whole connection pool.
   */
  function onDownload(release: AgentReleaseRow) {
    setError(null)
    setDownloadingId(release.id)
    downloadAgentRelease(release.id, release.fileName)
    window.setTimeout(() => setDownloadingId(null), 4_000)
  }

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
          The Windows agent installer, versioned. Devices report the version they run; a published
          release here is what they can be updated to — after the agent itself has re-verified the
          download's SHA-256, and its signature when the release carries one.
        </div>
        {canManage && (
          <button
            type="button"
            className={showUpload ? undefined : 'btn-primary'}
            onClick={() => setShowUpload(!showUpload)}
          >
            {!showUpload && <Icon name="plus" size={14} />}
            {showUpload ? 'Cancel' : 'Upload release'}
          </button>
        )}
      </div>

      {showUpload && canManage && (
        <div className="card card-section">
          <h2>New agent release</h2>
          <div className="form-grid">
            <div className="field">
              <label className="field-label" htmlFor="rel-file">
                Agent MSI
              </label>
              <input
                id="rel-file"
                type="file"
                accept=".msi"
                onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              />
            </div>
            <div className="field">
              <label className="field-label" htmlFor="rel-version">
                Version
              </label>
              <input
                id="rel-version"
                value={version}
                onChange={(e) => setVersion(e.target.value)}
                placeholder="1.1.0"
              />
              <div className="field-hint">
                Three numeric parts. The server reads the ProductVersion out of the MSI itself and
                refuses an upload that declares anything else.
              </div>
            </div>
            {/* No signer field. The signer is a fact the server reads from the
                MSI's own Authenticode signature, never something an uploader
                declares -- the party supplying a build does not get to say who
                is trusted to have signed it. */}
            <div className="field full">
              <label className="field-label" htmlFor="rel-notes">
                Release notes <span className="muted">(optional)</span>
              </label>
              <textarea
                id="rel-notes"
                rows={3}
                value={notes}
                onChange={(e) => setNotes(e.target.value)}
                placeholder="What changed in this build."
              />
            </div>
            <div className="full btn-row" style={{ marginBottom: 4 }}>
              <button
                type="button"
                className={`btn-primary${busy ? ' btn-loading' : ''}`}
                disabled={busy || !file || !version.trim()}
                onClick={() => void onUpload()}
              >
                {busy ? 'Uploading…' : 'Upload as draft'}
              </button>
              <span className="muted" style={{ fontSize: 12 }}>
                The SHA-256 is computed in your browser and re-computed by the server as it stores
                the bytes.
              </span>
            </div>
          </div>
        </div>
      )}

      {/* The three answers, stated together. A newer draft sitting above the
          published build is a normal and deliberate state -- it is where an
          unsigned build belongs -- but it has to be visible rather than inferred
          from two cards showing two different numbers. */}
      {uploaded && gap !== 'none' && (
        <div className="warn-banner" role="status">
          <strong>{describeReleaseGap(gap, uploaded, eligible)}</strong>
          {uploaded && !latest && (
            <div className="row-sub">Newest registered: {uploaded.version} ({uploaded.status}).</div>
          )}
          {uploaded && latest && (
            <div className="row-sub">
              Newest registered {uploaded.version} ({uploaded.status}) · fleet updates target{' '}
              {eligible?.version ?? 'nothing'}.
            </div>
          )}
        </div>
      )}

      {latest && (
        <div className="card card-section">
          <div className="card-header">
            <h2>Windows Agent — latest published release</h2>
            <span className="badge ok">Published</span>
          </div>
          <div className="detail-title" style={{ marginBottom: 10 }}>
            <h1 style={{ fontSize: 26, margin: 0 }}>{latest.version}</h1>
            <span className="badge neutral plain">windows / {latest.architecture}</span>
            {/* What this build has been verified to be, under the platform's
                trust model. Under Internal a signature is neither required nor
                read, so its absence is not a warning -- it is the model. */}
            <span className={`badge ${latest.signerSubject ? 'info' : 'ok'}`}>
              {latest.signerSubject ? `Signed: ${latest.signerSubject}` : trustModeLabel(trustMode)}
            </span>
            <span className="spacer" />
            <button
              type="button"
              className="btn-primary"
              disabled={downloadingId === latest.id}
              onClick={() => onDownload(latest)}
            >
              {downloadingId === latest.id ? 'Download started…' : 'Download MSI'}
            </button>
          </div>
          <dl className="kv">
            <dt>File</dt>
            <dd>{latest.fileName} ({formatBytes(latest.contentSizeBytes)})</dd>
            <dt>SHA-256</dt>
            <dd>
              <code style={{ overflowWrap: 'anywhere' }}>{latest.sha256}</code>{' '}
              <button
                type="button"
                className="btn-ghost btn-sm"
                onClick={() => void navigator.clipboard.writeText(latest.sha256)}
              >
                Copy
              </button>
            </dd>
            <dt>Released</dt>
            <dd>{latest.publishedAt ? new Date(latest.publishedAt).toLocaleString() : '—'}</dd>
            {latest.releaseNotes && (
              <>
                <dt>Release notes</dt>
                <dd style={{ whiteSpace: 'pre-wrap' }}>{latest.releaseNotes}</dd>
              </>
            )}
          </dl>
        </div>
      )}

      <div className="card">
        <h2>All releases</h2>
        {loading && <div className="loading">Loading releases…</div>}
        {!loading && releases.length === 0 && (
          <div className="empty-state">
            <Icon name="updates" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No agent releases yet</div>
            <div>Upload the built MSI to create the first release.</div>
          </div>
        )}
        {releases.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Version</th>
                  <th>Target</th>
                  <th>Status</th>
                  <th>Signer</th>
                  <th>Uploaded by</th>
                  <th>Created</th>
                  <th style={{ textAlign: 'right' }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {releases.map((r) => (
                  <tr key={r.id}>
                    <td>
                      <div>{r.version}</div>
                      <div className="row-sub">{r.fileName}</div>
                      {downloadHint(r) && <div className="row-sub muted">{downloadHint(r)}</div>}
                    </td>
                    <td>
                      {r.platform}/{r.architecture}
                    </td>
                    <td>
                      <span
                        className={`badge ${
                          r.status === 'Published' ? 'ok' : r.status === 'Revoked' ? 'crit' : 'neutral'
                        }`}
                      >
                        {r.status}
                      </span>
                    </td>
                    <td style={{ fontSize: 12 }}>
                      {r.signerSubject ?? <span className="muted">unsigned</span>}
                    </td>
                    <td>{r.createdByDisplay}</td>
                    <td>{new Date(r.createdAt).toLocaleDateString()}</td>
                    <td style={{ textAlign: 'right' }}>
                      <div className="btn-row" style={{ justifyContent: 'flex-end' }}>
                        {/* Draft included: fetching a build to install by hand is
                            not the same as letting the platform push it to
                            machines. Revoked stays undownloadable. */}
                        {isDownloadable(r) && (
                          <button
                            type="button"
                            className="btn-sm"
                            title={downloadHint(r) ?? undefined}
                            disabled={downloadingId === r.id}
                            onClick={() => onDownload(r)}
                          >
                            {downloadingId === r.id ? 'Started…' : 'Download'}
                          </button>
                        )}
                        {/* Draft only, like Publish: the server refuses to swap
                            the bytes of anything already published or revoked. */}
                        {canManage && r.status === 'Draft' && (
                          <button
                            type="button"
                            className="btn-sm"
                            onClick={() => {
                              setReplaceFile(null)
                              setReplaceTarget(r)
                            }}
                          >
                            Replace MSI
                          </button>
                        )}
                        {canManage && r.status === 'Draft' && (
                          <button
                            type="button"
                            className="btn-primary btn-sm"
                            onClick={() => onPublish(r)}
                          >
                            Publish
                          </button>
                        )}
                        {canManage && r.status !== 'Revoked' && (
                          <button
                            type="button"
                            className="btn-danger btn-sm"
                            onClick={() => setConfirmRevoke(r)}
                          >
                            Revoke
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {publishTarget && (
        <ReleaseStepUpDialog
          title={`Publish agent release ${publishTarget.version}?`}
          confirmLabel="Publish release"
          busyLabel="Publishing…"
          fallbackError="The release could not be published."
          onCancel={() => setPublishTarget(null)}
          onConfirm={(stepUp) => onPublishConfirmed(publishTarget, stepUp)}
        >
          <>
            Publishing makes this MSI installable fleet-wide as SYSTEM — every managed device can
            be updated to it, and it becomes the build offered for download and self-update.
          </>
        </ReleaseStepUpDialog>
      )}

      {replaceTarget && (
        <ReleaseStepUpDialog
          title={`Replace the MSI for ${replaceTarget.version}?`}
          confirmLabel="Replace artifact"
          busyLabel="Replacing…"
          fallbackError="The artifact could not be replaced."
          ready={replaceFile !== null}
          onCancel={() => {
            setReplaceTarget(null)
            setReplaceFile(null)
          }}
          onConfirm={(stepUp) =>
            replaceFile
              ? onReplaceConfirmed(replaceTarget, replaceFile, stepUp)
              : Promise.resolve()}
          fields={
            <div className="field">
              <label className="field-label" htmlFor="replace-artifact-file">
                Replacement MSI
              </label>
              <input
                id="replace-artifact-file"
                type="file"
                accept=".msi"
                onChange={(e) => setReplaceFile(e.target.files?.[0] ?? null)}
              />
              <div className="field-hint">
                The same build, typically its Authenticode-signed counterpart. The server re-reads
                the ProductVersion — it must still be {replaceTarget.version} — recomputes the
                SHA-256 as it stores the bytes, and records the signer it finds.
              </div>
            </div>
          }
        >
          <>
            This swaps the stored bytes of draft {replaceTarget.version} — the file a later publish
            would make installable fleet-wide as SYSTEM. Devices are untouched until the release is
            published.
          </>
        </ReleaseStepUpDialog>
      )}

      {confirmRevoke && (
        <ConfirmDialog
          title={`Revoke agent release ${confirmRevoke.version}?`}
          confirmLabel="Yes, revoke"
          onCancel={() => setConfirmRevoke(null)}
          onConfirm={() => void onRevoke(confirmRevoke)}
        >
          <>
            Nothing will be able to download or install this build any more, and a revoked release
            cannot be re-published — upload a fresh one instead. Agents already running it are
            unaffected. This action is audited.
          </>
        </ConfirmDialog>
      )}
    </>
  )
}

function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`
}
