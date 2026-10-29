import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  ApiError,
  getDeviceSecurityUpdates,
  getSecurityUpdateOverview,
  installAllSecurityUpdates,
  installSecurityUpdates,
  scanAllSecurityUpdates,
  scanSecurityUpdates,
  suppressSecurityUpdate,
  unsuppressSecurityUpdate,
} from '../../api/client'

/**
 * The security-update client functions reach the API with exactly one `/api`
 * prefix and the routes the server maps — the same property apiPaths.test.ts and
 * driverUpdateApiPaths.test.ts guard for the rest of the client, for the same
 * reason: a doubled prefix is a 404 that blanks a whole page. The install also
 * has to carry its chosen updateIds as a JSON body, or it would install nothing;
 * hide/unhide carry exactly the contract's body and answer 204 with no body —
 * and, unlike drivers, a hide always carries its (required) reason.
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
    [
      'suppressSecurityUpdate',
      () => suppressSecurityUpdate(DEVICE, 'u1', 'breaks a line-of-business app'),
      'POST',
      `/api/admin/v1/devices/${DEVICE}/security-updates/suppress`,
    ],
    [
      'unsuppressSecurityUpdate',
      () => unsuppressSecurityUpdate(DEVICE, 'u1'),
      'POST',
      `/api/admin/v1/devices/${DEVICE}/security-updates/unsuppress`,
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

describe('Hide / unhide a security update', () => {
  it('suppress sends { updateId, reason } as JSON', async () => {
    const { inits } = captureRequests(noContent)

    await suppressSecurityUpdate(DEVICE, 'u1', 'Breaks the line-of-business app — vendor fix pending')

    const headers = inits[0]?.headers as Record<string, string>
    expect(inits[0]?.method).toBe('POST')
    expect(headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(inits[0]?.body as string)).toEqual({
      updateId: 'u1',
      reason: 'Breaks the line-of-business app — vendor fix pending',
    })
  })

  it('suppress trims the reason and always sends it — never omits it, so the server judges a blank one', async () => {
    const { inits } = captureRequests(noContent)

    await suppressSecurityUpdate(DEVICE, 'u1', '  padded  ')
    await suppressSecurityUpdate(DEVICE, 'u2', '   ')

    expect(inits.map((i) => JSON.parse(i?.body as string))).toEqual([
      { updateId: 'u1', reason: 'padded' },
      { updateId: 'u2', reason: '' },
    ])
  })

  it('unsuppress sends exactly { updateId } as JSON', async () => {
    const { inits } = captureRequests(noContent)

    await unsuppressSecurityUpdate(DEVICE, 'u1')

    const headers = inits[0]?.headers as Record<string, string>
    expect(inits[0]?.method).toBe('POST')
    expect(headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(inits[0]?.body as string)).toEqual({ updateId: 'u1' })
  })

  it.each([
    ['suppressSecurityUpdate', () => suppressSecurityUpdate(DEVICE, 'u1', 'why')],
    ['unsuppressSecurityUpdate', () => unsuppressSecurityUpdate(DEVICE, 'u1')],
  ])('%s resolves on a bodiless 204 instead of failing to parse it', async (_name, call) => {
    captureRequests(noContent)

    await expect(call()).resolves.toBeUndefined()
  })

  it('encodes the device id into the path', async () => {
    const { urls } = captureRequests(noContent)

    await suppressSecurityUpdate('a/b', 'u1', 'why')
    await unsuppressSecurityUpdate('a/b', 'u1')

    expect(urls).toEqual([
      '/api/admin/v1/devices/a%2Fb/security-updates/suppress',
      '/api/admin/v1/devices/a%2Fb/security-updates/unsuppress',
    ])
  })

  /** The page shows a refusal's problem `detail` verbatim, so the client must carry it through. */
  const problem = (status: number, detail: string) => () =>
    new Response(JSON.stringify({ title: 'Refused', status, detail }), {
      status,
      headers: { 'Content-Type': 'application/problem+json' },
    })

  it.each([
    ['suppressSecurityUpdate', () => suppressSecurityUpdate(DEVICE, 'u1', '   '), 400, 'A reason is required to hide a security update.'],
    ['suppressSecurityUpdate', () => suppressSecurityUpdate(DEVICE, 'u1', 'x'.repeat(201)), 400, 'Reason must be 200 characters or fewer.'],
    ['suppressSecurityUpdate', () => suppressSecurityUpdate(DEVICE, 'u1', 'why'), 404, 'Device not found.'],
    ['unsuppressSecurityUpdate', () => unsuppressSecurityUpdate(DEVICE, 'u1'), 403, 'You do not have permission to manage updates.'],
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
