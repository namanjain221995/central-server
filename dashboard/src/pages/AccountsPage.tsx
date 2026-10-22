import { useEffect, useState } from 'react'
import { useAuth } from '../auth/AuthContext'
import {
  ApiError,
  bulkCreateLocalUser,
  bulkDeleteLocalUser,
  bulkDisableLocalUser,
  bulkEnableLocalUser,
  bulkResetLocalPassword,
  deviceName,
  getDevices,
  getGroups,
  type DeviceGroupSummary,
  type DeviceListItem,
} from '../api/client'
import { Icon } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import {
  actionLabel,
  actionVerb,
  provisionSummary,
  resultBadges,
  validateCreateForm,
  validateLifecycleForm,
  validateResetForm,
  type AccountsAction,
  type AccountsTargetMode,
  type BulkOutcome,
} from './accountsView'

const DEVICE_PAGE_SIZE = 100

/**
 * Fleet-wide local Windows account management: create one account, reset its
 * password, or disable/enable/delete it, across all devices, a group, or a
 * selection. It drives the server's bulk endpoints, which fan the existing
 * per-device task out; this page only collects the account and the target and
 * reports the server's own counts back.
 */
export function AccountsPage() {
  const { hasPermission } = useAuth()
  const canCreate = hasPermission('user.create')
  const canReset = hasPermission('user.reset_password')
  const canGrantAdmin = hasPermission('user.change_type')
  // The same permission covers disable and enable, as it does on a device's
  // Users tab: both flip the one switch.
  const canDisable = hasPermission('user.disable')
  const canDelete = hasPermission('user.delete')
  const canUse = canCreate || canReset || canDisable || canDelete

  const [action, setAction] = useState<AccountsAction>(
    canCreate ? 'create' : canReset ? 'reset' : canDisable ? 'disable' : 'delete')

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
  const [confirmingDelete, setConfirmingDelete] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [outcome, setOutcome] = useState<{ summary: string; result: BulkOutcome } | null>(null)

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

  // Disable/enable/delete act on an account that already exists, so only its
  // name and the target matter; every provisioning field disappears.
  const needsPassword = action === 'create' || action === 'reset'

  const target = { mode, groupId, deviceIds }
  const problem = action === 'create'
    ? validateCreateForm({ username, password, confirmPassword, accountType }, target)
    : action === 'reset'
      ? validateResetForm({ username, password, confirmPassword }, target)
      : validateLifecycleForm({ username }, target)

  async function submitAction(selected: { allDevices?: boolean; groupId?: string; deviceIds?: string[] }) {
    switch (action) {
      case 'create':
        return bulkCreateLocalUser({
          username: username.trim(),
          fullName: fullName.trim() || undefined,
          description: description.trim() || undefined,
          password,
          enabled,
          mustChangePasswordAtNextLogon: mustChange,
          accountType,
          ...selected,
        })
      case 'reset':
        return bulkResetLocalPassword({ username: username.trim(), password, ...selected })
      case 'disable':
        return bulkDisableLocalUser({ username: username.trim(), ...selected })
      case 'enable':
        return bulkEnableLocalUser({ username: username.trim(), ...selected })
      case 'delete':
        return bulkDeleteLocalUser({ username: username.trim(), ...selected })
    }
  }

  async function onSubmit() {
    const selected = buildTarget()
    if (busy || problem !== null || selected === null) return

    setBusy(true)
    setError(null)
    setOutcome(null)
    try {
      const result = await submitAction(selected)

      setOutcome({ summary: provisionSummary(result, actionVerb[action]), result })
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
          Create, disable, enable or delete a local Windows account, or reset its password, across
          all devices, a group, or a selection. Offline machines apply the change when they next check in.
        </div>
      </div>

      <div className="card card-section">
        <div className="field full">
          <span className="field-label">Action</span>
          <div className="radio-row">
            {canCreate && (
              <label><input type="radio" name="acct-action" checked={action === 'create'}
                onChange={() => { setAction('create'); setOutcome(null) }} /> {actionLabel.create}</label>
            )}
            {canReset && (
              <label><input type="radio" name="acct-action" checked={action === 'reset'}
                onChange={() => { setAction('reset'); setOutcome(null) }} /> {actionLabel.reset}</label>
            )}
            {canDisable && (
              <label><input type="radio" name="acct-action" checked={action === 'disable'}
                onChange={() => { setAction('disable'); setOutcome(null) }} /> {actionLabel.disable}</label>
            )}
            {canDisable && (
              <label><input type="radio" name="acct-action" checked={action === 'enable'}
                onChange={() => { setAction('enable'); setOutcome(null) }} /> {actionLabel.enable}</label>
            )}
            {canDelete && (
              <label><input type="radio" name="acct-action" checked={action === 'delete'}
                onChange={() => { setAction('delete'); setOutcome(null) }} /> {actionLabel.delete}</label>
            )}
          </div>
          {action === 'delete' && (
            <div className="warn-banner">
              The account will be permanently deleted from every targeted device. The user's
              Windows profile folder may remain on the device, but the account and its sign-in
              cannot be recovered.
            </div>
          )}
        </div>

        <div className="form-grid">
          <div className="field">
            <label className="field-label" htmlFor="acct-username">Username</label>
            <input id="acct-username" value={username} autoComplete="off"
              onChange={(e) => setUsername(e.target.value)} placeholder="e.g. itadmin" />
            {action === 'reset' && (
              <span className="field-hint">The account must already exist on the devices.</span>
            )}
            {!needsPassword && (
              <span className="field-hint">Devices where this account does not exist are skipped.</span>
            )}
          </div>

          {action === 'create' && (
            <div className="field">
              <label className="field-label" htmlFor="acct-fullname">Display name (optional)</label>
              <input id="acct-fullname" value={fullName} autoComplete="off"
                onChange={(e) => setFullName(e.target.value)} />
            </div>
          )}

          {needsPassword && (
            <div className="field">
              <label className="field-label" htmlFor="acct-password">Password</label>
              <input id="acct-password" type="password" value={password} autoComplete="new-password"
                onChange={(e) => setPassword(e.target.value)} />
              <span className="field-hint">At least 8 characters. The same password is set on every targeted device.</span>
            </div>
          )}

          {needsPassword && (
            <div className="field">
              <label className="field-label" htmlFor="acct-confirm">Confirm password</label>
              <input id="acct-confirm" type="password" value={confirmPassword} autoComplete="new-password"
                onChange={(e) => setConfirmPassword(e.target.value)} />
              {confirmPassword.length > 0 && password !== confirmPassword && (
                <span className="field-message">Passwords do not match.</span>
              )}
            </div>
          )}

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
          <button className={`${action === 'delete' ? 'btn-danger' : 'btn-accent'}${busy ? ' btn-loading' : ''}`}
            disabled={busy || problem !== null} title={problem ?? undefined}
            // Deletion is the one action that cannot be walked back, so it alone
            // goes through a confirmation; disable is reversed by enable.
            onClick={() => { if (action === 'delete') setConfirmingDelete(true); else void onSubmit() }}>
            {actionLabel[action]}
          </button>
        </div>
      </div>

      {confirmingDelete && (
        <ConfirmDialog
          title={`Delete '${username.trim()}' from the targeted devices?`}
          confirmLabel="Delete account"
          onCancel={() => setConfirmingDelete(false)}
          onConfirm={() => { setConfirmingDelete(false); void onSubmit() }}
        >
          The account '{username.trim()}' will be permanently deleted from every targeted device
          and cannot be recovered from this console. The user's Windows profile folder may remain
          on each device, but the account and its sign-in are gone. Devices where the account does
          not exist are skipped.
        </ConfirmDialog>
      )}
    </>
  )
}
