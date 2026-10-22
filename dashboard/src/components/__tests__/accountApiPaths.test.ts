import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  bulkCreateLocalUser,
  bulkDeleteLocalUser,
  bulkDisableLocalUser,
  bulkEnableLocalUser,
  bulkResetLocalPassword,
  getFleetAccountSummary,
} from '../../api/client'

/**
 * The bulk local-account client functions reach the API with exactly one `/api`
 * prefix and the routes the server maps — the same property apiPaths.test.ts and
 * announcementApiPaths.test.ts guard for the rest of the client, for the same
 * reason: a doubled prefix is a 404 that blanks a whole page.
 */

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

describe('Bulk local-account API paths', () => {
  it.each([
    [
      'bulkCreateLocalUser',
      () => bulkCreateLocalUser({
        username: 'itadmin',
        password: 'hunter2hunter',
        enabled: true,
        mustChangePasswordAtNextLogon: true,
        accountType: 'StandardUser',
        allDevices: true,
      }),
      'POST',
      '/api/admin/v1/local-users/bulk',
    ],
    [
      'bulkResetLocalPassword',
      () => bulkResetLocalPassword({ username: 'itadmin', password: 'hunter2hunter', allDevices: true }),
      'POST',
      '/api/admin/v1/local-users/bulk/reset-password',
    ],
    [
      'bulkDisableLocalUser',
      () => bulkDisableLocalUser({ username: 'itadmin', allDevices: true }),
      'POST',
      '/api/admin/v1/local-users/bulk/disable',
    ],
    [
      'bulkEnableLocalUser',
      () => bulkEnableLocalUser({ username: 'itadmin', allDevices: true }),
      'POST',
      '/api/admin/v1/local-users/bulk/enable',
    ],
    [
      'bulkDeleteLocalUser',
      () => bulkDeleteLocalUser({ username: 'itadmin', allDevices: true }),
      'POST',
      '/api/admin/v1/local-users/bulk/delete',
    ],
    [
      'getFleetAccountSummary',
      () => getFleetAccountSummary(),
      'GET',
      '/api/admin/v1/local-users/fleet-summary',
    ],
  ])('%s requests the mapped route with one /api prefix', async (_name, call, method, expected) => {
    const { urls, inits } = captureRequests()

    await call()

    expect(urls).toEqual([expected])
    expect(urls[0].includes('/api/api')).toBe(false)
    expect(urls[0].split('/api/').length - 1).toBe(1)
    expect(inits[0]?.method ?? 'GET').toBe(method)
  })

  it('sends a lifecycle action as JSON naming the account and exactly one target', async () => {
    const { inits } = captureRequests()

    await bulkDisableLocalUser({ username: 'itadmin', groupId: 'g1' })

    const headers = inits[0]?.headers as Record<string, string>
    expect(headers['Content-Type']).toBe('application/json')
    // Exactly one of allDevices/groupId/deviceIds: the server rejects anything
    // else, so the body must not carry the other target keys at all, not even null.
    expect(JSON.parse(inits[0]?.body as string)).toEqual({ username: 'itadmin', groupId: 'g1' })
  })

  it('names specific devices and no group when the target is a selection', async () => {
    const { inits } = captureRequests()

    await bulkDeleteLocalUser({ username: 'itadmin', deviceIds: ['d1', 'd2'] })

    expect(JSON.parse(inits[0]?.body as string)).toEqual({ username: 'itadmin', deviceIds: ['d1', 'd2'] })
  })

  it('carries no password or provisioning fields on enable', async () => {
    const { inits } = captureRequests()

    await bulkEnableLocalUser({ username: 'itadmin', allDevices: true })

    expect(JSON.parse(inits[0]?.body as string)).toEqual({ username: 'itadmin', allDevices: true })
  })
})
