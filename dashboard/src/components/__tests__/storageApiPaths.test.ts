import { afterEach, describe, expect, it, vi } from 'vitest'
import { getFleetStorageSummary } from '../../api/client'

/**
 * The fleet storage client function reaches the API with exactly one `/api` prefix
 * and the route the server maps, for the same reason the other apiPaths tests guard
 * it: a doubled prefix is a 404 that blanks the whole page.
 */
afterEach(() => {
  vi.unstubAllGlobals()
})

describe('Fleet storage API path', () => {
  it('requests the mapped route with one /api prefix as a GET', async () => {
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

    await getFleetStorageSummary()

    expect(urls).toEqual(['/api/admin/v1/storage/fleet-summary'])
    expect(urls[0].includes('/api/api')).toBe(false)
    expect(urls[0].split('/api/').length - 1).toBe(1)
    expect(inits[0]?.method ?? 'GET').toBe('GET')
  })
})
