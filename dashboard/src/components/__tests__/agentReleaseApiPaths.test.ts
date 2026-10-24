import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  ApiError,
  publishAgentRelease,
  replaceAgentReleaseArtifact,
  revokeAgentRelease,
  updateDeviceAgent,
} from '../../api/client'
import { apiFailureMessage } from '../../pages/agentReleaseView'

/**
 * The agent-release step-up calls, at the wire.
 *
 * Three actions put SYSTEM-level code on the path to machines -- publish,
 * artifact replacement, and queueing a device self-update -- and the server now
 * refuses each without the caller's own password and a justification, exactly
 * like a recovery-key reveal. These tests pin what actually leaves the browser:
 * the mapped route with one /api prefix (the property apiPaths.test.ts guards
 * for the rest of the client), the two step-up fields in the body the server
 * binds them from, and nothing extra. Revoke is asserted body-less on purpose:
 * withdrawing a build is the safe direction and carries no step-up.
 */

const RELEASE = '0192f3a1-1111-7000-8000-aaaabbbbcccc'
const DEVICE = '01a01bc4-aa58-7bf2-bcfd-9616efcdd614'

/** SHA-256 of the string 'test', the bytes the fake MSI below holds. */
const SHA256_OF_TEST = '9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08'

function captureRequests(response?: () => Response): {
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
      return response
        ? response()
        : new Response(JSON.stringify({}), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
    }),
  )

  return { urls, inits }
}

/** A header's value whatever shape `headers` took, compared case-insensitively. */
function header(init: RequestInit | undefined, name: string): string | undefined {
  const headers = init?.headers
  if (!headers) return undefined
  if (headers instanceof Headers) return headers.get(name) ?? undefined
  if (Array.isArray(headers)) {
    return headers.find(([key]) => key.toLowerCase() === name.toLowerCase())?.[1]
  }
  const key = Object.keys(headers).find((k) => k.toLowerCase() === name.toLowerCase())
  return key ? (headers as Record<string, string>)[key] : undefined
}

function fakeMsi(): File {
  return new File(['test'], 'EndpointPlatformAgent-1.5.0-x64.msi', {
    type: 'application/octet-stream',
  })
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('agent-release API paths', () => {
  it.each([
    [
      'publishAgentRelease',
      () => publishAgentRelease(RELEASE, 'pw', 'rolling out the fix'),
      'POST',
      `/api/admin/v1/agent-releases/${RELEASE}/publish`,
    ],
    [
      'updateDeviceAgent',
      () => updateDeviceAgent(DEVICE, RELEASE, 'pw', 'rolling out the fix'),
      'POST',
      `/api/admin/v1/devices/${DEVICE}/actions/update-agent`,
    ],
    [
      'replaceAgentReleaseArtifact',
      () => replaceAgentReleaseArtifact(RELEASE, fakeMsi(), 'pw', 'signed build'),
      'PUT',
      `/api/admin/v1/agent-releases/${RELEASE}/artifact`,
    ],
    [
      'revokeAgentRelease',
      () => revokeAgentRelease(RELEASE),
      'POST',
      `/api/admin/v1/agent-releases/${RELEASE}/revoke`,
    ],
  ])('%s requests the mapped route with one /api prefix', async (_name, call, method, expected) => {
    const { urls, inits } = captureRequests()

    await call()

    expect(urls).toEqual([expected])
    expect(urls[0].split('/api/').length - 1).toBe(1)
    expect(inits[0]?.method).toBe(method)
    // Anti-CSRF: the API refuses cookie-authenticated mutations without it.
    expect(header(inits[0], 'X-Requested-With')).toBe('XMLHttpRequest')
  })
})

describe('the step-up fields the server binds', () => {
  it('publish sends exactly currentPassword and justification, as JSON', async () => {
    const { inits } = captureRequests()

    await publishAgentRelease(RELEASE, 'my password', 'rolling out the 1.21.1 fix')

    expect(header(inits[0], 'Content-Type')).toBe('application/json')
    const body = JSON.parse(inits[0]?.body as string) as Record<string, unknown>
    expect(Object.keys(body).sort()).toEqual(['currentPassword', 'justification'])
    expect(body.currentPassword).toBe('my password')
    expect(body.justification).toBe('rolling out the 1.21.1 fix')
  })

  it('update-agent names the release beside the step-up fields', async () => {
    const { inits } = captureRequests()

    await updateDeviceAgent(DEVICE, RELEASE, 'my password', 'driver-install fix')

    expect(header(inits[0], 'Content-Type')).toBe('application/json')
    const body = JSON.parse(inits[0]?.body as string) as Record<string, unknown>
    expect(Object.keys(body).sort()).toEqual(['currentPassword', 'justification', 'releaseId'])
    expect(body.releaseId).toBe(RELEASE)
    expect(body.currentPassword).toBe('my password')
    expect(body.justification).toBe('driver-install fix')
  })

  /**
   * The replacement is a multipart form: the step-up rides as string fields
   * beside the file, and the SHA-256 is computed here over the actual bytes so a
   * damaged upload is refused rather than recorded. No Content-Type is set by
   * hand -- the browser must write the multipart boundary itself.
   */
  it('replace-artifact carries file, recomputed sha256 and the step-up fields in one form', async () => {
    const { inits } = captureRequests()

    await replaceAgentReleaseArtifact(RELEASE, fakeMsi(), 'my password', 'signed counterpart')

    const body = inits[0]?.body as FormData
    expect(body).toBeInstanceOf(FormData)
    expect([...body.keys()].sort()).toEqual(['currentPassword', 'file', 'justification', 'sha256'])
    expect(body.get('sha256')).toBe(SHA256_OF_TEST)
    expect(body.get('currentPassword')).toBe('my password')
    expect(body.get('justification')).toBe('signed counterpart')
    expect((body.get('file') as File).name).toBe('EndpointPlatformAgent-1.5.0-x64.msi')
    expect(header(inits[0], 'Content-Type')).toBeUndefined()
  })

  /** Revoke stays body-less: no step-up, because withdrawing a build is the safe direction. */
  it('revoke sends no body and no step-up', async () => {
    const { inits } = captureRequests()

    await revokeAgentRelease(RELEASE)

    expect(inits[0]?.body).toBeUndefined()
    expect(header(inits[0], 'Content-Type')).toBeUndefined()
  })
})

describe('what a step-up refusal surfaces', () => {
  /**
   * The dialogs show the server's reason verbatim, which means the client must
   * carry the problem `detail` through -- "That password is not correct." is the
   * difference between retyping a password and filing a bug.
   */
  const problem = (status: number, detail: string, headers: Record<string, string> = {}) => () =>
    new Response(JSON.stringify({ title: 'Refused', status, detail }), {
      status,
      headers: { 'Content-Type': 'application/problem+json', ...headers },
    })

  it.each([
    ['publishAgentRelease', () => publishAgentRelease(RELEASE, 'wrong', 'reason here')],
    ['updateDeviceAgent', () => updateDeviceAgent(DEVICE, RELEASE, 'wrong', 'reason here')],
    ['replaceAgentReleaseArtifact', () => replaceAgentReleaseArtifact(RELEASE, fakeMsi(), 'wrong', 'reason here')],
  ])('%s carries the 403 wrong-password detail verbatim', async (_name, call) => {
    captureRequests(problem(403, 'That password is not correct.'))

    const error = await call().then(
      () => null,
      (e: unknown) => e,
    )

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(403)
    expect((error as ApiError).detail).toBe('That password is not correct.')
    // The dialogs render refusals through this; the fallback must not win here.
    expect(apiFailureMessage(error, 'fallback')).toBe('That password is not correct.')
  })

  it('carries the 429 rate-limit detail verbatim', async () => {
    captureRequests(problem(429, 'Too many agent-release actions. Try again later.', { 'Retry-After': '300' }))

    const error = await publishAgentRelease(RELEASE, 'pw', 'reason here').then(
      () => null,
      (e: unknown) => e,
    )

    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).status).toBe(429)
    expect(apiFailureMessage(error, 'fallback')).toBe('Too many agent-release actions. Try again later.')
  })
})
