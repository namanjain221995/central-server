import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  getChromeProfileExtensions,
  getDeviceChrome,
  requestInventoryRefresh,
  type ChromeDeviceDetail,
  type ChromeExtensionRow,
  type ChromeProfileRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import {
  chromeStatusLabel,
  chromeStatusTone,
  compareExtensions,
  countLabel,
  enabledLabel,
  enabledTone,
  formatDate,
  formatDateTime,
  installTypeLabel,
  profileSubtitle,
  profileTitle,
  scopeLabel,
  updateStatusLabel,
  updateStatusTone,
} from './chromeView'

type Tab = 'overview' | 'profiles' | 'extensions' | 'updates'

const TABS: { key: Tab; label: string }[] = [
  { key: 'overview', label: 'Overview' },
  { key: 'profiles', label: 'Profiles' },
  { key: 'extensions', label: 'Extensions' },
  { key: 'updates', label: 'Updates' },
]

/**
 * One device's Chrome, below the group table: the installation, its profiles,
 * each profile's extensions, and what is known about updates.
 *
 * Read-only in this phase. There is no control here that installs, removes or
 * updates anything: a button that only changed a database row would be worse
 * than its absence. The one action is Refresh inventory, which asks the agent
 * for a new snapshot through the same flag the device page uses.
 *
 * Profiles are shown for inspection. Whatever enforcement arrives later will be
 * machine-wide (Chrome's enterprise policy applies to every profile on the PC),
 * so the profile list answers "what does each profile have", not "which profile
 * to change".
 */
export function ChromeDeviceWorkspace({ deviceId, onClose }: { deviceId: string; onClose: () => void }) {
  const { hasPermission } = useAuth()
  const canRefresh = hasPermission('device.refresh_inventory')

  const [detail, setDetail] = useState<ChromeDeviceDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('overview')
  const [selectedProfileId, setSelectedProfileId] = useState<string | null>(null)
  const [extensions, setExtensions] = useState<Record<string, ChromeExtensionRow[]>>({})
  const [extensionsError, setExtensionsError] = useState<string | null>(null)
  const [refreshing, setRefreshing] = useState(false)

  const load = useCallback(async () => {
    setError(null)
    try {
      setDetail(await getDeviceChrome(deviceId))
    } catch {
      setDetail(null)
      setError('Chrome information for this device could not be loaded.')
    } finally {
      setLoading(false)
    }
  }, [deviceId])

  // A new device is a new workspace: nothing from the previous one carries over.
  useEffect(() => {
    setLoading(true)
    setDetail(null)
    setSelectedProfileId(null)
    setExtensions({})
    setExtensionsError(null)
    setNotice(null)
    void load()
  }, [deviceId, load])

  // Keep a profile selected while there is one to select, so the Profiles and
  // Extensions tabs never open on an empty right-hand side.
  useEffect(() => {
    if (!detail) return
    setSelectedProfileId((current) =>
      current && detail.profiles.some((p) => p.profileId === current) ? current : (detail.profiles[0]?.profileId ?? null),
    )
  }, [detail])

  useEffect(() => {
    if (!selectedProfileId || extensions[selectedProfileId]) return
    let cancelled = false
    setExtensionsError(null)
    getChromeProfileExtensions(deviceId, selectedProfileId)
      .then((rows) => {
        if (!cancelled) setExtensions((cache) => ({ ...cache, [selectedProfileId]: [...rows].sort(compareExtensions) }))
      })
      .catch(() => {
        if (!cancelled) setExtensionsError('The extensions of this profile could not be loaded.')
      })
    return () => {
      cancelled = true
    }
  }, [deviceId, selectedProfileId, extensions])

  async function onRefresh() {
    setRefreshing(true)
    setNotice(null)
    try {
      await requestInventoryRefresh(deviceId)
      setNotice('Refresh requested. The agent uploads a new snapshot on its next heartbeat.')
      await load()
    } catch {
      setError('The inventory refresh could not be requested.')
    } finally {
      setRefreshing(false)
    }
  }

  const selectedProfile = detail?.profiles.find((p) => p.profileId === selectedProfileId) ?? null

  return (
    <div className="card" style={{ marginTop: 16 }}>
      <div className="card-header" style={{ flexWrap: 'wrap', gap: 8 }}>
        <div>
          <div className="muted" style={{ fontSize: 12.5 }}>Device Chrome details</div>
          <h2 style={{ margin: 0, display: 'flex', alignItems: 'center', gap: 10 }}>
            {detail ? (
              <Link to={`/devices/${detail.deviceId}`}>{detail.displayName ?? detail.hostname}</Link>
            ) : (
              'Loading…'
            )}
            {detail && (
              <span className={`badge ${detail.isOnline ? 'ok' : 'neutral'}`}>{detail.isOnline ? 'Online' : 'Offline'}</span>
            )}
            {detail?.inventoryRefreshPending && <span className="badge warn">Refresh pending</span>}
          </h2>
        </div>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {canRefresh && (
            <button type="button" className="btn-sm" disabled={refreshing || loading} onClick={() => void onRefresh()}>
              <Icon name="refresh" size={14} />
              Refresh inventory
            </button>
          )}
          <button type="button" className="btn-ghost btn-sm" onClick={onClose} aria-label="Close Chrome details">
            Close
          </button>
        </div>
      </div>

      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span style={{ flex: 1 }}>{error}</span>
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

      {loading && <p className="muted">Loading Chrome details…</p>}

      {detail && (
        <>
          <div className="segmented" role="tablist" aria-label="Chrome details sections">
            {TABS.map((t) => (
              <button
                key={t.key}
                type="button"
                role="tab"
                aria-selected={tab === t.key}
                className={tab === t.key ? 'active' : undefined}
                onClick={() => setTab(t.key)}
              >
                {t.label}
              </button>
            ))}
          </div>

          {tab === 'overview' && <OverviewTab detail={detail} />}

          {tab === 'profiles' && (
            <ProfilesTab
              detail={detail}
              selectedProfile={selectedProfile}
              onSelect={setSelectedProfileId}
              extensions={selectedProfileId ? (extensions[selectedProfileId] ?? null) : null}
              extensionsError={extensionsError}
            />
          )}

          {tab === 'extensions' && (
            <ExtensionsTab
              detail={detail}
              selectedProfile={selectedProfile}
              onSelect={setSelectedProfileId}
              extensions={selectedProfileId ? (extensions[selectedProfileId] ?? null) : null}
              extensionsError={extensionsError}
            />
          )}

          {tab === 'updates' && <UpdatesTab detail={detail} />}
        </>
      )}
    </div>
  )
}

// ------------------------------------------------------------------ tabs

function NotReported() {
  return (
    <div className="empty-state">
      <Icon name="chrome" size={36} strokeWidth={1.25} className="icon" />
      <div className="title">Chrome not reported yet</div>
      <div>
        This device has not sent its Chrome section. It needs an agent that collects it; refresh inventory once the agent is
        current and wait for its next heartbeat.
      </div>
    </div>
  )
}

function OverviewTab({ detail }: { detail: ChromeDeviceDetail }) {
  const installation = detail.installation
  if (!installation) return <NotReported />

  return (
    <>
      <dl className="kv" style={{ margin: '12px 0 0' }}>
        <dt>Chrome</dt>
        <dd>
          <span className={`badge ${chromeStatusTone(installation.status)}`}>{chromeStatusLabel(installation.status)}</span>
        </dd>
        <dt>Version</dt>
        <dd>{installation.version ?? '—'}</dd>
        <dt>Channel</dt>
        <dd>{installation.channel ?? '—'}</dd>
        <dt>Architecture</dt>
        <dd>{installation.architecture ?? '—'}</dd>
        <dt>Installation</dt>
        <dd>{scopeLabel(installation)}</dd>
        <dt>Executable</dt>
        <dd style={{ wordBreak: 'break-all' }}>{installation.executablePath ?? '—'}</dd>
        <dt>Profiles</dt>
        <dd>{countLabel(detail.profiles.length, 'profile')}</dd>
        <dt>Collected</dt>
        <dd>{formatDateTime(installation.collectedAt)}</dd>
      </dl>
      {installation.status === 'NotInstalled' && detail.profiles.length > 0 && (
        <p className="muted" style={{ marginTop: 10 }}>
          Chrome is not installed, but profile data is still on the disk: Chrome’s uninstaller leaves it behind unless told
          otherwise.
        </p>
      )}
      {installation.status === 'Error' && (
        <p className="muted" style={{ marginTop: 10 }}>
          The agent could not read every profile last time. The profiles shown are the last complete set it reported.
        </p>
      )}
    </>
  )
}

function ProfileList({
  profiles,
  selectedProfile,
  onSelect,
}: {
  profiles: ChromeProfileRow[]
  selectedProfile: ChromeProfileRow | null
  onSelect: (profileId: string) => void
}) {
  return (
    <div className="card split-aside" style={{ margin: 0 }}>
      <h2 style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        Chrome profiles <span className="badge plain neutral">{profiles.length}</span>
      </h2>
      {profiles.map((p) => (
        <button
          key={p.profileId}
          type="button"
          className="list-item"
          aria-selected={selectedProfile?.profileId === p.profileId}
          onClick={() => onSelect(p.profileId)}
        >
          <div className="list-item-title">
            {profileTitle(p)}
            {p.isManaged && <span className="muted" style={{ fontWeight: 400 }}> · managed</span>}
          </div>
          <div className="list-item-sub">
            {profileSubtitle(p)} · {countLabel(p.extensionCount, 'extension')}
          </div>
        </button>
      ))}
    </div>
  )
}

function ProfilesTab({
  detail,
  selectedProfile,
  onSelect,
  extensions,
  extensionsError,
}: {
  detail: ChromeDeviceDetail
  selectedProfile: ChromeProfileRow | null
  onSelect: (profileId: string) => void
  extensions: ChromeExtensionRow[] | null
  extensionsError: string | null
}) {
  if (!detail.installation) return <NotReported />
  if (detail.profiles.length === 0) {
    return (
      <div className="empty-state">
        <Icon name="users" size={36} strokeWidth={1.25} className="icon" />
        <div className="title">No Chrome profiles</div>
        <div>No user on this device has run Chrome, or its profile data could not be read.</div>
      </div>
    )
  }

  return (
    <div className="split" style={{ marginTop: 12 }}>
      <ProfileList profiles={detail.profiles} selectedProfile={selectedProfile} onSelect={onSelect} />
      <div className="card split-main" style={{ margin: 0 }}>
        {selectedProfile && (
          <>
            <div className="card-header">
              <div>
                <h2 style={{ margin: 0 }}>Extensions — {profileTitle(selectedProfile)}</h2>
                <div className="muted">
                  {profileSubtitle(selectedProfile)} · last active {formatDate(selectedProfile.lastActiveAt)}
                </div>
              </div>
            </div>
            <ExtensionsTable extensions={extensions} error={extensionsError} />
          </>
        )}
      </div>
    </div>
  )
}

function ExtensionsTab({
  detail,
  selectedProfile,
  onSelect,
  extensions,
  extensionsError,
}: {
  detail: ChromeDeviceDetail
  selectedProfile: ChromeProfileRow | null
  onSelect: (profileId: string) => void
  extensions: ChromeExtensionRow[] | null
  extensionsError: string | null
}) {
  if (!detail.installation) return <NotReported />
  if (detail.profiles.length === 0) {
    return (
      <div className="empty-state">
        <Icon name="software" size={36} strokeWidth={1.25} className="icon" />
        <div className="title">No extensions</div>
        <div>Extensions belong to profiles, and this device has reported none.</div>
      </div>
    )
  }

  return (
    <>
      <div className="toolbar" style={{ marginTop: 12 }}>
        <label className="muted" htmlFor="chrome-profile-select">
          Profile
        </label>
        <select
          id="chrome-profile-select"
          style={{ width: 'auto', minWidth: 220 }}
          value={selectedProfile?.profileId ?? ''}
          onChange={(e) => onSelect(e.target.value)}
        >
          {detail.profiles.map((p) => (
            <option key={p.profileId} value={p.profileId}>
              {profileTitle(p)} — {p.userAccount ?? p.userSid}
            </option>
          ))}
        </select>
      </div>
      <ExtensionsTable extensions={extensions} error={extensionsError} />
    </>
  )
}

function ExtensionsTable({ extensions, error }: { extensions: ChromeExtensionRow[] | null; error: string | null }) {
  if (error) return <div className="warn-banner">{error}</div>
  if (!extensions) return <p className="muted">Loading extensions…</p>
  if (extensions.length === 0) {
    return (
      <div className="empty-state">
        <Icon name="software" size={36} strokeWidth={1.25} className="icon" />
        <div className="title">No extensions in this profile</div>
        <div>Chrome recorded none — not even its own built-ins.</div>
      </div>
    )
  }

  return (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Version</th>
            <th>Status</th>
            <th>Managed</th>
            <th>Source</th>
          </tr>
        </thead>
        <tbody>
          {extensions.map((e) => (
            <tr key={e.extensionRowId}>
              <td>
                <div>{e.name ?? <span className="muted">(unnamed)</span>}</div>
                <div className="muted" style={{ fontSize: 11.5, fontFamily: 'monospace' }}>{e.extensionId}</div>
              </td>
              <td>{e.version ?? '—'}</td>
              <td>
                <span className={`badge ${enabledTone(e.enabled)}`}>{enabledLabel(e.enabled)}</span>
              </td>
              <td>
                <span className={`badge ${e.isManaged ? 'ok' : 'neutral'}`}>{e.isManaged ? 'Yes' : 'No'}</span>
              </td>
              <td className={e.isComponent ? 'muted' : undefined}>{installTypeLabel(e.installType)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function UpdatesTab({ detail }: { detail: ChromeDeviceDetail }) {
  const installation = detail.installation
  if (!installation) return <NotReported />

  return (
    <>
      <dl className="kv" style={{ margin: '12px 0 0' }}>
        <dt>Installed version</dt>
        <dd>{installation.version ?? '—'}</dd>
        <dt>Channel</dt>
        <dd>{installation.channel ?? '—'}</dd>
        <dt>Update status</dt>
        <dd>
          <span className={`badge ${updateStatusTone(installation.updateStatus)}`}>
            {updateStatusLabel(installation.updateStatus)}
          </span>
        </dd>
        <dt>Last update check</dt>
        <dd>{formatDateTime(installation.lastUpdateCheck)}</dd>
        <dt>Google Update</dt>
        <dd>{installation.updaterVersion ?? '—'}</dd>
        <dt>Collected</dt>
        <dd>{formatDateTime(installation.collectedAt)}</dd>
      </dl>
      {installation.updateStatus === 'Unknown' && (
        // Honest rather than reassuring: nothing on the server can yet say
        // whether this version is current, so the page does not either.
        <p className="muted" style={{ marginTop: 10 }}>
          Whether this version is current cannot be judged until a Chrome release reference is configured on the server (a
          later phase). Nothing is guessed in the meantime; “Last update check” is Google Update’s own record when it keeps
          one.
        </p>
      )}
    </>
  )
}
