import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  endChat,
  getChatDevices,
  getChatMessages,
  sendChatMessage,
  startChat,
} from '../../api/client'

/**
 * The Live Chat client functions reach the API with exactly one `/api` prefix
 * and the routes the server maps — the same property driverUpdateApiPaths.test.ts
 * and announcementApiPaths.test.ts guard for the rest of the client, for the same
 * reason: a doubled prefix is a 404 that blanks a whole page. Sending a message
 * also has to carry its text as a `{body}` JSON payload, or nothing is said.
 */

const DEVICE = '01a01bc4-aa58-7bf2-bcfd-9616efcdd614'
const SESSION = 'e7d3b0a2-1c44-4f9a-9b21-2a6f0c5d8e10'

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

describe('Live Chat API paths', () => {
  it.each([
    ['getChatDevices', () => getChatDevices(), 'GET', '/api/admin/v1/chat/devices'],
    ['startChat', () => startChat(DEVICE), 'POST', `/api/admin/v1/devices/${DEVICE}/chat`],
    [
      'getChatMessages',
      () => getChatMessages(SESSION, 5),
      'GET',
      `/api/admin/v1/chat/${SESSION}/messages?after=5`,
    ],
    [
      'sendChatMessage',
      () => sendChatMessage(SESSION, 'hello'),
      'POST',
      `/api/admin/v1/chat/${SESSION}/messages`,
    ],
    ['endChat', () => endChat(SESSION), 'POST', `/api/admin/v1/chat/${SESSION}/end`],
  ])('%s requests the mapped route with one /api prefix', async (_name, call, method, expected) => {
    const { urls, inits } = captureRequests()

    await call()

    expect(urls).toEqual([expected])
    expect(urls[0].includes('/api/api')).toBe(false)
    expect(urls[0].split('/api/').length - 1).toBe(1)
    expect(inits[0]?.method ?? 'GET').toBe(method)
  })

  it('reads from the cursor it is given', async () => {
    const { urls } = captureRequests()

    await getChatMessages(SESSION, 0)

    expect(urls[0]).toBe(`/api/admin/v1/chat/${SESSION}/messages?after=0`)
  })

  it('sends the message text as a {body} JSON payload', async () => {
    const { inits } = captureRequests()

    await sendChatMessage(SESSION, 'please reboot when you can')

    const headers = inits[0]?.headers as Record<string, string>
    // The shared request() helper labels a string body as JSON; without it the
    // API answers 415 before the endpoint runs.
    expect(headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(inits[0]?.body as string)).toEqual({ body: 'please reboot when you can' })
  })
})
