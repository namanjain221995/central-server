import { afterEach, describe, expect, it, vi } from 'vitest'
import { startFileCollection } from '../../api/client'

/**
 * The start-collection client reaches the mapped route with exactly one `/api`
 * prefix, as a POST carrying the step-up and selection — the same path guard the
 * other apiPaths tests hold for the rest of the client.
 */
afterEach(() => {
  vi.unstubAllGlobals()
})

describe('File collection API path', () => {
  it('POSTs to the device-scoped file-collections route with one /api prefix', async () => {
    const calls: { url: string; init?: RequestInit }[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        calls.push({ url: typeof input === 'string' ? input : input.toString(), init })
        return new Response(JSON.stringify({ transferId: 't1' }), {
          status: 200,
          headers: { 'Content-Type': 'application/json' },
        })
      }),
    )

    const result = await startFileCollection('dev-123', {
      recipientPublicKey: 'KEY',
      userSid: 'S-1-5-21-1-2-3-1001',
      rootKeys: ['Desktop', 'Downloads'],
      currentPassword: 'pw',
      justification: 'offboarding recovery',
    })

    expect(result.transferId).toBe('t1')
    expect(calls).toHaveLength(1)
    expect(calls[0].url).toBe('/api/admin/v1/devices/dev-123/file-collections')
    expect(calls[0].url.includes('/api/api')).toBe(false)
    expect(calls[0].init?.method).toBe('POST')
    const body = JSON.parse(String(calls[0].init?.body))
    expect(body.rootKeys).toEqual(['Desktop', 'Downloads'])
    expect(body.userSid).toBe('S-1-5-21-1-2-3-1001')
  })
})
