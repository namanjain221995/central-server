import { ApiError } from '../api/client'
import type {
  DeviceGroupCandidate,
  DeviceTaskItem,
  GroupAction,
  GroupActionDeviceResult,
  GroupDeviceOutcome,
} from '../api/client'
import { restartStage } from './restartView'

/**
 * The rules behind the Groups page, kept out of the component so each can be
 * tested on its own. Every one is a client-side reading of what the server
 * decided -- the server resolves membership, online state and authority.
 */

/** One entry in a group's Actions menu. */
export interface GroupActionItem {
  key: GroupAction | 'force-stop' | 'cancel-restart'
  label: string
  /** Interrupts people, so the menu marks it. */
  destructive: boolean
}

/** The permissions that decide which group actions are offered at all. */
export interface GroupActionPermissions {
  restart: boolean
  shutdown: boolean
  lock: boolean
  signOut: boolean
  executeTasks: boolean
}

/**
 * The actions a group offers: exactly the device actions this platform has,
 * each shown only to someone who holds that device action's permission -- the
 * server requires the same one.
 *
 * There is no Sleep. The platform has no sleep task for a single device, so it
 * has none for a group; a menu item that did nothing would be worse than none.
 */
export function groupActionItems(perms: GroupActionPermissions): GroupActionItem[] {
  const items: GroupActionItem[] = []
  if (perms.restart) items.push({ key: 'restart', label: 'Restart…', destructive: true })
  if (perms.shutdown) items.push({ key: 'shutdown', label: 'Shut down…', destructive: true })
  if (perms.lock) items.push({ key: 'lock', label: 'Lock…', destructive: false })
  if (perms.signOut) items.push({ key: 'signout', label: 'Sign out…', destructive: true })
  if (perms.executeTasks) items.push({ key: 'force-stop', label: 'Force Stop an application…', destructive: true })
  if (perms.restart) items.push({ key: 'cancel-restart', label: 'Cancel pending restarts…', destructive: false })
  return items
}

/** The single-device name for each action, for confirmation copy. */
export const GROUP_ACTION_VERBS: Record<GroupAction, { verb: string; noun: string }> = {
  restart: { verb: 'Restart', noun: 'restart' },
  shutdown: { verb: 'Shut down', noun: 'shutdown' },
  lock: { verb: 'Lock', noun: 'lock' },
  signout: { verb: 'Sign out', noun: 'sign-out' },
}

/**
 * How a candidate device reads in the Add Devices dialog. A device belongs to
 * exactly one group, so adding it here takes it out of wherever it is now --
 * and the dialog has to say so before anyone clicks.
 */
export function describeCandidate(candidate: DeviceGroupCandidate, targetGroupName: string): string {
  if (candidate.inThisGroup) return `Already in ${targetGroupName}`
  if (candidate.currentGroupIsBuiltIn) return `In ${candidate.currentGroupName}`
  return `Will move from ${candidate.currentGroupName}`
}

/** What a membership change did to one device, in words. */
export function membershipOutcomeLabel(outcome: GroupDeviceOutcome): string {
  switch (outcome) {
    case 'Moved':
      return 'Moved'
    case 'AlreadyInGroup':
      return 'Already in this group'
    case 'NotInGroup':
      return 'Not in this group'
    case 'NotFound':
      return 'Not found'
    case 'ChangedConcurrently':
      return 'Changed by someone else — reload and try again'
  }
}

/**
 * Where one device is in a group action, combining what queueing said with what
 * its task has reported since.
 */
export type DeviceActionState =
  | 'Pending'
  | 'Scheduled'
  | 'Restarted'
  | 'Succeeded'
  | 'Offline'
  | 'AlreadyInProgress'
  | 'NotEligible'
  | 'NotAuthorized'
  | 'Failed'
  | 'Expired'
  | 'Cancelled'
  | 'TooLateToCancel'

/**
 * A device's state for an action.
 *
 * A restart Windows has accepted is `Scheduled` until its moment passes and only
 * then `Restarted` -- never reported as done while the machine is still counting
 * down. An offline device is `Offline` and nothing else: it was not targeted, so
 * it neither succeeded nor failed.
 */
export function deviceState(
  action: GroupAction | 'cancel-restart',
  result: GroupActionDeviceResult,
  task: Pick<DeviceTaskItem, 'type' | 'status' | 'resultJson'> | undefined,
  now: Date,
): DeviceActionState {
  switch (result.outcome) {
    case 'Offline':
    case 'AlreadyInProgress':
    case 'NotEligible':
    case 'NotAuthorized':
    case 'Cancelled':
    case 'TooLateToCancel':
      return result.outcome
    case 'Queued':
      break
  }

  if (!task) return 'Pending'

  if (action === 'restart') {
    switch (restartStage(task, now)) {
      case 'Queued':
      case 'Executing':
        return 'Pending'
      case 'Scheduled':
        return 'Scheduled'
      case 'Succeeded':
        return 'Restarted'
      case 'Failed':
        return 'Failed'
      case 'Expired':
        return 'Expired'
      case 'Cancelled':
        return 'Cancelled'
    }
  }

  switch (task.status) {
    case 'Succeeded':
      return 'Succeeded'
    case 'Failed':
      return 'Failed'
    case 'Expired':
      return 'Expired'
    case 'Cancelled':
      return 'Cancelled'
    default:
      return 'Pending'
  }
}

/** The labels an administrator sees for each state. */
export const DEVICE_STATE_LABELS: Record<DeviceActionState, string> = {
  Pending: 'Waiting for device',
  Scheduled: 'Restart scheduled',
  Restarted: 'Restarted',
  Succeeded: 'Done',
  Offline: 'Offline — not sent',
  AlreadyInProgress: 'Restart already in progress',
  NotEligible: 'Not eligible',
  NotAuthorized: 'Not authorized',
  Failed: 'Failed',
  Expired: 'Expired — device never received it',
  Cancelled: 'Cancelled',
  TooLateToCancel: 'Already delivered — too late to cancel',
}

/** Badge tone for a state. Green is only for work the device actually did. */
export function deviceStateTone(state: DeviceActionState, action: GroupAction | 'cancel-restart'): 'ok' | 'warn' | 'err' | 'neutral' {
  switch (state) {
    case 'Restarted':
    case 'Succeeded':
      return 'ok'
    case 'Cancelled':
      return action === 'cancel-restart' ? 'ok' : 'neutral'
    case 'Pending':
    case 'Scheduled':
    case 'Offline':
    case 'AlreadyInProgress':
      return 'warn'
    case 'NotEligible':
    case 'NotAuthorized':
    case 'Failed':
    case 'Expired':
    case 'TooLateToCancel':
      return 'err'
  }
}

/** The group's overall result. */
export type GroupAggregate = 'InProgress' | 'AllSucceeded' | 'CompletedWithIssues' | 'NoEligibleDevices' | 'Failed'

export const GROUP_AGGREGATE_LABELS: Record<GroupAggregate, string> = {
  InProgress: 'In progress',
  AllSucceeded: 'All succeeded',
  CompletedWithIssues: 'Completed with issues',
  NoEligibleDevices: 'No eligible online devices',
  Failed: 'Failed',
}

function isSuccess(state: DeviceActionState, action: GroupAction | 'cancel-restart'): boolean {
  return state === 'Restarted' || state === 'Succeeded' || (action === 'cancel-restart' && state === 'Cancelled')
}

/** A genuine failure, as opposed to a device that was merely busy or away. */
function isFailure(state: DeviceActionState): boolean {
  return state === 'Failed' || state === 'Expired' || state === 'NotEligible' || state === 'NotAuthorized' || state === 'TooLateToCancel'
}

/**
 * The group's result, never collapsing devices into a single success.
 *
 * - In progress while any device is still waiting, or still counting down.
 * - No eligible online devices when nothing was sent at all.
 * - All succeeded only when every device in the group did the work -- an offline
 *   device is an issue, never a success.
 * - Failed when nothing succeeded and something genuinely failed.
 * - Completed with issues otherwise.
 */
export function aggregate(action: GroupAction | 'cancel-restart', states: readonly DeviceActionState[]): GroupAggregate {
  if (states.some((s) => s === 'Pending' || s === 'Scheduled')) return 'InProgress'
  if (states.every((s) => s === 'Offline')) return 'NoEligibleDevices'

  const successes = states.filter((s) => isSuccess(s, action)).length
  if (successes === states.length) return 'AllSucceeded'
  if (successes === 0 && states.some(isFailure)) return 'Failed'
  return 'CompletedWithIssues'
}

/** Counts per label, in a stable order, omitting zeroes: "3 Restarted · 1 Offline · 1 Failed". */
export function tally(states: readonly DeviceActionState[]): { label: string; count: number; state: DeviceActionState }[] {
  const order = Object.keys(DEVICE_STATE_LABELS) as DeviceActionState[]
  return order
    .map((state) => ({ state, label: DEVICE_STATE_LABELS[state], count: states.filter((s) => s === state).length }))
    .filter((t) => t.count > 0)
}

/** True once no device's state can change any more, so polling can stop. */
export function isSettled(states: readonly DeviceActionState[]): boolean {
  return !states.some((s) => s === 'Pending')
}

/** "Restart 4 devices?" -- the number is the online count, which is what will be sent. */
export function actionConfirmTitle(action: GroupAction, onlineCount: number): string {
  const { verb } = GROUP_ACTION_VERBS[action]
  return `${verb} ${onlineCount} device${onlineCount === 1 ? '' : 's'}?`
}

/** The server's own reason for a refusal when it gave one, rather than a guess. */
export function errorText(e: unknown, fallback: string): string {
  return e instanceof ApiError && e.detail ? e.detail : fallback
}
