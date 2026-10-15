import { afterEach, describe, expect, it, vi } from 'vitest'
import { getAnnouncements, sendAnnouncement } from '../../api/client'

/**
 * The announcement client functions reach the API with exactly one `/api`
 * prefix and the routes the server maps — the same property apiPaths.test.ts
 * and catalogApiPaths.test.ts guard for the rest of the client, for the same
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

describe('Announcement API paths', () => {
  it.each([
    [
      'sendAnnouncement',
      () => sendAnnouncement({ title: 't', message: 'm', groupId: 'g1' }),
      'POST',
      '/api/admin/v1/announcements',
    ],
    ['getAnnouncements', () => getAnnouncements(), 'GET', '/api/admin/v1/announcements'],
  ])('%s requests the mapped route with one /api prefix', async (_name, call, method, expected) => {
    const { urls, inits } = captureRequests()

    await call()

    expect(urls).toEqual([expected])
    expect(urls[0].includes('/api/api')).toBe(false)
    expect(urls[0].split('/api/').length - 1).toBe(1)
    expect(inits[0]?.method ?? 'GET').toBe(method)
  })

  it('sends a group broadcast as JSON naming the group and nothing else', async () => {
    const { inits } = captureRequests()

    await sendAnnouncement({ title: 'Maintenance', message: 'Back at 18:00.', groupId: 'g1' })

    const headers = inits[0]?.headers as Record<string, string>
    expect(headers['Content-Type']).toBe('application/json')
    // Exactly one of groupId/deviceIds: the server rejects both-or-neither, so
    // the body must not carry a deviceIds key at all, not even null.
    expect(JSON.parse(inits[0]?.body as string)).toEqual({
      title: 'Maintenance',
      message: 'Back at 18:00.',
      groupId: 'g1',
    })
  })

  it('sends a device broadcast naming the devices and no group', async () => {
    const { inits } = captureRequests()

    await sendAnnouncement({ title: 't', message: 'm', deviceIds: ['d1', 'd2'] })

    expect(JSON.parse(inits[0]?.body as string)).toEqual({
      title: 't',
      message: 'm',
      deviceIds: ['d1', 'd2'],
    })
  })
})
