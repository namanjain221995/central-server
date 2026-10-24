import { useEffect, useState } from 'react'
import {
  ApiError,
  deviceName,
  getDevices,
  getGroups,
  removeChromeAccountEverywhere,
  type ChromeAccountRemovalResult,
  type ChromeAccountRemovalTarget,
  type DeviceGroupSummary,
  type DeviceListItem,
} from '../api/client'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'
import { validateTarget, type AccountsTargetMode } from './accountsView'
import {
  AgentFloorNote,
  removalTallyBadges,
  removeEverywhereSummary,
  RestartCheckboxLabel,
  RestartFinishesNote,
  ScheduledRemovalNote,
} from './chromeProfilesView'

const DEVICE_PAGE_SIZE = 100

/**
 * Remove the Chrome profile signed in to one Google account from every targeted
 * device — the fleet-wide form of the per-device Remove, aimed with the same
 * All devices / group / selection pickers the bulk Accounts page uses.
 *
 * Nothing here is authoritative. The server re-resolves the target against the
 * caller's scope and answers with tallies alone; the result view renders those
 * counts — whatever keys arrive — and never a local guess. A refusal's RFC 7807
 * `detail` (asking for restarts without the restart permission, say) is shown
 * verbatim. `canRestart` mirrors the device Restart action's permission: the
 * checkbox exists only for a viewer who could restart these devices one by one.
 */
export function RemoveChromeAccountDialog({
  accountEmail,
  canRestart,
  onClose,
}: {
  accountEmail: string
  canRestart: boolean
  onClose: () => void
}) {
  useDialogDismiss(onClose)

  const [mode, setMode] = useState<AccountsTargetMode>('all')
  const [groupId, setGroupId] = useState<string | null>(null)
  const [deviceIds, setDeviceIds] = useState<string[]>([])
  const [groups, setGroups] = useState<DeviceGroupSummary[] | null>(null)
  const [devices, setDevices] = useState<DeviceListItem[] | null>(null)
  const [deviceTotal, setDeviceTotal] = useState(0)
  const [search, setSearch] = useState('')
  const [restartDevices, setRestartDevices] = useState(false)

  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<ChromeAccountRemovalResult | null>(null)

  useEffect(() => {
    let cancelled = false
    // The built-in "All Devices" group is represented by the explicit "All
    // devices" option, so it is left out of the list — same as the Accounts page.
    getGroups()
      .then((list) => {
        if (!cancelled) setGroups(list.filter((g) => !g.isBuiltIn))
      })
      .catch(() => {
        if (!cancelled) setError('Could not load groups.')
      })
    return () => {
      cancelled = true
    }
  }, [])

  // Searched and paged server-side; retired devices are excluded at the query —
  // they will never pick a removal task up.
  useEffect(() => {
    if (mode !== 'devices') return
    let cancelled = false
    getDevices(1, DEVICE_PAGE_SIZE, search, 'Active')
      .then((page) => {
        if (cancelled) return
        setDevices(page.items)
        setDeviceTotal(page.totalCount)
      })
      .catch(() => {
        if (!cancelled) setError('Could not load devices.')
      })
    return () => {
      cancelled = true
    }
  }, [mode, search])

  const problem = validateTarget({ mode, groupId, deviceIds })

  function toggleDevice(id: string) {
    setDeviceIds((current) => (current.includes(id) ? current.filter((x) => x !== id) : [...current, id]))
  }

  /** Exactly the contract's target: a kind, and only the field that kind needs. */
  function buildTarget(): ChromeAccountRemovalTarget | null {
    if (mode === 'all') return { kind: 'all' }
    if (mode === 'group') return groupId !== null ? { kind: 'group', groupId } : null
    return deviceIds.length > 0 ? { kind: 'devices', deviceIds: [...deviceIds] } : null
  }

  async function onSubmit() {
    // The busy guard is what stops a double click removing twice.
    if (busy || problem !== null || result !== null) return
    const target = buildTarget()
    if (target === null) return

    setBusy(true)
    setError(null)
    try {
      setResult(
        await removeChromeAccountEverywhere({
          accountEmail,
          target,
          restartDevices: canRestart && restartDevices,
        }),
      )
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) {
        setError('That target resolved to no devices you can act on.')
      } else {
        // The server's RFC 7807 detail verbatim — a 403 for the restart
        // permission names the permission better than a guess here would.
        setError(e instanceof ApiError ? (e.detail ?? e.message) : 'The removal could not be queued.')
      }
    } finally {
      setBusy(false)
    }
  }

  const badges = result !== null ? removalTallyBadges(result) : []

  return (
    <div className="overlay">
      <div className="dialog" role="dialog" aria-modal="true" aria-label="Remove Chrome profile on all devices">
        <div className="dialog-header">
          <h2>Remove Chrome profile on all devices</h2>
          <div className="sub">{accountEmail}</div>
        </div>

        <div className="dialog-body">
          {error && (
            <div className="error-banner" role="alert">
              <Icon name="alert" size={15} />
              <span>{error}</span>
            </div>
          )}

          {result !== null ? (
            <>
              {/* The server's own counts, exactly as the bulk Accounts page
                  reports its outcome: one sentence, then the non-zero tallies
                  as badges — every key the server sent, known or not. */}
              <div className="notice-banner" role="status">
                <Icon name="check" size={15} />
                <span>{removeEverywhereSummary(result)}</span>
              </div>
              {badges.length > 0 && (
                <div className="btn-row" style={{ marginTop: 8, flexWrap: 'wrap' }}>
                  {badges.map((b) => (
                    <span key={b.label} className={`badge ${b.tone}`}>
                      {b.value} {b.label}
                    </span>
                  ))}
                </div>
              )}
              <p className="field-hint" style={{ marginTop: 10 }}>
                {ScheduledRemovalNote}
              </p>
            </>
          ) : (
            <>
              <div className="warn-banner">
                This removes the Chrome profile signed in to <strong>{accountEmail}</strong> from every targeted
                device. Bookmarks, history and saved passwords stored only in that profile are deleted from each
                device; anything synced to the Google account stays in the account.
              </div>

              <div className="field">
                <span className="field-label">Apply to</span>
                <div className="radio-row">
                  <label>
                    <input type="radio" name="chrome-remove-target" checked={mode === 'all'} onChange={() => setMode('all')} />
                    {' '}All devices
                  </label>
                  <label>
                    <input type="radio" name="chrome-remove-target" checked={mode === 'group'} onChange={() => setMode('group')} />
                    {' '}A group (department)
                  </label>
                  <label>
                    <input type="radio" name="chrome-remove-target" checked={mode === 'devices'} onChange={() => setMode('devices')} />
                    {' '}Specific devices
                  </label>
                </div>
              </div>

              {mode === 'all' && (
                <p className="group-note">Every active device in your scope. Retired devices are not included.</p>
              )}

              {mode === 'group' && (
                <>
                  {groups === null && <div className="loading">Loading groups…</div>}
                  {groups !== null && groups.length === 0 && (
                    <div className="empty-state">
                      <Icon name="groups" size={36} strokeWidth={1.25} className="icon" />
                      <div className="title">No groups you can see</div>
                      <div>Groups outside your scope are not shown.</div>
                    </div>
                  )}
                  {groups !== null && groups.length > 0 && (
                    <div className="table-wrap" style={{ maxHeight: 220, overflowY: 'auto' }}>
                      <table className="table">
                        <thead>
                          <tr>
                            <th style={{ width: 32 }}></th>
                            <th>Group</th>
                            <th>Devices</th>
                            <th>Online now</th>
                          </tr>
                        </thead>
                        <tbody>
                          {groups.map((g) => (
                            <tr key={g.id}>
                              <td>
                                <input
                                  type="radio"
                                  name="chrome-remove-group"
                                  aria-label={`Apply to ${g.name}`}
                                  checked={groupId === g.id}
                                  onChange={() => setGroupId(g.id)}
                                />
                              </td>
                              <td>{g.name}</td>
                              <td>{g.deviceCount}</td>
                              <td>{g.onlineCount}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </>
              )}

              {mode === 'devices' && (
                <>
                  <div className="input-search">
                    <Icon name="search" size={15} className="search-icon" />
                    <input
                      type="search"
                      placeholder="Search devices…"
                      aria-label="Search devices"
                      value={search}
                      onChange={(e) => setSearch(e.target.value)}
                    />
                  </div>
                  {devices === null && <div className="loading">Loading devices…</div>}
                  {devices !== null && devices.length === 0 && (
                    <div className="empty-state">
                      <div className="title">No devices match</div>
                    </div>
                  )}
                  {devices !== null && devices.length > 0 && (
                    <div className="table-wrap" style={{ maxHeight: 260, overflowY: 'auto' }}>
                      <table className="table">
                        <thead>
                          <tr>
                            <th style={{ width: 32 }}></th>
                            <th>Device</th>
                            <th>Logged-on user</th>
                            <th>Status</th>
                          </tr>
                        </thead>
                        <tbody>
                          {devices.map((d) => (
                            <tr key={d.id}>
                              <td>
                                <input
                                  type="checkbox"
                                  aria-label={`Select ${deviceName(d)}`}
                                  checked={deviceIds.includes(d.id)}
                                  onChange={() => toggleDevice(d.id)}
                                />
                              </td>
                              <td>{deviceName(d)}</td>
                              <td className="muted">{d.loggedOnUser ?? '—'}</td>
                              <td>
                                {d.isOnline ? (
                                  <span className="badge ok">Online</span>
                                ) : (
                                  <span className="badge crit">Offline</span>
                                )}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                  {devices !== null && deviceTotal > devices.length && (
                    <p className="group-note">
                      Showing {devices.length} of {deviceTotal} active devices. Narrow the search, or use a group or
                      all devices.
                    </p>
                  )}
                </>
              )}

              {canRestart && (
                <div className="field" style={{ marginTop: 8 }}>
                  <label className="check-row">
                    <input
                      type="checkbox"
                      checked={restartDevices}
                      onChange={(e) => setRestartDevices(e.target.checked)}
                    />
                    {' '}{RestartCheckboxLabel}
                  </label>
                  <span className="field-hint">{RestartFinishesNote}</span>
                </div>
              )}

              <p className="field-hint" style={{ marginTop: 4 }}>
                {ScheduledRemovalNote} {AgentFloorNote}
              </p>
            </>
          )}
        </div>

        <div className="dialog-footer">
          {result !== null ? (
            <button type="button" className="btn-sm" onClick={onClose}>
              Close
            </button>
          ) : (
            <>
              <button type="button" className="btn-sm" onClick={onClose} disabled={busy}>
                Cancel
              </button>
              <button
                type="button"
                className={`btn-danger${busy ? ' btn-loading' : ''}`}
                disabled={busy || problem !== null}
                title={problem ?? undefined}
                onClick={() => void onSubmit()}
              >
                {busy ? 'Removing…' : 'Remove everywhere'}
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  )
}
