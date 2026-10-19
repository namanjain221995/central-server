import { createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useState, type ReactNode } from 'react'
import { saveThemePreference } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import {
  SYSTEM_DARK_QUERY,
  applyTheme,
  cachePreference,
  isThemePreference,
  preferenceAfterIdentity,
  readCachedPreference,
  resolveTheme,
  systemPrefersDark,
  type ResolvedTheme,
  type ThemePreference,
} from './theme'

interface ThemeState {
  /** What the administrator chose: Light, Dark or System. */
  preference: ThemePreference
  /** What is on screen now. System resolves to one of the two. */
  resolved: ResolvedTheme
  /**
   * Applies the choice at once and saves it to the signed-in account. Resolves
   * when the server has it; on failure the previous choice is restored and the
   * promise rejects, so the caller can say it was not saved.
   */
  setPreference: (next: ThemePreference) => Promise<void>
}

const ThemeContext = createContext<ThemeState | null>(null)

/**
 * The one place the theme is decided. Must sit inside AuthProvider: the account
 * preference comes with the signed-in identity.
 *
 * Order of authority, lowest first: System (nothing known yet), this browser's
 * copy (painted before React by public/theme-init.js), then the account
 * preference once an identity loads. Signing in as a different administrator
 * replaces the browser copy with theirs, and nothing here ever writes the copy
 * back to an account.
 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth()
  const [preference, setPreferenceState] = useState<ThemePreference>(() => readCachedPreference())
  const [systemDark, setSystemDark] = useState<boolean>(() => systemPrefersDark())

  // Follow the operating system while System is chosen: the listener is cheap,
  // so it stays attached and resolveTheme decides whether it matters.
  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return
    const query = window.matchMedia(SYSTEM_DARK_QUERY)
    const onChange = (event: MediaQueryListEvent) => setSystemDark(event.matches)
    query.addEventListener('change', onChange)
    return () => query.removeEventListener('change', onChange)
  }, [])

  // The account preference wins as soon as an identity arrives: on first load,
  // after sign-in, and after any refresh of the identity.
  const accountPreference = user?.themePreference
  useEffect(() => {
    if (!isThemePreference(accountPreference)) return
    setPreferenceState((current) => preferenceAfterIdentity(current, accountPreference))
    cachePreference(accountPreference)
  }, [user?.userId, accountPreference])

  const resolved = resolveTheme(preference, systemDark)

  // Layout effect, so the attribute changes in the same frame as the render
  // that chose it, never one paint later.
  useLayoutEffect(() => {
    applyTheme(resolved)
  }, [resolved])

  const setPreference = useCallback(
    async (next: ThemePreference) => {
      const previous = preference
      setPreferenceState(next)
      cachePreference(next)
      try {
        await saveThemePreference(next)
      } catch (error) {
        setPreferenceState(previous)
        cachePreference(previous)
        throw error
      }
    },
    [preference],
  )

  const value = useMemo(() => ({ preference, resolved, setPreference }), [preference, resolved, setPreference])

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>
}

export function useTheme(): ThemeState {
  const context = useContext(ThemeContext)
  if (!context) throw new Error('useTheme must be used inside ThemeProvider.')
  return context
}
