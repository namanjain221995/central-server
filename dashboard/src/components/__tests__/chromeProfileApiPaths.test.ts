import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  removeChromeProfile,
  type ChromeProfileActionAccepted,
  type ChromeProfileRow,
  type ChromeProfileStatus,
} from '../../api/client'

/**
 * The profile lifecycle action reaches the API with exactly one `/api` prefix
 * and the route the server maps — the same property chromeApiPaths.test.ts and
 * chromeExtensionRuleApiPaths.test.ts guard, for the same reason: a doubled
 * prefix is a 404 that blanks a whole page. The 202 body is `{taskId}` and
 * nothing more, and the profile row type carries the lifecycle fields the
 * server now sends.
 */

const PROFILE = '0192f3a1-2222-7000-8000-aaaabbbbcccc'
const TASK = '0192f3a1-3333-7000-8000-aaaabbbbcccc'

function captureRequests(): { urls: string[]; inits: (RequestInit | undefined)[] } {
  const urls: string[] = []
  const inits: (RequestInit | undefined)[] = []

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      urls.push(typeof input === 'string' ? input : input.toString())
      inits.push(init)
      // The contract's acknowledgement: 202 Accepted with the task to follow.
      return new Response(JSON.stringify({ taskId: TASK }), {
        status: 202,
        headers: { 'Content-Type': 'application/json' },
      })
    }),
  )

  return { urls, inits }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('Chrome profile action API paths', () => {
  it('posts the remove action to the mapped route with one /api prefix and no body', async () => {
    const { urls, inits } = captureRequests()

    await removeChromeProfile(PROFILE)

    expect(urls).toEqual([`/api/admin/v1/chrome/profiles/${PROFILE}/actions/remove`])
    expect(urls[0].includes('/api/api')).toBe(false)
    expect(urls[0].split('/api/').length - 1).toBe(1)
    expect(inits[0]?.method).toBe('POST')
    expect(inits[0]?.body).toBeUndefined()
  })

  it('returns the 202 acknowledgement typed as {taskId}', async () => {
    captureRequests()

    const accepted: ChromeProfileActionAccepted = await removeChromeProfile(PROFILE)

    expect(accepted).toEqual({ taskId: TASK })
  })

  it('encodes the id so a malformed one cannot escape its path segment', async () => {
    const { urls } = captureRequests()

    await removeChromeProfile('a/b?c')

    expect(urls).toEqual(['/api/admin/v1/chrome/profiles/a%2Fb%3Fc/actions/remove'])
  })

  it('carries the lifecycle fields on the profile row type', () => {
    // Compile-time coverage of the extended wire contract: `id` (what the
    // action addresses), the two nullable booleans (null = an agent too old to
    // say), the server-authoritative status, and the pre-existing lastActiveAt.
    const row: ChromeProfileRow = {
      id: PROFILE,
      profileId: PROFILE,
      userSid: 'S-1-5-21-1-2-3-1001',
      userAccount: 'TECHSARA\\naman',
      accountEmail: null,
      profileKey: 'Profile 2',
      profileName: 'Work',
      profilePath: 'C:\\Users\\naman\\AppData\\Local\\Google\\Chrome\\User Data\\Profile 2',
      isManaged: false,
      lastActiveAt: null,
      signedIn: null,
      signInBroken: null,
      status: 'dormant',
      extensionCount: 0,
      managedExtensionCount: 0,
      collectedAt: '2026-10-24T08:00:00Z',
    }
    const status: ChromeProfileStatus = row.status

    expect(row.id).toBe(PROFILE)
    expect(status).toBe('dormant')
    expect(row.signedIn).toBeNull()
    expect(row.signInBroken).toBeNull()
  })
})
