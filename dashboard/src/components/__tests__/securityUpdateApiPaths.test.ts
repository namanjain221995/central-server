import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  getDeviceSecurityUpdates,
  getSecurityUpdateOverview,
  installAllSecurityUpdates,
  installSecurityUpdates,
  scanAllSecurityUpdates,
  scanSecurityUpdates,
} from '../../api/client'

/**
 * The security-update client functions reach the API with exactly one `/api`
 * prefix and the routes the server maps — the same property apiPaths.test.ts and
 * driverUpdateApiPaths.test.ts guard for the rest of the client, for the same
 * reason: a doubled prefix is a 404 that blanks a whole page. The install also
 * has to carry its chosen updateIds as a JSON body, or it would install nothing.
 */

const DEVICE = '01a01bc4-aa58-7bf2-bcfd-9616efcdd614'

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

describe('Security update API paths', () => {
  it.each([
    ['getSecurityUpdateOverview', () => getSecurityUpdateOverview(), 'GET', '/api/admin/v1/security-updates/overview'],
    [
      'getDeviceSecurityUpdates',
      () => getDeviceSecurityUpdates(DEVICE),
      'GET',
      `/api/admin/v1/devices/${DEVICE}/security-updates`,
    ],
    [
      'scanSecurityUpdates',
      () => scanSecurityUpdates(DEVICE),
      'POST',
      `/api/admin/v1/devices/${DEVICE}/security-updates/scan`,
    ],
    [
      'installSecurityUpdates',
      () => installSecurityUpdates(DEVICE, ['u1']),
      'POST',
      `/api/admin/v1/devices/${DEVICE}/security-updates/install`,
    ],
    ['scanAllSecurityUpdates', () => scanAllSecurityUpdates(), 'POST', '/api/admin/v1/security-updates/scan-all'],
    [
      'installAllSecurityUpdates',
      () => installAllSecurityUpdates(),
      'POST',
      '/api/admin/v1/security-updates/install-all',
    ],
  ])('%s requests the mapped route with one /api prefix', async (_name, call, method, expected) => {
    const { urls, inits } = captureRequests()

    await call()

    expect(urls).toEqual([expected])
    expect(urls[0].includes('/api/api')).toBe(false)
    expect(urls[0].split('/api/').length - 1).toBe(1)
    expect(inits[0]?.method ?? 'GET').toBe(method)
  })

  it('sends the chosen updateIds as a JSON body on install', async () => {
    const { inits } = captureRequests()

    await installSecurityUpdates(DEVICE, ['u1', 'u2'])

    const headers = inits[0]?.headers as Record<string, string>
    // The shared request() helper labels a string body as JSON; without it the
    // API answers 415 before the endpoint runs.
    expect(headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(inits[0]?.body as string)).toEqual({ updateIds: ['u1', 'u2'] })
  })
})
