import { describe, expect, it } from 'vitest'
import {
  apiFailureMessage,
  deploymentWarning,
  describeReleaseGap,
  downloadHint,
  isDownloadable,
  newestPublished,
  newestUpdateEligible,
  newestUploaded,
  releaseGap,
  publishWillBeRefused,
  trustModeLabel,
  validateStepUpForm,
} from '../../pages/agentReleaseView'
import {
  eligibleDevices,
  isEligible,
  upgradeTargets,
  type DeviceUpdateCandidate,
} from '../../pages/agentUpdateView'
import { ApiError, type AgentReleaseRow } from '../../api/client'

/**
 * Which release is "the latest", and what that entitles it to.
 *
 * Written against the real production shape, because the bug this file exists
 * for was invisible in the abstract: the estate holds 1.1.0 to 1.1.4 published,
 * and a 1.4.1 build that was made and installed by hand but never registered.
 * The page reported 1.1.4 as the latest release while the controlled device was
 * running 1.4.1, and both statements were true at once -- which is exactly why
 * one number cannot answer the question.
 */

function release(over: Partial<AgentReleaseRow> = {}): AgentReleaseRow {
  return {
    id: 'r-' + (over.version ?? '1.0.0'),
    version: '1.0.0',
    platform: 'Windows',
    architecture: 'x64',
    fileName: 'EndpointPlatformAgent-1.0.0-x64.msi',
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

/** The production estate: 1.1.0-1.1.4 published, 1.4.1 registered but unsigned and draft. */
function estateWith141Registered(): AgentReleaseRow[] {
  return [
    release({ id: 'r-141', version: '1.4.1', status: 'Draft', signerSubject: null, publishedAt: null }),
    release({ id: 'r-114', version: '1.1.4' }),
    release({ id: 'r-113', version: '1.1.3' }),
    release({ id: 'r-110', version: '1.1.0' }),
  ]
}

const estateBefore141 = (): AgentReleaseRow[] =>
  estateWith141Registered().filter((r) => r.version !== '1.4.1')

describe('the three answers', () => {
  it('distinguishes newest registered from newest published from newest deployable', () => {
    const releases = estateWith141Registered()

    expect(newestUploaded(releases)?.version).toBe('1.4.1')
    expect(newestPublished(releases)?.version).toBe('1.1.4')
    expect(newestUpdateEligible(releases)?.version).toBe('1.1.4')
  })

  /**
   * The original defect. The list arrives newest-created first and the page took
   * the first Published row, so publishing an older build after a newer one made
   * the older one "latest". Ordering by version rather than arrival fixes it.
   */
  it('orders by version, not by upload order', () => {
    const outOfOrder = [
      release({ id: 'late', version: '1.1.2', createdAt: '2026-09-09T00:00:00Z' }),
      release({ id: 'early', version: '1.1.4', createdAt: '2026-01-01T00:00:00Z' }),
    ]

    expect(newestPublished(outOfOrder)?.id).toBe('early')
  })

  it('ignores revoked releases when reporting what is held', () => {
    const releases = [...estateBefore141(), release({ version: '2.0.0', status: 'Revoked' })]

    expect(newestUploaded(releases)?.version).toBe('1.1.4')
    expect(newestPublished(releases)?.version).toBe('1.1.4')
  })

  it('ignores releases whose version cannot be read', () => {
    const releases = [...estateBefore141(), release({ version: 'nightly', status: 'Draft' })]

    expect(newestUploaded(releases)?.version).toBe('1.1.4')
  })

  it('answers nothing for an empty estate', () => {
    expect(newestUploaded([])).toBeNull()
    expect(newestPublished([])).toBeNull()
    expect(newestUpdateEligible([])).toBeNull()
  })
})

describe('the gap between held and deployed', () => {
  it('reports the 1.4.1 case as a newer unpublished build', () => {
    const releases = estateWith141Registered()

    expect(releaseGap(releases)).toBe('unpublished-newer')

    const text = describeReleaseGap(
      releaseGap(releases), newestUploaded(releases), newestUpdateEligible(releases))

    expect(text).toContain('1.4.1')
    expect(text).toContain('1.1.4')
    expect(text).toContain('not published')
  })

  it('reports no gap when the newest build is the published one', () => {
    expect(releaseGap(estateBefore141())).toBe('none')
  })

  it('reports an estate that has registered but published nothing', () => {
    const drafts = [release({ version: '1.4.1', status: 'Draft' })]

    expect(releaseGap(drafts)).toBe('nothing-published')
    expect(describeReleaseGap('nothing-published', drafts[0], null)).toContain('no device can be updated')
  })

  it('reports an empty estate', () => {
    expect(releaseGap([])).toBe('no-releases')
    expect(describeReleaseGap('no-releases', null, null)).toContain('No agent releases')
  })
})

describe('unsigned releases', () => {
  /**
   * The rule the feature turns on. Registering 1.4.1 must not make it a fleet
   * target; only publishing does that, and it stays a Draft until someone
   * decides otherwise.
   */
  it('a registered unsigned draft is not deployable to anything', () => {
    const releases = estateWith141Registered()
    const draft = releases.find((r) => r.version === '1.4.1')!

    expect(newestUpdateEligible(releases)?.version).not.toBe('1.4.1')

    const device: DeviceUpdateCandidate = {
      deviceId: 'd1', hostname: 'PC-1', agentVersion: '1.1.4', status: 'Active',
    }

    expect(eligibleDevices([device], draft)).toHaveLength(0)
    expect(upgradeTargets(device, releases).map((r) => r.version)).not.toContain('1.4.1')
  })

  /**
   * Internal is the production model. Signatures are not read, so nothing about
   * them is a reason to refuse, and the console must not pretend otherwise.
   */
  it('under Internal an unsigned draft is publishable and carries no warning', () => {
    const draft = release({ version: '1.4.1', status: 'Draft', signerSubject: null })

    expect(publishWillBeRefused(draft, 'Internal')).toBe(false)
    expect(deploymentWarning(draft, 'Internal')).toBeNull()
  })

  it('under Public an unsigned draft is refused, with the fix named', () => {
    const draft = release({ version: '1.4.1', status: 'Draft', signerSubject: null })

    expect(publishWillBeRefused(draft, 'Public')).toBe(true)
    const warning = deploymentWarning(draft, 'Public')
    expect(warning).toContain('Public')
    expect(warning).toContain('Authenticode-signed')
    expect(warning).not.toContain('hash verification alone')
  })

  it('has nothing to warn about for a signed release in either mode', () => {
    expect(deploymentWarning(release(), 'Internal')).toBeNull()
    expect(deploymentWarning(release(), 'Public')).toBeNull()
    expect(publishWillBeRefused(release(), 'Public')).toBe(false)
  })

  /** The badge must say what the model is, and must not call an Internal build a problem. */
  it('describes the trust model accurately', () => {
    const internal = trustModeLabel('Internal')
    expect(internal).toContain('Internal release')
    expect(internal).toContain('SHA-256 verified')
    expect(internal).toContain('not required')
    expect(internal).not.toMatch(/warn|cannot|missing/i)

    expect(trustModeLabel('Public')).toContain('Authenticode signature required')
  })

  /**
   * A published release is, by construction, one the server verified. The console
   * still computes eligibility from status alone -- the server is the gate and has
   * already applied it -- so a Published row with no signer is treated as the
   * server said, not second-guessed. Under Internal that row is not an oddity to
   * tolerate: it is what every publish produces, because no signature is ever
   * read and none is recorded.
   */
  it('treats a published release with no signer as deployable, because Internal publishes exactly that', () => {
    const releases = [release({ version: '1.4.1', signerSubject: null })]

    expect(newestUpdateEligible(releases)?.version).toBe('1.4.1')
  })
})

describe('existing 1.1.x behaviour is unchanged', () => {
  it('still reports 1.1.4 as the deployable release', () => {
    expect(newestUpdateEligible(estateBefore141())?.version).toBe('1.1.4')
  })

  it('still offers 1.1.4 to a device behind it', () => {
    const device: DeviceUpdateCandidate = {
      deviceId: 'd1', hostname: 'MSI', agentVersion: '1.1.1', status: 'Active',
    }

    expect(upgradeTargets(device, estateBefore141()).map((r) => r.version)).toEqual(['1.1.4', '1.1.3'])
  })
})

describe('a device already ahead of every published release', () => {
  /**
   * The controlled device. It runs 1.4.1 while the newest published build is
   * 1.1.4, so it is ahead of the fleet rather than behind it -- and must never
   * be offered a downgrade, whether or not 1.4.1 is registered.
   */
  const controlled: DeviceUpdateCandidate = {
    deviceId: 'd-controlled', hostname: 'OMDEVSINH-TECHS', agentVersion: '1.4.1', status: 'Active',
  }

  it('is offered nothing while 1.4.1 is unregistered', () => {
    expect(upgradeTargets(controlled, estateBefore141())).toHaveLength(0)
    expect(eligibleDevices([controlled], newestUpdateEligible(estateBefore141())!)).toHaveLength(0)
  })

  it('is still offered nothing once 1.4.1 is registered as a draft', () => {
    expect(upgradeTargets(controlled, estateWith141Registered())).toHaveLength(0)
  })

  it('is not counted as behind in a bulk selection targeting 1.1.4', () => {
    const fleet = [
      controlled,
      { deviceId: 'd-msi', hostname: 'MSI', agentVersion: '1.1.1', status: 'Active' },
      { deviceId: 'd-pjcc', hostname: 'DESKTOP-PJCC143', agentVersion: '1.1.2', status: 'Active' },
      { deviceId: 'd-old', hostname: 'AWS-VERIFY-PC', agentVersion: '1.0.0', status: 'Retired' },
    ]

    const chosen = eligibleDevices(fleet, newestUpdateEligible(estateBefore141())!).map((d) => d.hostname)

    expect(chosen).toEqual(['MSI', 'DESKTOP-PJCC143'])
    expect(chosen).not.toContain('OMDEVSINH-TECHS')
    expect(chosen).not.toContain('AWS-VERIFY-PC')
  })
})

describe('downloading a build', () => {
  /**
   * The distinction the whole change rests on. Downloading is an administrator
   * fetching an artifact to install by hand; publishing is the platform pushing it
   * onto machines. A Draft must be downloadable without being deployable.
   */
  it('allows a draft to be downloaded', () => {
    expect(isDownloadable(release({ status: 'Draft' }))).toBe(true)
  })

  it('allows a published release to be downloaded', () => {
    expect(isDownloadable(release({ status: 'Published' }))).toBe(true)
  })

  /** Revoked is withdrawn: nothing may download or install it any more. */
  it('refuses a revoked release', () => {
    expect(isDownloadable(release({ status: 'Revoked' }))).toBe(false)
  })

  /** Downloadable must not mean deployable. */
  it('a downloadable draft is still not a fleet target', () => {
    const draft = release({ version: '1.4.1', status: 'Draft', signerSubject: null })

    expect(isDownloadable(draft)).toBe(true)
    expect(newestUpdateEligible([draft])).toBeNull()
    expect(isEligible(
      { deviceId: 'd', hostname: 'PC', agentVersion: '1.1.4', status: 'Active' }, draft)).toBe(false)
  })

  /**
   * Status is the whole hint. It used to add "and unsigned" for a null signer,
   * which under Internal is true of every build and so tells an operator
   * nothing; where the signature does change an outcome -- an unsigned draft
   * under Public -- deploymentWarning is the mode-aware place that says so.
   */
  it('says a draft is manual-install only, whatever it is signed with', () => {
    expect(downloadHint(release({ status: 'Draft' })))
      .toContain('Not offered to devices until published')
    expect(downloadHint(release({ status: 'Draft', signerSubject: null })))
      .toBe(downloadHint(release({ status: 'Draft' })))
    expect(downloadHint(release({ status: 'Draft', signerSubject: null })))
      .not.toMatch(/unsigned/i)
  })

  it('has nothing to add for a published release', () => {
    expect(downloadHint(release({ status: 'Published' }))).toBeNull()
  })
})

describe('what the operator is told when the server refuses', () => {
  /**
   * The refusal is the useful part. A release declared as 1.7.1 over a 1.7.0
   * package is refused by the server with both versions named; the page must
   * show exactly that, not a generic "upload failed" that sends the operator
   * to check the file extension.
   */
  it('shows the server reason verbatim when there is one', () => {
    const error = new ApiError(
      400,
      'Release upload failed',
      'corr-1',
      'The declared release version does not match the MSI. Declared release: 1.7.1 · MSI ProductVersion: 1.7.0',
    )

    const text = apiFailureMessage(error, 'Upload failed.')

    expect(text).toContain('Declared release: 1.7.1')
    expect(text).toContain('MSI ProductVersion: 1.7.0')
    expect(text).not.toContain('Upload failed.')
  })

  it('shows the duplicate-artifact reason verbatim', () => {
    const error = new ApiError(
      422,
      'Request failed with HTTP 422',
      null,
      'The artifact already belongs to another release version. Release 1.5.0 (Published) already uses this artifact.',
    )

    expect(apiFailureMessage(error, 'The release could not be published.')).toContain('1.5.0 (Published)')
  })

  it('falls back when the server gave no reason', () => {
    expect(apiFailureMessage(new ApiError(500, 'Request failed with HTTP 500', null), 'Fallback.')).toBe('Fallback.')
    expect(apiFailureMessage(new ApiError(502, 'Request failed with HTTP 502', null, '   '), 'Fallback.')).toBe('Fallback.')
  })

  it('falls back for anything that is not an API error', () => {
    expect(apiFailureMessage(new TypeError('Failed to fetch'), 'Fallback.')).toBe('Fallback.')
    expect(apiFailureMessage(undefined, 'Fallback.')).toBe('Fallback.')
  })

  /** The generic HTTP line is never what the operator sees when a real reason exists. */
  it('never surfaces the generic request line in place of a reason', () => {
    const error = new ApiError(422, 'Request to /x failed with HTTP 422', null, 'Named reason.')

    expect(apiFailureMessage(error, 'Fallback.')).toBe('Named reason.')
  })
})

describe('the release-action step-up gate', () => {
  /**
   * Mirrors the recovery-key reveal gate, against the server's own rule
   * (CurrentPassword 1..256, Justification 3..500). Client-side courtesy only:
   * the server verifies and audits independently, so nothing here may be looser
   * than the server -- a form this lets through must never be refused on shape.
   */
  it('accepts a password and a real justification', () => {
    expect(validateStepUpForm({ currentPassword: 'pw', justification: 'rolling out the fix' })).toBe(true)
  })

  it('refuses an empty password', () => {
    expect(validateStepUpForm({ currentPassword: '', justification: 'rolling out the fix' })).toBe(false)
  })

  /** A stray keystroke is not a reason; mirrors the recovery-key reveal gate. */
  it('refuses a justification shorter than three characters after trimming', () => {
    expect(validateStepUpForm({ currentPassword: 'pw', justification: 'ok' })).toBe(false)
    expect(validateStepUpForm({ currentPassword: 'pw', justification: '  a  ' })).toBe(false)
    expect(validateStepUpForm({ currentPassword: 'pw', justification: '      ' })).toBe(false)
  })

  it('accepts exactly three characters of justification, trimmed', () => {
    expect(validateStepUpForm({ currentPassword: 'pw', justification: ' fix ' })).toBe(true)
  })

  /**
   * The server's StringLength caps, mirrored so the form cannot submit a body
   * the server will refuse as malformed. At the cap is valid; one over is not.
   */
  it('holds the server caps: justification 500, password 256', () => {
    expect(validateStepUpForm({ currentPassword: 'pw', justification: 'j'.repeat(500) })).toBe(true)
    expect(validateStepUpForm({ currentPassword: 'pw', justification: 'j'.repeat(501) })).toBe(false)
    expect(validateStepUpForm({ currentPassword: 'p'.repeat(256), justification: 'fix' })).toBe(true)
    expect(validateStepUpForm({ currentPassword: 'p'.repeat(257), justification: 'fix' })).toBe(false)
  })

  /** The password is never trimmed: whitespace can be part of a real password. */
  it('does not trim the password', () => {
    expect(validateStepUpForm({ currentPassword: ' ', justification: 'fix' })).toBe(true)
  })
})
