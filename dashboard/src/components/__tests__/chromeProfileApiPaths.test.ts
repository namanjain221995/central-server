import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  removeChromeAccountEverywhere,
  removeChromeProfile,
  type ChromeAccountRemovalResult,
  type ChromeProfileActionAccepted,
  type ChromeProfileRow,
  type ChromeProfileStatus,
} from '../../api/client'

/**
 * The profile lifecycle actions reach the API with exactly one `/api` prefix
 * and the routes the server maps — the same property chromeApiPaths.test.ts and
 * chromeExtensionRuleApiPaths.test.ts guard, for the same reason: a doubled
 * prefix is a 404 that blanks a whole page. The body shapes are exact: a plain
 * single remove still has NO body (older servers accept it unchanged), a
 * restart rides as `{"restartDevice":true}`, and the fleet action sends exactly
 * the contract's `{accountEmail, target, restartDevices}`. The profile row type
 * carries the lifecycle fields the server now sends.
 */

const PROFILE = '0192f3a1-2222-7000-8000-aaaabbbbcccc'
const TASK = '0192f3a1-3333-7000-8000-aaaabbbbcccc'
const RESTART_TASK = '0192f3a1-4444-7000-8000-aaaabbbbcccc'

function captureRequests(body: unknown = { taskId: TASK }, status = 202): {
  urls: string[]
  inits: (RequestInit | undefined)[]
} {
  const urls: string[] = []
  const inits: (RequestInit | undefined)[] = []

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      urls.push(typeof input === 'string' ? input : input.toString())
      inits.push(init)
      // The contract's acknowledgement: 202 Accepted with the task(s) to
      // follow, or the fleet action's 200 with its tallies.
      return new Response(JSON.stringify(body), {
        status,
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

describe('Chrome profile removal with a restart', () => {
  it('sends exactly {"restartDevice":true} as JSON only when the restart is asked for', async () => {
    const { urls, inits } = captureRequests({ taskId: TASK, restartTaskId: RESTART_TASK })

    await removeChromeProfile(PROFILE, true)

    expect(urls).toEqual([`/api/admin/v1/chrome/profiles/${PROFILE}/actions/remove`])
    expect(inits[0]?.method).toBe('POST')
    const headers = inits[0]?.headers as Record<string, string>
    expect(headers['Content-Type']).toBe('application/json')
    expect(JSON.parse(inits[0]?.body as string)).toEqual({ restartDevice: true })
  })

  it('sends no body at all when restartDevice is explicitly false — the shape every server accepts', async () => {
    const { inits } = captureRequests()

    await removeChromeProfile(PROFILE, false)

    expect(inits[0]?.method).toBe('POST')
    expect(inits[0]?.body).toBeUndefined()
  })

  it('parses the 202 with both the removal task and the restart task', async () => {
    captureRequests({ taskId: TASK, restartTaskId: RESTART_TASK })

    const accepted: ChromeProfileActionAccepted = await removeChromeProfile(PROFILE, true)

    expect(accepted).toEqual({ taskId: TASK, restartTaskId: RESTART_TASK })
  })

  it('parses a null restartTaskId — the contract when no restart was asked for', async () => {
    captureRequests({ taskId: TASK, restartTaskId: null })

    const accepted = await removeChromeProfile(PROFILE)

    expect(accepted.taskId).toBe(TASK)
    expect(accepted.restartTaskId).toBeNull()
  })
})

describe('Fleet Chrome account removal API', () => {
  const TALLY: ChromeAccountRemovalResult = {
    targetedDevices: 9,
    queuedRemovals: 5,
    queuedRestarts: 5,
    skippedNoProfile: 2,
    skippedDefault: 1,
    rejectedAgentTooOld: 1,
  }

  it('posts to the mapped route with one /api prefix', async () => {
    const { urls, inits } = captureRequests(TALLY, 200)

    await removeChromeAccountEverywhere({
      accountEmail: 'person@example.com',
      target: { kind: 'all' },
      restartDevices: false,
    })

    expect(urls).toEqual(['/api/admin/v1/chrome/accounts/actions/remove'])
    expect(urls[0].includes('/api/api')).toBe(false)
    expect(urls[0].split('/api/').length - 1).toBe(1)
    expect(inits[0]?.method).toBe('POST')
    const headers = inits[0]?.headers as Record<string, string>
    expect(headers['Content-Type']).toBe('application/json')
  })

  it('sends exactly the contract body for an all-devices target', async () => {
    const { inits } = captureRequests(TALLY, 200)

    await removeChromeAccountEverywhere({
      accountEmail: 'person@example.com',
      target: { kind: 'all' },
      restartDevices: false,
    })

    // The target carries its kind and nothing else — no null groupId or empty
    // deviceIds riding along for the server to reject.
    expect(JSON.parse(inits[0]?.body as string)).toEqual({
      accountEmail: 'person@example.com',
      target: { kind: 'all' },
      restartDevices: false,
    })
  })

  it('names the group, and only the group, for a group target', async () => {
    const { inits } = captureRequests(TALLY, 200)

    await removeChromeAccountEverywhere({
      accountEmail: 'person@example.com',
      target: { kind: 'group', groupId: 'g-42' },
      restartDevices: true,
    })

    expect(JSON.parse(inits[0]?.body as string)).toEqual({
      accountEmail: 'person@example.com',
      target: { kind: 'group', groupId: 'g-42' },
      restartDevices: true,
    })
  })

  it('names the devices for a selection target', async () => {
    const { inits } = captureRequests(TALLY, 200)

    await removeChromeAccountEverywhere({
      accountEmail: 'person@example.com',
      target: { kind: 'devices', deviceIds: ['d1', 'd2'] },
      restartDevices: false,
    })

    expect(JSON.parse(inits[0]?.body as string)).toEqual({
      accountEmail: 'person@example.com',
      target: { kind: 'devices', deviceIds: ['d1', 'd2'] },
      restartDevices: false,
    })
  })

  it('returns the tallies exactly as the server sent them, keys this build does not know included', async () => {
    captureRequests({ ...TALLY, offline: 3, alreadyQueued: 2 }, 200)

    const result = await removeChromeAccountEverywhere({
      accountEmail: 'person@example.com',
      target: { kind: 'all' },
      restartDevices: false,
    })

    expect(result).toEqual({ ...TALLY, offline: 3, alreadyQueued: 2 })
    expect(result.queuedRemovals).toBe(5)
    expect(result['alreadyQueued']).toBe(2)
  })
})
