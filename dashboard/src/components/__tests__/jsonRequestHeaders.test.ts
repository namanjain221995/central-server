import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  createDeployment,
  forceStopApplication,
  getAgentReleases,
  previewDeployment,
  restartDevice,
  revokeUsbAccess,
} from '../../api/client'
// The file itself, as text, via Vite's ?raw import -- typed by vite/client, so no
// Node types are needed in the app config.
import clientSource from '../../api/client.ts?raw'

/**
 * Every JSON request body must say that it is JSON.
 *
 * This exists because of a real production failure. `forceStopApplication`
 * sent `JSON.stringify(...)` through the shared `request()` helper without a
 * Content-Type, so fetch labelled the body `text/plain` and the API's body
 * binding answered 415 before the endpoint ever ran. No task was created, no
 * audit entry was written, and the agent was never involved; the console showed
 * "could not be stopped" for a feature that had never once executed. Eighteen
 * server-side tests were green throughout, because they post with
 * `PostAsJsonAsync`, which sets the header the browser did not.
 *
 * The fix lives in the helper, so the assertion here is against the request
 * that actually leaves the browser: stub fetch, make the call, read the headers.
 */

const DEVICE = '01a01bc4-aa58-7bf2-bcfd-9616efcdd614'
const PACKAGE = '0192f3a1-1111-7000-8000-aaaabbbbcccc'

/** Captures the RequestInit of every fetch, answering an empty JSON success. */
function captureRequests(): { inits: RequestInit[]; urls: string[] } {
  const inits: RequestInit[] = []
  const urls: string[] = []

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      urls.push(typeof input === 'string' ? input : input.toString())
      inits.push(init ?? {})

      return new Response(JSON.stringify({ processesQueued: 0, devices: [] }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }),
  )

  return { inits, urls }
}

/** A header's value whatever shape `headers` took, compared case-insensitively. */
function header(init: RequestInit, name: string): string | undefined {
  const headers = init.headers
  if (!headers) return undefined
  if (headers instanceof Headers) return headers.get(name) ?? undefined
  if (Array.isArray(headers)) {
    return headers.find(([key]) => key.toLowerCase() === name.toLowerCase())?.[1]
  }
  const key = Object.keys(headers).find((k) => k.toLowerCase() === name.toLowerCase())
  return key ? (headers as Record<string, string>)[key] : undefined
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('JSON request bodies declare their media type', () => {
  /** The calls that reached production without it, and their neighbours. */
  it.each([
    ['forceStopApplication', () => forceStopApplication([DEVICE], 'Google Chrome', 'Google LLC')],
    // Restart carries its delay as a JSON body; a bare POST would silently mean "now".
    ['restartDevice', () => restartDevice(DEVICE, 600)],
    ['previewDeployment', () => previewDeployment(PACKAGE, [DEVICE], [])],
    ['createDeployment', () => createDeployment(PACKAGE, [DEVICE], [])],
    // One that always set it explicitly, to show the helper agrees with it.
    ['revokeUsbAccess', () => revokeUsbAccess(DEVICE, 'no longer needed')],
  ])('%s sends Content-Type: application/json', async (_name, call) => {
    const { inits } = captureRequests()

    await call().catch(() => {
      // Only the outgoing request is under test.
    })

    const init = inits[0]
    expect(init).toBeDefined()
    expect(init.method).toBe('POST')
    expect(typeof init.body).toBe('string')
    expect(header(init, 'Content-Type')).toBe('application/json')
    // Adding one header must not have cost the anti-CSRF one.
    expect(header(init, 'X-Requested-With')).toBe('XMLHttpRequest')
  })

  it('a request with no body carries no Content-Type', async () => {
    const { inits } = captureRequests()

    await getAgentReleases().catch(() => {})

    expect(inits[0].body).toBeUndefined()
    expect(header(inits[0], 'Content-Type')).toBeUndefined()
  })

  /**
   * What Force Stop sends is an application, and nothing about a process. Pinned
   * here at the wire, next to the header fix, so the fix could not have widened
   * the request: the server's refusal to accept a pid, image name or path is
   * only meaningful if the client never offers one.
   */
  it('the force-stop body names an application and nothing about a process', async () => {
    const { inits, urls } = captureRequests()

    await forceStopApplication([DEVICE], 'Google Chrome', 'Google LLC').catch(() => {})

    expect(urls[0]).toBe('/api/admin/v1/software/force-stop')
    const body = JSON.parse(inits[0].body as string) as Record<string, unknown>
    expect(Object.keys(body).sort()).toEqual(['deviceIds', 'name', 'publisher'])
    expect(body.deviceIds).toEqual([DEVICE])
    expect(body.name).toBe('Google Chrome')
    expect(body.publisher).toBe('Google LLC')
  })


  /**
   * The helper now covers every `request()` caller, but a handful of functions
   * call `fetch` directly (uploads, sign-in, policies). Each of those that sends
   * a JSON body must set the header itself, and this reads the source to say so
   * -- a new direct call written without it fails here rather than in
   * production.
   */
  it('every direct fetch() in client.ts that sends JSON sets the header itself', () => {
    const source = clientSource

    // Each chunk is one fetch(...) call and what follows it up to the next
    // top-level statement; a JSON body is a JSON.stringify inside that call.
    const calls = source.split(/\bfetch\(/).slice(1)
    const offenders: string[] = []

    for (const chunk of calls) {
      const call = chunk.slice(0, chunk.indexOf('\n}') === -1 ? undefined : chunk.indexOf('\n}'))
      const sendsJson = /body:\s*JSON\.stringify\(/.test(call)
      const declaresJson = /'Content-Type':\s*'application\/json'/.test(call)
      if (sendsJson && !declaresJson) {
        offenders.push(call.split('\n')[0].trim())
      }
    }

    expect(offenders, 'direct fetch() calls sending JSON without Content-Type').toEqual([])
    expect(calls.length).toBeGreaterThan(5) // the scan actually saw the file
  })
})
