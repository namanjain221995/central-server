import { useEffect, useId, useRef, useState } from 'react'
import {
  ApiError,
  clearCatalogAppIcon,
  createCatalogApp,
  getPackages,
  setCatalogAppIcon,
  updateCatalogApp,
  type CatalogAppInput,
  type CatalogAppRow,
  type CatalogVisibility,
  type DeviceGroupSummary,
  type PackageRow,
} from '../api/client'
import { AppTileIcon } from '../components/AppTileIcon'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'
import { useDialogFocus } from '../components/dialogFocus'
import { validateCatalogForm } from './catalogView'

interface Props {
  /** The app being edited, or null to add one. */
  app: CatalogAppRow | null
  groups: DeviceGroupSummary[]
  onCancel: () => void
  onSaved: () => void
}

/**
 * Add a package to the Company Portal, or change how it is presented there.
 *
 * The entry is saved first and the icon second, because they are two requests:
 * the icon has its own endpoint and its own rules (PNG, 64 KB). Nothing here is
 * authoritative -- the server re-validates the form and refuses a package that
 * is disabled or already listed, and its reason is what the banner shows.
 */
export function CatalogAppDialog({ app, groups, onCancel, onSaved }: Props) {
  const titleId = useId()
  const ids = useId()
  const container = useRef<HTMLDivElement>(null)
  useDialogDismiss(onCancel)
  useDialogFocus(container)

  const [packages, setPackages] = useState<PackageRow[] | null>(null)
  const [packageId, setPackageId] = useState(app?.packageId ?? '')
  const [displayName, setDisplayName] = useState(app?.displayName ?? '')
  const [description, setDescription] = useState(app?.description ?? '')
  const [category, setCategory] = useState(app?.category ?? '')
  const [visibility, setVisibility] = useState<CatalogVisibility>(app?.visibility ?? 'AllDevices')
  const [groupIds, setGroupIds] = useState<string[]>(app?.groupIds ?? [])
  const [iconFile, setIconFile] = useState<File | null>(null)
  const [removeIcon, setRemoveIcon] = useState(false)
  const [publish, setPublish] = useState(false)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // The id of an app this dialog has already created. A create that succeeds
  // and an icon upload that then fails leaves the app in place, so pressing
  // Save again must update it rather than create a second one and be told
  // the package is already listed.
  const [createdId, setCreatedId] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    getPackages()
      .then((rows) => {
        if (!cancelled) setPackages(rows)
      })
      .catch(() => {
        if (!cancelled) setError('Could not load packages.')
      })
    return () => {
      cancelled = true
    }
  }, [])

  // Disabled packages are not offered: they cannot be installed, so a new entry
  // for one would be a promise to employees the platform cannot keep. The one
  // an existing entry already points at stays listed so the select can show it.
  const selectable = (packages ?? []).filter((p) => !p.isWithdrawn || p.id === app?.packageId)

  function onPackagePicked(id: string) {
    const previous = packages?.find((p) => p.id === packageId)
    const next = packages?.find((p) => p.id === id)
    setPackageId(id)
    // The package name is the default display name; a name the administrator
    // typed themselves is kept.
    if (next && (!displayName.trim() || displayName === previous?.name)) setDisplayName(next.name)
  }

  function toggleGroup(id: string) {
    setGroupIds((current) =>
      current.includes(id) ? current.filter((g) => g !== id) : [...current, id],
    )
  }

  async function submit() {
    const problem = validateCatalogForm({ packageId, displayName, visibility, groupIds, icon: iconFile })
    if (problem) {
      setError(problem)
      return
    }

    setSaving(true)
    setError(null)
    const input: CatalogAppInput = {
      packageId,
      displayName: displayName.trim(),
      description: description.trim() || undefined,
      category: category.trim() || undefined,
      visibility,
      groupIds: visibility === 'SelectedGroups' ? groupIds : undefined,
    }

    try {
      let id = app?.id ?? createdId
      if (id) {
        await updateCatalogApp(id, input)
      } else {
        id = (await createCatalogApp({ ...input, publish })).id
        setCreatedId(id)
      }

      if (iconFile) await setCatalogAppIcon(id, iconFile)
      else if (removeIcon && app?.hasIcon) await clearCatalogAppIcon(id)

      onSaved()
    } catch (cause: unknown) {
      setError(cause instanceof ApiError ? (cause.detail ?? cause.message) : 'The app could not be saved.')
      setSaving(false)
    }
  }

  const ready = !saving && packageId !== '' && displayName.trim() !== ''

  return (
    <div
      ref={container}
      className="overlay"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
      tabIndex={-1}
    >
      <div className="dialog" style={{ maxWidth: 560 }}>
        <div className="dialog-header">
          <h2 id={titleId}>{app ? 'Edit app' : 'Add app to Company Portal'}</h2>
          {app && <div className="sub">{app.packageName} {app.packageVersion}</div>}
        </div>

        <div className="dialog-body">
          {error && (
            <div className="error-banner" role="alert">
              <Icon name="alert" size={15} />
              <span>{error}</span>
            </div>
          )}

          <div className="field">
            <label className="field-label" htmlFor={`${ids}-package`}>
              Package
            </label>
            <select
              id={`${ids}-package`}
              value={packageId}
              disabled={app !== null || packages === null}
              onChange={(e) => onPackagePicked(e.target.value)}
            >
              <option value="">{packages === null ? 'Loading packages…' : 'Choose a package'}</option>
              {selectable.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name} {p.version}
                </option>
              ))}
            </select>
            {app && (
              <span className="field-hint">
                The package cannot be changed. Remove the app and add it again to offer a different one.
              </span>
            )}
          </div>

          <div className="field">
            <label className="field-label" htmlFor={`${ids}-name`}>
              Display name
            </label>
            <input
              id={`${ids}-name`}
              value={displayName}
              maxLength={128}
              onChange={(e) => setDisplayName(e.target.value)}
              placeholder="Google Chrome"
            />
            <span className="field-hint">The name employees see in the Company Portal.</span>
          </div>

          <div className="field">
            <label className="field-label" htmlFor={`${ids}-description`}>
              Description <span className="muted">(optional)</span>
            </label>
            <textarea
              id={`${ids}-description`}
              rows={3}
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="What it is for and who should install it."
            />
          </div>

          <div className="field">
            <label className="field-label" htmlFor={`${ids}-category`}>
              Category <span className="muted">(optional)</span>
            </label>
            <input
              id={`${ids}-category`}
              value={category}
              maxLength={64}
              onChange={(e) => setCategory(e.target.value)}
              placeholder="Browsers"
            />
          </div>

          <div className="field">
            <span className="field-label">Visible to</span>
            <div className="radio-row">
              <label>
                <input
                  type="radio"
                  name={`${ids}-visibility`}
                  checked={visibility === 'AllDevices'}
                  onChange={() => setVisibility('AllDevices')}
                />
                All devices
              </label>
              <label>
                <input
                  type="radio"
                  name={`${ids}-visibility`}
                  checked={visibility === 'SelectedGroups'}
                  onChange={() => setVisibility('SelectedGroups')}
                />
                Selected groups
              </label>
            </div>
          </div>

          {visibility === 'SelectedGroups' && (
            <div className="table-wrap" style={{ maxHeight: 240, overflowY: 'auto', marginBottom: 14 }}>
              <table className="table">
                <thead>
                  <tr>
                    <th style={{ width: 32 }}></th>
                    <th>Group</th>
                    <th>Devices</th>
                  </tr>
                </thead>
                <tbody>
                  {groups.map((g) => (
                    <tr key={g.id}>
                      <td>
                        <input
                          type="checkbox"
                          aria-label={`Select ${g.name}`}
                          checked={groupIds.includes(g.id)}
                          onChange={() => toggleGroup(g.id)}
                        />
                      </td>
                      <td>{g.name}</td>
                      <td>{g.deviceCount}</td>
                    </tr>
                  ))}
                  {groups.length === 0 && (
                    <tr>
                      <td colSpan={3} className="muted">
                        No groups to choose from.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
          )}

          <div className="field">
            <label className="field-label" htmlFor={`${ids}-icon`}>
              Icon <span className="muted">(optional)</span>
            </label>
            {app?.hasIcon && !removeIcon && (
              <div className="app-tile">
                <AppTileIcon app={app} large />
                <button
                  type="button"
                  className="btn-sm"
                  onClick={() => {
                    setRemoveIcon(true)
                    setIconFile(null)
                  }}
                >
                  Remove icon
                </button>
              </div>
            )}
            {removeIcon && !iconFile && (
              <span className="muted" style={{ fontSize: 12.5 }}>
                The current icon will be removed when you save.
              </span>
            )}
            <input
              id={`${ids}-icon`}
              type="file"
              accept=".png,image/png"
              onChange={(e) => setIconFile(e.target.files?.[0] ?? null)}
            />
            <span className="field-hint">PNG, 64 KB at most. Shown as a square tile beside the name.</span>
          </div>

          {!app && (
            <label className="checkbox-inline">
              <input type="checkbox" checked={publish} onChange={(e) => setPublish(e.target.checked)} />
              Publish immediately
            </label>
          )}
        </div>

        <div className="dialog-footer">
          <button type="button" onClick={onCancel} disabled={saving}>
            Cancel
          </button>
          <button
            type="button"
            className={`btn-primary${saving ? ' btn-loading' : ''}`}
            disabled={!ready}
            onClick={() => void submit()}
          >
            {saving ? 'Saving…' : app ? 'Save changes' : publish ? 'Add and publish' : 'Add app'}
          </button>
        </div>
      </div>
    </div>
  )
}
