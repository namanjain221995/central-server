import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'
import { useAuth } from '../auth/AuthContext'
import { getWorkTimeSettings, type WorkTimeSettings } from '../api/client'

/**
 * The work-time feature's live on/off state, shared so the navigation and the
 * pages agree without each one fetching it.
 *
 * The feature is opt-in: off by default. When it is off the Time Management
 * navigation entry and page are hidden — this is what makes it "disappear" from
 * the console — and the server records nothing. Turning it on in Settings calls
 * {@link WorkTimeState.reload}, which re-reads the flag so the entry reappears
 * without a page refresh.
 *
 * Only fetched for an administrator who may view it (activity.view): a help-desk
 * operator who cannot see work time never triggers a predictable 403, and
 * `enabled` is false for them regardless, so the entry stays hidden.
 */
interface WorkTimeState {
  loading: boolean
  settings: WorkTimeSettings | null
  /** The feature is on AND the signed-in administrator may view it. */
  enabled: boolean
  canView: boolean
  canConfigure: boolean
  /** Re-read the settings from the server (after a save, so the nav updates). */
  reload: () => Promise<void>
}

const WorkTimeContext = createContext<WorkTimeState | null>(null)

export function WorkTimeProvider({ children }: { children: ReactNode }) {
  const { hasPermission } = useAuth()
  const canView = hasPermission('activity.view')
  const canConfigure = hasPermission('activity.configure')

  const [settings, setSettings] = useState<WorkTimeSettings | null>(null)
  const [loading, setLoading] = useState(canView)

  const reload = useCallback(async () => {
    if (!canView) {
      setSettings(null)
      setLoading(false)
      return
    }
    setLoading(true)
    try {
      setSettings(await getWorkTimeSettings())
    } catch {
      // Silent, like the enrollment badge: a failed read of a feature flag must
      // not put an error on every page. The feature stays hidden until a read
      // succeeds, which is the safe default.
      setSettings(null)
    } finally {
      setLoading(false)
    }
  }, [canView])

  useEffect(() => {
    void reload()
  }, [reload])

  const value = useMemo<WorkTimeState>(
    () => ({
      loading,
      settings,
      enabled: canView && !!settings?.enabled,
      canView,
      canConfigure,
      reload,
    }),
    [loading, settings, canView, canConfigure, reload],
  )

  return <WorkTimeContext.Provider value={value}>{children}</WorkTimeContext.Provider>
}

export function useWorkTime(): WorkTimeState {
  const context = useContext(WorkTimeContext)
  if (!context) {
    throw new Error('useWorkTime must be used inside WorkTimeProvider')
  }
  return context
}
