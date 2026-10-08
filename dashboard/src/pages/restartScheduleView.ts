import type {
  RestartSchedule,
  RestartScheduleCancelDevice,
  RestartScheduleCancelOutcome,
  RestartScheduleDevice,
  RestartScheduleDeviceState,
} from '../api/client'

/**
 * The rules behind Restart Management, kept pure so every boundary and every
 * sentence is testable without React.
 *
 * The server is the authority on all of it: it refuses a delay outside its
 * range, decides the warning, resolves the department when the restart goes
 * out, and names each device's state. What lives here is the console's side:
 * never offering what the server refuses, saying what a choice will do before
 * it is confirmed, and turning the server's names into words and colours.
 */

// ---------------------------------------------------------------- delays

/** The choices the dialog offers, in seconds. Mirrors the server's presets. */
export const SCHEDULE_DELAY_PRESETS: readonly { seconds: number; label: string }[] = [
  { seconds: 60, label: '1 minute' },
  { seconds: 120, label: '2 minutes' },
  { seconds: 300, label: '5 minutes' },
  { seconds: 600, label: '10 minutes' },
  { seconds: 900, label: '15 minutes' },
  { seconds: 1800, label: '30 minutes' },
  { seconds: 3600, label: '1 hour' },
  { seconds: 7200, label: '2 hours' },
]

/** Mirrors the server: one minute to seven days. Shorter than a minute is an immediate restart, which lives on the Groups page. */
export const SCHEDULE_MIN_DELAY_SECONDS = 60
export const SCHEDULE_MAX_DELAY_SECONDS = 7 * 24 * 3600

/** The server's default lead time; the real value comes back on every schedule. */
export const DEFAULT_WARNING_SECONDS = 300

export type CustomDelayUnit = 'minutes' | 'hours'

export function isAcceptedScheduleDelay(seconds: number): boolean {
  return Number.isInteger(seconds) && seconds >= SCHEDULE_MIN_DELAY_SECONDS && seconds <= SCHEDULE_MAX_DELAY_SECONDS
}

/**
 * The seconds a typed "other" value means, or null when it is not a whole,
 * positive number of minutes or hours. Fractions are refused rather than
 * rounded: "1.5 hours" typed by someone who meant 90 minutes is better asked
 * again than silently turned into something else.
 */
export function customDelaySeconds(value: string, unit: CustomDelayUnit): number | null {
  const trimmed = value.trim()
  if (!/^\d+$/.test(trimmed)) return null
  const amount = Number(trimmed)
  if (!Number.isSafeInteger(amount) || amount <= 0) return null
  return amount * (unit === 'hours' ? 3600 : 60)
}

/** The lead time a schedule with this delay gets: the configured warning, or the whole delay when that is shorter. */
export function warningFor(delaySeconds: number, configuredWarningSeconds = DEFAULT_WARNING_SECONDS): number {
  return Math.min(delaySeconds, configuredWarningSeconds)
}

/** "2 minutes", "1 hour 30 minutes", "2 days", "1 day 3 hours". */
export function describeScheduleDelay(seconds: number): string {
  if (seconds < 60) return `${seconds} second${seconds === 1 ? '' : 's'}`
  const days = Math.floor(seconds / 86_400)
  const hours = Math.floor((seconds % 86_400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const parts: string[] = []
  if (days > 0) parts.push(`${days} day${days === 1 ? '' : 's'}`)
  if (hours > 0) parts.push(`${hours} hour${hours === 1 ? '' : 's'}`)
  if (minutes > 0) parts.push(`${minutes} minute${minutes === 1 ? '' : 's'}`)
  return parts.join(' ')
}

/** "in 1 h 52 min", "in 45 s", "now", "3 min ago". Coarse on purpose: this is a glance, not a stopwatch. */
export function describeRelative(iso: string, now: Date): string {
  const diff = Math.round((new Date(iso).getTime() - now.getTime()) / 1000)
  if (Number.isNaN(diff)) return '—'
  const abs = Math.abs(diff)
  if (abs < 5) return 'now'
  const words =
    abs < 60
      ? `${abs} s`
      : abs < 3600
        ? `${Math.floor(abs / 60)} min`
        : abs < 86_400
          ? `${Math.floor(abs / 3600)} h ${Math.floor((abs % 3600) / 60)} min`
          : `${Math.floor(abs / 86_400)} d ${Math.floor((abs % 86_400) / 3600)} h`
  return diff > 0 ? `in ${words}` : `${words} ago`
}

/**
 * What confirming a delay will do, said before the button: when, how many
 * devices, what they will see, and until when it can be called off without
 * any device noticing. One sentence per fact.
 */
export function describeSchedulePlan(
  delaySeconds: number,
  onlineCount: number,
  deviceCount: number,
  now: Date,
  configuredWarningSeconds = DEFAULT_WARNING_SECONDS,
): string[] {
  const warning = warningFor(delaySeconds, configuredWarningSeconds)
  const restartAt = new Date(now.getTime() + delaySeconds * 1000)
  const dispatchAt = new Date(restartAt.getTime() - warning * 1000)
  const lines = [
    `Restart ${deviceCount} device${deviceCount === 1 ? '' : 's'} in ${describeScheduleDelay(delaySeconds)}, at ${restartAt.toLocaleString()}.`,
    `Devices on agent 1.15 or later get the restart now and carry it out at that time even if they go offline; ${onlineCount} of ${deviceCount} ${onlineCount === 1 ? 'is' : 'are'} online now.`,
    'Older agents are restarted only if they are online shortly before that time.',
    `Each device shows a ${describeScheduleDelay(warning)} warning first.`,
  ]
  lines.push(
    warning < delaySeconds
      ? `You can cancel without any device noticing until ${dispatchAt.toLocaleTimeString()}; after that, cancelling goes through each device.`
      : 'The restart goes to the devices straight away; cancelling then goes through each device.',
  )
  return lines
}

// ---------------------------------------------------------------- states

export type Tone = 'ok' | 'warn' | 'crit' | 'info' | 'neutral'

export const DEVICE_STATE_LABELS: Record<RestartScheduleDeviceState, string> = {
  WillRestart: 'Will restart',
  Excluded: 'Cancelled',
  OfflineNow: 'Offline now',
  Queued: 'Queued',
  Executing: 'Executing',
  Scheduled: 'Counting down',
  Restarted: 'Restarted',
  Failed: 'Failed',
  Expired: 'Expired',
  Cancelled: 'Cancelled',
  CancelRequested: 'Cancel requested',
  CancelFailed: 'Cancel failed',
  CancelUnsupported: 'Cannot cancel',
  SkippedOffline: 'Skipped (offline)',
  SkippedBusy: 'Skipped (busy)',
  SkippedIneligible: 'Skipped',
  SkippedUnauthorized: 'Skipped (not authorised)',
  SkippedMissed: 'Skipped (server missed it)',
  AwaitingDevice: 'Waiting for device',
  Armed: 'Armed on device',
  NotRestarted: 'Did not restart',
}

export function deviceStateTone(state: RestartScheduleDeviceState): Tone {
  switch (state) {
    case 'WillRestart':
    case 'Queued':
    case 'Executing':
    case 'Scheduled':
    case 'CancelRequested':
    case 'AwaitingDevice':
    case 'Armed':
      // Not done yet -- amber, never green.
      return 'warn'
    case 'Restarted':
      return 'ok'
    case 'Failed':
    case 'Expired':
    case 'CancelFailed':
    case 'CancelUnsupported':
    case 'NotRestarted':
      return 'crit'
    case 'OfflineNow':
    case 'SkippedOffline':
    case 'SkippedBusy':
    case 'SkippedIneligible':
    case 'SkippedUnauthorized':
    case 'SkippedMissed':
      return 'info'
    case 'Excluded':
    case 'Cancelled':
      return 'neutral'
  }
}

/**
 * Whether "cancel" still means anything for this device. Before the restart
 * goes out, any device that is not already excluded. After it, a device whose
 * restart is still ahead of it -- and, once Windows has accepted it, only if
 * its agent can abort; the server would refuse the rest anyway, so the box is
 * not offered.
 */
export function isCancellable(device: Pick<RestartScheduleDevice, 'state' | 'supportsCancel'>): boolean {
  switch (device.state) {
    case 'WillRestart':
    case 'OfflineNow':
    case 'Queued':
    case 'AwaitingDevice':
      return true
    case 'Executing':
    case 'Scheduled':
    case 'Armed':
    case 'CancelFailed':
      return device.supportsCancel
    default:
      return false
  }
}

/** "3 will restart · 1 offline now · 1 cancelled" -- the counts that matter for this schedule, in state order. */
export function summarizeDevices(devices: readonly Pick<RestartScheduleDevice, 'state'>[]): string {
  if (devices.length === 0) return 'No devices'
  const counts = new Map<RestartScheduleDeviceState, number>()
  for (const d of devices) counts.set(d.state, (counts.get(d.state) ?? 0) + 1)
  return [...counts.entries()].map(([state, n]) => `${n} ${DEVICE_STATE_LABELS[state].toLowerCase()}`).join(' · ')
}

/** Whether any device is still between "sent" and "done": the page polls faster while this is true. */
export function isInFlight(schedule: Pick<RestartSchedule, 'status' | 'devices'>): boolean {
  if (schedule.status !== 'Dispatched') return false
  return schedule.devices.some((d) =>
    ['Queued', 'Executing', 'Scheduled', 'CancelRequested', 'AwaitingDevice', 'Armed'].includes(d.state),
  )
}

export type ScheduleStage = 'Scheduled' | 'In progress' | 'Done' | 'Cancelled' | 'Missed'

export function scheduleStage(schedule: Pick<RestartSchedule, 'status' | 'devices'>): ScheduleStage {
  switch (schedule.status) {
    case 'Pending':
      return 'Scheduled'
    case 'Dispatched':
      return isInFlight(schedule) ? 'In progress' : 'Done'
    case 'Cancelled':
      return 'Cancelled'
    case 'Missed':
      return 'Missed'
  }
}

export function scheduleStageTone(stage: ScheduleStage): Tone {
  switch (stage) {
    case 'Scheduled':
    case 'In progress':
      return 'warn'
    case 'Done':
      return 'ok'
    case 'Cancelled':
      return 'neutral'
    case 'Missed':
      return 'crit'
  }
}

// ---------------------------------------------------------------- cancel results

const CANCEL_OUTCOME_WORDS: Record<RestartScheduleCancelOutcome, string> = {
  Excluded: 'cancelled',
  AlreadyExcluded: 'already cancelled',
  CancelledBeforeDelivery: 'cancelled before the device saw it',
  CancelRequested: 'cancel sent to the device',
  AlreadyRequested: 'cancel already on its way',
  AlreadyCancelled: 'already cancelled',
  Unsupported: 'cannot be cancelled by this agent',
  NothingToCancel: 'nothing to cancel',
  NotInSchedule: 'not in this department',
  NotAuthorized: 'not authorised',
}

/**
 * One line for the banner after a cancellation: "Cancelled for 3 devices; 1
 * cancel sent to the device; 1 cannot be cancelled by this agent." Counts only,
 * grouped by what happened, in the order it happened to them.
 */
export function describeCancelResult(devices: readonly RestartScheduleCancelDevice[], wholeDepartment: boolean): string {
  if (devices.length === 0) {
    return wholeDepartment ? 'The restart was cancelled. Nothing had reached any device.' : 'Nothing to cancel.'
  }
  const counts = new Map<RestartScheduleCancelOutcome, number>()
  for (const d of devices) counts.set(d.outcome, (counts.get(d.outcome) ?? 0) + 1)
  const parts = [...counts.entries()].map(([outcome, n]) => {
    const words = CANCEL_OUTCOME_WORDS[outcome]
    return outcome === 'Excluded' || outcome === 'CancelledBeforeDelivery'
      ? `${words} for ${n} device${n === 1 ? '' : 's'}`
      : `${n} ${words}`
  })
  const sentence = parts.join('; ')
  return sentence.charAt(0).toUpperCase() + sentence.slice(1) + '.'
}
