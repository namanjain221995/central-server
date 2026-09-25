import { useCallback, useEffect, useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  getChromeGroupDevices,
  getChromeOverview,
  getGroups,
  type ChromeGroupDevices,
  type ChromeOverview,
  type DeviceGroupSummary,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { ChromeDeviceWorkspace } from './ChromeDeviceWorkspace'
import {
  chromeStatusLabel,
  chromeStatusTone,
  matchesSearch,
  overviewTiles,
  updateStatusLabel,
  updateStatusTone,
  versionLabel,
} from './chromeView'

/**
 * Chrome Management: Chrome across the fleet, by group, by device, by profile.
 *
 * The group panel is the Groups page's own list, and a group's devices are the
 * server's answer for that group -- so "All Devices" here is the built-in
 * group's devices, the ones in no custom group, and a device is listed under
 * exactly one group. Nothing on this page decides membership, online state or
 * counts; it shows what the server decided and re-reads it on the same cadence
 * as the Groups page.
 *
 * Read-only in this phase. There is no install, remove or update control here
 * because none exists on the server yet, and a control that only looked like
 * one would be worse than none.
 */
export function ChromePage() {
  const { hasPermission } = useAuth()
  const canView = hasPermission('chrome.view')

  const [overview, setOverview] = useState<ChromeOverview | null>(null)
  const [groups, setGroups] = useState<DeviceGroupSummary[]>([])
  const [selectedGroupId, setSelectedGroupId] = useState<string | null>(null)
  const [members, setMembers] = useState<ChromeGroupDevices | null>(null)
  const [search, setSearch] = useState('')
  const [selectedDeviceId, setSelectedDeviceId] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const loadOverview = useCallback(async () => {
    try {
      setOverview(await getChromeOverview())
    } catch {
      setError('Could not load the Chrome overview.')
    }
  }, [])

  const loadGroups = useCallback(async () => {
    try {
      const list = await getGroups()
      setGroups(list)
      // Keep the selection if it still exists; otherwise fall back to All Devices.
      setSelectedGroupId((current) =>
        current && list.some((g) => g.id === current) ? current : (list.find((g) => g.isBuiltIn)?.id ?? list[0]?.id ?? null),
      )
    } catch {
      setError('Could not load groups.')
    }
  }, [])

  const loadMembers = useCallback(async (groupId: string) => {
    try {
      setMembers(await getChromeGroupDevices(groupId))
    } catch {
      setMembers(null)
      setError('Could not load the devices in this group.')
    }
  }, [])

  useEffect(() => {
    if (!canView) return
    void loadOverview()
    void loadGroups()
  }, [canView, loadOverview, loadGroups])

  // The device table is re-read every 30 seconds, as on the Groups page:
  // membership and online state are the server's, and a device moved into
  // another group leaves this table on the next read rather than lingering.
  useEffect(() => {
    if (!canView || !selectedGroupId) return
    void loadMembers(selectedGroupId)
    const timer = setInterval(() => void loadMembers(selectedGroupId), 30_000)
    return () => clearInterval(timer)
  }, [canView, selectedGroupId, loadMembers])

  // The workspace belongs to the table above it. A device that is no longer in
  // the selected group -- moved, retired, or the group changed -- closes it.
  useEffect(() => {
    if (selectedDeviceId && members && !members.devices.some((d) => d.deviceId === selectedDeviceId)) {
      setSelectedDeviceId(null)
    }
  }, [members, selectedDeviceId])

  const visible = useMemo(() => (members?.devices ?? []).filter((d) => matchesSearch(d, search)), [members, search])
  const selectedGroup = groups.find((g) => g.id === selectedGroupId) ?? null

  if (!canView) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to view Chrome information.</p>
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
          Manage Google Chrome across your devices and groups. View profiles, extensions, and keep Chrome up to date.
        </div>
      </div>

      <div className="stat-grid">
        {overviewTiles(overview).map((tile) => (
          <div key={tile.label} className={`card stat-card${tile.tone && tile.value !== '—' ? ` tone-${tile.tone}` : ''}`}>
            <div className="stat-label">{tile.label}</div>
            <div className={`stat-value${tile.value === '—' ? ' unknown' : ''}`}>{tile.value}</div>
            {tile.note && <div className="stat-note">{tile.note}</div>}
          </div>
        ))}
      </div>

      <div className="split">
        <div className="card split-aside">
          <h2>Groups</h2>
          {groups.length === 0 && (
            <div className="empty-state">
              <Icon name="groups" size={36} strokeWidth={1.25} className="icon" />
              <div className="title">No groups you can see</div>
              <div>Groups outside your scope are not shown.</div>
            </div>
          )}
          {groups.map((g) => (
            <button
              key={g.id}
              type="button"
              className="list-item"
              aria-selected={selectedGroupId === g.id}
              onClick={() => {
                setSelectedGroupId(g.id)
                setSelectedDeviceId(null)
              }}
            >
              <div className="list-item-title">
                {g.name}
                {g.isBuiltIn && <span className="muted" style={{ fontWeight: 400 }}> · built-in</span>}
              </div>
              <div className="list-item-sub">
                {g.deviceCount} device{g.deviceCount === 1 ? '' : 's'} · {g.onlineCount} online
              </div>
            </button>
          ))}
        </div>

        <div className="card split-main">
          {!selectedGroup && (
            <div className="empty-state">
              <Icon name="chevron-right" size={36} strokeWidth={1.25} className="icon" />
              <div className="title">Select a group</div>
              <div>Choose a group to see its devices and their Chrome.</div>
            </div>
          )}

          {selectedGroup && (
            <>
              <div className="card-header" style={{ flexWrap: 'wrap', gap: 8 }}>
                <div>
                  <h2 style={{ margin: 0 }}>{selectedGroup.name}</h2>
                  <div className="muted">
                    {selectedGroup.deviceCount} device{selectedGroup.deviceCount === 1 ? '' : 's'} · {selectedGroup.onlineCount}{' '}
                    online
                    {selectedGroup.isBuiltIn && ' · every device not in another group'}
                  </div>
                </div>
                <div className="input-search">
                  <Icon name="search" size={15} className="search-icon" />
                  <input
                    type="search"
                    placeholder="Search devices…"
                    aria-label="Search devices by name"
                    value={search}
                    onChange={(e) => setSearch(e.target.value)}
                  />
                </div>
              </div>

              {members && members.devices.length === 0 && (
                <div className="empty-state">
                  <Icon name="devices" size={36} strokeWidth={1.25} className="icon" />
                  <div className="title">No devices in this group</div>
                  <div>
                    {selectedGroup.isBuiltIn
                      ? 'Every device has been placed in another group.'
                      : 'Add devices to the group on the Groups page to see their Chrome here.'}
                  </div>
                </div>
              )}

              {members && members.devices.length > 0 && visible.length === 0 && (
                <div className="empty-state">
                  <Icon name="search" size={36} strokeWidth={1.25} className="icon" />
                  <div className="title">No device matches</div>
                  <div>Nothing in {selectedGroup.name} matches “{search.trim()}”.</div>
                </div>
              )}

              {visible.length > 0 && (
                <div className="table-wrap">
                  <table className="table">
                    <thead>
                      <tr>
                        <th>Device</th>
                        <th>Chrome</th>
                        <th>Profiles</th>
                        <th>Extensions</th>
                        <th>Update status</th>
                        <th>Status</th>
                        <th style={{ textAlign: 'right' }}>Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {visible.map((d) => {
                        const selected = d.deviceId === selectedDeviceId
                        return (
                          <tr key={d.deviceId} aria-selected={selected}>
                            <td>
                              <div>
                                <Link to={`/devices/${d.deviceId}`}>{d.displayName ?? d.hostname}</Link>
                              </div>
                              {d.displayName && <div className="muted" style={{ fontSize: 11.5 }}>{d.hostname}</div>}
                            </td>
                            <td>
                              <div>{versionLabel(d)}</div>
                              <span className={`badge ${chromeStatusTone(d.chromeStatus)}`}>{chromeStatusLabel(d.chromeStatus)}</span>
                            </td>
                            <td>
                              <span className="badge plain neutral">{d.profileCount}</span>
                            </td>
                            <td>
                              <span className="badge plain neutral">{d.extensionCount}</span>
                            </td>
                            <td>
                              <span className={`badge ${updateStatusTone(d.updateStatus)}`}>{updateStatusLabel(d.updateStatus)}</span>
                            </td>
                            <td>
                              <span className={`badge ${d.isOnline ? 'ok' : 'neutral'}`}>{d.isOnline ? 'Online' : 'Offline'}</span>
                            </td>
                            <td style={{ textAlign: 'right' }}>
                              <button
                                type="button"
                                className={selected ? 'btn-primary btn-sm' : 'btn-sm'}
                                aria-pressed={selected}
                                onClick={() => setSelectedDeviceId(selected ? null : d.deviceId)}
                              >
                                {selected ? 'Selected' : 'Manage'}
                              </button>
                            </td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}
        </div>
      </div>

      {selectedDeviceId && <ChromeDeviceWorkspace deviceId={selectedDeviceId} onClose={() => setSelectedDeviceId(null)} />}
    </>
  )
}
