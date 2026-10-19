import { describe, expect, it } from 'vitest'
import rawCss from '../../index.css?raw'
import initScript from '../../../public/theme-init.js?raw'
import {
  THEME_CANVAS,
  THEME_PREFERENCES,
  THEME_STORAGE_KEY,
  THEME_SWITCHING_CLASS,
  applyTheme,
  cachePreference,
  isThemePreference,
  preferenceAfterIdentity,
  readCachedPreference,
  resolveTheme,
  type ResolvedTheme,
  type ThemePreference,
} from '../../theme/theme'

/** A Storage stand-in. `broken` behaves like a private window that refuses storage. */
function memoryStorage(initial: Record<string, string> = {}, broken = false): Storage {
  const data = new Map(Object.entries(initial))
  const fail = () => {
    if (broken) throw new Error('SecurityError: storage is disabled')
  }
  return {
    get length() {
      return data.size
    },
    clear: () => data.clear(),
    key: (i: number) => [...data.keys()][i] ?? null,
    getItem: (k: string) => {
      fail()
      return data.get(k) ?? null
    },
    setItem: (k: string, v: string) => {
      fail()
      data.set(k, v)
    },
    removeItem: (k: string) => void data.delete(k),
  }
}

/** Just enough of a document for applyTheme and theme-init.js. */
function fakeDocument() {
  const attributes = new Map<string, string>()
  const meta = { content: THEME_CANVAS.dark, setAttribute: (_: string, v: string) => (meta.content = v) }
  const root = {
    dataset: {} as Record<string, string>,
    style: { colorScheme: '' },
    setAttribute: (name: string, value: string) => {
      attributes.set(name, value)
      if (name === 'data-theme') root.dataset.theme = value
    },
  }
  return {
    documentElement: root,
    querySelector: (selector: string) => (selector === 'meta[name="theme-color"]' ? meta : null),
    meta,
    theme: () => root.dataset.theme,
  }
}

/** Runs public/theme-init.js as the browser would, before React, and returns what it painted. */
function runInitScript(stored: string | null, systemDark: boolean, storageBroken = false) {
  const doc = fakeDocument()
  const window = {
    localStorage: memoryStorage(stored === null ? {} : { [THEME_STORAGE_KEY]: stored }, storageBroken),
    matchMedia: (query: string) => ({ matches: query === '(prefers-color-scheme: dark)' ? systemDark : !systemDark }),
  }
  new Function('window', 'document', initScript)(window, doc)
  return { theme: doc.theme(), colorScheme: doc.documentElement.style.colorScheme, metaColour: doc.meta.content }
}

describe('resolving a choice to what is on screen', () => {
  it('shows light and dark as chosen, whatever the system says', () => {
    for (const systemDark of [true, false]) {
      expect(resolveTheme('light', systemDark)).toBe('light')
      expect(resolveTheme('dark', systemDark)).toBe('dark')
    }
  })

  it('follows the system while System is chosen, including when the system changes', () => {
    expect(resolveTheme('system', true)).toBe('dark')
    expect(resolveTheme('system', false)).toBe('light')
  })

  it('accepts only the three values', () => {
    expect(THEME_PREFERENCES.every(isThemePreference)).toBe(true)
    for (const value of ['Light', 'DARK', 'blue', '', null, undefined, 1]) {
      expect(isThemePreference(value)).toBe(false)
    }
  })
})

describe('the browser copy, for reloads and the sign-in page', () => {
  it('survives a reload', () => {
    const store = memoryStorage()
    cachePreference('light', store)
    expect(readCachedPreference(store)).toBe('light')
  })

  it('falls back to System when nothing, or nonsense, is stored', () => {
    expect(readCachedPreference(memoryStorage())).toBe('system')
    expect(readCachedPreference(memoryStorage({ [THEME_STORAGE_KEY]: 'purple' }))).toBe('system')
  })

  it('never throws when the browser refuses storage', () => {
    const store = memoryStorage({}, true)
    expect(() => cachePreference('dark', store)).not.toThrow()
    expect(readCachedPreference(store)).toBe('system')
  })
})

describe('signing in', () => {
  it("replaces what this browser showed with the account's own choice", () => {
    expect(preferenceAfterIdentity('dark', 'light')).toBe('light')
    expect(preferenceAfterIdentity('light', 'system')).toBe('system')
  })

  it('keeps what is showing when the server sends no valid preference', () => {
    expect(preferenceAfterIdentity('dark', undefined)).toBe('dark')
    expect(preferenceAfterIdentity('light', 'neon')).toBe('light')
  })

  it("gives the second administrator on a shared browser their own theme, not the first one's", () => {
    const store = memoryStorage()
    // Administrator A signs in with Light; the browser copy follows their account.
    let showing = preferenceAfterIdentity(readCachedPreference(store), 'light')
    cachePreference(showing, store)
    expect(showing).toBe('light')

    // A signs out, B signs in on the same browser: the sign-in page still shows A's
    // last theme (it is only a copy), and B's account preference then replaces it.
    showing = preferenceAfterIdentity(readCachedPreference(store), 'dark')
    cachePreference(showing, store)
    expect(showing).toBe('dark')
    expect(readCachedPreference(store)).toBe('dark')
  })
})

describe('putting the theme on the page', () => {
  it('sets the attribute the stylesheet keys on, the native colour scheme and the browser chrome colour', () => {
    for (const theme of ['light', 'dark'] as ResolvedTheme[]) {
      const doc = fakeDocument()
      applyTheme(theme, doc as unknown as Document)
      expect(doc.theme()).toBe(theme)
      expect(doc.documentElement.style.colorScheme).toBe(theme)
      expect(doc.meta.content).toBe(THEME_CANVAS[theme])
    }
  })
})

describe('the first paint (public/theme-init.js)', () => {
  const cases: [string | null, boolean, ResolvedTheme][] = [
    ['light', true, 'light'],
    ['light', false, 'light'],
    ['dark', true, 'dark'],
    ['dark', false, 'dark'],
    ['system', true, 'dark'],
    ['system', false, 'light'],
    [null, true, 'dark'],
    [null, false, 'light'],
    ['nonsense', false, 'light'],
  ]

  it.each(cases)('stored %s with a dark system %s paints %s before the app loads', (stored, systemDark, expected) => {
    const painted = runInitScript(stored, systemDark)
    expect(painted.theme).toBe(expected)
    expect(painted.colorScheme).toBe(expected)
    expect(painted.metaColour).toBe(THEME_CANVAS[expected])
  })

  it('agrees with the app for every stored value and system setting, so the app never repaints on load', () => {
    for (const stored of THEME_PREFERENCES as readonly ThemePreference[]) {
      for (const systemDark of [true, false]) {
        expect(runInitScript(stored, systemDark).theme).toBe(resolveTheme(stored, systemDark))
      }
    }
  })

  it('still paints a theme when storage is refused', () => {
    expect(runInitScript('light', true, true).theme).toBe('dark')
  })

  it('reads the same storage key and canvas colours as the app', () => {
    expect(initScript).toContain(`'${THEME_STORAGE_KEY}'`)
    expect(initScript).toContain(THEME_CANVAS.dark)
    expect(initScript).toContain(THEME_CANVAS.light)
  })
})

describe('the stylesheet', () => {
  // Line endings depend on the checkout (CRLF on Windows); compare on LF.
  const css = rawCss.replace(/\r\n/g, '\n')

  function tokensIn(selectorStart: string): Map<string, string> {
    const start = css.indexOf(selectorStart)
    expect(start).toBeGreaterThanOrEqual(0)
    const block = css.slice(css.indexOf('{', start) + 1, css.indexOf('\n}', start))
    return new Map([...block.matchAll(/(--color-[a-z0-9-]+)\s*:\s*([^;]+);/g)].map((m) => [m[1], m[2].trim()]))
  }

  const dark = tokensIn(':root,\n.theme-scope-dark {')
  const light = tokensIn(":root[data-theme='light'],\n.theme-scope-light {")

  // Shared on purpose: the broadcast orange already reads on white and on navy,
  // and white text on a solid primary is white in both themes.
  const sharedByDesign = new Set([
    '--color-accent-solid',
    '--color-accent-solid-hover',
    '--color-accent-active',
    '--color-primary-fg',
    '--color-primary-solid',
  ])

  it('gives every colour token a light value, so no component is left dark in the light theme', () => {
    const missing = [...dark.keys()].filter((token) => !light.has(token) && !sharedByDesign.has(token))
    expect(missing).toEqual([])
  })

  it('defines nothing in the light theme that the dark theme lacks', () => {
    expect([...light.keys()].filter((token) => !dark.has(token))).toEqual([])
  })

  it('keeps colours out of component rules: only the token blocks and two deliberate exceptions hold literals', () => {
    const afterTokens = css.slice(css.indexOf('\n}', css.indexOf(":root[data-theme='light']")) + 2)
    const literals = [...afterTokens.matchAll(/#[0-9a-fA-F]{3,8}\b|rgba?\([^)]*\)/g)].map((m) => m[0])
    // The QR code's white quiet zone (scanners need it in either theme) and a
    // faint neutral shadow that works on both.
    expect(literals.sort()).toEqual(['#ffffff', 'rgb(0 0 0 / 8%)'])
  })
})

describe('switching themes', () => {
  it('suspends transitions for the switch and clears the suspension afterwards', () => {
    const added: string[] = []
    const removed: string[] = []
    const doc = fakeDocument() as unknown as { documentElement: { classList: unknown; dataset: Record<string, string> } }
    doc.documentElement.classList = { add: (c: string) => added.push(c), remove: (c: string) => removed.push(c) }

    applyTheme('light', doc as unknown as Document)

    expect(added).toEqual([THEME_SWITCHING_CLASS])
    expect(removed).toEqual([THEME_SWITCHING_CLASS])
    expect(doc.documentElement.dataset.theme).toBe('light')
  })

  it('does nothing extra when the theme is already showing', () => {
    const added: string[] = []
    const doc = fakeDocument() as unknown as { documentElement: { classList: unknown; dataset: Record<string, string> } }
    doc.documentElement.dataset.theme = 'dark'
    doc.documentElement.classList = { add: (c: string) => added.push(c), remove: () => {} }

    applyTheme('dark', doc as unknown as Document)

    expect(added).toEqual([])
  })
})
