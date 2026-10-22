import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  deleteChromeExtensionRule,
  getChromeExtensionRules,
  setChromeExtensionRule,
} from '../../api/client'

/**
 * The Chrome extension-rule client functions reach the API with exactly one
 * `/api` prefix and the routes the server maps — the same property
 * chromeApiPaths.test.ts and liveChatApiPaths.test.ts guard for the rest of the
 * client, for the same reason: a doubled prefix is a 404 that blanks a whole
 * page. Setting a rule also has to carry `{mode, displayName}` as a JSON
 * payload, or the device would be told to enforce nothing.
 */

const DEVICE = '01a01bc4-aa58-7bf2-bcfd-9616efcdd614'
const EXTENSION = 'cjpalhdlnbpafiamejdnhcphjbkeiagm'

function captureRequests(): { urls: string[]; inits: (RequestInit | undefined)[] } {
  const urls: string[] = []
  const inits: (RequestInit | undefined)[] = []

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      urls.push(typeof input === 'string' ? input : input.toString())
      inits.push(init)
      return new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }),
  )

  return { urls, inits }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('Chrome extension rule API paths', () => {
  it.each([
    [
      'getChromeExtensionRules',
      () => getChromeExtensionRules(DEVICE),
      'GET',
      `/api/admin/v1/devices/${DEVICE}/chrome/extension-rules`,
    ],
    [
      'setChromeExtensionRule',
      () => setChromeExtensionRule(DEVICE, EXTENSION, 'Blocked'),
      'PUT',
      `/api/admin/v1/devices/${DEVICE}/chrome/extension-rules/${EXTENSION}`,
    ],
    [
      'deleteChromeExtensionRule',
      () => deleteChromeExtensionRule(DEVICE, EXTENSION),
      'DELETE',
      `/api/admin/v1/devices/${DEVICE}/chrome/extension-rules/${EXTENSION}`,
    ],
  ])('%s requests the mapped route with one /api prefix', async (_name, call, method, expected) => {
    const { urls, inits } = captureRequests()

    await call()

    expect(urls).toEqual([expected])
    expect(urls[0].includes('/api/api')).toBe(false)
    expect(urls[0].split('/api/').length - 1).toBe(1)
    expect(inits[0]?.method ?? 'GET').toBe(method)
  })

  it('sends the rule as a {mode, displayName} JSON payload', async () => {
    const { inits } = captureRequests()

    await setChromeExtensionRule(DEVICE, EXTENSION, 'ForceInstalled', 'uBlock Origin')

    const headers = inits[0]?.headers as Record<string, string>
    // The shared request() helper labels a string body as JSON; without it the
    // API answers 415 before the endpoint runs.
    expect(headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(inits[0]?.body as string)).toEqual({ mode: 'ForceInstalled', displayName: 'uBlock Origin' })
  })

  it('omits displayName from the payload when none is given', async () => {
    const { inits } = captureRequests()

    await setChromeExtensionRule(DEVICE, EXTENSION, 'Allowed')

    expect(JSON.parse(inits[0]?.body as string)).toEqual({ mode: 'Allowed' })
  })

  it('sends no body on delete', async () => {
    const { inits } = captureRequests()

    await deleteChromeExtensionRule(DEVICE, EXTENSION)

    expect(inits[0]?.body).toBeUndefined()
  })

  it('encodes ids so a malformed one cannot escape its path segment', async () => {
    const { urls } = captureRequests()

    await setChromeExtensionRule('a/b', 'c?d', 'Blocked')

    expect(urls).toEqual(['/api/admin/v1/devices/a%2Fb/chrome/extension-rules/c%3Fd'])
  })
})
