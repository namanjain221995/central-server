import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'
import {
  getCurrentUser,
  login as apiLogin,
  logout as apiLogout,
  sessionExpiredEvent,
  verifyMfa as apiVerifyMfa,
  type CurrentUser,
  type MfaChallenge,
} from '../api/client'

interface AuthState {
  /** null while the initial session probe is in flight. */
  initializing: boolean
  user: CurrentUser | null
  /**
   * Resolves to a challenge when the password was right but a code is owed, and
   * to null when the session is established.
   *
   * The challenge is returned rather than held here: it is not an authentication
   * state the rest of the application should be able to observe, only a value the
   * sign-in screen carries between its two steps.
   */
  login: (email: string, password: string) => Promise<MfaChallenge | null>
  /** Completes a sign-in that was waiting on a second factor. */
  verifyMfa: (challengeToken: string, code: string) => Promise<void>
  logout: () => Promise<void>
  /**
   * Re-reads the current identity from the server.
   *
   * Needed after anything that changes what the gates in App look at — enrolling
   * a second factor flips `mfaEnrolmentRequired`, and nothing else would tell the
   * console to stop rendering the enrolment screen.
   */
  refresh: () => Promise<void>
  /** Convenience for hiding UI the user cannot use. NOT security - the server enforces. */
  hasPermission: (permission: string) => boolean
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null)
  const [initializing, setInitializing] = useState(true)

  // Probe the session cookie on first load so a refreshed tab stays signed in.
  useEffect(() => {
    let cancelled = false

    getCurrentUser()
      .then((current) => {
        if (!cancelled) setUser(current)
      })
      .catch(() => {
        /* no session - the login page will render */
      })
      .finally(() => {
        if (!cancelled) setInitializing(false)
      })

    return () => {
      cancelled = true
    }
  }, [])

  // Any 401 from any API call drops the local user state.
  useEffect(() => {
    const onExpired = () => setUser(null)
    sessionExpiredEvent.addEventListener('expired', onExpired)
    return () => sessionExpiredEvent.removeEventListener('expired', onExpired)
  }, [])

  const login = useCallback(async (email: string, password: string) => {
    const result = await apiLogin(email, password)

    if (result.kind === 'mfa-required') {
      // Deliberately no setUser: nothing is authenticated yet. Setting a user
      // here would render the console behind the code prompt and, worse, would
      // make a password-only sign-in look complete to every other component.
      return result.challenge
    }

    setUser(result.user)
    return null
  }, [])

  const verifyMfa = useCallback(async (challengeToken: string, code: string) => {
    setUser(await apiVerifyMfa(challengeToken, code))
  }, [])

  const logout = useCallback(async () => {
    await apiLogout()
    setUser(null)
  }, [])

  const refresh = useCallback(async () => {
    setUser(await getCurrentUser())
  }, [])

  const hasPermission = useCallback(
    (permission: string) => user?.permissions.includes(permission) ?? false,
    [user],
  )

  const value = useMemo(
    () => ({ initializing, user, login, verifyMfa, logout, refresh, hasPermission }),
    [initializing, user, login, verifyMfa, logout, refresh, hasPermission],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used inside AuthProvider')
  }
  return context
}
