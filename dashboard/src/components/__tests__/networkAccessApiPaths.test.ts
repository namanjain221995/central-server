import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  getNetworkAccessPolicy,
  updateNetworkAccessPolicy,
  type NetworkAccessPolicy,
} from '../../api/client'

/**
 * The network-access client functions reach the API with exactly one `/api`
 * prefix and the routes the server maps — the same property accountApiPaths and
 * apiPaths guard for the rest of the client. Both functions share one route;
 * only the verb separates reading the policy from replacing it.
 */

function captureRequests(body: unknown = {}): { urls: string[]; inits: (RequestInit | undefined)[] } {
  const urls: string[] = []
  const inits: (RequestInit | undefined)[] = []

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      urls.push(typeof input === 'string' ? input : input.toString())
      inits.push(init)
      return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }),
  )

  return { urls, inits }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('Network access API paths', () => {
  it.each([
    [
      'getNetworkAccessPolicy',
      () => getNetworkAccessPolicy(),
      'GET',
    ],
    [
      'updateNetworkAccessPolicy',
      () => updateNetworkAccessPolicy({ enabled: true, allowedCidrs: ['203.0.113.0/24'] }),
      'PUT',
    ],
  ])('%s requests the mapped route with one /api prefix', async (_name, call, method) => {
    const { urls, inits } = captureRequests()

    await call()

    expect(urls).toEqual(['/api/admin/v1/settings/network'])
    expect(urls[0].includes('/api/api')).toBe(false)
    expect(urls[0].split('/api/').length - 1).toBe(1)
    expect(inits[0]?.method ?? 'GET').toBe(method)
  })

  it('sends the replacement policy as JSON with exactly the two fields', async () => {
    const { inits } = captureRequests()

    await updateNetworkAccessPolicy({ enabled: false, allowedCidrs: ['198.51.100.7', '2001:db8::/32'] })

    const headers = inits[0]?.headers as Record<string, string>
    expect(headers['Content-Type']).toBe('application/json')
    // The PUT replaces the whole policy: enabled and the list, nothing else —
    // in particular no client-side copy of callerIp or the audit fields.
    expect(JSON.parse(inits[0]?.body as string)).toEqual({
      enabled: false,
      allowedCidrs: ['198.51.100.7', '2001:db8::/32'],
    })
  })

  it('returns the server policy as-is, including the audit fields and callerIp', async () => {
    const policy: NetworkAccessPolicy = {
      enabled: true,
      allowedCidrs: ['203.0.113.0/24', '2001:db8::/32'],
      updatedAtUtc: '2026-10-24T09:30:00Z',
      updatedBy: 'admin@example.com',
      callerIp: '203.0.113.7',
    }
    captureRequests(policy)

    await expect(getNetworkAccessPolicy()).resolves.toEqual(policy)
  })
})
