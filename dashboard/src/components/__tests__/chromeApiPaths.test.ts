import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  getChromeGroupDevices,
  getChromeOverview,
  getChromeProfileExtensions,
  getDeviceChrome,
} from '../../api/client'

/**
 * The Chrome client functions reach the API with exactly one `/api` prefix, the
 * routes the server maps, and every id and search term encoded. The same
 * property apiPaths.test.ts guards for the rest of the client, for the same
 * reason: a doubled prefix is a 404 that blanks a whole page.
 */

const DEVICE = '01a01bc4-aa58-7bf2-bcfd-9616efcdd614'
const GROUP = '0192f3a1-1111-7000-8000-aaaabbbbcccc'
const PROFILE = '0192f3a1-2222-7000-8000-aaaabbbbcccc'

function captureUrl(): { urls: string[] } {
  const urls: string[] = []

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      urls.push(typeof input === 'string' ? input : input.toString())
      return new Response(JSON.stringify({}), { status: 200, headers: { 'Content-Type': 'application/json' } })
    }),
  )

  return { urls }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('Chrome API paths', () => {
  it.each([
    ['getChromeOverview', () => getChromeOverview(), '/api/admin/v1/chrome/overview'],
    ['getChromeGroupDevices', () => getChromeGroupDevices(GROUP), `/api/admin/v1/chrome/groups/${GROUP}/devices`],
    ['getDeviceChrome', () => getDeviceChrome(DEVICE), `/api/admin/v1/devices/${DEVICE}/chrome`],
    [
      'getChromeProfileExtensions',
      () => getChromeProfileExtensions(DEVICE, PROFILE),
      `/api/admin/v1/devices/${DEVICE}/chrome/profiles/${PROFILE}/extensions`,
    ],
  ])('%s requests the mapped route with one /api prefix', async (_name, call, expected) => {
    const { urls } = captureUrl()

    await call()

    expect(urls).toEqual([expected])
    expect(urls[0].startsWith('/api/')).toBe(true)
    expect(urls[0].includes('/api/api')).toBe(false)
  })

  it('sends the search term as an encoded query and omits it when blank', async () => {
    const { urls } = captureUrl()

    await getChromeGroupDevices(GROUP, 'front desk & co')
    await getChromeGroupDevices(GROUP, '   ')
    await getChromeGroupDevices(GROUP)

    expect(urls).toEqual([
      `/api/admin/v1/chrome/groups/${GROUP}/devices?q=front%20desk%20%26%20co`,
      `/api/admin/v1/chrome/groups/${GROUP}/devices`,
      `/api/admin/v1/chrome/groups/${GROUP}/devices`,
    ])
  })

  it('encodes ids so a malformed one cannot escape its path segment', async () => {
    const { urls } = captureUrl()

    await getChromeProfileExtensions('a/b', 'c?d')

    expect(urls).toEqual(['/api/admin/v1/devices/a%2Fb/chrome/profiles/c%3Fd/extensions'])
  })
})
