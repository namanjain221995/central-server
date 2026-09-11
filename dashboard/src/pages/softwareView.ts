import type {
  DeviceSoftwareItem,
  RunningFilter,
  SoftwareInstallation,
  SoftwareTitle,
} from '../api/client'

export type { RunningFilter } from '../api/client'

/**
 * Pure view logic for the software inventory browser.
 *
 * Kept out of the component so the rules can be tested without a DOM, in the
 * same shape as agentReleaseView and elevationView.
 */

/**
 * Field separator for composite keys: ASCII Unit Separator.
 *
 * A character no application name, version or publisher can contain, so
 * ("A", "B|C") and ("A|B", "C") cannot collapse into one key and hide a title.
 */
const SEPARATOR = String.fromCharCode(31)

/** Marker for an absent field, distinct from a field that is genuinely empty text. */
const ABSENT = String.fromCharCode(0)

/**
 * A stable key for one title.
 *
 * A title is (name, version, publisher) — the same triple the server groups on.
 * An absent version or publisher is part of the identity, not a wildcard, so it
 * gets its own marker rather than colliding with a title whose value is literally
 * empty.
 */
export function titleKey(title: Pick<SoftwareTitle, 'name' | 'version' | 'publisher'>): string {
  const part = (value: string | null) => (value === null ? ABSENT : value)
  return [title.name, part(title.version), part(title.publisher)].join(SEPARATOR)
}

export function isSameTitle(
  a: Pick<SoftwareTitle, 'name' | 'version' | 'publisher'> | null,
  b: Pick<SoftwareTitle, 'name' | 'version' | 'publisher'>,
): boolean {
  return a !== null && titleKey(a) === titleKey(b)
}

/**
 * How an installation reached the machine, in words an operator can act on.
 *
 * A per-user install names its account because that is what makes it removable:
 * uninstalling it for one person leaves it running for everyone else. Agents
 * older than 1.5.0 could not tell, and that is said plainly rather than guessed
 * at — reporting "All users" for an unknown scope would be a claim the platform
 * has not earned.
 */
export function scopeLabel(
  installation: Pick<SoftwareInstallation, 'installationScope' | 'installedForUser'>,
): string {
  if (installation.installationScope === 'User') {
    return installation.installedForUser
      ? `Per-user — ${installation.installedForUser}`
      : 'Per-user'
  }

  if (installation.installationScope === 'Machine') {
    return 'All users'
  }

  return 'Scope not reported'
}

/**
 * What the registry view actually tells us.
 *
 * Deliberately NOT called architecture. The value is the uninstall key the entry
 * was found under, and 64-bit products routinely register under WOW6432Node —
 * Chrome, Edge and Brave all report x86. Presenting that as the binary's
 * architecture would state something the platform has not determined.
 */
export function registryViewLabel(architecture: string | null): string {
  if (architecture === 'x64') return '64-bit registry'
  if (architecture === 'x86') return '32-bit registry'
  return '—'
}

/**
 * One line summarising who has a title, for the detail header.
 *
 * Devices and installations are counted separately because they differ once
 * per-user installs exist: three people with the same product on one machine is
 * one device and three installations, and collapsing the two would either
 * overstate fleet coverage or hide work that still has to be done per user.
 */
export function installationSummary(
  installations: readonly SoftwareInstallation[],
  totalCount: number,
): string {
  if (totalCount === 0) return 'Not installed on any device in scope'

  const devices = new Set(installations.map((i) => i.deviceId)).size
  const deviceText = `${devices} device${devices === 1 ? '' : 's'}`

  if (totalCount === devices) return `Installed on ${deviceText}`

  return `${totalCount} installations across ${deviceText}`
}

/**
 * How many machines have a title, from the count the titles list already
 * carries.
 *
 * `installCount` is a count of distinct devices, not of installations: the
 * server groups the fleet by (name, version, publisher) and counts the devices
 * behind each group, so this may never be worded as a number of copies. It is
 * also the one count that no view filter touches, which is why the header
 * leans on it.
 */
export function installedFootprint(installCount: number): string {
  if (installCount === 0) return 'Not installed on any device in scope'
  return `Installed on ${installCount} device${installCount === 1 ? '' : 's'}`
}

/**
 * The "Installations" line of the software drill-down header.
 *
 * The header describes the title; the table under it describes one view of the
 * title. Those came apart when the running filter moved to the server: the
 * device list is now filtered before it is counted, so its total is the number
 * of installations in that state and nothing else. Reading the header off that
 * total shrank the installed footprint every time an operator chose "Running",
 * and a title installed on ten machines but running on none was announced as
 * "Not installed on any device in scope" — the one sentence that would send
 * someone to close a ticket that is still open.
 *
 * So the footprint comes from the title's own count, which no filter touches,
 * and the filtered number is added as what it actually is. The two are named
 * separately — devices and installations — because they are counts of different
 * things: a title with per-user copies has more of the second than the first,
 * and "2 of 10" across those units would be arithmetic on two different
 * populations.
 */
export function installationsSummary(
  title: Pick<SoftwareTitle, 'installCount'>,
  filter: RunningFilter,
  installations: { items: readonly SoftwareInstallation[]; totalCount: number } | null,
): string {
  if (filter === 'all') {
    // Unfiltered, the loaded list is the better source: it can tell three
    // installations across two devices apart, which a device count cannot.
    // Before it lands, the title's own count is already true.
    return installations === null
      ? installedFootprint(title.installCount)
      : installationSummary(installations.items, installations.totalCount)
  }

  const footprint = installedFootprint(title.installCount)

  // Nothing to qualify: no installations exist, so no filtered count of them
  // says anything the footprint has not already said.
  if (installations === null || title.installCount === 0) return footprint

  return `${footprint} — ${runningStateCount(filter, installations.totalCount)}`
}

/**
 * How many installations the last inventory put in one running state.
 *
 * "Reported" is load-bearing in both directions. Running and not-running are
 * both claims that need evidence, and a row from an agent that sent none is in
 * neither count; saying "were not running" without "reported" would quietly
 * fold those silent rows into the stopped number.
 */
function runningStateCount(filter: Exclude<RunningFilter, 'all'>, count: number): string {
  const state = filter === 'running' ? 'running' : 'not running'
  if (count === 0) return `no installation was reported ${state} at the last inventory`
  return `${count} installation${count === 1 ? ' was' : 's were'} reported ${state} at the last inventory`
}

/**
 * Whether Force Stop can be offered for an installed application.
 *
 * Force Stop resolves an application to its running processes by install path,
 * because a display name cannot be turned into an image name safely — "Microsoft
 * Visual Studio Code" runs as Code.exe. Without a path there is no evidence, so
 * the action is not offered rather than guessed at.
 *
 * The server re-checks this and is the authority; this only avoids showing a
 * control that is certain to report "unavailable".
 */
export function canForceStop(installLocation: string | null): boolean {
  return installLocation !== null && installLocation.trim().length > 0
}

/**
 * What a Force Stop attempt did, in words.
 *
 * Each outcome is distinct on purpose: "not running" and "cannot be resolved"
 * look the same to an operator who is only told it did not work, but only the
 * second means the action will never work for that application.
 */
export function forceStopMessage(application: string, outcome: string): string {
  switch (outcome) {
    case 'Queued':
      // "Asked to stop", not "stopped": the device checks which processes are
      // actually running and terminates them on its next check-in, so claiming
      // completion here would be a claim the console cannot support.
      return `${application} was asked to stop. The device checks which processes are running before stopping them.`
    case 'NotRunning':
      return `${application} is not running on this device.`
    case 'NotInstalled':
      return `${application} is not installed on this device.`
    case 'Unresolvable':
      return `Force Stop is unavailable for ${application}: it reports no usable install location.`
    case 'NotEligible':
      return `${application} could not be stopped: the device is retired or its agent is too old.`
    default:
      return `${application} could not be stopped.`
  }
}

/**
 * The identity of one row on a device's software list.
 *
 * Name, version and user together: one machine legitimately holds the same
 * application once per user, and anything keyed by name alone — a busy flag,
 * an open menu — would light up every duplicate when one of them is acted on.
 */
export function softwareRowKey(
  item: Pick<DeviceSoftwareItem, 'name' | 'version' | 'installedForUser'>,
): string {
  return `${item.name}|${item.version ?? ''}|${item.installedForUser ?? ''}`
}

/** What the last inventory could say about whether an application was running. */
export type RunningState = 'running' | 'stopped' | 'unknown'

/**
 * Whether the application was running when the device last reported.
 *
 * The server's `isRunning` is the answer when it is present: true and false are
 * what the evidence said, null is the server saying the row has no evidence at
 * all — an agent older than 1.9.0 — and that is "unknown", not "stopped". A
 * server that predates the field sends nothing, and then the evidence list it
 * does send is read the same way it would have been on the server.
 */
export function runningState(item: Pick<DeviceSoftwareItem, 'isRunning' | 'evidence'>): RunningState {
  if (item.isRunning === true) return 'running'
  if (item.isRunning === false) return 'stopped'
  if (item.isRunning === null) return 'unknown'

  if (!item.evidence || item.evidence.length === 0) return 'unknown'
  return item.evidence.some((e) => e.source === 'RunningProcess') ? 'running' : 'stopped'
}

/** The three views of a software list, in the order they are offered. */
export const RUNNING_FILTERS: readonly { key: RunningFilter; label: string }[] = [
  { key: 'all', label: 'All' },
  { key: 'running', label: 'Running' },
  { key: 'stopped', label: 'Not running' },
]

/**
 * Whether a row belongs under a running-state filter.
 *
 * A row whose state is unknown appears only under All: "Not running" is a
 * claim, and listing an older agent's silence there would make it one the
 * platform has not earned.
 */
export function matchesRunningFilter(
  item: Pick<DeviceSoftwareItem, 'isRunning' | 'evidence'>,
  filter: RunningFilter,
): boolean {
  if (filter === 'all') return true
  return runningState(item) === filter
}

/**
 * Whether this device's inventory can answer the running-state question at all.
 *
 * Process evidence arrived with agent 1.9.0. An older agent reports what is
 * installed and nothing about what was running, so every one of its rows is
 * unknown and both filtered views are empty for a reason that has nothing to do
 * with the software on the machine.
 *
 * Asked of the whole list rather than the filtered one on purpose: the agent
 * either reports running state or it does not, and a search that happens to
 * select only silent rows is a different fact that must not be described as an
 * old agent.
 */
export function reportsRunningState(
  items: readonly Pick<DeviceSoftwareItem, 'isRunning' | 'evidence'>[],
): boolean {
  return items.some((item) => runningState(item) !== 'unknown')
}

/** Why a device's software table has no rows to show. */
export interface EmptySoftwareListMessage {
  title: string
  detail: string
}

/** What narrowed a device's software list down to nothing. */
export interface EmptySoftwareListInput {
  filter: RunningFilter
  /** Whether a search term is also narrowing the list. */
  searching: boolean
  /** Whether Windows components are being hidden. */
  hidingSystem: boolean
  /** Whether the device reported running state for any application at all. */
  reportsRunningState: boolean
}

/**
 * What to say when every row has been filtered away.
 *
 * A table header over nothing is the worst of the possible answers: it looks
 * like a load that failed, and it leaves the operator to guess which of three
 * controls emptied it. The cause is knowable in each case, so it is said.
 *
 * The old-agent case is checked first and outranks the others because it is the
 * only one the operator cannot act on by changing a control: on such a device
 * "Running" and "Not running" can never match anything, whatever else is typed
 * in the search box. It is worded as an observation about the inventory with
 * the likely cause named, not as a verdict on the agent's version — a 1.9.0
 * agent whose evidence collection found nothing looks identical from here.
 */
export function emptySoftwareListMessage(input: EmptySoftwareListInput): EmptySoftwareListMessage {
  if (input.filter !== 'all' && !input.reportsRunningState) {
    return {
      title: 'This device does not report running state',
      detail:
        'Nothing in its last inventory says which applications were running — agents older than 1.9.0 do not report it — so no application here can appear under Running or Not running. They are all still listed under All.',
    }
  }

  if (input.searching) {
    return {
      title: 'No applications match your search',
      detail: input.filter === 'all'
        ? 'Nothing in this device’s inventory matches the search.'
        : 'Nothing in this device’s inventory matches both the search and this running-state filter.',
    }
  }

  if (input.filter !== 'all') {
    const state = input.filter === 'running' ? 'running' : 'not running'
    return {
      title: 'No applications match this filter',
      detail: `No application on this device was reported ${state} at its last inventory.`,
    }
  }

  if (input.hidingSystem) {
    return {
      title: 'Only Windows components were reported',
      detail:
        'Every entry in this device’s inventory is a framework, inbox app or system component. Turn on “Show Windows components” to see them.',
    }
  }

  return {
    title: 'No applications to show',
    detail: 'Nothing in this device’s inventory matches the current view.',
  }
}

/** One entry in a software row's Actions menu. */
export interface RowAction {
  key: 'force-stop'
  label: string
  enabled: boolean
  /** Why the action is unavailable, when it is; shown on the disabled item. */
  reason: string | null
}

/** The permissions that decide which actions a row offers at all. */
export interface RowActionPermissions {
  /** `task.execute`: Force Stop terminates processes, so it needs that, not a software permission. */
  canExecuteTasks: boolean
}

/**
 * The actions a software row offers, in menu order.
 *
 * An action the operator lacks permission for is not listed — the convention
 * across the console, and the server enforces it regardless. An action the row
 * cannot support is listed but disabled, with the reason, so an operator learns
 * why rather than wondering where the item went.
 */
export function rowActions(
  item: Pick<DeviceSoftwareItem, 'installLocation'>,
  perms: RowActionPermissions,
): RowAction[] {
  const actions: RowAction[] = []

  if (perms.canExecuteTasks) {
    const enabled = canForceStop(item.installLocation)
    actions.push({
      key: 'force-stop',
      label: 'Force Stop',
      enabled,
      reason: enabled ? null : 'No install location was reported for this application',
    })
  }

  return actions
}

/** Devices that can be acted on: a retired device is not a deployment target. */
export function activeInstallations(
  installations: readonly SoftwareInstallation[],
): SoftwareInstallation[] {
  return installations.filter((i) => i.deviceStatus === 'Active')
}

/**
 * The categories the endpoint labels as not-an-application: present on the
 * machine, collected, and hidden from the default view. Kept as the same list
 * the server hides for the fleet-wide titles, so the two views agree.
 */
export const SYSTEM_CATEGORIES: readonly string[] = ['FrameworkOrResource', 'InboxApp', 'Component']

/**
 * Whether a row belongs to the default view.
 *
 * A row with no category came from an agent that predates discovery and could
 * not say; it is shown rather than hidden by a label it never had.
 */
export function isApplicationRow(category: string | null | undefined): boolean {
  return category === null || category === undefined || !SYSTEM_CATEGORIES.includes(category)
}

/**
 * What kind of thing a row is, in words. The raw value is shown for anything
 * the console does not know, rather than nothing: a label a newer agent adds
 * is still information.
 */
export function categoryLabel(category: string | null | undefined): string {
  switch (category) {
    case 'Application':
      return 'Application'
    case 'InboxApp':
      return 'Windows app'
    case 'RuntimeOrSdk':
      return 'Runtime / SDK'
    case 'FrameworkOrResource':
      return 'Framework / resources'
    case 'Component':
      return 'System component'
    case 'Observed':
      return 'Running only'
    case 'Transient':
      return 'Installer / updater'
    case null:
    case undefined:
      return 'Not reported'
    default:
      return category
  }
}

/**
 * How the endpoint knows the application is there.
 *
 * "Observed" is the one that matters to say plainly: nothing installed it, a
 * process is the only witness, and it may be gone at the next inventory. An
 * agent older than 1.9.0 reported only installation records, so an absent
 * value is "Installed" in fact but is labelled as the older agent's silence.
 */
export function confidenceLabel(confidence: string | null | undefined): string {
  switch (confidence) {
    case 'Installed':
      return 'Installed'
    case 'Observed':
      return 'Running only'
    case null:
    case undefined:
      return 'Installed (not stated)'
    default:
      return confidence
  }
}

/** Badge tone for a confidence: an observed row is a warning, an installed one is neutral. */
export function confidenceTone(confidence: string | null | undefined): 'neutral' | 'warn' {
  return confidence === 'Observed' ? 'warn' : 'neutral'
}

/**
 * Which discovery source an evidence entry names, in words an operator can
 * act on. The three installation records are called what they are; the rest
 * are hints and are named as such.
 */
export function evidenceSourceLabel(source: string): string {
  switch (source) {
    case 'WindowsInstaller':
      return 'Windows Installer product'
    case 'UninstallRegistry':
      return 'Uninstall registration'
    case 'PackageRegistration':
      return 'Package registration'
    case 'AppPaths':
      return 'Execution alias'
    case 'StartMenuShortcut':
      return 'Start Menu shortcut'
    case 'ExecutableMetadata':
      return 'Executable metadata'
    case 'RunningProcess':
      return 'Running process'
    default:
      return source
  }
}

/**
 * What a signature status means for the signer shown next to it.
 *
 * Presence, not trust: "Signed" says the file carries a signature naming this
 * subject, or that Windows verified a package as this publisher. It does not
 * say the certificate chains to a trusted root, and the label must not imply
 * that it does.
 */
export function signerLabel(
  signerSubject: string | null | undefined,
  signatureStatus: string | null | undefined,
): string {
  if (signatureStatus === 'Signed' && signerSubject) return commonName(signerSubject)
  if (signatureStatus === 'Signed') return 'Signed'
  if (signatureStatus === 'Unsigned') return 'Unsigned'
  if (signatureStatus === 'Unreadable') return 'Signature unreadable'
  return '—'
}

/** The CN of a subject, or the subject itself when it has none; quoted values are unwrapped. */
export function commonName(subject: string): string {
  const at = subject.search(/CN=/i)
  if (at < 0) return subject

  const rest = subject.slice(at + 3).trimStart()
  if (rest.startsWith('"')) {
    const close = rest.indexOf('"', 1)
    return close < 0 ? rest.slice(1) : rest.slice(1, close)
  }

  const comma = rest.indexOf(',')
  return (comma < 0 ? rest : rest.slice(0, comma)).trim() || subject
}
