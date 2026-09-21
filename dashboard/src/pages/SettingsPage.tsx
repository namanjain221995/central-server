import { useState } from 'react'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { AccessLevelsPanel } from './AccessLevelsPanel'
import { AdministratorsPanel } from './AdministratorsPanel'
import { SecurityPanel } from './SecurityPanel'

type Tab = 'administrators' | 'access-levels' | 'security'

/**
 * Who may use this console, and what each of them can do.
 *
 * The two tabs are gated differently on purpose. Administrators needs
 * <code>platform.user.view</code>, which by design only Super Administrator and
 * Auditor hold. Access levels needs nothing beyond being signed in: it describes
 * the product's own permission model, which is identical in every deployment and
 * published in the repository, and an IT Administrator — who holds 37 of the 41
 * permissions but not that one — must still be able to read what their own level
 * grants them.
 */
export function SettingsPage() {
  const { hasPermission } = useAuth()
  const canViewAdministrators = hasPermission('platform.user.view')

  // Opens on whichever tab the signed-in administrator can actually use, so the
  // page never opens on a panel they are not allowed to see.
  const [tab, setTab] = useState<Tab>(canViewAdministrators ? 'administrators' : 'access-levels')

  return (
    <>
      <div className="segmented" role="tablist" aria-label="Settings sections">
        {canViewAdministrators && (
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'administrators'}
            className={tab === 'administrators' ? 'active' : undefined}
            onClick={() => setTab('administrators')}
          >
            <Icon name="users" size={14} />
            Administrators
          </button>
        )}
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'access-levels'}
          className={tab === 'access-levels' ? 'active' : undefined}
          onClick={() => setTab('access-levels')}
        >
          <Icon name="shield-check" size={14} />
          Access levels
        </button>
        {/* Ungated: it shows only the signed-in administrator's own second factor. */}
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'security'}
          className={tab === 'security' ? 'active' : undefined}
          onClick={() => setTab('security')}
        >
          <Icon name="key" size={14} />
          My security
        </button>
      </div>

      {tab === 'security' && <SecurityPanel />}
      {tab !== 'security' &&
        (tab === 'administrators' && canViewAdministrators
          ? <AdministratorsPanel />
          : <AccessLevelsPanel />)}
    </>
  )
}
