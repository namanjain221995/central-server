import { ApiError, type AgentReleaseRow, type AgentReleaseTrustMode } from '../api/client'
import { compareVersions, isSigned } from './agentUpdateView'

/**
 * What to tell the operator when the server refused a release action.
 *
 * The server's refusal is the useful part -- "Declared release: 1.7.1 · MSI
 * ProductVersion: 1.7.0" names the fix -- and it arrives as the `detail` of a
 * problem response. It is shown as the server wrote it. The fallback is for a
 * failure that carried no reason: a network error, or a status with no body.
 * Nothing here decides that an action succeeded; the caller re-reads the
 * server's state for that.
 */
export function apiFailureMessage(error: unknown, fallback: string): string {
  const detail = error instanceof ApiError ? error.detail?.trim() : undefined
  return detail ? detail : fallback
}

/**
 * Whether a release-action step-up form may be submitted.
 *
 * Mirrors the recovery-key reveal gate, against the server's own rule
 * (CurrentPassword 1..256, Justification 3..500): a step-up password must be
 * present and a justification must be more than a stray keystroke. Client-side
 * only, for immediate feedback -- the server verifies and audits independently,
 * and what it refuses it refuses whatever this said.
 */
export function validateStepUpForm({
  currentPassword,
  justification,
}: {
  currentPassword: string
  justification: string
}): boolean {
  const reason = justification.trim()
  return (
    currentPassword.length >= 1
    && currentPassword.length <= 256
    && reason.length >= 3
    && reason.length <= 500
  )
}

/**
 * Which release is "the latest", asked three different ways.
 *
 * The page used to have one answer -- the first Published row in a list ordered
 * by upload time -- and that answer was wrong twice over. It ordered by when a
 * release was uploaded rather than by its version, so publishing an older build
 * after a newer one would have made the older one "latest". And it collapsed
 * three genuinely different questions into one, so a build that existed but was
 * not published was indistinguishable from one that did not exist at all.
 *
 * The three questions an operator actually has:
 *
 *   - What is the newest build we hold?      (newestUploaded)
 *   - What is the newest build we offer?     (newestPublished)
 *   - What would a fleet update install?     (newestUpdateEligible)
 *
 * They can all differ, and when they do the difference is the useful part.
 */

/** Highest version among the given releases, or null when none has a readable version. */
function highestVersion(releases: AgentReleaseRow[]): AgentReleaseRow | null {
  const readable = releases.filter((r) => compareVersions(r.version, '0.0.0') !== null)

  if (readable.length === 0) return null

  return readable.reduce((best, candidate) =>
    (compareVersions(candidate.version, best.version) ?? 0) > 0 ? candidate : best,
  )
}

/**
 * The newest build the platform holds, whatever its status.
 *
 * Includes drafts. A build that has been uploaded but deliberately not published
 * is exactly the case the old logic could not show, and it is the case that
 * matters when an operator is asking "did the new agent ever get registered?".
 */
export function newestUploaded(releases: AgentReleaseRow[]): AgentReleaseRow | null {
  return highestVersion(releases.filter((r) => r.status !== 'Revoked'))
}

/** The newest build currently offered for download and self-update. */
export function newestPublished(releases: AgentReleaseRow[]): AgentReleaseRow | null {
  return highestVersion(releases.filter((r) => r.status === 'Published'))
}

/**
 * The newest build a device could actually be moved to.
 *
 * Mirrors the server: a release is targetable when it is Published and its
 * version parses. Deliberately <em>not</em> narrowed by signature, because
 * narrowing it here would be a lie -- the platform will queue and install an
 * unsigned published release, and a console that quietly hid that would leave an
 * operator believing a protection exists that does not. The signature is
 * reported instead, by {@link deploymentWarning}.
 */
export function newestUpdateEligible(releases: AgentReleaseRow[]): AgentReleaseRow | null {
  return newestPublished(releases)
}

/** How the three answers relate, for a reader who has not thought about it. */
export type ReleaseGapReason = 'none' | 'unpublished-newer' | 'nothing-published' | 'no-releases'

/**
 * Whether the newest build we hold is the newest build we would deploy.
 *
 * The whole point of the summary. A newer draft sitting above the published one
 * is a normal, deliberate state -- it is where an unsigned build belongs -- but
 * it must be visible rather than inferred from two cards showing two numbers.
 */
export function releaseGap(releases: AgentReleaseRow[]): ReleaseGapReason {
  const uploaded = newestUploaded(releases)
  const eligible = newestUpdateEligible(releases)

  if (uploaded === null) return 'no-releases'
  if (eligible === null) return 'nothing-published'

  return (compareVersions(uploaded.version, eligible.version) ?? 0) > 0
    ? 'unpublished-newer'
    : 'none'
}

export function describeReleaseGap(
  reason: ReleaseGapReason,
  uploaded: AgentReleaseRow | null,
  eligible: AgentReleaseRow | null,
): string {
  switch (reason) {
    case 'no-releases':
      return 'No agent releases have been registered.'
    case 'nothing-published':
      return `${uploaded?.version ?? 'A release'} is registered but nothing is published, so no device can be updated.`
    case 'unpublished-newer':
      return `${uploaded?.version} is registered but not published. Fleet updates still target ${eligible?.version}.`
    case 'none':
      return 'The newest registered release is the one devices will be updated to.'
  }
}

/**
 * One line describing what a published release has been verified to be, under
 * the platform's trust mode. Shown beside the version so nobody has to infer the
 * model from a blank signer column.
 */
export function trustModeLabel(mode: AgentReleaseTrustMode): string {
  return mode === 'Internal'
    ? 'Internal release · SHA-256 verified · Authenticode certificate not required for company deployment'
    : 'Public release · SHA-256 verified · Authenticode signature required'
}

/**
 * What an operator must know before trying to publish this build.
 *
 * Returns null when there is nothing to say. The server is the gate and its
 * answer wins; the console only repeats the rule so a refusal is not a surprise.
 * Under Internal there is nothing to say about signatures at all: they are not
 * read. Under Public an unsigned draft will be refused, and the fix is a signed
 * artifact, not a different click.
 */
export function deploymentWarning(release: AgentReleaseRow, mode: AgentReleaseTrustMode): string | null {
  if (mode === 'Public' && release.status !== 'Published' && !isSigned(release)) {
    return 'This release is unsigned. Under the Public trust model the server will refuse to '
      + 'publish it until its artifact is replaced with a build Authenticode-signed by the '
      + 'configured publisher.'
  }

  return null
}

/**
 * Whether the server will refuse to publish this release as it stands, as far as
 * the console can tell. Internal refuses nothing on grounds the console can see
 * -- integrity is checked server-side over the stored bytes -- so the button is
 * always offered and the server's 422, if any, is shown verbatim.
 */
export function publishWillBeRefused(release: AgentReleaseRow, mode: AgentReleaseTrustMode): boolean {
  return mode === 'Public' && !isSigned(release)
}

/**
 * Whether this release can be downloaded from the console.
 *
 * Draft counts. Downloading is an administrator fetching an artifact to install by
 * hand; publishing is the platform pushing it onto machines by itself. Requiring
 * the second before allowing the first is backwards for an unsigned build, where
 * the safe way to try one is on a single machine you are standing at.
 *
 * Revoked does not count: a revoked release is withdrawn, and nothing may download
 * or install it any more.
 */
export function isDownloadable(release: AgentReleaseRow): boolean {
  return release.status !== 'Revoked'
}

/**
 * What downloading this build actually gets you, said plainly next to the button.
 *
 * A Draft is downloadable but not deployable, and an operator should not have to
 * infer that from a status badge two columns away. Status is the whole answer:
 * the signature used to be mentioned here too, but "and unsigned" is not a
 * qualifier under Internal -- it is true of every release -- and where it does
 * change an outcome, an unsigned draft under Public, {@link deploymentWarning}
 * says so with the mode in hand and names the fix.
 */
export function downloadHint(release: AgentReleaseRow): string | null {
  if (release.status !== 'Draft') return null

  return 'Draft: install manually. Not offered to devices until published.'
}
