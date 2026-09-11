/**
 * Minimal typed client for the Admin API.
 *
 * All requests go through the Vite dev proxy (`/api` -> Admin API), so the
 * browser sees one origin. Authentication headers are added here in Phase 3;
 * nothing in this module ever persists credentials to localStorage - the
 * session token will live in memory / an HttpOnly cookie, not in script-readable
 * storage.
 */

export interface ServiceInfo {
  service: string
  version: string
  environment: string
}

export interface HealthCheckEntry {
  name: string
  status: 'Healthy' | 'Degraded' | 'Unhealthy'
  durationMs: number
}

export interface HealthReport {
  status: 'Healthy' | 'Degraded' | 'Unhealthy'
  totalDurationMs: number
  checks: HealthCheckEntry[]
}

export class ApiError extends Error {
  readonly status: number
  readonly correlationId: string | null
  /**
   * The server's own explanation, when it gave one: the `detail` of an RFC 7807
   * problem response. This is where an actionable refusal lives -- "Declared
   * release: 1.7.1 · MSI ProductVersion: 1.7.0" -- as opposed to `message`,
   * which only says which request failed with which status.
   */
  readonly detail: string | null

  constructor(status: number, message: string, correlationId: string | null, detail: string | null = null) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.correlationId = correlationId
    this.detail = detail
  }
}

/**
 * The `detail` (or failing that, `title`) of a problem response, or null when
 * the body is not one. Never throws: an error path must not produce a second
 * error while explaining the first.
 */
async function problemDetail(response: Response): Promise<string | null> {
  try {
    const type = response.headers.get('Content-Type') ?? ''
    if (!type.includes('json')) return null
    const body = (await response.json()) as { detail?: unknown; title?: unknown }
    if (typeof body.detail === 'string' && body.detail.trim()) return body.detail
    if (typeof body.title === 'string' && body.title.trim()) return body.title
    return null
  } catch {
    return null
  }
}

/**
 * Fired when any API call returns 401 - the session has expired or been
 * revoked. The auth provider listens and returns the user to the login page.
 */
export const sessionExpiredEvent = new EventTarget()

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      Accept: 'application/json',
      // Anti-CSRF: the API refuses cookie-authenticated mutations without this.
      'X-Requested-With': 'XMLHttpRequest',
      // A string body through this helper is always JSON.stringify output. Without
      // this header fetch labels it text/plain, and the API's body binding
      // answers 415 before the endpoint runs -- which is how Force Stop reached
      // production with a button that had never once created a task. Declared
      // here, once, so a caller cannot forget it; a caller's own headers still
      // win, and a non-string body (FormData sets its own boundary) is left alone.
      ...(typeof init?.body === 'string' ? { 'Content-Type': 'application/json' } : {}),
      ...init?.headers,
    },
  })

  if (response.status === 401) {
    sessionExpiredEvent.dispatchEvent(new Event('expired'))
  }

  if (!response.ok) {
    // Surface the correlation id so a user can quote it to an operator, and the
    // server's own reason so a page can show it instead of guessing.
    const correlationId = response.headers.get('X-Correlation-Id')
    throw new ApiError(
      response.status,
      `Request to ${path} failed with HTTP ${response.status}`,
      correlationId,
      await problemDetail(response),
    )
  }

  // A successful mutation may answer 204 with no body. Parsing that as JSON
  // throws on an empty string, turning a success into a spurious failure.
  if (response.status === 204 || response.headers.get('Content-Length') === '0') {
    return undefined as T
  }

  return (await response.json()) as T
}

export interface DeviceListItem {
  id: string
  /** The Windows computer name, as reported by the agent. */
  hostname: string
  /** The administrator's console label, or null when none is set. */
  displayName: string | null
  operatingSystem: string | null
  agentVersion: string
  status: 'Active' | 'Retired'
  lastSeenAt: string | null
  isOnline: boolean
  enrolledAt: string
  /** Newest published agent version for this device's platform, or null. */
  latestAgentVersion: string | null
  /** True when a strictly newer published agent exists for this device. */
  agentUpdateAvailable: boolean
}

export interface DevicePage {
  items: DeviceListItem[]
  totalCount: number
  page: number
  pageSize: number
}

export interface DeviceCounts {
  total: number
  online: number
  offline: number
  retired: number
}

export function getServiceInfo(): Promise<ServiceInfo> {
  return request<ServiceInfo>('/')
}

export function getDevices(
  page: number,
  pageSize: number,
  search: string,
  status?: 'Active' | 'Retired',
): Promise<DevicePage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (search.trim().length > 0) {
    params.set('search', search.trim())
  }
  if (status) {
    params.set('status', status)
  }
  return request<DevicePage>(`/admin/v1/devices?${params}`)
}

export function getDeviceCounts(): Promise<DeviceCounts> {
  return request<DeviceCounts>('/admin/v1/devices/counts')
}

export interface DeviceDisk {
  name: string
  fileSystem: string | null
  sizeBytes: number
  freeBytes: number
}

export interface DeviceHardware {
  serialNumber: string | null
  manufacturer: string | null
  model: string | null
  cpuName: string | null
  cpuPhysicalCores: number | null
  cpuLogicalProcessors: number | null
  totalMemoryBytes: number | null
  disks: DeviceDisk[] | null
  collectedAt: string
}

export interface DeviceNetworkInterface {
  name: string
  macAddress: string | null
  ipAddresses: string[] | null
  isUp: boolean
}

export interface DeviceLocalUser {
  sid: string
  name: string
  fullName: string | null
  description: string | null
  enabled: boolean
  passwordRequired: boolean
  passwordExpires: boolean
  lastLogon: string | null
  isLocalAdministrator: boolean
}

export interface DeviceLocalGroupMember {
  name: string
  sid: string | null
  memberType: string
}

export interface DeviceLocalGroup {
  sid: string
  name: string
  description: string | null
  memberCount: number
  isAdministrators: boolean
  members: DeviceLocalGroupMember[] | null
}

export interface DeviceDetail {
  id: string
  /** The Windows computer name, as reported by the agent. */
  hostname: string
  /** The administrator's console label, or null when none is set. */
  displayName: string | null
  /**
   * Whether the agent has checked in recently enough to be considered
   * reachable. Same definition as the device list — the server computes both,
   * so the UI never invents its own staleness threshold.
   */
  isOnline: boolean
  operatingSystem: string | null
  agentVersion: string
  status: 'Active' | 'Retired'
  lastSeenAt: string | null
  enrolledAt: string
  machineIdentifier: string
  loggedOnUser: string | null
  inventoryCollectedAt: string | null
  inventoryRefreshPending: boolean
  hardware: DeviceHardware | null
  networkInterfaces: DeviceNetworkInterface[]
  localUsers: DeviceLocalUser[]
  localGroups: DeviceLocalGroup[]
  software: DeviceSoftwareItem[]
  securityPosture: SecurityPosture | null
  services: DeviceServiceRow[]
  processes: DeviceProcessRow[]
  windowsUpdate: DeviceUpdateDetail | null
}

export interface DeviceUpdateHistoryRow {
  title: string
  date: string | null
  operation: string
  result: string
}
export interface DeviceUpdateDetail {
  rebootRequired: boolean
  failedUpdateCount: number
  collectedAt: string
  history: DeviceUpdateHistoryRow[]
}

export interface DeviceServiceRow {
  name: string
  displayName: string
  status: string
  startMode: string
}
export interface DeviceProcessRow {
  processId: number
  name: string
  workingSetBytes: number
  executablePath: string | null
  collectedAt: string
}

export interface DeviceTaskItem {
  id: string
  type: string
  status: string
  createdByDisplay: string
  createdAt: string
  deliveredAt: string | null
  completedAt: string | null
  resultMessage: string | null
}

export async function controlService(
  deviceId: string,
  serviceName: string,
  action: 'Start' | 'Stop' | 'Restart',
): Promise<{ taskId: string }> {
  return request<{ taskId: string }>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/actions/control-service`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ serviceName, action }),
    },
  )
}

export function getDeviceTasks(deviceId: string): Promise<DeviceTaskItem[]> {
  return request<DeviceTaskItem[]>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/tasks`)
}

// ---- USB and peripheral control -------------------------------------------

export type UsbPolicy = 'Restricted' | 'ReadOnly' | 'Enabled'

/**
 * The two levels an administrator can actually grant.
 *
 * `Restricted` is deliberately absent: it is the absence of a grant, reached
 * by revoking rather than by granting, and the server rejects it here.
 */
export type UsbGrantablePolicy = Exclude<UsbPolicy, 'Restricted'>

/**
 * What the endpoint is actually doing, as distinct from what was decided.
 *
 * `Pending` and `Drifted` are deliberately different words. Pending means the
 * machine has not reported back yet — usually because it is offline. Drifted
 * means it reported something other than what was asked, which on Windows
 * generally means a local administrator re-enabled the device by hand. Only one
 * of those needs investigating.
 */
export type UsbEnforcementState = 'Enforced' | 'Pending' | 'Drifted' | 'Failed' | 'NotApplicable'

export interface UsbDeviceRow {
  id: string
  instanceId: string
  deviceClass: string
  isStorage: boolean
  vendorId: string | null
  productId: string | null
  /** Null when the device exposes none. Never a placeholder. */
  serialNumber: string | null
  manufacturer: string | null
  product: string | null
  isConnected: boolean
  firstSeenAt: string
  lastSeenAt: string
  disconnectedAt: string | null
  policy: UsbPolicy
  policyExpiresAt: string | null
  enforcementState: UsbEnforcementState
  enforcedAt: string | null
  enforcementError: string | null
  /** The grant currently in force, if any — what a Revoke button acts on. */
  liveRequestId: string | null
}

export interface UsbAccessRequestRow {
  id: string
  deviceId: string
  deviceName: string
  usbDeviceId: string
  instanceId: string
  product: string | null
  status: 'Pending' | 'Approved' | 'Rejected' | 'Expired' | 'Revoked'
  source: 'Administrator' | 'Endpoint'
  justification: string
  requestedAt: string
  decidedByDisplay: string | null
  decidedAt: string | null
  expiresAt: string | null
  decisionNote: string | null
  isLive: boolean
}

export function getDeviceUsbDevices(deviceId: string): Promise<UsbDeviceRow[]> {
  return request<UsbDeviceRow[]>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/usb-devices`)
}

export function getUsbAccessRequests(liveOnly = false, limit = 100): Promise<UsbAccessRequestRow[]> {
  return request<UsbAccessRequestRow[]>(
    `/admin/v1/usb-access-requests?liveOnly=${liveOnly}&limit=${limit}`,
  )
}

/**
 * Grants temporary read-only access to one USB storage device.
 *
 * There is no parameter for the level of access, because there is only one:
 * read-only. Write access is not something this API can express.
 */
export function grantUsbAccess(
  deviceId: string,
  usbDeviceId: string,
  durationMinutes: number,
  justification: string,
  policy: UsbGrantablePolicy = 'ReadOnly',
): Promise<{ requestId: string; expiresAt: string; policy: UsbPolicy }> {
  return request<{ requestId: string; expiresAt: string; policy: UsbPolicy }>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/usb-devices/${encodeURIComponent(usbDeviceId)}/grant`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ durationMinutes, justification, policy }),
    },
  )
}

export function revokeUsbAccess(requestId: string, note?: string): Promise<void> {
  return request<void>(`/admin/v1/usb-access-requests/${encodeURIComponent(requestId)}/revoke`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ note: note ?? null }),
  })
}

/** Re-sends the device's current policy. Repairs a Drifted device; grants nothing. */
export function reapplyUsbPolicy(deviceId: string): Promise<{ taskId: string }> {
  return request<{ taskId: string }>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/usb-devices/reapply`,
    { method: 'POST' },
  )
}

export async function queueDeviceAction(
  deviceId: string,
  action: 'restart' | 'shutdown' | 'lock' | 'signout',
): Promise<{ taskId: string }> {
  // The taskId is the point: creating the task is not the outcome, and the id
  // is what lets the UI follow the task to what actually happened on Windows.
  return request<{ taskId: string }>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/actions/${action}`,
    { method: 'POST' },
  )
}

/**
 * Queues termination of one process, guarded by the image name the
 * administrator saw. If the PID has been reused by a different executable by
 * the time the agent acts, the agent refuses rather than killing a stranger.
 */
export async function terminateProcess(
  deviceId: string,
  processId: number,
  expectedImageName: string,
): Promise<{ taskId: string }> {
  return request<{ taskId: string }>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/actions/terminate-process`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ processId, expectedImageName }),
    },
  )
}

/**
 * Cancels a task that is still Queued. The server refuses once the task has
 * been delivered -- the agent may already be acting on it, and a cancellation
 * that stops nothing would be a lie. Authorization is per task type: you may
 * cancel exactly what you are permitted to queue.
 */
export async function cancelDeviceTask(deviceId: string, taskId: string): Promise<void> {
  await request<void>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/tasks/${encodeURIComponent(taskId)}/cancel`,
    { method: 'POST' },
  )
}

export interface FleetTaskItem {
  id: string
  deviceId: string
  deviceHostname: string
  deviceDisplayName: string | null
  type: string
  status: string
  createdByDisplay: string
  createdAt: string
  deliveredAt: string | null
  completedAt: string | null
  resultMessage: string | null
}

export interface FleetTaskPage {
  items: FleetTaskItem[]
  totalCount: number
  page: number
  pageSize: number
}

export function getRecentTasks(
  page: number,
  pageSize: number,
  status?: string,
): Promise<FleetTaskPage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (status) params.set('status', status)
  return request<FleetTaskPage>(`/admin/v1/tasks?${params}`)
}

/**
 * What kind of software a row is, decided on the endpoint. Null from agents
 * older than 1.9.0. `FrameworkOrResource`, `InboxApp` and `Component` are the
 * categories the default view hides.
 */
export type SoftwareCategory =
  | 'Application'
  | 'InboxApp'
  | 'RuntimeOrSdk'
  | 'FrameworkOrResource'
  | 'Component'
  | 'Observed'
  | 'Transient'

/**
 * How sure the endpoint is that an application is present. `Installed` means an
 * installation record vouches for it; `Observed` means only a running process
 * does — a portable application. Null from agents older than 1.9.0, for which
 * every row was an installation record.
 */
export type SoftwareConfidence = 'Installed' | 'Observed'

/** One reason an endpoint believes an application exists. */
export interface SoftwareEvidence {
  /** WindowsInstaller, UninstallRegistry, PackageRegistration, AppPaths, StartMenuShortcut, ExecutableMetadata or RunningProcess. */
  source: string
  /** What that source called it: a shortcut's name, a file's product name, a process name. */
  name: string | null
  /** What it pointed at: a product code, a package full name, or an executable path. */
  detail: string | null
}

export interface SoftwareTitle {
  name: string
  version: string | null
  publisher: string | null
  installCount: number
  category?: SoftwareCategory | string | null
  confidence?: SoftwareConfidence | string | null
}
export interface SoftwareTitlePage {
  items: SoftwareTitle[]
  totalCount: number
  page: number
  pageSize: number
}
export interface DeviceSoftwareItem {
  name: string
  version: string | null
  publisher: string | null
  installDate: string | null
  /** The registry view the entry was found in, not the binary's architecture. */
  architecture: string | null
  /**
   * `Machine`, `User`, or null when reported by an agent older than 1.5.0 —
   * which could not tell, because it read HKCU as LocalSystem and so saw no
   * per-user software at all.
   */
  installationScope: string | null
  installedForUser: string | null
  productCode: string | null
  /**
   * Where the application was installed. Force Stop resolves an application to
   * its processes by this path, so its absence means the application cannot be
   * stopped — the server is still the authority on that.
   */
  installLocation: string | null
  /** Package, WindowsInstaller, Registered or Executable; null from agents older than 1.9.0. */
  identityKind?: string | null
  /** The identity that survives an update. */
  stableKey?: string | null
  confidence?: SoftwareConfidence | string | null
  category?: SoftwareCategory | string | null
  packageFamilyName?: string | null
  /** The primary executable, when a source named one. Informational; Force Stop acts on installLocation. */
  executablePath?: string | null
  /** The signer the endpoint read: verified by Windows for a package, a claim for a file. */
  signerSubject?: string | null
  /** Signed, Unsigned or Unreadable. */
  signatureStatus?: string | null
  /** Why the endpoint believes the application exists. Empty from older agents. */
  evidence?: SoftwareEvidence[]
  /**
   * Whether the last inventory saw a process of this application running: a
   * `RunningProcess` evidence entry. Null when the row carries no evidence at
   * all, which is an agent older than 1.9.0 saying nothing rather than saying
   * "not running". Absent from a server that predates the field.
   */
  isRunning?: boolean | null
}

/** What Force Stop did on one device. */
export interface ForceStopDeviceOutcome {
  deviceId: string
  hostname: string
  outcome: 'Queued' | 'NotInstalled' | 'NotRunning' | 'Unresolvable' | 'NotEligible'
  processesQueued: number
}

export interface ForceStopResult {
  processesQueued: number
  devices: ForceStopDeviceOutcome[]
}

/**
 * Stops a named installed application on the given devices.
 *
 * Deliberately takes an application name, never a process name or path: the
 * server resolves those from its own inventory, so the browser cannot ask for an
 * arbitrary process to be terminated.
 */
export function forceStopApplication(
  deviceIds: string[], name: string, publisher: string | null,
): Promise<ForceStopResult> {
  return request<ForceStopResult>('/admin/v1/software/force-stop', {
    method: 'POST',
    body: JSON.stringify({ deviceIds, name, publisher }),
  })
}


/**
 * Which installations to list by running state, as the last inventory saw it:
 * `all` (the default), `running`, or `stopped` — which means the row has
 * evidence and none of it is a running process, so rows from agents too old
 * to report evidence are in neither `running` nor `stopped`.
 */
export type RunningFilter = 'all' | 'running' | 'stopped'

/**
 * Which titles to list: `applications` (the default) hides frameworks, inbox
 * apps and components; `all` shows everything the endpoints reported.
 */
export type SoftwareTitleView = 'applications' | 'all'

export function getSoftwareTitles(
  page: number,
  pageSize: number,
  search: string,
  publisher: string,
  view: SoftwareTitleView = 'applications',
): Promise<SoftwareTitlePage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (search.trim()) params.set('search', search.trim())
  if (publisher) params.set('publisher', publisher)
  if (view !== 'applications') params.set('view', view)
  return request<SoftwareTitlePage>(`/admin/v1/software?${params}`)
}
export function getSoftwarePublishers(): Promise<string[]> {
  return request<string[]>('/admin/v1/software/publishers')
}

/** How a deployment's targets were chosen. */
export type DeploymentTargetType = 'Devices' | 'Groups' | 'Mixed'

/**
 * What deploying would do, before anything is created.
 *
 * The counts are the server's own resolution and eligibility decision — the same
 * code the real deployment runs — so the dialog cannot promise something the
 * deployment then declines to do.
 */
export interface DeploymentPlan {
  packageId: string
  packageName: string
  packageVersion: string
  targeted: number
  needsInstall: number
  alreadyInstalled: number
  newerInstalled: number
  retired: number
  notComparable: number
}

export interface DeploymentTally {
  total: number
  pending: number
  installing: number
  succeeded: number
  failed: number
  expired: number
  skipped: number
  /** Queued, but the device has been silent long enough to be waiting on it. */
  offline: number
  cancelled: number
}

export interface DeploymentSummary {
  id: string
  packageId: string
  packageName: string
  packageVersion: string
  targetType: DeploymentTargetType
  createdByDisplay: string
  createdAt: string
  tally: DeploymentTally
}

export interface DeploymentSummaryPage {
  items: DeploymentSummary[]
  totalCount: number
  page: number
  pageSize: number
}

/** One device's outcome. `status` is derived from the task, never stored. */
export interface DeploymentDeviceResult {
  deviceId: string
  hostname: string
  displayName: string | null
  deviceStatus: string
  lastSeenAt: string | null
  status: string
  reason: string
  observedVersion: string | null
  taskId: string | null
  resultMessage: string | null
  completedAt: string | null
  /** 1 for the original deployment, 2 for the first retry, and so on. */
  attempt: number
}

export interface DeploymentDetail {
  id: string
  packageId: string
  packageName: string
  packageVersion: string
  targetType: DeploymentTargetType
  createdByDisplay: string
  createdAt: string
  tally: DeploymentTally
  targets: DeploymentDeviceResult[]
}

export interface DeploymentCreated {
  deploymentId: string
  targeted: number
  queued: number
  skipped: number
}

/**
 * Both targeting lists go in one request.
 *
 * Deliberately bulk: a request per device would be 350 round trips for one
 * operator action, and would leave no single record of what was intended. The
 * server resolves and authorizes every id.
 */
export function previewDeployment(
  packageId: string, deviceIds: string[], groupIds: string[],
): Promise<DeploymentPlan> {
  return request<DeploymentPlan>('/admin/v1/deployments/preview', {
    method: 'POST',
    body: JSON.stringify({ packageId, deviceIds, groupIds }),
  })
}

export function createDeployment(
  packageId: string, deviceIds: string[], groupIds: string[],
): Promise<DeploymentCreated> {
  return request<DeploymentCreated>('/admin/v1/deployments', {
    method: 'POST',
    body: JSON.stringify({ packageId, deviceIds, groupIds }),
  })
}

export function getDeployments(page: number, pageSize: number): Promise<DeploymentSummaryPage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  return request<DeploymentSummaryPage>(`/admin/v1/deployments?${params}`)
}

export function getDeployment(deploymentId: string): Promise<DeploymentDetail> {
  return request<DeploymentDetail>(`/admin/v1/deployments/${deploymentId}`)
}

/**
 * Re-runs the devices that did not succeed, as a new attempt.
 *
 * The server re-decides everything — authorization, package lifecycle, retired
 * state, eligibility — so a device that has since become compliant or been
 * retired is not sent an install because it failed earlier.
 */
export function retryDeployment(deploymentId: string): Promise<DeploymentCreated> {
  return request<DeploymentCreated>(`/admin/v1/deployments/${deploymentId}/retry`, { method: 'POST' })
}

/**
 * Cancels work that has not reached an agent. A delivered install is running on
 * a Windows machine and is deliberately left alone.
 */
export function cancelDeployment(
  deploymentId: string,
): Promise<{ deploymentId: string; considered: number; cancelled: number }> {
  return request<{ deploymentId: string; considered: number; cancelled: number }>(
    `/admin/v1/deployments/${deploymentId}/cancel`, { method: 'POST' })
}

/**
 * One installation of a title on one device.
 *
 * `deviceId` is the identity: hostnames repeat across a fleet and change, so
 * nothing addresses a device by one. `installedForUser` is set only for a
 * per-user install, so a device can legitimately appear more than once here --
 * that is two real installations, not a duplicate row.
 */
export interface SoftwareInstallation {
  deviceId: string
  hostname: string
  displayName: string | null
  deviceStatus: string
  lastSeenAt: string | null
  installationScope: string | null
  installedForUser: string | null
  architecture: string | null
  installLocation: string | null
  productCode: string | null
  collectedAt: string
  identityKind?: string | null
  stableKey?: string | null
  confidence?: SoftwareConfidence | string | null
  category?: SoftwareCategory | string | null
  packageFamilyName?: string | null
  executablePath?: string | null
  signerSubject?: string | null
  signatureStatus?: string | null
  /** Whether that device's last inventory saw it running; null when the row has no evidence at all. */
  isRunning?: boolean | null
}

export interface SoftwareInstallationPage {
  items: SoftwareInstallation[]
  totalCount: number
  page: number
  pageSize: number
}

/**
 * The devices one title is installed on.
 *
 * Version and publisher are part of the title's identity, including when absent:
 * a title with no recorded version is a distinct title, not a wildcard, so the
 * absent case is sent explicitly rather than omitted.
 *
 * `running` is the opposite: `all` is the absence of a filter, so it is omitted
 * rather than sent as a literal the server would have to know.
 */
export function getSoftwareInstallations(
  name: string,
  version: string | null,
  publisher: string | null,
  page: number,
  pageSize: number,
  running: RunningFilter = 'all',
): Promise<SoftwareInstallationPage> {
  const params = new URLSearchParams({ name, page: String(page), pageSize: String(pageSize) })
  if (version !== null) params.set('version', version)
  if (publisher !== null) params.set('publisher', publisher)
  if (running !== 'all') params.set('running', running)
  return request<SoftwareInstallationPage>(`/admin/v1/software/installations?${params}`)
}

export interface SecurityPosture {
  defenderAntivirusEnabled: boolean | null
  defenderRealtimeProtectionEnabled: boolean | null
  defenderSignatureAgeDays: number | null
  firewallDomainEnabled: boolean | null
  firewallPrivateEnabled: boolean | null
  firewallPublicEnabled: boolean | null
  secureBootEnabled: boolean | null
  tpmPresent: boolean | null
  tpmEnabled: boolean | null
  tpmSpecVersion: string | null
  bitLockerSystemDriveStatus: string | null
  localAdministratorCount: number | null
  collectedAt: string
  complianceScore: number | null
}
export interface DeviceSecuritySummary {
  deviceId: string
  hostname: string
  complianceScore: number | null
  defenderEnabled: boolean | null
  firewallEnabled: boolean | null
  secureBootEnabled: boolean | null
  tpmEnabled: boolean | null
  bitLockerSystemDriveStatus: string | null
  localAdministratorCount: number | null
  collectedAt: string
}
export interface SecurityOverview {
  summary: {
    devicesReporting: number
    averageScore: number | null
    healthy: number
    needsAttention: number
    critical: number
  }
  devices: DeviceSecuritySummary[]
}
export function getSecurityOverview(): Promise<SecurityOverview> {
  return request<SecurityOverview>('/admin/v1/security/overview')
}

export interface PolicyRow {
  id: string
  type: string
  name: string
  description: string
  isEnabled: boolean
  currentVersionNumber: number
  compliant: number
  nonCompliant: number
  unknown: number
}
export interface PolicyComplianceRow {
  deviceId: string
  hostname: string
  state: string
  policyVersionNumber: number
  evaluatedAt: string
  deviations: string[] | null
}
export function getPolicies(): Promise<PolicyRow[]> {
  return request<PolicyRow[]>('/admin/v1/policies')
}
export function getPolicyCompliance(policyId: string): Promise<PolicyComplianceRow[]> {
  return request<PolicyComplianceRow[]>(`/admin/v1/policies/${encodeURIComponent(policyId)}/compliance`)
}
export async function createScreenLockPolicy(name: string, description: string, maxTimeoutSeconds: number): Promise<void> {
  const r = await fetch('/api/admin/v1/policies', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ type: 'ScreenLockTimeout', name, description, maxTimeoutSeconds }),
  })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Create policy failed', r.headers.get('X-Correlation-Id'))
}
export async function assignPolicy(policyId: string, deviceId: string): Promise<void> {
  const r = await fetch(`/api/admin/v1/policies/${encodeURIComponent(policyId)}/assign`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ deviceId }),
  })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Assign policy failed', r.headers.get('X-Correlation-Id'))
}

export interface GroupRow { id: string; name: string; description: string; type: string; memberCount: number }
export interface GroupMember { id: string; hostname: string; status: string }
export function getGroups(): Promise<GroupRow[]> { return request<GroupRow[]>('/admin/v1/groups') }
export function getGroupMembers(groupId: string): Promise<GroupMember[]> {
  return request<GroupMember[]>(`/admin/v1/groups/${encodeURIComponent(groupId)}/members`)
}
export async function createGroup(name: string, description: string): Promise<void> {
  const r = await fetch('/api/admin/v1/groups', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' }, body: JSON.stringify({ name, description }) })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Create group failed', r.headers.get('X-Correlation-Id'))
}
export async function addGroupMember(groupId: string, deviceId: string): Promise<void> {
  const r = await fetch(`/api/admin/v1/groups/${encodeURIComponent(groupId)}/members`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' }, body: JSON.stringify({ deviceId }) })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Add member failed', r.headers.get('X-Correlation-Id'))
}

export function getDevice(deviceId: string): Promise<DeviceDetail> {
  return request<DeviceDetail>(`/admin/v1/devices/${encodeURIComponent(deviceId)}`)
}

// ---------------------------------------------------------------------------
// Drivers and BitLocker (Milestone 13).
//
// Both are read-only. There is deliberately no client function here that
// installs a driver, encrypts, decrypts, suspends or resumes anything -- those
// capabilities either do not exist yet or are not exposed to this console.
//
// No recovery key appears in any of these types. The agent never reads one, the
// API never returns one, and there is therefore no field to redact.
// ---------------------------------------------------------------------------

/** Healthy | Problem | Disabled | Unknown. Unknown is never Healthy. */
export type DriverHealthState = 'Healthy' | 'Problem' | 'Disabled' | 'Unknown'

/** What a problem is attributable to. */
export type DriverFaultKind = 'None' | 'Driver' | 'Device' | 'Indeterminate'

export interface DriverRow {
  instanceId: string
  deviceName: string
  deviceClass: string | null
  manufacturer: string | null
  driverProvider: string | null
  driverVersion: string | null
  driverDate: string | null
  infName: string | null
  /** Raw Windows problem code; null when the endpoint could not read it. */
  problemCode: number | null
  health: DriverHealthState
  faultKind: DriverFaultKind
  problemDescription: string
  /** Three-valued: null means the signature could not be verified either way. */
  isSigned: boolean | null
  collectedAt: string
}

export interface DriverFault {
  instanceId: string
  deviceName: string
  deviceClass: string | null
  problemCode: number | null
  faultKind: DriverFaultKind
  problemDescription: string
}

export interface DriverHealthSummary {
  deviceId: string
  hostname: string
  displayName: string | null
  state: DriverHealthState
  /** Null when nothing has been reported -- the evidence that the verdict is absent too. */
  lastReportedAt: string | null
  driverFaultCount: number
  deviceFaultCount: number
  indeterminateFaultCount: number
  /** Counted, never a fault: this platform disables devices itself. */
  disabledCount: number
  unknownCount: number
  totalCount: number
  faults: DriverFault[]
  limitation: string
}

export async function getDeviceDrivers(deviceId: string, problemsOnly = false): Promise<DriverRow[]> {
  const query = problemsOnly ? '?problemsOnly=true' : ''
  return request<DriverRow[]>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/drivers${query}`)
}

export async function getDeviceDriverHealth(deviceId: string): Promise<DriverHealthSummary> {
  return request<DriverHealthSummary>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/driver-health`)
}

/** Whether the endpoint could answer BitLocker questions at all. */
export type BitLockerAvailability = 'Unknown' | 'Available' | 'AccessDenied' | 'NotAvailable' | 'Error'

export type BitLockerVolumeState =
  | 'Unknown' | 'NotEncrypted' | 'EncryptionInProgress'
  | 'DecryptionInProgress' | 'Protected' | 'Suspended'

export type BitLockerReadiness =
  | 'Unknown' | 'Protected' | 'EncryptionInProgress' | 'Suspended'
  | 'ReadyToEncrypt' | 'TpmNotReady' | 'NotEncrypted' | 'NotSupported'

export interface BitLockerVolumeRow {
  deviceIdentifier: string
  driveLetter: string | null
  persistentVolumeId: string | null
  volumeType: number | null
  conversionStatus: number | null
  protectionStatus: number | null
  state: BitLockerVolumeState
  encryptionPercentage: number | null
  encryptionMethod: number | null
  /** Presence only. The password behind the protector is never read or returned. */
  hasRecoveryPasswordProtector: boolean | null
  /** Protector GUIDs. Identifiers, not secrets: one unlocks nothing. */
  recoveryProtectorIds: string[]
  collectedAt: string
}

export interface BitLockerReadinessVolume {
  deviceIdentifier: string
  driveLetter: string | null
  isOperatingSystemVolume: boolean
  state: BitLockerVolumeState
  hasRecoveryPasswordProtector: boolean | null
}

export interface BitLockerReadinessSummary {
  deviceId: string
  hostname: string
  displayName: string | null
  readiness: BitLockerReadiness
  availability: BitLockerAvailability
  lastReportedAt: string | null
  tpmPresent: boolean | null
  tpmEnabled: boolean | null
  tpmSpecVersion: string | null
  /** The long-standing single-field posture summary, unchanged in meaning. */
  systemDriveStatus: string | null
  protectedVolumeCount: number
  unprotectedVolumeCount: number
  unknownVolumeCount: number
  totalVolumeCount: number
  volumes: BitLockerReadinessVolume[]
  limitation: string
}

export async function getDeviceBitLockerVolumes(deviceId: string): Promise<BitLockerVolumeRow[]> {
  return request<BitLockerVolumeRow[]>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/bitlocker-volumes`)
}

export async function getDeviceBitLockerReadiness(deviceId: string): Promise<BitLockerReadinessSummary> {
  return request<BitLockerReadinessSummary>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/bitlocker-readiness`)
}

// ---------------------------------------------------------------------------
// BitLocker recovery-key escrow.
//
// The list type has no field for a key, plaintext or sealed -- the API returns
// none, and there is deliberately nothing here to hold one. Only revealEscrow
// ever carries key material, only in its response, and only in memory.
// ---------------------------------------------------------------------------

export interface EscrowRow {
  id: string
  volumeDeviceIdentifier: string
  keyProtectorId: string
  driveLetter: string | null
  isActive: boolean

  /**
   * Which mechanism filed this key.
   *
   * `Manual` was typed by a named administrator who vouched for it; `Automatic`
   * was collected and sealed by the endpoint itself. They carry different trust
   * and different controls, so the console must never render one as the other.
   */
  origin: 'Manual' | 'Automatic'

  escrowedAt: string
  escrowedBy: string
  supersededAt: string | null
  revealedCount: number
  lastRevealedAt: string | null
}

export async function getBitLockerEscrows(deviceId: string): Promise<EscrowRow[]> {
  return request<EscrowRow[]>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/bitlocker-escrows`)
}

export async function escrowRecoveryKey(
  deviceId: string,
  volumeDeviceIdentifier: string,
  keyProtectorId: string,
  recoveryPassword: string,
): Promise<{ id: string }> {
  return request<{ id: string }>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/bitlocker-escrows`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ volumeDeviceIdentifier, keyProtectorId, recoveryPassword }),
    },
  )
}

export interface RevealedKey {
  escrowId: string
  keyProtectorId: string
  driveLetter: string | null
  recoveryPassword: string
}

/**
 * Reveals an escrowed key. POST, never GET: a GET would put the operation in
 * browser history, proxy logs and Referer headers, and cannot carry the step-up
 * password this requires.
 *
 * The returned password is held in component state and nowhere else -- never in
 * localStorage, sessionStorage, the URL or router state.
 */
export async function revealRecoveryKey(
  escrowId: string,
  currentPassword: string,
  justification: string,
): Promise<RevealedKey> {
  const response = await fetch(
    `/api/admin/v1/bitlocker-escrows/${encodeURIComponent(escrowId)}/reveal`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
      body: JSON.stringify({ currentPassword, justification }),
    },
  )

  if (!response.ok) {
    // Surfaces the server's reason -- rate limited, wrong password, deleted --
    // which the shared request helper would flatten into a status code.
    const problem = await response.json().catch(() => null)
    throw new ApiError(
      response.status,
      problem?.title ?? problem?.detail ?? `Reveal failed with HTTP ${response.status}`,
      response.headers.get('X-Correlation-Id'),
    )
  }

  return (await response.json()) as RevealedKey
}

export async function deleteRecoveryKeyEscrow(escrowId: string): Promise<void> {
  await request<void>(`/admin/v1/bitlocker-escrows/${encodeURIComponent(escrowId)}`, {
    method: 'DELETE',
  })
}

export async function requestInventoryRefresh(deviceId: string): Promise<void> {
  const response = await fetch(`/api/admin/v1/devices/${encodeURIComponent(deviceId)}/refresh-inventory`, {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
  })
  if (response.status === 401) {
    sessionExpiredEvent.dispatchEvent(new Event('expired'))
  }
  if (!response.ok) {
    throw new ApiError(response.status, 'Inventory refresh request failed', response.headers.get('X-Correlation-Id'))
  }
}

// ---------------------------------------------------------------- auth

export interface CurrentUser {
  userId: string
  email: string
  displayName: string
  permissions: string[]
}

export async function login(email: string, password: string): Promise<CurrentUser> {
  const response = await fetch('/api/admin/v1/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ email, password }),
  })

  if (!response.ok) {
    throw new ApiError(response.status, 'Sign-in failed', response.headers.get('X-Correlation-Id'))
  }

  // The HttpOnly session cookie was set by this response; the sessionToken field
  // in the body exists for non-browser clients and is deliberately not stored.
  const body = (await response.json()) as {
    userId: string
    email: string
    displayName: string
    permissions: string[]
  }

  return {
    userId: body.userId,
    email: body.email,
    displayName: body.displayName,
    permissions: body.permissions,
  }
}

export async function logout(): Promise<void> {
  await fetch('/api/admin/v1/auth/logout', {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
  })
}

export function getCurrentUser(): Promise<CurrentUser> {
  return request<CurrentUser>('/admin/v1/auth/me')
}

export function getReadiness(): Promise<HealthReport> {
  // /health/ready returns 503 when unhealthy, which `request` would treat as an
  // error; a health page wants the body either way, so fetch it directly.
  return fetch('/api/health/ready', { headers: { Accept: 'application/json' } }).then(
    async (response) => (await response.json()) as HealthReport,
  )
}

export interface DeviceUpdateSummaryRow {
  deviceId: string
  hostname: string
  rebootRequired: boolean
  failedUpdateCount: number
  collectedAt: string
}
export interface UpdateOverview {
  summary: {
    devicesReporting: number
    rebootPending: number
    withFailedUpdates: number
  }
  devices: DeviceUpdateSummaryRow[]
}
export function getUpdateOverview(): Promise<UpdateOverview> {
  return request<UpdateOverview>('/admin/v1/updates/overview')
}

export interface PackageRow {
  id: string
  name: string
  version: string
  publisher: string | null
  type: string
  sha256: string
  fileName: string
  sizeBytes: number
  msiProductCode: string
  requiredSignerSubject: string | null
  isWithdrawn: boolean
  createdByDisplay: string
  createdAt: string
}

export function getPackages(): Promise<PackageRow[]> {
  return request<PackageRow[]>('/admin/v1/packages/')
}

/** SHA-256 of a file as lowercase hex, computed in the browser. */
export async function sha256Hex(file: File): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('')
}

export interface NewPackageMeta {
  name: string
  version: string
  publisher?: string
  msiProductCode: string
  requiredSignerSubject?: string
}

export async function uploadPackage(file: File, meta: NewPackageMeta): Promise<void> {
  const sha256 = await sha256Hex(file)
  const form = new FormData()
  form.append('file', file, file.name)
  form.append('name', meta.name)
  form.append('version', meta.version)
  if (meta.publisher) form.append('publisher', meta.publisher)
  form.append('sha256', sha256)
  form.append('msiProductCode', meta.msiProductCode)
  if (meta.requiredSignerSubject) form.append('requiredSignerSubject', meta.requiredSignerSubject)

  const r = await fetch('/api/admin/v1/packages/', {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
    body: form,
  })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Package upload failed', r.headers.get('X-Correlation-Id'))
}

/**
 * Disable a package: it stops being deployable.
 *
 * Catalogue availability only. Nothing is uninstalled, and devices that already
 * have this package are unaffected.
 */
export async function withdrawPackage(packageId: string): Promise<void> {
  await request(`/admin/v1/packages/${encodeURIComponent(packageId)}/withdraw`, { method: 'POST' })
}

/** Enable a previously disabled package, making it deployable again. */
export async function restorePackage(packageId: string): Promise<void> {
  await request(`/admin/v1/packages/${encodeURIComponent(packageId)}/restore`, { method: 'POST' })
}

export async function deployPackageToDevice(packageId: string, deviceId: string): Promise<void> {
  await request(`/admin/v1/packages/${encodeURIComponent(packageId)}/deploy`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ deviceId }),
  })
}

export async function offboardDevice(deviceId: string): Promise<void> {
  await request(`/admin/v1/devices/${encodeURIComponent(deviceId)}/offboard`, { method: 'POST' })
}

export async function reactivateDevice(deviceId: string): Promise<void> {
  await request(`/admin/v1/devices/${encodeURIComponent(deviceId)}/reactivate`, { method: 'POST' })
}

export interface FleetReport {
  devices: { total: number; online: number; offline: number; retired: number }
  security: { devicesReporting: number; averageScore: number | null; needsAttention: number; critical: number }
  updates: { devicesReporting: number; rebootPending: number; withFailedUpdates: number }
  policies: { enabledPolicies: number; nonCompliantResults: number }
  tasks: { queued: number; delivered: number; succeeded: number; failed: number; expired: number; cancelled: number }
  activePackages: number
}

export function getFleetReport(): Promise<FleetReport> {
  return request<FleetReport>('/admin/v1/reports/summary')
}

// ---- Windows local account management (Device -> Users / Groups) ----

/**
 * One temporary local-administrator elevation, as the console reads it.
 *
 * `state` is the platform's authorization. Whether Windows has actually applied
 * it is answered separately by the account's own `isLocalAdministrator`, because
 * the two can disagree -- see `describeEnforcement`.
 */
export type ElevationState =
  | 'Requested'
  | 'Approved'
  | 'Active'
  | 'Rejected'
  | 'Expired'
  | 'Revoked'
  | 'Failed'

export interface ElevationRow {
  id: string
  deviceId: string
  /** The identity a decision was recorded against; a username can be renamed. */
  targetSid: string
  targetUsername: string
  state: ElevationState
  /** Computed server-side from the clock, so a row the sweeper has not reached still reads correctly. */
  isLive: boolean
  justification: string
  requestedAt: string
  requestedBy: string
  approvedAt: string | null
  approvedBy: string | null
  activatedAt: string | null
  expiresAt: string | null
  revokedAt: string | null
  decisionNote: string | null
  failureReason: string | null
}

export function getElevations(deviceId: string): Promise<ElevationRow[]> {
  return request<ElevationRow[]>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/elevations`)
}

/**
 * Requests an elevation, approving it in the same act when a duration is given.
 *
 * Omitting the duration leaves the request pending. The two states stay distinct
 * server-side, so a genuine second-person approval can be introduced later
 * without changing this call.
 */
export function requestElevation(
  deviceId: string,
  targetSid: string,
  justification: string,
  durationMinutes: number | null,
): Promise<{ id: string; state: ElevationState; expiresAt: string | null }> {
  return request(`/admin/v1/devices/${encodeURIComponent(deviceId)}/elevations`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ targetSid, justification, durationMinutes }),
  })
}

export function approveElevation(
  elevationId: string,
  durationMinutes: number,
): Promise<{ id: string; state: ElevationState; expiresAt: string | null }> {
  return request(`/admin/v1/elevations/${encodeURIComponent(elevationId)}/approve`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ durationMinutes }),
  })
}

export function revokeElevation(elevationId: string, note: string | null): Promise<void> {
  return request(`/admin/v1/elevations/${encodeURIComponent(elevationId)}/revoke`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ note }),
  })
}

export interface LocalUserRow {
  sid: string
  name: string
  fullName: string | null
  description: string | null
  enabled: boolean
  passwordRequired: boolean
  passwordExpires: boolean
  lastLogon: string | null
  isLocalAdministrator: boolean
  collectedAt: string
}

export interface LocalGroupRow {
  sid: string
  name: string
  description: string | null
  memberCount: number
  isAdministrators: boolean
  members: { name: string; sid: string | null; memberType: string }[] | null
  collectedAt: string
}

/**
 * Whether this endpoint's interactive accounts are standard users.
 *
 * Derived by the server from the accounts the endpoint last reported, so this is
 * a view, not a control — reading it changes nothing on the machine.
 */
export type LocalAdminCompliance = 'Compliant' | 'NonCompliant' | 'Unknown'

export interface LocalAdminFinding {
  sid: string
  username: string
  enabled: boolean
  isAdministrator: boolean
  /** Why this account was discounted, or null when it counts towards the verdict. */
  excludedReason: string | null
  countsAgainstCompliance: boolean
}

export interface LocalAdminPosture {
  deviceId: string
  hostname: string
  displayName: string | null
  compliance: LocalAdminCompliance
  /** Null when nothing has been reported — which is what Unknown means. */
  lastReportedAt: string | null
  interactiveAdministrators: { sid: string; username: string; enabled: boolean }[]
  findings: LocalAdminFinding[]
  limitation: string
}

export function getLocalAdminPosture(deviceId: string): Promise<LocalAdminPosture> {
  return request<LocalAdminPosture>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-admin-posture`,
  )
}

/**
 * Changes the signed-in administrator's own password.
 *
 * Does not use the shared `request` helper, for one reason: that helper
 * deliberately discards the response body on failure, and here the server's
 * message IS the useful part -- "must be at least 12 characters" is what the
 * person needs to read, and a generic "HTTP 400" is not.
 *
 * The password is sent in the request body over HTTPS and held nowhere else. It
 * is never placed in a URL, in storage, or in a log line: a body is not recorded
 * by proxies or browser history, whereas a query string is.
 *
 * On success the caller's session is already dead -- the server rotates the
 * security stamp, which invalidates every session including this one -- so the
 * caller must return the user to the sign-in screen.
 */
export async function changeAdminPassword(input: {
  currentPassword: string
  newPassword: string
  confirmPassword: string
}): Promise<{ changed: boolean; sessionsRevoked: number; message: string }> {
  const response = await fetch('/api/admin/v1/auth/change-password', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Accept: 'application/json',
      // Anti-CSRF, same gate as every other cookie-authenticated mutation.
      'X-Requested-With': 'XMLHttpRequest',
    },
    body: JSON.stringify(input),
  })

  if (response.ok) {
    return (await response.json()) as { changed: boolean; sessionsRevoked: number; message: string }
  }

  // A ProblemDetails body carries the reason. Fall back to a generic message if
  // the response is not the shape we expect, rather than showing "undefined".
  let detail: string | null = null
  try {
    const body = (await response.json()) as { detail?: string; title?: string }
    detail = body.detail ?? body.title ?? null
  } catch {
    detail = null
  }

  throw new ApiError(
    response.status,
    detail ?? 'The password could not be changed.',
    response.headers.get('X-Correlation-Id'),
  )
}

export function getLocalUsers(deviceId: string): Promise<LocalUserRow[]> {
  return request<LocalUserRow[]>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users`)
}

export function getLocalGroups(deviceId: string): Promise<LocalGroupRow[]> {
  return request<LocalGroupRow[]>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/local-groups`)
}

/** Every mutation returns the queued task id so the UI can follow it to completion. */
export interface QueuedTask { taskId: string; status: string }

async function localAccountAction<T = QueuedTask>(
  path: string,
  method: 'POST' | 'DELETE',
  body?: unknown,
): Promise<T> {
  const response = await fetch(`/api${path}`, {
    method,
    headers: {
      Accept: 'application/json',
      'X-Requested-With': 'XMLHttpRequest',
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  })

  if (response.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))

  if (!response.ok) {
    // A safety rule (last administrator, protected account) answers 409 with a
    // human-readable reason; surface it rather than a generic failure.
    let detail: string | null = null
    try {
      const problem = await response.json()
      detail = problem?.title ?? problem?.detail ?? null
    } catch {
      detail = null
    }
    throw new ApiError(response.status, detail ?? `Request failed with HTTP ${response.status}`,
      response.headers.get('X-Correlation-Id'))
  }

  return (await response.json()) as T
}

export function changeAccountType(
  deviceId: string, sid: string, accountType: 'Administrator' | 'StandardUser',
): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users/${encodeURIComponent(sid)}/change-account-type`,
    'POST', { accountType })
}

export function setLocalUserEnabled(deviceId: string, sid: string, enabled: boolean): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users/${encodeURIComponent(sid)}/${enabled ? 'enable' : 'disable'}`,
    'POST')
}

export interface UserConfigurationProfile {
  key: string
  displayName: string
  description: string
  accountType: 'StandardUser' | 'Administrator'
  enabled: boolean
  mustChangePasswordAtNextLogon: boolean
  additionalGroups: string[]
  grantsAdministrator: boolean
}

export interface ProfileCatalog {
  profiles: UserConfigurationProfile[]
  /**
   * Groups that may be assigned on THIS device: the policy allow-list intersected
   * with the groups the device reported. Not every Windows edition has every group
   * in the policy, so this is what the operator may actually pick.
   */
  permittedAdditionalGroups: string[]
  /** The unfiltered policy allow-list, so the UI can explain what is missing and why. */
  policyAdditionalGroups: string[]
  /** False when the device has never reported its groups; the full allow-list is offered then. */
  deviceGroupsKnown: boolean
  /** Whether this operator may create an administrator. The server re-checks on submit. */
  canGrantAdministrator: boolean
}

export function getUserProfiles(deviceId: string): Promise<ProfileCatalog> {
  return request<ProfileCatalog>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/local-user-profiles`)
}

export interface CreateLocalUserBody {
  username: string
  fullName?: string
  description?: string
  password: string
  enabled: boolean
  mustChangePasswordAtNextLogon: boolean
  accountType: 'StandardUser' | 'Administrator'
  additionalGroups: string[]
  profileKey?: string
}

export function createLocalUser(deviceId: string, body: CreateLocalUserBody): Promise<QueuedTask> {
  return localAccountAction(`/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users`, 'POST', body)
}

export function deleteLocalUser(deviceId: string, sid: string): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users/${encodeURIComponent(sid)}`, 'DELETE')
}

export function resetLocalUserPassword(deviceId: string, sid: string, password: string): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users/${encodeURIComponent(sid)}/reset-password`,
    'POST', { password })
}

export function forceLocalUserPasswordChange(deviceId: string, sid: string): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users/${encodeURIComponent(sid)}/force-password-change`,
    'POST')
}

export function addLocalGroupMember(deviceId: string, groupSid: string, memberSid: string): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-groups/${encodeURIComponent(groupSid)}/members`,
    'POST', { memberSid })
}

export function removeLocalGroupMember(deviceId: string, groupSid: string, memberSid: string): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-groups/${encodeURIComponent(groupSid)}/members/${encodeURIComponent(memberSid)}`,
    'DELETE')
}

// ---------------------------------------------------------------------------
// Pending agent enrollment
//
// A Windows PC that has installed the MSI asks to be managed and then waits.
// Nothing is issued until an administrator approves it here, so this is the
// authorization gate for the whole enrollment flow.
//
// Everything below is identity and state. The proof secret, the sealed
// enrollment token and the device credential are all server-side only and are
// never sent to the browser -- there is deliberately no field for them here.
// ---------------------------------------------------------------------------

export interface PendingEnrollment {
  /** SHA-256 the agent published. Identifies the request; it is not a credential. */
  requestId: string
  hostname: string
  /** SMBIOS UUID. Lets an administrator tell a re-enrolling machine from a new one. */
  machineIdentifier: string
  operatingSystem: string | null
  agentVersion: string
  requestedAt: string
  expiresAt: string
  status: 'Pending' | 'Approved' | 'Rejected'
  /** Display name of the deciding administrator, once decided. */
  approvedBy: string | null
}

export interface EnrollmentDecision {
  requestId: string
  status: string
  hostname: string
  message: string
}

export function getPendingEnrollments(): Promise<PendingEnrollment[]> {
  return request<PendingEnrollment[]>('/admin/v1/enrollments/pending')
}

export function approveEnrollment(requestId: string): Promise<EnrollmentDecision> {
  return request<EnrollmentDecision>(
    `/admin/v1/enrollments/${encodeURIComponent(requestId)}/approve`,
    { method: 'POST' },
  )
}

export function rejectEnrollment(requestId: string): Promise<EnrollmentDecision> {
  return request<EnrollmentDecision>(
    `/admin/v1/enrollments/${encodeURIComponent(requestId)}/reject`,
    { method: 'POST' },
  )
}

/**
 * What a device should be called on screen: the administrator's label if one is
 * set, otherwise the machine's real hostname. Mirrors the server's fallback so
 * the two can never disagree.
 */
export function deviceName(device: { hostname: string; displayName: string | null }): string {
  return device.displayName ?? device.hostname
}

/**
 * Sets or clears the console display name for a device.
 *
 * Pass null or a blank string to clear it, which restores the hostname. This
 * changes a label in this console only -- it does not rename Windows, and
 * nothing about it reaches the endpoint.
 */
export async function setDeviceDisplayName(
  deviceId: string,
  displayName: string | null,
): Promise<void> {
  await request<void>(`/admin/v1/devices/${deviceId}/display-name`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ displayName }),
  })
}

// ---- Agent releases (Milestone 10) -----------------------------------------

export interface AgentReleaseRow {
  id: string
  version: string
  platform: string
  architecture: string
  fileName: string
  sha256: string
  signerSubject: string | null
  releaseNotes: string | null
  contentSizeBytes: number
  status: 'Draft' | 'Published' | 'Revoked'
  createdByDisplay: string
  createdAt: string
  publishedAt: string | null
  revokedAt: string | null
}

export interface LatestAgentRelease {
  available: boolean
  releaseId?: string
  version?: string
  architecture?: string
  fileName?: string
  sha256?: string
  signerSubject?: string | null
  releaseNotes?: string | null
  sizeBytes?: number
  publishedAt?: string | null
}

export function getAgentReleases(): Promise<AgentReleaseRow[]> {
  return request<AgentReleaseRow[]>('/admin/v1/agent-releases/')
}

/**
 * The trust model releases are published under. Platform-wide, not per release.
 * Internal: server-computed SHA-256, re-checked at publish and at install, over
 * HTTPS, under authorization -- no Authenticode certificate required. Public:
 * additionally an Authenticode signature by the configured publisher.
 */
export type AgentReleaseTrustMode = 'Internal' | 'Public'

export function getAgentReleasePolicy(): Promise<{ trustMode: AgentReleaseTrustMode }> {
  return request<{ trustMode: AgentReleaseTrustMode }>('/admin/v1/agent-releases/policy')
}

export function getLatestAgentRelease(): Promise<LatestAgentRelease> {
  return request<LatestAgentRelease>('/admin/v1/agent-releases/latest')
}

/**
 * Uploads a new agent MSI as a Draft release. The SHA-256 is computed here in
 * the browser and re-computed by the server as it stores the bytes -- a
 * mismatch anywhere discards the upload.
 */
export async function createAgentRelease(
  file: File,
  meta: { version: string; releaseNotes?: string },
): Promise<void> {
  const sha256 = await sha256Hex(file)
  const form = new FormData()
  form.append('file', file, file.name)
  form.append('version', meta.version)
  form.append('sha256', sha256)
  if (meta.releaseNotes) form.append('releaseNotes', meta.releaseNotes)

  const r = await fetch('/api/admin/v1/agent-releases/', {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
    body: form,
  })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) {
    throw new ApiError(r.status, 'Release upload failed', r.headers.get('X-Correlation-Id'), await problemDetail(r))
  }
}

export async function publishAgentRelease(releaseId: string): Promise<void> {
  await request<void>(`/admin/v1/agent-releases/${encodeURIComponent(releaseId)}/publish`, { method: 'POST' })
}

export async function revokeAgentRelease(releaseId: string): Promise<void> {
  await request<void>(`/admin/v1/agent-releases/${encodeURIComponent(releaseId)}/revoke`, { method: 'POST' })
}

/**
 * Starts a native browser download of a release's MSI.
 *
 * Deliberately NOT fetch+blob. A 29 MB blob fetch shows no progress and no
 * busy state, so the button gets clicked repeatedly -- and six in-flight
 * fetches consume the browser's entire HTTP/1.1 connection pool for the
 * origin, queueing every poll and navigation behind them until the transfers
 * finish. The page freezes for a minute while the server serves each request
 * perfectly; that reads as "the dashboard is down". A plain navigation hands
 * the transfer to the browser's download manager instead: real progress UI,
 * no page-held connection, no memory buffering. Authentication rides the
 * HttpOnly __Host- session cookie, and the server answers with
 * Content-Disposition: attachment, so the SPA never navigates away.
 */
export function downloadAgentRelease(releaseId: string, _fileName: string): void {
  const anchor = document.createElement('a')
  anchor.href = `/api/admin/v1/agent-releases/${encodeURIComponent(releaseId)}/download`
  // The server names the file via Content-Disposition; the attribute only
  // marks the click as a download so the SPA route never changes.
  anchor.download = ''
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
}

/** Queues a device's agent self-update to a published release. */
export async function updateDeviceAgent(
  deviceId: string,
  releaseId: string,
): Promise<{ taskId: string }> {
  return request<{ taskId: string }>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/actions/update-agent`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ releaseId }),
    },
  )
}

/** Automatic-escrow retry state for one protector. Metadata only; no key material. */
export interface EscrowAttemptRow {
  id: string
  deviceId: string
  volumeDeviceIdentifier: string
  keyProtectorId: string
  state: string
  attemptCount: number
  maxAttempts: number
  lastFailure: string
  nextAttemptAt: string | null
  lastAttemptAt: string | null
  escrowedAt: string | null
}

/**
 * A device's automatic-escrow position.
 *
 * `eligible` comes from the device's active credential, not from whether any
 * attempt exists. The two say different things: a device that is not eligible
 * must re-enroll before it can participate at all, whereas one that is eligible
 * with no attempts yet simply has not been reached, and will be.
 */
export interface AutomaticEscrowStatus {
  eligible: boolean
  sealingKeyFingerprint: string | null
  attempts: EscrowAttemptRow[]
}

export function getBitLockerEscrowAttempts(deviceId: string): Promise<AutomaticEscrowStatus> {
  return request<AutomaticEscrowStatus>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/bitlocker-escrow-attempts`,
  )
}

/**
 * Re-arms automatic collection for one protector that stopped retrying.
 *
 * Grants no access to any key: it clears a failure count so the endpoint may try
 * again. Revealing a password still requires its own permission, the step-up
 * password and the reveal rate limiter.
 */
export function resetEscrowAttempts(attemptId: string): Promise<{ status: string }> {
  return request<{ status: string }>(
    `/admin/v1/bitlocker-escrow-attempts/${encodeURIComponent(attemptId)}/reset`,
    { method: 'POST' },
  )
}
