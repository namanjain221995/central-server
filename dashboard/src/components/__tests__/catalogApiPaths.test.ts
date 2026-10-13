import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  clearCatalogAppIcon,
  createCatalogApp,
  deleteCatalogApp,
  getCatalogApps,
  getCatalogInstalls,
  publishCatalogApp,
  setCatalogAppIcon,
  unpublishCatalogApp,
  updateCatalogApp,
} from '../../api/client'

/**
 * The Company Portal client functions reach the API with exactly one `/api`
 * prefix and the routes the server maps -- the same property apiPaths.test.ts
 * guards for the rest of the client, for the same reason: a doubled prefix is
 * a 404 that blanks a whole page.
 */

const APP = '0192f3a1-3333-7000-8000-aaaabbbbcccc'
const INPUT = { packageId: 'p1', displayName: 'Chrome', visibility: 'AllDevices' as const }

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

describe('Company Portal API paths', () => {
  it.each([
    ['getCatalogApps', () => getCatalogApps(), 'GET', '/api/admin/v1/catalog/'],
    ['createCatalogApp', () => createCatalogApp(INPUT), 'POST', '/api/admin/v1/catalog/'],
    ['updateCatalogApp', () => updateCatalogApp(APP, INPUT), 'PUT', `/api/admin/v1/catalog/${APP}`],
    ['deleteCatalogApp', () => deleteCatalogApp(APP), 'DELETE', `/api/admin/v1/catalog/${APP}`],
    ['publishCatalogApp', () => publishCatalogApp(APP), 'POST', `/api/admin/v1/catalog/${APP}/publish`],
    ['unpublishCatalogApp', () => unpublishCatalogApp(APP), 'POST', `/api/admin/v1/catalog/${APP}/unpublish`],
    ['clearCatalogAppIcon', () => clearCatalogAppIcon(APP), 'DELETE', `/api/admin/v1/catalog/${APP}/icon`],
    [
      'getCatalogInstalls',
      () => getCatalogInstalls(2, 50),
      'GET',
      '/api/admin/v1/catalog/installs?page=2&pageSize=50',
    ],
  ])('%s requests the mapped route with one /api prefix', async (_name, call, method, expected) => {
    const { urls, inits } = captureRequests()

    await call()

    expect(urls).toEqual([expected])
    expect(urls[0].includes('/api/api')).toBe(false)
    expect(inits[0]?.method ?? 'GET').toBe(method)
  })

  it('sends the entry as JSON', async () => {
    const { inits } = captureRequests()

    await createCatalogApp({ ...INPUT, publish: true })

    const headers = inits[0]?.headers as Record<string, string>
    expect(headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(inits[0]?.body as string)).toEqual({ ...INPUT, publish: true })
  })

  /**
   * The icon goes up as multipart, and the content type is left for the
   * browser to set: it is the only party that knows the form boundary, and a
   * JSON content type on a FormData body is a 415 before the endpoint runs.
   */
  it('uploads the icon as a multipart PUT and leaves the content type to the browser', async () => {
    const { urls, inits } = captureRequests()
    const png = new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], 'chrome.png', { type: 'image/png' })

    await setCatalogAppIcon(APP, png)

    expect(urls).toEqual([`/api/admin/v1/catalog/${APP}/icon`])
    expect(inits[0]?.method).toBe('PUT')
    const body = inits[0]?.body
    expect(body).toBeInstanceOf(FormData)
    expect((body as FormData).get('file')).toBeInstanceOf(File)

    const headers = inits[0]?.headers as Record<string, string>
    expect(headers['Content-Type']).toBeUndefined()
    expect(headers['X-Requested-With']).toBe('XMLHttpRequest')
  })

  it('encodes ids so a malformed one cannot escape its path segment', async () => {
    const { urls } = captureRequests()

    await publishCatalogApp('a/b')

    expect(urls).toEqual(['/api/admin/v1/catalog/a%2Fb/publish'])
  })
})
