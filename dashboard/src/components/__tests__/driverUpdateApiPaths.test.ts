import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  ApiError,
  getDeviceDriverUpdates,
  getDriverUpdateOverview,
  installAllDriverUpdates,
  installDriverUpdates,
  scanAllDriverUpdates,
  scanDriverUpdates,
  suppressDriverUpdate,
  unsuppressDriverUpdate,
} from '../../api/client'

/**
 * The driver-update client functions reach the API with exactly one `/api`
 * prefix and the routes the server maps — the same property apiPaths.test.ts and
 * announcementApiPaths.test.ts guard for the rest of the client, for the same
 * reason: a doubled prefix is a 404 that blanks a whole page. The install also
 * has to carry its chosen updateIds as a JSON body, or it would install nothing;
 * hide/unhide carry exactly the contract's body and answer 204 with no body.
 */

const DEVICE = '01a01bc4-aa58-7bf2-bcfd-9616efcdd614'

function captureRequests(respond?: () => Response): { urls: string[]; inits: (RequestInit | undefined)[] } {
  const urls: string[] = []
  const inits: (RequestInit | undefined)[] = []

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      urls.push(typeof input === 'string' ? input : input.toString())
      inits.push(init)
      return respond
        ? respond()
        : new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }),
  )

  return { urls, inits }
}

/** What the suppress/unsuppress routes answer on success: 204, no body. */
const noContent = () => new Response(null, { status: 204 })

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
    [
      'suppressDriverUpdate',
      () => suppressDriverUpdate(DEVICE, 'u1', 'vendor firmware'),
      'POST',
      `/api/admin/v1/devices/${DEVICE}/driver-updates/suppress`,
    ],
    [
      'unsuppressDriverUpdate',
      () => unsuppressDriverUpdate(DEVICE, 'u1'),
      'POST',
      `/api/admin/v1/devices/${DEVICE}/driver-updates/unsuppress`,
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

    await installDriverUpdates(DEVICE, ['u1', 'u2'])

    const headers = inits[0]?.headers as Record<string, string>
    // The shared request() helper labels a string body as JSON; without it the
    // API answers 415 before the endpoint runs.
    expect(headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(inits[0]?.body as string)).toEqual({ updateIds: ['u1', 'u2'] })
  })
})

describe('Hide / unhide a driver update', () => {
  it('suppress sends { updateId, reason } as JSON', async () => {
    const { inits } = captureRequests(noContent)

    await suppressDriverUpdate(DEVICE, 'u1', 'ASUS firmware — use the vendor tool')

    const headers = inits[0]?.headers as Record<string, string>
    expect(inits[0]?.method).toBe('POST')
    expect(headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(inits[0]?.body as string)).toEqual({
      updateId: 'u1',
      reason: 'ASUS firmware — use the vendor tool',
    })
  })

  it('suppress trims the reason and omits it entirely when blank or absent', async () => {
    const { inits } = captureRequests(noContent)

    await suppressDriverUpdate(DEVICE, 'u1', '  padded  ')
    await suppressDriverUpdate(DEVICE, 'u2', '   ')
    await suppressDriverUpdate(DEVICE, 'u3', '')
    await suppressDriverUpdate(DEVICE, 'u4')

    expect(inits.map((i) => JSON.parse(i?.body as string))).toEqual([
      { updateId: 'u1', reason: 'padded' },
      { updateId: 'u2' },
      { updateId: 'u3' },
      { updateId: 'u4' },
    ])
  })

  it('unsuppress sends exactly { updateId } as JSON', async () => {
    const { inits } = captureRequests(noContent)

    await unsuppressDriverUpdate(DEVICE, 'u1')

    const headers = inits[0]?.headers as Record<string, string>
    expect(inits[0]?.method).toBe('POST')
    expect(headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(inits[0]?.body as string)).toEqual({ updateId: 'u1' })
  })

  it.each([
    ['suppressDriverUpdate', () => suppressDriverUpdate(DEVICE, 'u1', 'why')],
    ['unsuppressDriverUpdate', () => unsuppressDriverUpdate(DEVICE, 'u1')],
  ])('%s resolves on a bodiless 204 instead of failing to parse it', async (_name, call) => {
    captureRequests(noContent)

    await expect(call()).resolves.toBeUndefined()
  })

  it('encodes the device id into the path', async () => {
    const { urls } = captureRequests(noContent)

    await suppressDriverUpdate('a/b', 'u1')
    await unsuppressDriverUpdate('a/b', 'u1')

    expect(urls).toEqual([
      '/api/admin/v1/devices/a%2Fb/driver-updates/suppress',
      '/api/admin/v1/devices/a%2Fb/driver-updates/unsuppress',
    ])
  })

  /** The page shows a refusal's problem `detail` verbatim, so the client must carry it through. */
  const problem = (status: number, detail: string) => () =>
    new Response(JSON.stringify({ title: 'Refused', status, detail }), {
      status,
      headers: { 'Content-Type': 'application/problem+json' },
    })

  it.each([
    ['suppressDriverUpdate', () => suppressDriverUpdate(DEVICE, 'u1', 'x'.repeat(201)), 400, 'Reason must be 200 characters or fewer.'],
    ['suppressDriverUpdate', () => suppressDriverUpdate(DEVICE, 'u1'), 404, 'Device not found.'],
    ['unsuppressDriverUpdate', () => unsuppressDriverUpdate(DEVICE, 'u1'), 403, 'You do not have permission to manage driver updates.'],
  ])('%s carries the %d problem detail verbatim', async (_name, call, status, detail) => {
    captureRequests(problem(status, detail))

    const error = await call().then(
      () => null,
      (e: unknown) => e,
    )

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(status)
    expect((error as ApiError).detail).toBe(detail)
  })
})
