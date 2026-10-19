/**
 * Light, dark or follow the operating system — the administrator's choice, and
 * how it reaches the page.
 *
 * The choice belongs to the account: the server stores it and returns it with the
 * signed-in identity, so it follows the administrator to any browser. The browser
 * keeps a copy only to paint the right theme before that answer arrives (and on
 * the sign-in page, where there is no account yet). The copy is never sent back:
 * only an explicit choice on the Appearance tab writes to the server, so one
 * person's cached theme can never become another person's preference.
 *
 * Everything colour-related happens in CSS. This module only decides which of
 * two values goes on <html data-theme>; index.css maps each to a token set.
 */

export type ThemePreference = 'light' | 'dark' | 'system'
export type ResolvedTheme = 'light' | 'dark'

export const THEME_PREFERENCES: readonly ThemePreference[] = ['light', 'dark', 'system']

/** The browser copy. public/theme-init.js reads the same key before React loads. */
export const THEME_STORAGE_KEY = 'epp.theme'

/** Matches the dark canvas the console has always opened on. */
export const DEFAULT_PREFERENCE: ThemePreference = 'system'

export const SYSTEM_DARK_QUERY = '(prefers-color-scheme: dark)'

/** The canvas colour of each theme, for the browser's own chrome (<meta name="theme-color">). */
export const THEME_CANVAS: Record<ResolvedTheme, string> = {
  dark: '#0b1220',
  light: '#f3f5f9',
}

export function isThemePreference(value: unknown): value is ThemePreference {
  return value === 'light' || value === 'dark' || value === 'system'
}

/**
 * What to show once an identity arrives: the account's own choice when it has a
 * valid one, otherwise whatever is showing now (an older server that does not
 * send the field, or no one signed in).
 */
export function preferenceAfterIdentity(current: ThemePreference, account: unknown): ThemePreference {
  return isThemePreference(account) ? account : current
}

/** System follows the operating system; the other two are what they say. */
export function resolveTheme(preference: ThemePreference, systemPrefersDark: boolean): ResolvedTheme {
  if (preference === 'system') return systemPrefersDark ? 'dark' : 'light'
  return preference
}

/**
 * Browser storage can be missing or throw (private windows, blocked site data).
 * The theme must still work there, from the account preference alone.
 */
function storage(): Storage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage
  } catch {
    return null
  }
}

export function readCachedPreference(store: Storage | null = storage()): ThemePreference {
  try {
    const value = store?.getItem(THEME_STORAGE_KEY)
    return isThemePreference(value) ? value : DEFAULT_PREFERENCE
  } catch {
    return DEFAULT_PREFERENCE
  }
}

export function cachePreference(preference: ThemePreference, store: Storage | null = storage()): void {
  try {
    store?.setItem(THEME_STORAGE_KEY, preference)
  } catch {
    /* No storage: the account preference still applies after sign-in. */
  }
}

export function systemPrefersDark(): boolean {
  try {
    return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      ? window.matchMedia(SYSTEM_DARK_QUERY).matches
      : true
  } catch {
    return true
  }
}

/** Class that suspends CSS transitions while the theme changes (see index.css). */
export const THEME_SWITCHING_CLASS = 'theme-switching'

/** Puts the resolved theme on the document. Idempotent; safe to call on every render. */
export function applyTheme(theme: ResolvedTheme, doc: Document = document): void {
  const root = doc.documentElement
  if (root.dataset.theme !== theme) {
    // Controls fade their colours on hover (160 ms). Without this, switching theme
    // would animate every button and link from the old palette to the new one, and
    // the page would briefly show a mix of the two.
    root.classList?.add(THEME_SWITCHING_CLASS)
    root.dataset.theme = theme
    const settle = () => root.classList?.remove(THEME_SWITCHING_CLASS)
    if (typeof requestAnimationFrame === 'function') {
      requestAnimationFrame(() => requestAnimationFrame(settle))
    } else {
      settle()
    }
  }
  root.style.colorScheme = theme

  const meta = doc.querySelector('meta[name="theme-color"]')
  if (meta) meta.setAttribute('content', THEME_CANVAS[theme])
}
