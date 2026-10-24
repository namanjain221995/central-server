import { ApiError, type AgentReleaseRow } from '../api/client'

/**
 * Which releases a device may actually be moved to, and why not otherwise.
 *
 * The rules live here rather than in the component because they are the whole
 * feature: an agent update runs an installer as SYSTEM on the target machine, so
 * offering an ineligible target is worse than offering none. The server enforces
 * every rule independently -- this exists so the console does not invite a click
 * that will be refused, and so the reason can be shown rather than a bare error.
 */

/** Why a device cannot receive a particular release. */
export type IneligibilityReason =
  | 'retired'
  | 'unpublished'
  | 'revoked'
  | 'not-newer'
  | 'unknown-version'

export interface DeviceUpdateCandidate {
  deviceId: string
  hostname: string
  agentVersion: string
  status: string
}

/**
 * Compares two three-part versions.
 *
 * Mirrors the server's `AgentVersionNumber`. Anything that does not parse as
 * three numeric parts is treated as unknown rather than guessed at: a device
 * whose reported version cannot be read must not be assumed to be behind.
 */
export function compareVersions(left: string, right: string): number | null {
  const parse = (v: string): number[] | null => {
    const core = v.split('+')[0].trim()
    const parts = core.split('.')

    if (parts.length !== 3) return null
    if (!parts.every((p) => /^\d+$/.test(p))) return null

    return parts.map(Number)
  }

  const a = parse(left)
  const b = parse(right)

  if (a === null || b === null) return null

  for (let i = 0; i < 3; i++) {
    if (a[i] !== b[i]) return a[i] - b[i]
  }

  return 0
}

/** Whether `release` is strictly newer than `installed`. */
export function isNewer(release: string, installed: string): boolean {
  const c = compareVersions(release, installed)
  return c !== null && c > 0
}

/**
 * Why this device cannot take this release, or null when it can.
 *
 * Deliberately returns the first blocking reason rather than a list: the
 * operator needs to know what to fix, and the reasons are not independent.
 */
export function ineligibilityReason(
  device: DeviceUpdateCandidate,
  release: AgentReleaseRow,
): IneligibilityReason | null {
  // Checked first: an offboarded machine is not a target whatever its version.
  if (device.status === 'Retired') return 'retired'

  // A revoked release was published once and has since been withdrawn; a draft
  // never was. Distinguished because the operator's next step differs.
  if (release.status === 'Revoked') return 'revoked'
  if (release.status !== 'Published') return 'unpublished'

  if (compareVersions(release.version, device.agentVersion) === null) {
    return 'unknown-version'
  }

  // Covers both same-version reinstalls and downgrades, which the platform
  // does not offer.
  if (!isNewer(release.version, device.agentVersion)) return 'not-newer'

  return null
}

export function isEligible(device: DeviceUpdateCandidate, release: AgentReleaseRow): boolean {
  return ineligibilityReason(device, release) === null
}

export function describeIneligibility(reason: IneligibilityReason): string {
  switch (reason) {
    case 'retired':
      return 'Retired devices cannot be updated'
    case 'unpublished':
      return 'That release is not published'
    case 'revoked':
      return 'That release has been revoked'
    case 'not-newer':
      return 'Already on this version or newer'
    case 'unknown-version':
      return 'The reported agent version could not be read'
  }
}

/**
 * The devices a bulk update would actually touch.
 *
 * The single most important function here. "Update all" must never mean every
 * row in the database: it means every device the operator can see, that is not
 * retired, and that is genuinely behind the chosen release. Anything else is
 * excluded and counted, so the confirmation can say what was left out instead of
 * silently narrowing.
 */
export function eligibleDevices(
  devices: DeviceUpdateCandidate[],
  release: AgentReleaseRow,
): DeviceUpdateCandidate[] {
  return devices.filter((d) => isEligible(d, release))
}

/** Releases this device could move to, newest first. */
export function upgradeTargets(
  device: DeviceUpdateCandidate,
  releases: AgentReleaseRow[],
): AgentReleaseRow[] {
  return releases
    .filter((r) => isEligible(device, r))
    .sort((a, b) => compareVersions(b.version, a.version) ?? 0)
}

/** A short summary of what a bulk action will and will not do. */
export function describeSelection(
  devices: DeviceUpdateCandidate[],
  release: AgentReleaseRow,
): string {
  const eligible = eligibleDevices(devices, release).length
  const skipped = devices.length - eligible

  if (eligible === 0) {
    return `No selected device can take ${release.version}.`
  }

  const plural = eligible === 1 ? 'device' : 'devices'
  const tail = skipped > 0 ? `, ${skipped} skipped as ineligible` : ''

  return `${eligible} ${plural} will be updated to ${release.version}${tail}.`
}

/**
 * Whether this release row declares an Authenticode signer.
 *
 * A fact about the row, not a verdict on the build. A signer is recorded only
 * where a mode verifies one -- the server reads it from the artifact's own
 * signature under the Public trust model -- so under Internal it is null for
 * every release by construction, and that is the model rather than a defect.
 * Nothing here gates on it: see `publishWillBeRefused` in agentReleaseView for
 * the one place the answer changes an outcome.
 */
export function isSigned(release: AgentReleaseRow): boolean {
  return release.signerSubject !== null && release.signerSubject.trim().length > 0
}

/**
 * What the agent will actually verify about this build before installing it.
 *
 * Reported, never enforced here -- and what is reported has to be what happens.
 * The agent always re-computes the SHA-256 over the bytes it downloaded and
 * refuses a mismatch. It checks a signature only when the release declares a
 * signer; told there is none, it says so in its log and installs on hash
 * verification alone. Under Internal -- the default, and what this deployment
 * runs -- no release declares one, so the label this replaced ("the agent will
 * refuse to install this") promised a protection that does not exist.
 *
 * Deliberately mode-blind, and correct in both modes because install-time
 * behaviour follows the release row rather than the server's current mode: the
 * endpoint is handed `info.SignerSubject` from the release metadata, so a
 * release published with a null signer keeps installing on hash alone even
 * after the platform is switched to Public. The mode itself is named on the
 * Agent releases page, which is the page that fetches the policy.
 */
export function signingLabel(release: AgentReleaseRow): string {
  return isSigned(release)
    ? `Signed by ${release.signerSubject}`
    : 'Unsigned — the agent installs it after verifying the SHA-256; no signature is checked'
}

/**
 * Adapts a device list row to the shape the targeting rules work on.
 *
 * Exists so the console has one definition of "which device" rather than two
 * that can drift: the detail page and the estate list both go through here.
 */
export function toCandidate(device: {
  id: string
  hostname: string
  agentVersion: string
  status: string
}): DeviceUpdateCandidate {
  return {
    deviceId: device.id,
    hostname: device.hostname,
    agentVersion: device.agentVersion,
    status: device.status,
  }
}

/** Releases that may be chosen as a bulk target, newest first. */
export function publishedReleases(releases: AgentReleaseRow[]): AgentReleaseRow[] {
  return releases
    .filter((r) => r.status === 'Published')
    .sort((a, b) => compareVersions(b.version, a.version) ?? 0)
}

/** The outcome of queueing one device's update. */
export interface UpdateOutcome {
  hostname: string
  error: string | null
}

/**
 * Whether a refusal is about the operator rather than the device.
 *
 * A 403 is the step-up gate ("That password is not correct.") and a 429 the
 * rate limiter; neither says anything about the device the call named, and both
 * would refuse every remaining device for the same reason. A bulk run must stop
 * on one instead of replaying it down the list -- each wrong-password attempt
 * counts towards the operator's own sign-in lockout.
 */
export function isOperatorRefusal(error: unknown): boolean {
  return error instanceof ApiError && (error.status === 403 || error.status === 429)
}

/**
 * The server's reason for one device's refusal, verbatim when it gave one.
 *
 * The detail is where the actionable part lives -- "Release 1.5.0 is not newer
 * than the installed agent" -- and it never quotes what the operator typed, so
 * it is safe to show as written.
 */
export function refusalText(error: unknown): string {
  if (error instanceof ApiError && error.detail?.trim()) return error.detail
  return error instanceof Error ? error.message : 'Refused'
}

/**
 * What to tell the operator after a bulk run.
 *
 * Failures are named rather than counted away. A partial success is the normal
 * case -- the server re-checks every rule independently and may refuse a device
 * the console thought was eligible -- so it must read as a partial success and
 * not as either a clean pass or a total failure.
 */
export function summariseResults(results: UpdateOutcome[]): string {
  const failed = results.filter((r) => r.error !== null)
  const queued = results.length - failed.length

  if (results.length === 0) return 'Nothing was queued.'

  if (failed.length === 0) {
    return `Update queued on ${queued} device${queued === 1 ? '' : 's'}.`
  }

  if (queued === 0) {
    return `No update was queued. ${failed.length} device${failed.length === 1 ? '' : 's'} refused.`
  }

  return `Update queued on ${queued} of ${results.length} devices; ${failed.length} refused.`
}
