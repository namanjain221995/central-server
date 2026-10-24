import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { AccessLevelsPanel } from './AccessLevelsPanel'
import { AdministratorsPanel } from './AdministratorsPanel'
import { AppearancePanel } from './AppearancePanel'
import { NetworkAccessPanel } from './NetworkAccessPanel'
import { SecurityPanel } from './SecurityPanel'

type Tab = 'administrators' | 'access-levels' | 'network-access' | 'security' | 'appearance'

/**
 * Who may use this console, and what each of them can do.
 *
 * The gated tabs are gated differently on purpose. Administrators needs
 * <code>platform.user.view</code>, which by design only Super Administrator and
 * Auditor hold. Network access needs <code>platform.settings.manage</code> —
 * Super Administrator alone — because it decides where the whole console
 * answers from. Access levels needs nothing beyond being signed in: it describes
 * the product's own permission model, which is identical in every deployment and
 * published in the repository, and an IT Administrator — who holds 37 of the 41
 * permissions but not that one — must still be able to read what their own level
 * grants them.
 */
export function SettingsPage() {
  const { hasPermission } = useAuth()
  const canViewAdministrators = hasPermission('platform.user.view')
  const canManagePlatformSettings = hasPermission('platform.settings.manage')

  // Opens on whichever tab the signed-in administrator can actually use, so the
  // page never opens on a panel they are not allowed to see.
  // ?tab=appearance (or security) opens that tab directly; the two ungated tabs
  // are the only ones a link may choose, so a link cannot open a gated panel.
  const [searchParams] = useSearchParams()
  const linked = searchParams.get('tab')
  const [tab, setTab] = useState<Tab>(
    linked === 'appearance' || linked === 'security'
      ? linked
      : canViewAdministrators
        ? 'administrators'
        : 'access-levels',
  )

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
        {canManagePlatformSettings && (
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'network-access'}
            className={tab === 'network-access' ? 'active' : undefined}
            onClick={() => setTab('network-access')}
          >
            <Icon name="security" size={14} />
            Network access
          </button>
        )}
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
        {/* Ungated: it changes only the signed-in administrator's own preference. */}
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'appearance'}
          className={tab === 'appearance' ? 'active' : undefined}
          onClick={() => setTab('appearance')}
        >
          <Icon name="dashboard" size={14} />
          Appearance
        </button>
      </div>

      {tab === 'security' && <SecurityPanel />}
      {tab === 'appearance' && <AppearancePanel />}
      {/* A gated tab somebody lost the permission for mid-session falls back to
          the ungated panel rather than rendering a view that can only 403. */}
      {tab === 'network-access' &&
        (canManagePlatformSettings ? <NetworkAccessPanel /> : <AccessLevelsPanel />)}
      {tab === 'administrators' &&
        (canViewAdministrators ? <AdministratorsPanel /> : <AccessLevelsPanel />)}
      {tab === 'access-levels' && <AccessLevelsPanel />}
    </>
  )
}
