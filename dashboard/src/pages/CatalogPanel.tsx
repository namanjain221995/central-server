import { useCallback, useEffect, useState } from 'react'
import {
  ApiError,
  deleteCatalogApp,
  getCatalogApps,
  getGroups,
  publishCatalogApp,
  unpublishCatalogApp,
  type CatalogAppRow,
  type DeviceGroupSummary,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { AppTileIcon } from '../components/AppTileIcon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { Icon } from '../components/Icon'
import { CatalogAppDialog } from './CatalogAppDialog'
import { describeVisibility, sortCatalog } from './catalogView'

type Dialog =
  | { kind: 'create' }
  | { kind: 'edit'; app: CatalogAppRow }
  | { kind: 'remove'; app: CatalogAppRow }
  | null

/** The Company Portal catalogue: approved packages employees may install themselves (software.deploy to change). */
export function CatalogPanel() {
  const { hasPermission } = useAuth()
  const canDeploy = hasPermission('software.deploy')
  const [apps, setApps] = useState<CatalogAppRow[] | null>(null)
  const [groups, setGroups] = useState<DeviceGroupSummary[]>([])
  const [error, setError] = useState<string | null>(null)
  const [dialog, setDialog] = useState<Dialog>(null)
  const [busyId, setBusyId] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setApps(sortCatalog(await getCatalogApps()))
      setError(null)
    } catch {
      setError('Could not load the Company Portal catalogue.')
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  // Names for the Visibility column. Loaded separately and allowed to fail
  // quietly: a catalogue without group names is still a catalogue, and the
  // count is shown either way.
  useEffect(() => {
    getGroups()
      .then(setGroups)
      .catch(() => setGroups([]))
  }, [])

  async function act(app: CatalogAppRow, action: () => Promise<void>, failure: string) {
    setBusyId(app.id)
    try {
      await action()
      setError(null)
      await load()
    } catch (cause: unknown) {
      // The server's own reason when it gave one -- a publish refused because
      // the package was disabled, say -- is more useful than the generic line.
      setError(cause instanceof ApiError && cause.detail ? cause.detail : failure)
    } finally {
      setBusyId(null)
    }
  }

  const editing = dialog?.kind === 'edit' ? dialog.app : null
  const removing = dialog?.kind === 'remove' ? dialog.app : null

  return (
    <div className="card card-section">
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      <div className="card-header">
        <div>
          <h2>Catalog</h2>
          <div className="muted" style={{ fontSize: 12.5, marginTop: 3, maxWidth: 620 }}>
            Approved packages employees can install themselves from the Company Portal, with the
            name, description and icon they see there.
          </div>
        </div>
        {canDeploy && (
          <button type="button" className="btn-primary" onClick={() => setDialog({ kind: 'create' })}>
            <Icon name="plus" size={14} />
            Add app
          </button>
        )}
      </div>

      <div className="info-banner">
        Only published apps appear in the Company Portal on employees’ PCs. Employees can install
        them without an administrator.
      </div>

      {apps === null && !error && <div className="loading">Loading apps…</div>}

      {apps && apps.length === 0 && (
        <div className="empty-state">
          <Icon name="portal" size={36} strokeWidth={1.25} className="icon" />
          <div className="title">No apps in the Company Portal</div>
          <div>Add an approved package to let employees install it themselves.</div>
        </div>
      )}

      {apps && apps.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>App</th>
                <th>Package</th>
                <th>Visibility</th>
                <th>Status</th>
                <th>Updated</th>
                {canDeploy && <th style={{ textAlign: 'right' }}>Actions</th>}
              </tr>
            </thead>
            <tbody>
              {apps.map((a) => {
                const visibility = describeVisibility(a, groups)
                const busy = busyId === a.id
                return (
                  <tr key={a.id}>
                    <td>
                      <div className="app-tile">
                        <AppTileIcon app={a} />
                        <div>
                          <div>{a.displayName}</div>
                          {a.category && <div className="row-sub">{a.category}</div>}
                        </div>
                      </div>
                    </td>
                    <td>
                      <div>
                        {a.packageName}
                        {/* Said beside the package, because it changes what the
                            entry means: a disabled package cannot be installed,
                            whatever the catalogue promises. */}
                        {a.packageWithdrawn && (
                          <span className="badge crit" style={{ marginLeft: 8 }}>
                            Withdrawn
                          </span>
                        )}
                      </div>
                      <div className="row-sub">{a.packageVersion}</div>
                    </td>
                    <td title={visibility.title ?? undefined}>{visibility.label}</td>
                    <td>
                      {a.isPublished ? (
                        <span className="badge ok">Published</span>
                      ) : (
                        <span className="badge neutral">Unpublished</span>
                      )}
                    </td>
                    <td>{new Date(a.updatedAt).toLocaleString()}</td>
                    {canDeploy && (
                      <td style={{ textAlign: 'right' }}>
                        <div className="btn-row" style={{ justifyContent: 'flex-end' }}>
                          <button
                            type="button"
                            className="btn-sm"
                            disabled={busy}
                            onClick={() => setDialog({ kind: 'edit', app: a })}
                          >
                            Edit
                          </button>
                          {a.isPublished ? (
                            <button
                              type="button"
                              className="btn-sm"
                              disabled={busy}
                              onClick={() =>
                                void act(a, () => unpublishCatalogApp(a.id), 'The app could not be unpublished.')
                              }
                            >
                              Unpublish
                            </button>
                          ) : (
                            <button
                              type="button"
                              className="btn-sm"
                              disabled={busy || a.packageWithdrawn}
                              title={a.packageWithdrawn ? 'The package is disabled and cannot be installed.' : undefined}
                              onClick={() =>
                                void act(a, () => publishCatalogApp(a.id), 'The app could not be published.')
                              }
                            >
                              Publish
                            </button>
                          )}
                          <button
                            type="button"
                            className="btn-sm"
                            disabled={busy}
                            onClick={() => setDialog({ kind: 'remove', app: a })}
                          >
                            Remove
                          </button>
                        </div>
                      </td>
                    )}
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {(dialog?.kind === 'create' || editing) && (
        <CatalogAppDialog
          app={editing}
          groups={groups}
          onCancel={() => setDialog(null)}
          onSaved={() => {
            setDialog(null)
            void load()
          }}
        />
      )}

      {removing && (
        <ConfirmDialog
          title="Remove from Company Portal"
          confirmLabel="Remove"
          onCancel={() => setDialog(null)}
          onConfirm={() => {
            setDialog(null)
            void act(removing, () => deleteCatalogApp(removing.id), 'The app could not be removed.')
          }}
        >
          Remove “{removing.displayName}” from the Company Portal? Employees will no longer see it.
          Devices that already installed it are unaffected and nothing is uninstalled.
        </ConfirmDialog>
      )}
    </div>
  )
}
