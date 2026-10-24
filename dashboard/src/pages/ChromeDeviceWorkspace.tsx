import { useCallback, useEffect, useId, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  ApiError,
  deleteChromeExtensionRule,
  getChromeExtensionRules,
  getChromeProfileExtensions,
  getDeviceChrome,
  removeChromeProfile,
  requestInventoryRefresh,
  setChromeExtensionRule,
  type ChromeDeviceDetail,
  type ChromeExtensionRule,
  type ChromeExtensionRuleMode,
  type ChromeExtensionRow,
  type ChromeProfileRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { Icon } from '../components/Icon'
import { RowActionsMenu } from '../components/RowActionsMenu'
import { TaskProgress } from '../components/TaskProgress'
import { useTaskTracker } from '../components/useTaskTracker'
import {
  clearRuleLabel,
  compareRules,
  extensionIdError,
  extensionRuleMenuItems,
  normalizeExtensionId,
  ruleActionLabel,
  ruleForExtension,
  ruleModeForAction,
  ruleModeLabel,
  ruleModeTone,
  type ChromeExtensionRuleAction,
} from './chromeExtensionRulesView'
import {
  canRemove,
  canRemoveEverywhere,
  removeProfileLabel,
  removeRestartLabel,
  removeSuggested,
  removeTitle,
  RestartCheckboxLabel,
  RestartFinishesNote,
  ScheduledRemovalNote,
  statusBadge,
} from './chromeProfilesView'
import { RemoveChromeAccountDialog } from './RemoveChromeAccountDialog'
import {
  chromeStatusLabel,
  chromeStatusTone,
  compareExtensions,
  countLabel,
  enabledLabel,
  enabledTone,
  formatDate,
  formatDateTime,
  extensionSourceLabel,
  hiddenExtensionsNote,
  visibleExtensions,
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
 * each profile's extensions, what is known about updates — and, for an operator
 * with `chrome.manage`, control over the device's extensions. Per extension:
 * Block, Allow, Remove, or clear the rule; plus force-install by Chrome Web
 * Store ID. Each mutation queues a task the agent enforces as machine-wide
 * Chrome policy, and the workspace follows that task to what the device
 * actually did (useTaskTracker) rather than calling a stored row "applied".
 *
 * The rules list is the source of truth for policy state: the inventoried
 * extension rows only change when the device uploads its next snapshot, so a
 * fresh rule shows in Managed extensions before the inventory catches up.
 *
 * Profiles carry the server's lifecycle status (active, sign-in broken,
 * dormant, not signed in — the server's verdict, rendered as-is and never
 * re-derived here), and `chrome.manage` can remove a non-Default profile from
 * the device: a queued task followed to its real outcome like every other
 * mutation. Since agent 1.26.0 an open Chrome no longer fails it — the device
 * reports the removal as scheduled and completes it when Chrome closes or at
 * the next startup — and a viewer who also holds `device.restart` can queue a
 * restart alongside to finish it immediately. A signed-in profile additionally
 * offers "Remove on all devices…", the fleet-wide form of the same removal
 * (RemoveChromeAccountDialog). Extension enforcement stays machine-wide
 * (Chrome's enterprise policy applies to every profile on the PC), so the
 * extensions list answers "what does each profile have", not "which profile to
 * change".
 */
export function ChromeDeviceWorkspace({ deviceId, onClose }: { deviceId: string; onClose: () => void }) {
  const { hasPermission } = useAuth()
  const canRefresh = hasPermission('device.refresh_inventory')
  // Viewing rules rides the page's chrome view permission; every mutating
  // control below is gated on this and hidden without it. The server re-checks.
  const canManage = hasPermission('chrome.manage')
  // The restart-to-finish option rides the SAME permission as the device
  // Restart action (DeviceDetailPage's power actions): asking for a restart is
  // restarting, whatever queued it. Without it the checkbox does not exist and
  // the server would answer 403 anyway.
  const canRestart = hasPermission('device.restart')

  const [detail, setDetail] = useState<ChromeDeviceDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('overview')
  const [selectedProfileId, setSelectedProfileId] = useState<string | null>(null)
  const [extensions, setExtensions] = useState<Record<string, ChromeExtensionRow[]>>({})
  const [extensionsError, setExtensionsError] = useState<string | null>(null)
  const [refreshing, setRefreshing] = useState(false)
  const [rules, setRules] = useState<ChromeExtensionRule[] | null>(null)
  const [rulesError, setRulesError] = useState<string | null>(null)
  const [ruleBusy, setRuleBusy] = useState(false)
  const [confirmRemove, setConfirmRemove] = useState<{ extensionId: string; name: string | null } | null>(null)
  const [confirmProfileRemove, setConfirmProfileRemove] = useState<ChromeProfileRow | null>(null)
  // The restart-to-finish choice for the open confirmation. Reset every time
  // the dialog opens, so a restart picked for one removal never carries over
  // to the next (mirrors the restart dialog's delay on the device page).
  const [removeWithRestart, setRemoveWithRestart] = useState(false)
  const [removeEverywhere, setRemoveEverywhere] = useState<ChromeProfileRow | null>(null)
  const [profileBusy, setProfileBusy] = useState(false)

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

  const loadRules = useCallback(async () => {
    try {
      setRules([...(await getChromeExtensionRules(deviceId))].sort(compareRules))
      setRulesError(null)
    } catch {
      setRulesError('The extension rules for this device could not be loaded.')
    }
  }, [deviceId])

  // Rule mutations settle through their queued task; once the device has
  // answered, re-read the rules (and the detail, whose refresh flag may have
  // moved). The inventory itself only changes on the device's next snapshot,
  // which is why the rules — not the extension table — are read back here.
  const onTaskSettled = useCallback(async () => {
    await Promise.all([load(), loadRules()])
  }, [load, loadRules])

  const { tracked, track, dismiss } = useTaskTracker(deviceId, !(detail?.isOnline ?? false), onTaskSettled)
  const trackedRef = useRef(tracked)
  trackedRef.current = tracked

  // A new device is a new workspace: nothing from the previous one carries
  // over, task banners included — a task from the old device would otherwise
  // sit "Queued" forever, polled against a device that never had it.
  useEffect(() => {
    setLoading(true)
    setDetail(null)
    setSelectedProfileId(null)
    setExtensions({})
    setExtensionsError(null)
    setNotice(null)
    setRules(null)
    setRulesError(null)
    setConfirmRemove(null)
    setConfirmProfileRemove(null)
    setRemoveEverywhere(null)
    trackedRef.current.forEach((t) => dismiss(t.taskId))
    void load()
    void loadRules()
  }, [deviceId, load, loadRules, dismiss])

  /**
   * Sets one extension's rule and follows the queued task. Returns whether the
   * server accepted it, so the force-install form keeps its input on a refusal
   * instead of making the operator retype a 32-character ID.
   */
  const applyRule = useCallback(
    async (extensionId: string, mode: ChromeExtensionRuleMode, name: string | null): Promise<boolean> => {
      setRuleBusy(true)
      setError(null)
      try {
        const { taskId } = await setChromeExtensionRule(deviceId, extensionId, mode, name?.trim() || undefined)
        track(taskId, ruleActionLabel(mode, name, extensionId))
        await loadRules()
        return true
      } catch (e) {
        // 409 problem+json when the agent is too old for extension control;
        // its detail says which version is needed better than a guess here.
        setError(e instanceof ApiError && e.detail ? e.detail : 'The extension rule could not be queued.')
        return false
      } finally {
        setRuleBusy(false)
      }
    },
    [deviceId, track, loadRules],
  )

  const clearRule = useCallback(
    async (extensionId: string, name: string | null) => {
      setRuleBusy(true)
      setError(null)
      try {
        const { taskId } = await deleteChromeExtensionRule(deviceId, extensionId)
        track(taskId, clearRuleLabel(name, extensionId))
        await loadRules()
      } catch (e) {
        setError(e instanceof ApiError && e.detail ? e.detail : 'The extension rule could not be cleared.')
      } finally {
        setRuleBusy(false)
      }
    },
    [deviceId, track, loadRules],
  )

  /**
   * Queues the removal of one Chrome profile and follows the task exactly the
   * way an extension rule is followed: the agent's own report — "Scheduled: …"
   * while Chrome stays open, "profile's signed-in account no longer matches…" —
   * reaches the banner verbatim via useTaskTracker, and the profile list itself
   * only changes once the device has uploaded its next snapshot (onTaskSettled
   * re-reads the detail). When a restart was asked for, the server queues it as
   * a second task, which is tracked beside the first so the operator sees both
   * settle.
   */
  const removeProfile = useCallback(
    async (profile: ChromeProfileRow, restartDevice: boolean) => {
      setProfileBusy(true)
      setError(null)
      try {
        const { taskId, restartTaskId } = await removeChromeProfile(profile.id, restartDevice)
        track(taskId, removeProfileLabel(profile))
        if (restartTaskId) {
          track(restartTaskId, removeRestartLabel(profile))
          setNotice('Removal and restart queued. The restart finishes the removal immediately; both tasks are tracked below.')
        }
      } catch (e) {
        // 403/404, or the problem+json refusal naming the agent floor (1.26.0
        // for scheduled removal) — the server's own wording beats a guess.
        setError(e instanceof ApiError && e.detail ? e.detail : 'The profile removal could not be queued.')
      } finally {
        setProfileBusy(false)
      }
    },
    [track],
  )

  /** One entry point for every per-extension menu; Remove confirms first. */
  const onRuleAction = useCallback(
    (action: ChromeExtensionRuleAction, target: { extensionId: string; name: string | null }) => {
      if (action === 'remove') {
        setConfirmRemove(target)
      } else if (action === 'clear') {
        void clearRule(target.extensionId, target.name)
      } else {
        void applyRule(target.extensionId, ruleModeForAction(action), target.name)
      }
    },
    [applyRule, clearRule],
  )

  // While a refresh is pending the agent uploads within a heartbeat or two, so
  // the workspace re-reads every 10 seconds until the new snapshot lands and
  // then stops. Without this the operator who clicked Refresh sat looking at
  // the old snapshot behind a "Refresh pending" badge until they reopened it.
  const refreshPending = detail?.inventoryRefreshPending ?? false
  useEffect(() => {
    if (!refreshPending) return
    const timer = setInterval(() => void load(), 10_000)
    return () => clearInterval(timer)
  }, [refreshPending, load])

  // A new snapshot replaces every profile and extension row, so the cached
  // extension lists belong to profiles that no longer exist and are dropped.
  const snapshotAt = detail?.installation?.collectedAt ?? null
  useEffect(() => {
    setExtensions({})
  }, [snapshotAt])

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

      <TaskProgress tasks={tracked} onDismiss={dismiss} />

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
              rules={rules}
              canManage={canManage}
              ruleBusy={ruleBusy}
              onRuleAction={onRuleAction}
              profileBusy={profileBusy}
              onRemoveProfile={(profile) => {
                // Every removal starts without a restart; it is opted into
                // deliberately each time.
                setRemoveWithRestart(false)
                setConfirmProfileRemove(profile)
              }}
              onRemoveEverywhere={setRemoveEverywhere}
            />
          )}

          {tab === 'extensions' && (
            <ExtensionsTab
              detail={detail}
              selectedProfile={selectedProfile}
              onSelect={setSelectedProfileId}
              extensions={selectedProfileId ? (extensions[selectedProfileId] ?? null) : null}
              extensionsError={extensionsError}
              rules={rules}
              rulesError={rulesError}
              canManage={canManage}
              ruleBusy={ruleBusy}
              onRuleAction={onRuleAction}
              onForceInstall={(extensionId, name) => applyRule(extensionId, 'ForceInstalled', name)}
              onRetryRules={() => void loadRules()}
            />
          )}

          {tab === 'updates' && <UpdatesTab detail={detail} />}
        </>
      )}

      {confirmRemove && (
        <ConfirmDialog
          title={`Remove ${confirmRemove.name ?? 'this extension'} from this device?`}
          confirmLabel="Remove extension"
          onCancel={() => setConfirmRemove(null)}
          onConfirm={() => {
            const target = confirmRemove
            setConfirmRemove(null)
            void applyRule(target.extensionId, 'Removed', target.name)
          }}
        >
          Chrome uninstalls it from every profile on this device and keeps it out while the rule stands — the device
          applies the policy on its next check-in, without asking the person using it. Clear the rule later to let it be
          installed again.
        </ConfirmDialog>
      )}

      {confirmProfileRemove && (
        <ConfirmDialog
          title={`Remove Chrome profile "${profileTitle(confirmProfileRemove)}" from this device?`}
          confirmLabel="Remove profile"
          onCancel={() => setConfirmProfileRemove(null)}
          onConfirm={() => {
            const target = confirmProfileRemove
            setConfirmProfileRemove(null)
            void removeProfile(target, canRestart && removeWithRestart)
          }}
        >
          <>
            This removes the profile from Chrome on the device. Bookmarks, history and saved passwords stored only in
            this profile go with it; anything synced to a signed-in Google account stays in that account.{' '}
            {ScheduledRemovalNote}
            {canRestart && (
              // The restart option exists only for a viewer who could restart
              // this device anyway (device.restart); the server re-checks and
              // answers 403 regardless. Spans, not divs: ConfirmDialog renders
              // its children inside a <p>.
              <span style={{ display: 'block', marginTop: 12 }}>
                <label className="check-row">
                  <input
                    type="checkbox"
                    checked={removeWithRestart}
                    onChange={(e) => setRemoveWithRestart(e.target.checked)}
                  />
                  {' '}{RestartCheckboxLabel}
                </label>
                <span className="field-hint" style={{ display: 'block', marginTop: 4 }}>{RestartFinishesNote}</span>
              </span>
            )}
          </>
        </ConfirmDialog>
      )}

      {removeEverywhere?.accountEmail && (
        <RemoveChromeAccountDialog
          accountEmail={removeEverywhere.accountEmail}
          canRestart={canRestart}
          onClose={() => setRemoveEverywhere(null)}
        />
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
  canManage,
  busy,
  onRemove,
  onRemoveEverywhere,
}: {
  profiles: ChromeProfileRow[]
  selectedProfile: ChromeProfileRow | null
  onSelect: (profileId: string) => void
  canManage: boolean
  busy: boolean
  onRemove: (profile: ChromeProfileRow) => void
  onRemoveEverywhere: (profile: ChromeProfileRow) => void
}) {
  return (
    <div className="card split-aside" style={{ margin: 0 }}>
      <h2 style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        Chrome profiles <span className="badge plain neutral">{profiles.length}</span>
      </h2>
      {profiles.map((p) => {
        // The badge is the server's status verdict, never derived here; the
        // hint under it carries the explanation ("unused 12 days", the broken
        // sign-in wording).
        const badge = statusBadge(p.status, p.lastActiveAt)
        return (
          // The select button and the Remove button are siblings, not nested:
          // a button inside a button is invalid HTML and unreachable by keyboard.
          <div key={p.profileId} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
            <button
              type="button"
              className="list-item"
              style={{ flex: 1, minWidth: 0 }}
              aria-selected={selectedProfile?.profileId === p.profileId}
              onClick={() => onSelect(p.profileId)}
            >
              <div className="list-item-title" style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                <span>
                  {profileTitle(p)}
                  {p.isManaged && <span className="muted" style={{ fontWeight: 400 }}> · managed</span>}
                </span>
                <span className={`badge ${badge.tone}`} title={badge.hint ?? undefined}>{badge.label}</span>
              </div>
              {badge.hint && <div className="list-item-sub">{badge.hint}</div>}
              {p.accountEmail && <div className="list-item-sub">{p.accountEmail}</div>}
              <div className="list-item-sub">
                {profileSubtitle(p)} · {countLabel(p.extensionCount, 'extension')}
              </div>
            </button>
            {canManage && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 4, flexShrink: 0, alignItems: 'stretch' }}>
                <button
                  type="button"
                  // Dormant profiles are the cleanup target, so their Remove is
                  // the suggested (primary) button; it stays available on every
                  // non-Default profile regardless.
                  className={removeSuggested(p.status) ? 'btn-primary btn-sm' : 'btn-sm'}
                  disabled={!canRemove(p.profileKey) || busy}
                  title={removeTitle(p.profileKey)}
                  aria-label={`Remove Chrome profile ${profileTitle(p)}`}
                  onClick={() => onRemove(p)}
                >
                  Remove
                </button>
                {/* The fleet-wide action addresses a Google account, so it only
                    exists where one is signed in (and never on Default). */}
                {canRemoveEverywhere(p) && (
                  <button
                    type="button"
                    className="btn-ghost btn-sm"
                    disabled={busy}
                    title={`Removes the Chrome profile signed in to ${p.accountEmail} from every targeted device`}
                    aria-label={`Remove Chrome profile for ${p.accountEmail} on all devices`}
                    onClick={() => onRemoveEverywhere(p)}
                  >
                    Remove on all devices…
                  </button>
                )}
              </div>
            )}
          </div>
        )
      })}
    </div>
  )
}

function ProfilesTab({
  detail,
  selectedProfile,
  onSelect,
  extensions,
  extensionsError,
  rules,
  canManage,
  ruleBusy,
  onRuleAction,
  profileBusy,
  onRemoveProfile,
  onRemoveEverywhere,
}: {
  detail: ChromeDeviceDetail
  selectedProfile: ChromeProfileRow | null
  onSelect: (profileId: string) => void
  extensions: ChromeExtensionRow[] | null
  extensionsError: string | null
  rules: ChromeExtensionRule[] | null
  canManage: boolean
  ruleBusy: boolean
  onRuleAction: (action: ChromeExtensionRuleAction, target: { extensionId: string; name: string | null }) => void
  profileBusy: boolean
  onRemoveProfile: (profile: ChromeProfileRow) => void
  onRemoveEverywhere: (profile: ChromeProfileRow) => void
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
      <ProfileList
        profiles={detail.profiles}
        selectedProfile={selectedProfile}
        onSelect={onSelect}
        canManage={canManage}
        busy={profileBusy}
        onRemove={onRemoveProfile}
        onRemoveEverywhere={onRemoveEverywhere}
      />
      <div className="card split-main" style={{ margin: 0 }}>
        {selectedProfile && (
          <>
            <div className="card-header">
              <div>
                <h2 style={{ margin: 0 }}>Extensions — {profileTitle(selectedProfile)}</h2>
                <div className="muted">
                  {selectedProfile.accountEmail && <>{selectedProfile.accountEmail} · </>}
                  {profileSubtitle(selectedProfile)} · last active {formatDate(selectedProfile.lastActiveAt)}
                </div>
              </div>
            </div>
            <ExtensionsTable
              extensions={extensions}
              error={extensionsError}
              rules={rules}
              canManage={canManage}
              ruleBusy={ruleBusy}
              onRuleAction={onRuleAction}
            />
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
  rules,
  rulesError,
  canManage,
  ruleBusy,
  onRuleAction,
  onForceInstall,
  onRetryRules,
}: {
  detail: ChromeDeviceDetail
  selectedProfile: ChromeProfileRow | null
  onSelect: (profileId: string) => void
  extensions: ChromeExtensionRow[] | null
  extensionsError: string | null
  rules: ChromeExtensionRule[] | null
  rulesError: string | null
  canManage: boolean
  ruleBusy: boolean
  onRuleAction: (action: ChromeExtensionRuleAction, target: { extensionId: string; name: string | null }) => void
  onForceInstall: (extensionId: string, name: string | null) => Promise<boolean>
  onRetryRules: () => void
}) {
  // The inventory needs Chrome reported and a profile; the managed card below
  // does not. Rules are device-level — a force-install can be queued before the
  // device has ever reported the Chrome section — so the card always renders.
  const inventory = !detail.installation ? (
    <NotReported />
  ) : detail.profiles.length === 0 ? (
    <div className="empty-state">
      <Icon name="software" size={36} strokeWidth={1.25} className="icon" />
      <div className="title">No extensions</div>
      <div>Extensions belong to profiles, and this device has reported none.</div>
    </div>
  ) : (
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
              {profileTitle(p)} — {p.accountEmail ?? p.userAccount ?? p.userSid}
            </option>
          ))}
        </select>
      </div>
      <ExtensionsTable
        extensions={extensions}
        error={extensionsError}
        rules={rules}
        canManage={canManage}
        ruleBusy={ruleBusy}
        onRuleAction={onRuleAction}
      />
    </>
  )

  return (
    <>
      {inventory}
      <ManagedExtensionsCard
        rules={rules}
        rulesError={rulesError}
        canManage={canManage}
        busy={ruleBusy}
        onAction={onRuleAction}
        onForceInstall={onForceInstall}
        onRetry={onRetryRules}
      />
    </>
  )
}

function ExtensionsTable({
  extensions,
  error,
  rules,
  canManage,
  ruleBusy,
  onRuleAction,
}: {
  extensions: ChromeExtensionRow[] | null
  error: string | null
  rules: ChromeExtensionRule[] | null
  canManage: boolean
  ruleBusy: boolean
  onRuleAction: (action: ChromeExtensionRuleAction, target: { extensionId: string; name: string | null }) => void
}) {
  // Starts on what Chrome's own extensions menu shows, so the list agrees with
  // the count beside the profile; the rest is one click away, labelled.
  const [showAll, setShowAll] = useState(false)

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

  const rows = visibleExtensions(extensions, showAll)
  const note = hiddenExtensionsNote(extensions)

  return (
    <>
      {note && (
        <div className="toolbar" style={{ justifyContent: 'space-between', flexWrap: 'wrap' }}>
          <span className="muted">
            {showAll ? 'Showing everything Chrome recorded for this profile.' : `Showing what Chrome’s extensions menu shows. ${note}`}
          </span>
          <button type="button" className="btn-sm" onClick={() => setShowAll((v) => !v)}>
            {showAll ? 'Show active only' : `Show all (${extensions.length})`}
          </button>
        </div>
      )}
      {rows.length === 0 ? (
        <div className="empty-state">
          <Icon name="software" size={36} strokeWidth={1.25} className="icon" />
          <div className="title">No active extensions</div>
          <div>Nothing is enabled in this profile’s extensions menu.</div>
        </div>
      ) : (
    <div className="table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Name</th>
            <th>Version</th>
            <th>Status</th>
            <th>Managed</th>
            <th>Source</th>
            <th>Policy</th>
            {canManage && <th style={{ textAlign: 'right' }}>Actions</th>}
          </tr>
        </thead>
        <tbody>
          {rows.map((e) => {
            const rule = ruleForExtension(rules, e.extensionId)
            return (
            <tr key={e.extensionRowId} className={e.isActive ? undefined : 'muted'}>
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
              <td className={e.isActive ? undefined : 'muted'}>{extensionSourceLabel(e)}</td>
              <td>
                {rule ? (
                  <span className={`badge ${ruleModeTone(rule.mode)}`}>{ruleModeLabel(rule.mode)}</span>
                ) : (
                  <span className="muted">—</span>
                )}
              </td>
              {canManage && (
                <td style={{ textAlign: 'right' }}>
                  <RowActionsMenu
                    label={`Policy actions for ${e.name ?? e.extensionId}`}
                    items={extensionRuleMenuItems(rule?.mode ?? null, e.isComponent)}
                    disabled={ruleBusy}
                    onSelect={(action) => onRuleAction(action, { extensionId: e.extensionId, name: e.name })}
                  />
                </td>
              )}
            </tr>
            )
          })}
        </tbody>
      </table>
    </div>
      )}
    </>
  )
}

/**
 * Every rule on the device — force-installed extensions not yet in any
 * profile's inventory included — with the same actions as the table above.
 * This list, not the inventory, is the source of truth for policy state: the
 * inventory only changes when the device uploads its next snapshot.
 */
function ManagedExtensionsCard({
  rules,
  rulesError,
  canManage,
  busy,
  onAction,
  onForceInstall,
  onRetry,
}: {
  rules: ChromeExtensionRule[] | null
  rulesError: string | null
  canManage: boolean
  busy: boolean
  onAction: (action: ChromeExtensionRuleAction, target: { extensionId: string; name: string | null }) => void
  onForceInstall: (extensionId: string, name: string | null) => Promise<boolean>
  onRetry: () => void
}) {
  return (
    <div className="card" style={{ margin: '16px 0 0' }}>
      <div className="card-header">
        <div>
          <h2 style={{ margin: 0, display: 'flex', alignItems: 'center', gap: 8 }}>
            Managed extensions
            {rules && <span className="badge plain neutral">{rules.length}</span>}
          </h2>
          <div className="muted">
            Machine-wide extension policy for this device. A rule takes effect on the device’s next check-in; the
            extension tables above only change when the device uploads its next inventory, so a fresh rule shows here
            first.
          </div>
        </div>
      </div>

      {rulesError && (
        <div className="warn-banner">
          {rulesError}{' '}
          <button type="button" className="btn-sm" onClick={onRetry}>
            Retry
          </button>
        </div>
      )}

      {!rules && !rulesError && <p className="muted">Loading extension rules…</p>}

      {rules && rules.length === 0 && (
        <div className="empty-state">
          <Icon name="policies" size={36} strokeWidth={1.25} className="icon" />
          <div className="title">No managed extensions</div>
          <div>
            No extension on this device is forced, blocked, allowed or removed by policy.
            {canManage && ' Set a rule from an extension’s Actions menu, or force-install one below.'}
          </div>
        </div>
      )}

      {rules && rules.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Extension</th>
                <th>Policy</th>
                <th>Updated</th>
                {canManage && <th style={{ textAlign: 'right' }}>Actions</th>}
              </tr>
            </thead>
            <tbody>
              {rules.map((r) => (
                <tr key={r.extensionId}>
                  <td>
                    <div>{r.displayName ?? <span className="muted">(unnamed)</span>}</div>
                    <div className="muted" style={{ fontSize: 11.5, fontFamily: 'monospace' }}>{r.extensionId}</div>
                  </td>
                  <td>
                    <span className={`badge ${ruleModeTone(r.mode)}`}>{ruleModeLabel(r.mode)}</span>
                  </td>
                  <td className="muted">{formatDateTime(r.updatedAt)}</td>
                  {canManage && (
                    <td style={{ textAlign: 'right' }}>
                      <RowActionsMenu
                        label={`Policy actions for ${r.displayName ?? r.extensionId}`}
                        items={extensionRuleMenuItems(r.mode, false)}
                        disabled={busy}
                        onSelect={(action) => onAction(action, { extensionId: r.extensionId, name: r.displayName })}
                      />
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {canManage && <ForceInstallForm busy={busy} onSubmit={onForceInstall} />}
    </div>
  )
}

/**
 * Force-install by Chrome Web Store ID. The ID is validated here — 32 letters,
 * a through p — because nothing else can name a Web Store extension, and a typo
 * caught before the request beats a policy the device can never satisfy. The
 * fields are cleared only when the server accepted the rule, so a refusal (an
 * agent too old, say) does not make the operator retype a 32-character ID.
 */
function ForceInstallForm({
  busy,
  onSubmit,
}: {
  busy: boolean
  onSubmit: (extensionId: string, name: string | null) => Promise<boolean>
}) {
  const idFieldId = useId()
  const nameFieldId = useId()
  const errorId = useId()
  const [extensionId, setExtensionId] = useState('')
  const [name, setName] = useState('')
  const [fieldError, setFieldError] = useState<string | null>(null)

  async function submit() {
    const problem = extensionIdError(extensionId)
    setFieldError(problem)
    if (problem) return
    const accepted = await onSubmit(normalizeExtensionId(extensionId), name.trim() ? name.trim() : null)
    if (accepted) {
      setExtensionId('')
      setName('')
    }
  }

  return (
    <div className="action-group">
      <h3>Force-install an extension</h3>
      <div className="group-note">
        Installs it from the Chrome Web Store for every profile on this device and keeps it installed. The ID is the
        32-character ending of the extension’s Web Store URL.
      </div>
      <div className="form-grid">
        <div className="field">
          <label className="field-label" htmlFor={idFieldId}>
            Extension ID
          </label>
          <input
            id={idFieldId}
            value={extensionId}
            placeholder="e.g. cjpalhdlnbpafiamejdnhcphjbkeiagm"
            spellCheck={false}
            disabled={busy}
            aria-invalid={fieldError ? true : undefined}
            aria-describedby={fieldError ? errorId : undefined}
            onChange={(e) => {
              setExtensionId(e.target.value)
              // A message that has appeared clears the moment the input stops
              // deserving it; it never appears mid-typing.
              if (fieldError) setFieldError(extensionIdError(e.target.value))
            }}
          />
          {fieldError && (
            <div className="field-message" id={errorId}>
              {fieldError}
            </div>
          )}
        </div>
        <div className="field">
          <label className="field-label" htmlFor={nameFieldId}>
            Name <span className="muted">(optional)</span>
          </label>
          <input
            id={nameFieldId}
            value={name}
            placeholder="Shown in the list until the device reports it"
            disabled={busy}
            onChange={(e) => setName(e.target.value)}
          />
        </div>
      </div>
      <div className="btn-row">
        <button type="button" className="btn-primary btn-sm" disabled={busy} onClick={() => void submit()}>
          <Icon name="download" size={14} />
          Force-install
        </button>
      </div>
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
