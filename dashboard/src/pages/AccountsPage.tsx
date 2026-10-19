import { useEffect, useState } from 'react'
import { useAuth } from '../auth/AuthContext'
import {
  ApiError,
  bulkCreateLocalUser,
  bulkResetLocalPassword,
  deviceName,
  getDevices,
  getGroups,
  type BulkLocalAccountResult,
  type DeviceGroupSummary,
  type DeviceListItem,
} from '../api/client'
import { Icon } from '../components/Icon'
import {
  provisionSummary,
  resultBadges,
  validateCreateForm,
  validateResetForm,
  type AccountsAction,
  type AccountsTargetMode,
} from './accountsView'

const DEVICE_PAGE_SIZE = 100

/**
 * Fleet-wide local Windows account management: create one account, or reset its
 * password, across all devices, a group, or a selection. It drives the server's
 * bulk endpoint, which fans the existing per-device task out; this page only
 * collects the account and the target and reports the server's own counts back.
 */
export function AccountsPage() {
  const { hasPermission } = useAuth()
  const canCreate = hasPermission('user.create')
  const canReset = hasPermission('user.reset_password')
  const canGrantAdmin = hasPermission('user.change_type')
  const canUse = canCreate || canReset

  const [action, setAction] = useState<AccountsAction>(canCreate ? 'create' : 'reset')

  // Shared target.
  const [mode, setMode] = useState<AccountsTargetMode>('all')
  const [groupId, setGroupId] = useState<string | null>(null)
  const [deviceIds, setDeviceIds] = useState<string[]>([])

  // Account fields.
  const [username, setUsername] = useState('')
  const [fullName, setFullName] = useState('')
  const [description, setDescription] = useState('')
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [accountType, setAccountType] = useState<'StandardUser' | 'Administrator'>('StandardUser')
  const [enabled, setEnabled] = useState(true)
  const [mustChange, setMustChange] = useState(true)

  // Pickers.
  const [groups, setGroups] = useState<DeviceGroupSummary[] | null>(null)
  const [devices, setDevices] = useState<DeviceListItem[] | null>(null)
  const [deviceTotal, setDeviceTotal] = useState(0)
  const [search, setSearch] = useState('')

  // Submission.
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<{ summary: string; result: BulkLocalAccountResult } | null>(null)

  useEffect(() => {
    if (!canUse) return
    let cancelled = false
    // The built-in "All Devices" group is represented by the explicit "All devices"
    // option, so it is left out of the group list to avoid two ways to say the same.
    getGroups()
      .then((list) => { if (!cancelled) setGroups(list.filter((g) => !g.isBuiltIn)) })
      .catch(() => { if (!cancelled) setError('Could not load groups.') })
    return () => { cancelled = true }
  }, [canUse])

  useEffect(() => {
    if (!canUse) return
    let cancelled = false
    getDevices(1, DEVICE_PAGE_SIZE, search, 'Active')
      .then((page) => { if (cancelled) return; setDevices(page.items); setDeviceTotal(page.totalCount) })
      .catch(() => { if (!cancelled) setError('Could not load devices.') })
    return () => { cancelled = true }
  }, [canUse, search])

  if (!canUse) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to manage local accounts.</p>
      </div>
    )
  }

  function toggleDevice(id: string) {
    setDeviceIds((current) => (current.includes(id) ? current.filter((x) => x !== id) : [...current, id]))
  }

  function buildTarget(): { allDevices?: boolean; groupId?: string; deviceIds?: string[] } | null {
    if (mode === 'all') return { allDevices: true }
    if (mode === 'group') return groupId !== null ? { groupId } : null
    return deviceIds.length > 0 ? { deviceIds } : null
  }

  const target = { mode, groupId, deviceIds }
  const problem = action === 'create'
    ? validateCreateForm({ username, password, confirmPassword, accountType }, target)
    : validateResetForm({ username, password, confirmPassword }, target)

  async function onSubmit() {
    const selected = buildTarget()
    if (busy || problem !== null || selected === null) return

    setBusy(true)
    setError(null)
    setOutcome(null)
    try {
      const result = action === 'create'
        ? await bulkCreateLocalUser({
            username: username.trim(),
            fullName: fullName.trim() || undefined,
            description: description.trim() || undefined,
            password,
            enabled,
            mustChangePasswordAtNextLogon: mustChange,
            accountType,
            ...selected,
          })
        : await bulkResetLocalPassword({ username: username.trim(), password, ...selected })

      setOutcome({ summary: provisionSummary(result, action === 'create' ? 'created' : 'reset'), result })
      // Clear the secret; keep the rest so a follow-up on another target is quick.
      setPassword('')
      setConfirmPassword('')
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) {
        setError('That target resolved to no devices you can act on.')
      } else {
        setError(e instanceof ApiError ? (e.detail ?? e.message) : 'The operation could not be completed.')
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      {error && (
        <div className="error-banner" role="alert"><Icon name="alert" size={15} /><span>{error}</span></div>
      )}
      {outcome && (
        <div className="notice-banner" role="status"><Icon name="check" size={15} /><span>{outcome.summary}</span></div>
      )}

      <div className="page-header">
        <div className="lede">
          Create a local Windows account, or reset its password, across all devices, a group, or a
          selection. Offline machines apply the change when they next check in.
        </div>
      </div>

      <div className="card card-section">
        <div className="field full">
          <span className="field-label">Action</span>
          <div className="radio-row">
            {canCreate && (
              <label><input type="radio" name="acct-action" checked={action === 'create'}
                onChange={() => { setAction('create'); setOutcome(null) }} /> Create account</label>
            )}
            {canReset && (
              <label><input type="radio" name="acct-action" checked={action === 'reset'}
                onChange={() => { setAction('reset'); setOutcome(null) }} /> Reset password</label>
            )}
          </div>
        </div>

        <div className="form-grid">
          <div className="field">
            <label className="field-label" htmlFor="acct-username">Username</label>
            <input id="acct-username" value={username} autoComplete="off"
              onChange={(e) => setUsername(e.target.value)} placeholder="e.g. itadmin" />
            {action === 'reset' && (
              <span className="field-hint">The account must already exist on the devices.</span>
            )}
          </div>

          {action === 'create' && (
            <div className="field">
              <label className="field-label" htmlFor="acct-fullname">Display name (optional)</label>
              <input id="acct-fullname" value={fullName} autoComplete="off"
                onChange={(e) => setFullName(e.target.value)} />
            </div>
          )}

          <div className="field">
            <label className="field-label" htmlFor="acct-password">Password</label>
            <input id="acct-password" type="password" value={password} autoComplete="new-password"
              onChange={(e) => setPassword(e.target.value)} />
            <span className="field-hint">At least 8 characters. The same password is set on every targeted device.</span>
          </div>

          <div className="field">
            <label className="field-label" htmlFor="acct-confirm">Confirm password</label>
            <input id="acct-confirm" type="password" value={confirmPassword} autoComplete="new-password"
              onChange={(e) => setConfirmPassword(e.target.value)} />
            {confirmPassword.length > 0 && password !== confirmPassword && (
              <span className="field-message">Passwords do not match.</span>
            )}
          </div>

          {action === 'create' && (
            <div className="field full">
              <label className="field-label" htmlFor="acct-description">Description (optional)</label>
              <input id="acct-description" value={description} autoComplete="off"
                onChange={(e) => setDescription(e.target.value)} />
            </div>
          )}
        </div>

        {action === 'create' && (
          <>
            <div className="field full">
              <span className="field-label">Account type</span>
              <div className="radio-row">
                <label><input type="radio" name="acct-type" checked={accountType === 'StandardUser'}
                  onChange={() => setAccountType('StandardUser')} /> Standard user</label>
                <label><input type="radio" name="acct-type" disabled={!canGrantAdmin}
                  checked={accountType === 'Administrator'}
                  onChange={() => setAccountType('Administrator')} /> Administrator</label>
              </div>
              {accountType === 'Administrator' && (
                <div className="warn-banner">
                  This account will be a local administrator on every targeted device.
                </div>
              )}
              {!canGrantAdmin && <span className="field-hint">You may create standard users only.</span>}
            </div>

            <div className="field full">
              <label className="check-row"><input type="checkbox" checked={mustChange}
                onChange={(e) => setMustChange(e.target.checked)} /> User must change password at next logon</label>
              <label className="check-row"><input type="checkbox" checked={enabled}
                onChange={(e) => setEnabled(e.target.checked)} /> Account enabled</label>
            </div>
          </>
        )}

        <div className="field full">
          <span className="field-label">Apply to</span>
          <div className="radio-row">
            <label><input type="radio" name="acct-target" checked={mode === 'all'}
              onChange={() => setMode('all')} /> All devices</label>
            <label><input type="radio" name="acct-target" checked={mode === 'group'}
              onChange={() => setMode('group')} /> A group (department)</label>
            <label><input type="radio" name="acct-target" checked={mode === 'devices'}
              onChange={() => setMode('devices')} /> Specific devices</label>
          </div>
        </div>

        {mode === 'all' && (
          <p className="group-note">Every active device in your scope. Retired devices are not included.</p>
        )}

        {mode === 'group' && (
          <div className="field full">
            {groups === null && <div className="loading">Loading groups…</div>}
            {groups !== null && groups.length === 0 && (
              <div className="empty-state">
                <Icon name="groups" size={36} strokeWidth={1.25} className="icon" />
                <div className="title">No groups you can see</div>
                <div>Groups outside your scope are not shown.</div>
              </div>
            )}
            {groups !== null && groups.length > 0 && (
              <div className="table-wrap" style={{ maxHeight: 240, overflowY: 'auto' }}>
                <table className="table">
                  <thead><tr><th style={{ width: 32 }}></th><th>Group</th><th>Devices</th><th>Online now</th></tr></thead>
                  <tbody>
                    {groups.map((g) => (
                      <tr key={g.id}>
                        <td><input type="radio" name="acct-group" aria-label={`Apply to ${g.name}`}
                          checked={groupId === g.id} onChange={() => setGroupId(g.id)} /></td>
                        <td>{g.name}</td>
                        <td>{g.deviceCount}</td>
                        <td>{g.onlineCount}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}

        {mode === 'devices' && (
          <div className="field full">
            <div className="input-search">
              <Icon name="search" size={15} className="search-icon" />
              <input type="search" placeholder="Search devices…" aria-label="Search devices"
                value={search} onChange={(e) => setSearch(e.target.value)} />
            </div>
            {devices === null && <div className="loading">Loading devices…</div>}
            {devices !== null && devices.length === 0 && (
              <div className="empty-state"><div className="title">No devices match</div></div>
            )}
            {devices !== null && devices.length > 0 && (
              <div className="table-wrap" style={{ maxHeight: 280, overflowY: 'auto' }}>
                <table className="table">
                  <thead><tr><th style={{ width: 32 }}></th><th>Device</th><th>Logged-on user</th><th>Status</th></tr></thead>
                  <tbody>
                    {devices.map((d) => (
                      <tr key={d.id}>
                        <td><input type="checkbox" aria-label={`Select ${deviceName(d)}`}
                          checked={deviceIds.includes(d.id)} onChange={() => toggleDevice(d.id)} /></td>
                        <td>{deviceName(d)}</td>
                        <td className="muted">{d.loggedOnUser ?? '—'}</td>
                        <td>{d.isOnline
                          ? <span className="badge ok">Online</span>
                          : <span className="badge crit">Offline</span>}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
            {devices !== null && deviceTotal > devices.length && (
              <p className="group-note">
                Showing {devices.length} of {deviceTotal} active devices. Narrow the search, or use a group or all devices.
              </p>
            )}
          </div>
        )}

        {outcome && resultBadges(outcome.result).length > 0 && (
          <div className="btn-row" style={{ marginTop: 8 }}>
            {resultBadges(outcome.result).map((b) => (
              <span key={b.label} className={`badge ${b.tone}`}>{b.value} {b.label}</span>
            ))}
          </div>
        )}

        <div className="toolbar" style={{ marginTop: 12 }}>
          <span className="spacer" />
          <button className={`btn-accent${busy ? ' btn-loading' : ''}`} disabled={busy || problem !== null}
            title={problem ?? undefined} onClick={onSubmit}>
            {action === 'create' ? 'Create account' : 'Reset password'}
          </button>
        </div>
      </div>
    </>
  )
}
