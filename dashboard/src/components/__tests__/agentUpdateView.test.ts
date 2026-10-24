import { describe, expect, it } from 'vitest'
import {
  compareVersions,
  describeIneligibility,
  describeSelection,
  eligibleDevices,
  ineligibilityReason,
  isEligible,
  isNewer,
  isOperatorRefusal,
  isSigned,
  publishedReleases,
  refusalText,
  signingLabel,
  summariseResults,
  toCandidate,
  upgradeTargets,
  type DeviceUpdateCandidate,
} from '../../pages/agentUpdateView'
import { ApiError, type AgentReleaseRow } from '../../api/client'

/**
 * Which devices an agent update may touch.
 *
 * An agent update runs an installer as SYSTEM on the target machine, so the
 * cost of getting targeting wrong is not a failed task -- it is an installer on
 * a machine nobody meant to touch. The rule that matters most is that
 * "update all" never means every row in the database, and every exclusion below
 * is asserted rather than assumed.
 *
 * The server enforces all of this independently. These tests cover the console's
 * copy of the rules, which exists so an operator is not invited to click
 * something that will be refused.
 */

function release(over: Partial<AgentReleaseRow> = {}): AgentReleaseRow {
  return {
    id: 'r1',
    version: '1.5.0',
    platform: 'Windows',
    architecture: 'x64',
    fileName: 'EndpointPlatformAgent-1.5.0-x64.msi',
    sha256: 'a'.repeat(64),
    signerSubject: 'CN=Example Corp',
    releaseNotes: null,
    contentSizeBytes: 1024,
    status: 'Published',
    createdByDisplay: 'admin@test.local',
    createdAt: '2026-09-01T00:00:00Z',
    publishedAt: '2026-09-01T01:00:00Z',
    revokedAt: null,
    ...over,
  }
}

function device(over: Partial<DeviceUpdateCandidate> = {}): DeviceUpdateCandidate {
  return {
    deviceId: 'd1',
    hostname: 'PC-1',
    agentVersion: '1.4.1',
    status: 'Active',
    ...over,
  }
}

describe('version comparison', () => {
  it('orders three-part versions numerically, not lexically', () => {
    // The case a string compare gets wrong.
    expect(isNewer('1.10.0', '1.9.0')).toBe(true)
    expect(isNewer('1.9.0', '1.10.0')).toBe(false)
  })

  it('ignores build metadata after a plus', () => {
    expect(compareVersions('1.4.1+abc123', '1.4.1')).toBe(0)
  })

  it('treats an unreadable version as unknown rather than guessing', () => {
    expect(compareVersions('not-a-version', '1.0.0')).toBeNull()
    expect(compareVersions('1.0', '1.0.0')).toBeNull()

    // And unknown is never "newer": a device whose version cannot be read must
    // not be assumed to be behind.
    expect(isNewer('1.5.0', 'garbage')).toBe(false)
  })
})

describe('eligibility', () => {
  it('accepts a device genuinely behind a published release', () => {
    expect(isEligible(device(), release())).toBe(true)
    expect(ineligibilityReason(device(), release())).toBeNull()
  })

  /** Checked before anything else: an offboarded machine is never a target. */
  it('excludes retired devices', () => {
    expect(ineligibilityReason(device({ status: 'Retired' }), release())).toBe('retired')

    // ...even when the release would otherwise be a clear upgrade.
    expect(isEligible(device({ status: 'Retired', agentVersion: '1.0.0' }), release())).toBe(false)
  })

  it('excludes devices already on the release', () => {
    expect(ineligibilityReason(device({ agentVersion: '1.5.0' }), release())).toBe('not-newer')
  })

  it('excludes devices on a newer version', () => {
    expect(ineligibilityReason(device({ agentVersion: '1.6.0' }), release())).toBe('not-newer')
  })

  it('excludes drafts and revoked releases, and tells them apart', () => {
    expect(ineligibilityReason(device(), release({ status: 'Draft' }))).toBe('unpublished')
    expect(ineligibilityReason(device(), release({ status: 'Revoked' }))).toBe('revoked')
  })

  it('excludes devices whose reported version cannot be read', () => {
    expect(ineligibilityReason(device({ agentVersion: 'unknown' }), release()))
      .toBe('unknown-version')
  })

  it('gives every reason a readable explanation', () => {
    for (const r of ['retired', 'unpublished', 'revoked', 'not-newer', 'unknown-version'] as const) {
      expect(describeIneligibility(r).length).toBeGreaterThan(0)
    }
  })
})

describe('bulk targeting', () => {
  /**
   * The test this file exists for. A realistic mixture, including the exact
   * shape of the production estate: one retired device, devices on several
   * versions, and one already current.
   */
  it('never selects retired, current or ahead devices', () => {
    const devices = [
      device({ deviceId: 'old', agentVersion: '1.1.1' }),
      device({ deviceId: 'older', agentVersion: '1.0.0' }),
      device({ deviceId: 'current', agentVersion: '1.5.0' }),
      device({ deviceId: 'ahead', agentVersion: '2.0.0' }),
      device({ deviceId: 'retired', agentVersion: '1.0.0', status: 'Retired' }),
      device({ deviceId: 'unreadable', agentVersion: '?' }),
    ]

    const chosen = eligibleDevices(devices, release()).map((d) => d.deviceId)

    expect(chosen).toEqual(['old', 'older'])

    // Stated as exclusions too, so a future change that widens the filter fails
    // here rather than quietly updating more machines.
    expect(chosen).not.toContain('retired')
    expect(chosen).not.toContain('current')
    expect(chosen).not.toContain('ahead')
    expect(chosen).not.toContain('unreadable')
  })

  it('selects nothing when the release is not published', () => {
    const devices = [device({ agentVersion: '1.0.0' }), device({ agentVersion: '1.1.0' })]

    expect(eligibleDevices(devices, release({ status: 'Draft' }))).toHaveLength(0)
    expect(eligibleDevices(devices, release({ status: 'Revoked' }))).toHaveLength(0)
  })

  it('selects nothing from an empty list', () => {
    expect(eligibleDevices([], release())).toHaveLength(0)
  })

  /** The confirmation must say what will be skipped, not silently narrow. */
  it('summarises what will and will not happen', () => {
    const devices = [
      device({ deviceId: 'a', agentVersion: '1.0.0' }),
      device({ deviceId: 'b', agentVersion: '1.5.0' }),
      device({ deviceId: 'c', agentVersion: '1.0.0', status: 'Retired' }),
    ]

    const text = describeSelection(devices, release())

    expect(text).toContain('1 device')
    expect(text).toContain('2 skipped')
    expect(text).toContain('1.5.0')
  })

  it('says plainly when nothing can be updated', () => {
    expect(describeSelection([device({ agentVersion: '1.5.0' })], release()))
      .toContain('No selected device')
  })
})

describe('upgrade targets for one device', () => {
  it('offers only newer published releases, newest first', () => {
    const releases = [
      release({ id: 'r-old', version: '1.3.0' }),
      release({ id: 'r-new', version: '1.6.0' }),
      release({ id: 'r-mid', version: '1.5.0' }),
      release({ id: 'r-draft', version: '2.0.0', status: 'Draft' }),
    ]

    expect(upgradeTargets(device({ agentVersion: '1.4.1' }), releases).map((r) => r.id))
      .toEqual(['r-new', 'r-mid'])
  })

  it('offers nothing to a retired device', () => {
    expect(upgradeTargets(device({ status: 'Retired' }), [release()])).toHaveLength(0)
  })

  it('offers nothing when the device is already current', () => {
    expect(upgradeTargets(device({ agentVersion: '9.9.9' }), [release()])).toHaveLength(0)
  })
})

describe('release signing', () => {
  /**
   * Reported, never enforced here -- and what is reported has to be what
   * happens. The agent re-computes the SHA-256 over the bytes it downloaded and
   * refuses a mismatch; it checks an Authenticode signature only when the
   * release declares a signer, which only the Public trust model produces.
   * Under Internal -- the default, and what production runs -- no release
   * declares one: the launcher returns "no error" for a null signer and the
   * executor installs on hash verification alone.
   */
  it('reports a signed release with its signer', () => {
    expect(isSigned(release())).toBe(true)
    expect(signingLabel(release())).toContain('CN=Example Corp')
  })

  it('says an unsigned release is installed after a hash check, and no signature is checked', () => {
    for (const unsigned of [release({ signerSubject: null }), release({ signerSubject: '  ' })]) {
      expect(isSigned(unsigned)).toBe(false)

      const label = signingLabel(unsigned)

      expect(label).toContain('SHA-256')
      expect(label).toMatch(/no signature is checked/i)
    }
  })

  /**
   * The regression test this correction exists for. The label used to say "the
   * agent will refuse to install this", which was false for every release the
   * Internal gate publishes. Any future wording that re-promises a refusal fails
   * here rather than reaching an operator.
   */
  it('never claims a refusal the endpoint will not perform', () => {
    for (const r of [
      release(),
      release({ signerSubject: null }),
      release({ signerSubject: '  ' }),
      release({ signerSubject: null, status: 'Draft' }),
    ]) {
      expect(signingLabel(r)).not.toMatch(/refuse|reject|block|will not install|cannot install/i)
    }
  })

  /**
   * The module boundary this correction rests on. signingLabel describes what
   * the endpoint does, and the endpoint acts on the release row's declared
   * signer -- not on the server's current trust mode. So the label takes one
   * argument and must stay that way: a mode-aware label would print Public copy
   * beside a release published with a null signer, which still installs on hash
   * alone after a switch to Public.
   */
  it('depends on the release row alone', () => {
    expect(signingLabel).toHaveLength(1)
  })

  /**
   * Signing is deliberately NOT an eligibility rule. A release can be published
   * and selectable while unsigned -- under Internal that is every release -- and
   * nothing downstream refuses it. Making the console hide such a release would
   * move a security decision to the wrong layer and hide the actual model.
   */
  it('does not silently exclude unsigned releases from targeting', () => {
    expect(isEligible(device(), release({ signerSubject: null }))).toBe(true)
  })
})

describe('bulk target picker', () => {
  it('offers only published releases, newest first', () => {
    const all = [
      release({ id: 'a', version: '1.4.0' }),
      release({ id: 'b', version: '1.6.0' }),
      release({ id: 'draft', version: '2.0.0', status: 'Draft' }),
      release({ id: 'revoked', version: '1.9.0', status: 'Revoked' }),
    ]

    expect(publishedReleases(all).map((r) => r.id)).toEqual(['b', 'a'])
  })

  /** 1.4.1 is uploaded but deliberately unpublished; it must not be offerable. */
  it('offers nothing when no release is published', () => {
    expect(publishedReleases([release({ status: 'Draft' })])).toHaveLength(0)
  })
})

describe('device list adaptation', () => {
  it('carries the fields the targeting rules read, unchanged', () => {
    const row = {
      id: 'd9',
      hostname: 'OMDEVSINH-TECHS',
      agentVersion: '1.4.1',
      status: 'Active',
      displayName: 'Controlled device',
    }

    expect(toCandidate(row)).toEqual({
      deviceId: 'd9',
      hostname: 'OMDEVSINH-TECHS',
      agentVersion: '1.4.1',
      status: 'Active',
    })
  })

  /** Status must survive the hop: a retired row adapted into a candidate stays retired. */
  it('does not lose the retired status', () => {
    const candidate = toCandidate({
      id: 'd1', hostname: 'PC', agentVersion: '1.0.0', status: 'Retired',
    })

    expect(isEligible(candidate, release())).toBe(false)
  })
})

describe('reporting a bulk run', () => {
  it('reports a clean run', () => {
    expect(summariseResults([{ hostname: 'a', error: null }, { hostname: 'b', error: null }]))
      .toBe('Update queued on 2 devices.')
  })

  /**
   * The case that must not be rounded off. The server re-checks every rule, so a
   * device the console believed eligible can still be refused; that has to read
   * as a partial success rather than as a clean pass.
   */
  it('names a partial run as partial', () => {
    const text = summariseResults([
      { hostname: 'a', error: null },
      { hostname: 'b', error: 'Conflict' },
    ])

    expect(text).toContain('1 of 2')
    expect(text).toContain('1 refused')
  })

  it('does not describe a total failure as a success', () => {
    const text = summariseResults([{ hostname: 'a', error: 'Not found' }])

    expect(text).toContain('No update was queued')
    expect(text).not.toMatch(/queued on \d/)
  })

  it('says plainly when there was nothing to do', () => {
    expect(summariseResults([])).toBe('Nothing was queued.')
  })
})

describe('stopping a bulk run on an operator refusal', () => {
  /**
   * A 403 is the step-up gate and a 429 the rate limiter. Neither says anything
   * about the device the call named, and both would refuse every remaining
   * device identically -- while each wrong-password attempt counts towards the
   * operator's own sign-in lockout. The run must stop on one rather than replay
   * it down the list.
   */
  it('treats the step-up gate and the rate limit as operator refusals', () => {
    expect(isOperatorRefusal(new ApiError(403, 'HTTP 403', null, 'That password is not correct.'))).toBe(true)
    expect(isOperatorRefusal(new ApiError(429, 'HTTP 429', null, 'Too many agent-release actions. Try again later.'))).toBe(true)
  })

  /** A device-level refusal is about that device alone; the rest still get their turn. */
  it('lets device-level refusals continue the run', () => {
    expect(isOperatorRefusal(new ApiError(409, 'HTTP 409', null, 'Release 1.5.0 is not newer.'))).toBe(false)
    expect(isOperatorRefusal(new ApiError(404, 'HTTP 404', null))).toBe(false)
    expect(isOperatorRefusal(new ApiError(500, 'HTTP 500', null))).toBe(false)
  })

  it('does not mistake a network failure for an operator refusal', () => {
    expect(isOperatorRefusal(new TypeError('Failed to fetch'))).toBe(false)
    expect(isOperatorRefusal(undefined)).toBe(false)
  })
})

describe('what a refused device is reported with', () => {
  /** The server's own reason, verbatim, when it gave one. It never quotes what was typed. */
  it('prefers the problem detail', () => {
    const error = new ApiError(
      409,
      'Request to /x failed with HTTP 409',
      null,
      'Release 1.5.0 is not newer than the installed agent 1.5.0. Downgrades and same-version reinstalls are not offered.',
    )

    expect(refusalText(error)).toContain('not newer than the installed agent')
    expect(refusalText(error)).not.toContain('Request to /x')
  })

  it('falls back to the error message when the server gave no reason', () => {
    expect(refusalText(new ApiError(500, 'Request failed with HTTP 500', null))).toBe('Request failed with HTTP 500')
    expect(refusalText(new ApiError(502, 'Request failed with HTTP 502', null, '  '))).toBe('Request failed with HTTP 502')
    expect(refusalText(new TypeError('Failed to fetch'))).toBe('Failed to fetch')
  })

  it('says Refused for anything that is not an error at all', () => {
    expect(refusalText(undefined)).toBe('Refused')
    expect(refusalText('boom')).toBe('Refused')
  })
})
