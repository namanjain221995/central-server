import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  getDeviceDriverUpdates,
  getDriverUpdateOverview,
  installAllDriverUpdates,
  installDriverUpdates,
  scanAllDriverUpdates,
  scanDriverUpdates,
} from '../../api/client'

/**
 * The driver-update client functions reach the API with exactly one `/api`
 * prefix and the routes the server maps — the same property apiPaths.test.ts and
 * announcementApiPaths.test.ts guard for the rest of the client, for the same
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

describe('Driver update API paths', () => {
  it.each([
    ['getDriverUpdateOverview', () => getDriverUpdateOverview(), 'GET', '/api/admin/v1/driver-updates/overview'],
    [
      'getDeviceDriverUpdates',
      () => getDeviceDriverUpdates(DEVICE),
      'GET',
      `/api/admin/v1/devices/${DEVICE}/driver-updates`,
    ],
    [
      'scanDriverUpdates',
      () => scanDriverUpdates(DEVICE),
      'POST',
      `/api/admin/v1/devices/${DEVICE}/driver-updates/scan`,
    ],
    [
      'installDriverUpdates',
      () => installDriverUpdates(DEVICE, ['u1']),
      'POST',
      `/api/admin/v1/devices/${DEVICE}/driver-updates/install`,
    ],
    ['scanAllDriverUpdates', () => scanAllDriverUpdates(), 'POST', '/api/admin/v1/driver-updates/scan-all'],
    ['installAllDriverUpdates', () => installAllDriverUpdates(), 'POST', '/api/admin/v1/driver-updates/install-all'],
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

    await installDriverUpdates(DEVICE, ['u1', 'u2'])

    const headers = inits[0]?.headers as Record<string, string>
    // The shared request() helper labels a string body as JSON; without it the
    // API answers 415 before the endpoint runs.
    expect(headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(inits[0]?.body as string)).toEqual({ updateIds: ['u1', 'u2'] })
  })
})
