import type { DeviceTaskItem } from '../api/client'

/**
 * The rules behind the Restart Device dialog and the way a restart task is
 * described afterwards. Pure, so every wording and every boundary is testable
 * without React.
 *
 * The timing model is deliberately small. A restart carries one number: the
 * seconds the device waits once it has the task. The device hands that number
 * to Windows, which counts it down itself. Nothing here computes a deadline —
 * the only absolute time in the whole flow is the one the agent reports back
 * (`restartAt`) after Windows has accepted the request, and that is the time
 * the page shows.
 */

/**
 * "Now" is zero on the wire and a thirty-second warning on the device. The
 * server says so too; the number is mirrored here only for the dialog's copy.
 */
export const RESTART_IMMEDIATE_WARNING_SECONDS = 30

/**
 * Mirrors the server's accepted range. The server is the authority — a value
 * outside it is refused with a 400 — and this exists so the dialog never
 * offers something the server will refuse. The ceiling is the clamp every
 * deployed agent applies before calling Windows.
 */
export const RESTART_MIN_DELAY_SECONDS = 30
export const RESTART_MAX_DELAY_SECONDS = 3600

/** The delays the dialog offers, in seconds. 0 is "now". */
export const RESTART_DELAY_OPTIONS: readonly { seconds: number; label: string }[] = [
  { seconds: 0, label: 'Now' },
  { seconds: 60, label: '1 minute' },
  { seconds: 300, label: '5 minutes' },
  { seconds: 600, label: '10 minutes' },
  { seconds: 900, label: '15 minutes' },
  { seconds: 1800, label: '30 minutes' },
  { seconds: 3600, label: '1 hour' },
]

/** Whether a delay is one the server accepts: exactly 0, or within its inclusive range. */
export function isAcceptedRestartDelay(seconds: number): boolean {
  if (!Number.isInteger(seconds)) return false
  if (seconds === 0) return true
  return seconds >= RESTART_MIN_DELAY_SECONDS && seconds <= RESTART_MAX_DELAY_SECONDS
}

/** A number of seconds in words: "30 seconds", "1 minute", "1 hour", "2 minutes 30 seconds". */
export function describeDuration(seconds: number): string {
  if (seconds < 60) return `${seconds} second${seconds === 1 ? '' : 's'}`
  if (seconds === 3600) return '1 hour'
  const minutes = Math.floor(seconds / 60)
  const rest = seconds % 60
  const m = `${minutes} minute${minutes === 1 ? '' : 's'}`
  return rest === 0 ? m : `${m} ${rest} second${rest === 1 ? '' : 's'}`
}

/**
 * The sentence the confirmation shows for a chosen delay. Written to be read
 * beside "Restart <device>?", and to say the one thing an administrator has to
 * know about a timed restart: the countdown starts when the device gets the
 * task, not when the button is pressed.
 */
export function describeRestartTiming(delaySeconds: number): string {
  if (delaySeconds === 0) {
    return `Restart the device immediately. The signed-in user gets a ${RESTART_IMMEDIATE_WARNING_SECONDS}-second warning.`
  }
  return `Restart the device in ${describeDuration(delaySeconds)}, counted from when the device receives the task.`
}

/** The structured result a RestartDevice task carries once the agent has answered. */
export interface RestartResult {
  graceSeconds: number
  /** When Windows will act, as the agent reported it. Null unless the restart was accepted. */
  restartAt: string | null
  /** `Scheduled`, `Expired`, `AlreadyInProgress` or `Failed`. */
  outcome: string
}

/** Reads a restart result; null for a task that is not a restart, has no result yet, or carries something unreadable. */
export function restartResult(task: Pick<DeviceTaskItem, 'type' | 'resultJson'>): RestartResult | null {
  if (task.type !== 'RestartDevice' || !task.resultJson) return null
  try {
    const parsed = JSON.parse(task.resultJson) as Partial<RestartResult>
    if (typeof parsed.outcome !== 'string' || typeof parsed.graceSeconds !== 'number') return null
    return {
      graceSeconds: parsed.graceSeconds,
      restartAt: typeof parsed.restartAt === 'string' ? parsed.restartAt : null,
      outcome: parsed.outcome,
    }
  } catch {
    return null
  }
}

/**
 * The stages a restart moves through, as the page names them. `Scheduled` is
 * the one the generic task model does not have: the server has recorded a
 * success — Windows accepted the request — but the machine is still up, and
 * saying "succeeded" about a device that has not restarted would be a lie
 * the operator would catch a minute later.
 */
export type RestartStage = 'Queued' | 'Executing' | 'Scheduled' | 'Succeeded' | 'Failed' | 'Expired' | 'Cancelled'

/**
 * Which stage a restart task is in, judged from the server's status and, for a
 * success, from whether the moment Windows said it would act has passed.
 *
 * `Delivered` is `Executing`: the device has the task and has not yet reported.
 * A success whose `restartAt` is still in the future is `Scheduled`; once that
 * moment passes it is `Succeeded`, which still only means Windows accepted the
 * request — the device's next heartbeat is the proof it came back.
 */
export function restartStage(task: Pick<DeviceTaskItem, 'type' | 'status' | 'resultJson'>, now: Date): RestartStage {
  switch (task.status) {
    case 'Queued':
      return 'Queued'
    case 'Delivered':
      return 'Executing'
    case 'Failed':
      return 'Failed'
    case 'Expired':
      return 'Expired'
    case 'Cancelled':
      return 'Cancelled'
    case 'Succeeded': {
      const result = restartResult(task)
      if (result?.restartAt && new Date(result.restartAt).getTime() > now.getTime()) return 'Scheduled'
      return 'Succeeded'
    }
    default:
      return 'Queued'
  }
}

/**
 * The tracker's wording for a restart that Windows accepted: scheduled and
 * still pending while the countdown runs, and honest about what "succeeded"
 * means once it has passed.
 */
export function describeAcceptedRestart(result: RestartResult, now: Date): string {
  if (result.restartAt && new Date(result.restartAt).getTime() > now.getTime()) {
    const at = new Date(result.restartAt)
    return `Scheduled — Windows will restart the device at ${at.toLocaleTimeString()} (${describeDuration(result.graceSeconds)} after it received the task)`
  }
  return 'Succeeded — Windows accepted the restart; the device’s next heartbeat confirms it came back'
}
