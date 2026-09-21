import { useCallback, useEffect, useState } from 'react'
import {
  disablePlatformUser,
  enablePlatformUser,
  getPlatformUsers,
  resetPlatformUserPassword,
  type GeneratedCredential,
  type PlatformUserSummary,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { CreateAdministratorDialog } from './CreateAdministratorDialog'
import { ResetPasswordResultDialog } from './ResetPasswordResultDialog'

/**
 * The people who can administer this platform.
 *
 * Every mutation here needs <code>platform.user.manage</code>, which only Super
 * Administrator holds — managing endpoints and deciding who may manage endpoints
 * are separate duties. The controls are hidden without it and the page says which
 * permission is missing, rather than silently offering nothing.
 */
export function AdministratorsPanel() {
  const { user, hasPermission } = useAuth()
  const canManage = hasPermission('platform.user.manage')

  const [users, setUsers] = useState<PlatformUserSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busyId, setBusyId] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [resetResult, setResetResult] = useState<{ email: string; credential: GeneratedCredential } | null>(null)

  const refresh = useCallback(async () => {
    try {
      setUsers(await getPlatformUsers())
    } catch (cause: unknown) {
      setError(cause instanceof Error ? cause.message : 'Could not load administrators.')
    }
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  async function run(userId: string, action: () => Promise<void>, success: string) {
    setError(null)
    setNotice(null)
    setBusyId(userId)
    try {
      await action()
      setNotice(success)
      await refresh()
    } catch (cause: unknown) {
      setError(cause instanceof Error ? cause.message : 'The change was refused.')
    } finally {
      setBusyId(null)
    }
  }

  if (error && !users) {
    return (
      <div className="card">
        <div className="empty-state">
          <div className="icon" aria-hidden="true"><Icon name="alert" size={22} /></div>
          <div className="title">Could not load administrators</div>
          <p>{error}</p>
        </div>
      </div>
    )
  }

  return (
    <>
      {error && (
        <div className="warn-banner" role="alert">
          {error}
          <button type="button" className="btn-ghost btn-sm" onClick={() => setError(null)}>Dismiss</button>
        </div>
      )}
      {notice && (
        <div className="warn-banner" role="status">
          {notice}
          <button type="button" className="btn-ghost btn-sm" onClick={() => setNotice(null)}>Dismiss</button>
        </div>
      )}

      <div className="page-header">
        <p className="lede">
          Accounts that can sign in to this console. Each holds one access level, which
          decides what they may do. Signing in to a managed Windows PC is a different
          thing entirely — those are local accounts, managed per device.
        </p>
        {canManage && (
          <button type="button" className="btn-primary" onClick={() => setCreating(true)}>
            <Icon name="plus" size={14} />
            New administrator
          </button>
        )}
      </div>

      {!canManage && (
        <div className="warn-banner" role="status">
          You can see this list but not change it. Creating, disabling or resetting an
          administrator requires <code>platform.user.manage</code>, which only a Super
          Administrator holds.
        </div>
      )}

      <div className="card">
        <div className="table-wrap">
          <table className="table">
            <caption className="sr-only">Platform administrators</caption>
            <thead>
              <tr>
                <th scope="col">Administrator</th>
                <th scope="col">Access level</th>
                <th scope="col">Status</th>
                <th scope="col">Device scope</th>
                <th scope="col">Last signed in</th>
                {canManage && <th scope="col" style={{ width: 240 }}>Actions</th>}
              </tr>
            </thead>
            <tbody>
              {(users ?? []).map((row) => {
                const isSelf = row.id === user?.userId
                const busy = busyId === row.id

                return (
                  <tr key={row.id}>
                    <td>
                      <div style={{ fontWeight: 600 }}>{row.displayName}</div>
                      <div style={{ fontSize: 12, color: 'var(--color-text-muted)' }}>{row.email}</div>
                      {isSelf && <span className="badge info plain">You</span>}
                    </td>
                    <td>{row.roleDisplayName ?? <span className="badge warn">No access level</span>}</td>
                    <td>
                      <span className={statusBadge(row.status)}>{row.status}</span>
                      {row.mustChangePassword && (
                        <div style={{ marginTop: 4 }}>
                          <span className="badge warn plain">Must change password</span>
                        </div>
                      )}
                    </td>
                    <td>
                      {row.hasAllDeviceScope
                        ? 'All devices'
                        : <span title="This administrator reaches no device until a group scope is granted.">
                            Scoped groups only
                          </span>}
                    </td>
                    <td>{row.lastLoginAt ? new Date(row.lastLoginAt).toLocaleString() : 'Never'}</td>

                    {canManage && (
                      <td>
                        <div className="btn-row">
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            disabled={busy || isSelf}
                            title={isSelf ? 'Use Change password for your own account.' : undefined}
                            onClick={() => void run(
                              row.id,
                              async () => {
                                const credential = await resetPlatformUserPassword(row.id)
                                setResetResult({ email: row.email, credential })
                              },
                              `A new password was generated for ${row.email}.`,
                            )}
                          >
                            Reset password
                          </button>

                          {row.status === 'Disabled' ? (
                            <button
                              type="button"
                              className="btn-sm"
                              disabled={busy}
                              onClick={() => void run(
                                row.id,
                                () => enablePlatformUser(row.id),
                                `${row.email} can sign in again.`,
                              )}
                            >
                              Enable
                            </button>
                          ) : (
                            <button
                              type="button"
                              className="btn-danger btn-sm"
                              disabled={busy || isSelf || row.isSystemAccount}
                              title={
                                isSelf
                                  ? 'You cannot disable your own account.'
                                  : row.isSystemAccount
                                    ? 'This is a built-in account created by seeding.'
                                    : undefined
                              }
                              onClick={() => void run(
                                row.id,
                                () => disablePlatformUser(row.id),
                                `${row.email} can no longer sign in.`,
                              )}
                            >
                              Disable
                            </button>
                          )}
                        </div>
                      </td>
                    )}
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>

        {users?.length === 0 && (
          <div className="empty-state">
            <div className="icon" aria-hidden="true"><Icon name="users" size={22} /></div>
            <div className="title">No administrators</div>
          </div>
        )}
      </div>

      {creating && (
        <CreateAdministratorDialog
          onClose={() => setCreating(false)}
          onCreated={() => void refresh()}
        />
      )}

      {resetResult && (
        <ResetPasswordResultDialog
          email={resetResult.email}
          credential={resetResult.credential}
          onClose={() => setResetResult(null)}
        />
      )}
    </>
  )
}

function statusBadge(status: PlatformUserSummary['status']): string {
  switch (status) {
    case 'Active': return 'badge ok'
    case 'Disabled': return 'badge crit'
    case 'Locked': return 'badge warn'
    default: return 'badge neutral'
  }
}
