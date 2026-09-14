import { describe, expect, it } from 'vitest'
import type { DeviceGroupCandidate, GroupActionDeviceResult } from '../../api/client'
import {
  actionConfirmTitle,
  aggregate,
  describeCandidate,
  deviceState,
  deviceStateTone,
  groupActionItems,
  isSettled,
  tally,
  type DeviceActionState,
} from '../../pages/groupsView'

const ALL = { restart: true, shutdown: true, lock: true, signOut: true, executeTasks: true }

function result(outcome: GroupActionDeviceResult['outcome'], taskId: string | null = 't'): GroupActionDeviceResult {
  return { deviceId: 'd', hostname: 'PC-01', outcome, taskId }
}

function restartTask(status: string, restartAt?: Date) {
  return {
    type: 'RestartDevice',
    status,
    resultJson: restartAt
      ? JSON.stringify({ graceSeconds: 600, restartAt: restartAt.toISOString(), outcome: 'Scheduled', code: null })
      : null,
  }
}

describe('group actions menu', () => {
  it('offers exactly the single-device actions this platform has, and no Sleep', () => {
    const keys = groupActionItems(ALL).map((i) => i.key)
    expect(keys).toEqual(['restart', 'shutdown', 'lock', 'signout', 'force-stop', 'cancel-restart'])
    expect(keys).not.toContain('sleep')
  })

  it('shows an action only to someone holding that device action’s permission', () => {
    const helpdesk = groupActionItems({ restart: true, shutdown: false, lock: true, signOut: false, executeTasks: false })
    expect(helpdesk.map((i) => i.key)).toEqual(['restart', 'lock', 'cancel-restart'])
    expect(groupActionItems({ restart: false, shutdown: false, lock: false, signOut: false, executeTasks: false })).toEqual([])
  })
})

describe('add devices dialog', () => {
  const base: DeviceGroupCandidate = {
    id: 'd', hostname: 'PC-01', displayName: null, agentVersion: '1.9.0', isOnline: true,
    currentGroupId: 'g', currentGroupName: 'Sales', currentGroupIsBuiltIn: false, inThisGroup: false,
  }

  it('says plainly that adding a device moves it out of its current group', () => {
    expect(describeCandidate(base, 'Developers')).toBe('Will move from Sales')
  })

  it('distinguishes a device already in this group', () => {
    expect(describeCandidate({ ...base, inThisGroup: true }, 'Developers')).toBe('Already in Developers')
  })

  it('does not call a device in All Devices a move from a real group', () => {
    expect(describeCandidate({ ...base, currentGroupName: 'All Devices', currentGroupIsBuiltIn: true }, 'Developers'))
      .toBe('In All Devices')
  })
})

describe('per-device state', () => {
  const now = new Date('2026-09-15T10:00:00Z')

  it('never reports an offline device as having done anything', () => {
    expect(deviceState('restart', result('Offline', null), undefined, now)).toBe('Offline')
  })

  it('is waiting while the task has not been reported', () => {
    expect(deviceState('restart', result('Queued'), undefined, now)).toBe('Pending')
    expect(deviceState('restart', result('Queued'), restartTask('Queued'), now)).toBe('Pending')
    expect(deviceState('restart', result('Queued'), restartTask('Delivered'), now)).toBe('Pending')
  })

  /** The rule the whole feature turns on: accepted is not done. */
  it('says a restart Windows accepted is scheduled, not restarted, while it counts down', () => {
    const inTenMinutes = new Date(now.getTime() + 10 * 60_000)
    expect(deviceState('restart', result('Queued'), restartTask('Succeeded', inTenMinutes), now)).toBe('Scheduled')
  })

  it('says restarted only once the scheduled moment has passed', () => {
    const aMinuteAgo = new Date(now.getTime() - 60_000)
    expect(deviceState('restart', result('Queued'), restartTask('Succeeded', aMinuteAgo), now)).toBe('Restarted')
  })

  it('keeps busy, failed and expired devices distinct', () => {
    expect(deviceState('restart', result('AlreadyInProgress', null), undefined, now)).toBe('AlreadyInProgress')
    expect(deviceState('restart', result('Queued'), restartTask('Failed'), now)).toBe('Failed')
    expect(deviceState('restart', result('Queued'), restartTask('Expired'), now)).toBe('Expired')
  })

  it('reads other actions from the task status', () => {
    expect(deviceState('lock', result('Queued'), { type: 'LockDevice', status: 'Succeeded', resultJson: null }, now)).toBe('Succeeded')
    expect(deviceState('lock', result('Queued'), { type: 'LockDevice', status: 'Failed', resultJson: null }, now)).toBe('Failed')
  })

  it('does not show a delivered restart as cancelled', () => {
    expect(deviceState('cancel-restart', result('TooLateToCancel'), undefined, now)).toBe('TooLateToCancel')
  })

  it('never paints a scheduled or offline device green', () => {
    expect(deviceStateTone('Scheduled', 'restart')).not.toBe('ok')
    expect(deviceStateTone('Offline', 'restart')).not.toBe('ok')
    expect(deviceStateTone('Restarted', 'restart')).toBe('ok')
  })
})

describe('group result', () => {
  const s = (...states: DeviceActionState[]) => states

  it('is in progress while anything is waiting or still counting down', () => {
    expect(aggregate('restart', s('Restarted', 'Pending'))).toBe('InProgress')
    expect(aggregate('restart', s('Restarted', 'Scheduled'))).toBe('InProgress')
  })

  /** The example from the requirement: 3 restarted, 1 offline, 1 failed. */
  it('reports completed with issues, and does not collapse devices into one value', () => {
    const states = s('Restarted', 'Restarted', 'Restarted', 'Offline', 'Failed')

    expect(aggregate('restart', states)).toBe('CompletedWithIssues')
    expect(tally(states)).toEqual([
      { state: 'Restarted', label: 'Restarted', count: 3 },
      { state: 'Offline', label: 'Offline — not sent', count: 1 },
      { state: 'Failed', label: 'Failed', count: 1 },
    ])
  })

  it('is all succeeded only when every device did the work', () => {
    expect(aggregate('restart', s('Restarted', 'Restarted'))).toBe('AllSucceeded')
  })

  it('never counts an offline device towards success', () => {
    expect(aggregate('restart', s('Restarted', 'Offline'))).toBe('CompletedWithIssues')
  })

  it('reports no eligible devices when every device was offline, or there were none', () => {
    expect(aggregate('restart', s('Offline', 'Offline'))).toBe('NoEligibleDevices')
    expect(aggregate('restart', s())).toBe('NoEligibleDevices')
  })

  it('is failed when nothing succeeded and something genuinely failed', () => {
    expect(aggregate('restart', s('Failed', 'Offline'))).toBe('Failed')
    expect(aggregate('restart', s('Expired'))).toBe('Failed')
  })

  it('is not called failed when devices were merely busy', () => {
    expect(aggregate('restart', s('AlreadyInProgress', 'Offline'))).toBe('CompletedWithIssues')
  })

  it('treats a cancellation as the success of a cancel', () => {
    expect(aggregate('cancel-restart', s('Cancelled', 'Cancelled'))).toBe('AllSucceeded')
    expect(aggregate('cancel-restart', s('Cancelled', 'TooLateToCancel'))).toBe('CompletedWithIssues')
  })

  it('stops polling only when no device is still waiting', () => {
    expect(isSettled(s('Restarted', 'Pending'))).toBe(false)
    expect(isSettled(s('Scheduled', 'Offline'))).toBe(true)
  })
})

describe('confirmation', () => {
  it('names the number that will actually be sent', () => {
    expect(actionConfirmTitle('restart', 4)).toBe('Restart 4 devices?')
    expect(actionConfirmTitle('lock', 1)).toBe('Lock 1 device?')
  })
})
