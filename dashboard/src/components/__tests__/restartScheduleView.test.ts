import { describe, expect, it } from 'vitest'
import { restartSchedulePaths, type RestartScheduleDevice } from '../../api/client'
import {
  DEVICE_STATE_LABELS,
  SCHEDULE_DELAY_PRESETS,
  SCHEDULE_MAX_DELAY_SECONDS,
  SCHEDULE_MIN_DELAY_SECONDS,
  customDelaySeconds,
  describeCancelResult,
  describeRelative,
  describeScheduleDelay,
  describeSchedulePlan,
  deviceStateTone,
  isAcceptedScheduleDelay,
  isCancellable,
  isInFlight,
  scheduleStage,
  summarizeDevices,
  warningFor,
} from '../../pages/restartScheduleView'

/**
 * Restart Management's rules, all pure. The server decides; what is guarded
 * here is that the console never offers a delay the server refuses, tells the
 * truth about what confirming will do, and never shows a cancel box the server
 * would refuse to honour.
 */

const Now = new Date('2026-10-07T10:00:00Z')

function device(state: RestartScheduleDevice['state'], supportsCancel = true): RestartScheduleDevice {
  return {
    deviceId: 'd',
    hostname: 'PC',
    displayName: null,
    isOnline: true,
    agentVersion: supportsCancel ? '1.14.0' : '1.13.4',
    supportsCancel,
    state,
    detail: null,
    restartTaskId: null,
    cancelTaskId: null,
    restartAt: null,
    supportsOfflineRestart: false,
  }
}

describe('accepted delays', () => {
  it('mirrors the server: one minute to seven days, whole seconds', () => {
    expect(SCHEDULE_MIN_DELAY_SECONDS).toBe(60)
    expect(SCHEDULE_MAX_DELAY_SECONDS).toBe(604_800)
    expect(isAcceptedScheduleDelay(60)).toBe(true)
    expect(isAcceptedScheduleDelay(604_800)).toBe(true)
    for (const bad of [0, 30, 59, 604_801, -60, 1.5, Number.NaN]) {
      expect(isAcceptedScheduleDelay(bad), String(bad)).toBe(false)
    }
  })

  it('offers exactly the presets the feature asked for, all accepted', () => {
    expect(SCHEDULE_DELAY_PRESETS.map((p) => p.seconds)).toEqual([60, 120, 300, 600, 900, 1800, 3600, 7200])
    expect(SCHEDULE_DELAY_PRESETS.map((p) => p.label)).toEqual([
      '1 minute',
      '2 minutes',
      '5 minutes',
      '10 minutes',
      '15 minutes',
      '30 minutes',
      '1 hour',
      '2 hours',
    ])
    for (const p of SCHEDULE_DELAY_PRESETS) expect(isAcceptedScheduleDelay(p.seconds), p.label).toBe(true)
  })

  it('reads a typed "other" value as whole minutes or hours and nothing else', () => {
    expect(customDelaySeconds('90', 'minutes')).toBe(5400)
    expect(customDelaySeconds(' 3 ', 'hours')).toBe(10_800)
    expect(customDelaySeconds('1', 'minutes')).toBe(60)
    for (const bad of ['', '0', '-5', '1.5', '2h', 'abc', '1e3']) {
      expect(customDelaySeconds(bad, 'hours'), bad).toBeNull()
    }
  })
})

describe('the warning', () => {
  it('is the configured lead time, or the whole delay when that is shorter', () => {
    expect(warningFor(7200)).toBe(300)
    expect(warningFor(300)).toBe(300)
    expect(warningFor(120)).toBe(120)
    expect(warningFor(7200, 600)).toBe(600)
  })
})

describe('words', () => {
  it('describes a delay in days, hours and minutes', () => {
    expect(describeScheduleDelay(60)).toBe('1 minute')
    expect(describeScheduleDelay(120)).toBe('2 minutes')
    expect(describeScheduleDelay(3600)).toBe('1 hour')
    expect(describeScheduleDelay(5400)).toBe('1 hour 30 minutes')
    expect(describeScheduleDelay(7200)).toBe('2 hours')
    expect(describeScheduleDelay(86_400)).toBe('1 day')
    expect(describeScheduleDelay(93_900)).toBe('1 day 2 hours 5 minutes')
    expect(describeScheduleDelay(45)).toBe('45 seconds')
  })

  it('describes a moment relative to now, coarsely', () => {
    expect(describeRelative('2026-10-07T10:00:02Z', Now)).toBe('now')
    expect(describeRelative('2026-10-07T10:00:45Z', Now)).toBe('in 45 s')
    expect(describeRelative('2026-10-07T10:07:30Z', Now)).toBe('in 7 min')
    expect(describeRelative('2026-10-07T11:52:00Z', Now)).toBe('in 1 h 52 min')
    expect(describeRelative('2026-10-09T13:00:00Z', Now)).toBe('in 2 d 3 h')
    expect(describeRelative('2026-10-07T09:57:00Z', Now)).toBe('3 min ago')
    expect(describeRelative('garbage', Now)).toBe('—')
  })

  it('says what confirming a long delay will do, including until when it is a clean cancel', () => {
    const lines = describeSchedulePlan(7200, 3, 5, Now)

    expect(lines[0]).toContain('Restart 5 devices in 2 hours, at ')
    expect(lines[1]).toBe(
      'Devices on agent 1.15 or later get the restart now and carry it out at that time even if they go offline; 3 of 5 are online now.',
    )
    expect(lines[2]).toBe('Older agents are restarted only if they are online shortly before that time.')
    expect(lines[3]).toBe('Each device shows a 5 minutes warning first.')
    expect(lines[4]).toMatch(/^You can cancel without any device noticing until .*; after that, cancelling goes through each device\.$/)
  })

  it('says a short delay goes out at once', () => {
    const lines = describeSchedulePlan(120, 1, 1, Now)

    expect(lines[0]).toContain('Restart 1 device in 2 minutes')
    expect(lines[1]).toContain('1 of 1 is online now.')
    expect(lines[3]).toBe('Each device shows a 2 minutes warning first.')
    expect(lines[4]).toBe('The restart goes to the devices straight away; cancelling then goes through each device.')
  })
})

describe('device states', () => {
  it('has a label and a tone for every state the server can name', () => {
    const states = Object.keys(DEVICE_STATE_LABELS) as RestartScheduleDevice['state'][]
    expect(states).toHaveLength(21)
    for (const state of states) {
      expect(DEVICE_STATE_LABELS[state]).not.toBe('')
      expect(['ok', 'warn', 'crit', 'info', 'neutral']).toContain(deviceStateTone(state))
    }
  })

  it('never shows green for a restart that has not happened yet', () => {
    for (const state of ['WillRestart', 'Queued', 'Executing', 'Scheduled', 'CancelRequested', 'AwaitingDevice', 'Armed'] as const) {
      expect(deviceStateTone(state), state).toBe('warn')
    }
    expect(deviceStateTone('Restarted')).toBe('ok')
  })

  it('offers cancel only where the server would honour it', () => {
    expect(isCancellable(device('WillRestart'))).toBe(true)
    expect(isCancellable(device('OfflineNow'))).toBe(true)
    expect(isCancellable(device('Queued', false))).toBe(true)
    expect(isCancellable(device('Scheduled'))).toBe(true)
    expect(isCancellable(device('Scheduled', false))).toBe(false)
    expect(isCancellable(device('Executing', false))).toBe(false)
    expect(isCancellable(device('CancelFailed'))).toBe(true)
    // Sent in advance: withdrawn where it sits until the device has it, then
    // cancelled on the device (every agent that can hold one can cancel it).
    expect(isCancellable(device('AwaitingDevice'))).toBe(true)
    expect(isCancellable(device('Armed'))).toBe(true)
    for (const done of ['Excluded', 'Restarted', 'Failed', 'Expired', 'Cancelled', 'CancelRequested', 'CancelUnsupported', 'SkippedOffline', 'SkippedBusy', 'SkippedIneligible', 'SkippedUnauthorized', 'SkippedMissed', 'NotRestarted'] as const) {
      expect(isCancellable(device(done)), done).toBe(false)
    }
  })

  it('summarises a schedule by counting states', () => {
    expect(summarizeDevices([device('WillRestart'), device('WillRestart'), device('OfflineNow'), device('Excluded')])).toBe(
      '2 will restart · 1 offline now · 1 cancelled',
    )
    expect(summarizeDevices([])).toBe('No devices')
  })
})

describe('schedule stage', () => {
  it('is in progress while any device is between sent and done', () => {
    expect(scheduleStage({ status: 'Pending', devices: [] })).toBe('Scheduled')
    expect(scheduleStage({ status: 'Dispatched', devices: [device('Scheduled'), device('Restarted')] })).toBe('In progress')
    expect(scheduleStage({ status: 'Dispatched', devices: [device('Restarted'), device('SkippedOffline')] })).toBe('Done')
    expect(scheduleStage({ status: 'Dispatched', devices: [device('CancelRequested')] })).toBe('In progress')
    expect(scheduleStage({ status: 'Cancelled', devices: [] })).toBe('Cancelled')
    expect(scheduleStage({ status: 'Missed', devices: [] })).toBe('Missed')
    expect(isInFlight({ status: 'Pending', devices: [device('WillRestart')] })).toBe(false)
  })
})

describe('cancel results', () => {
  it('reports what happened, grouped and counted', () => {
    expect(
      describeCancelResult(
        [
          { deviceId: 'a', hostname: 'A', outcome: 'CancelledBeforeDelivery' },
          { deviceId: 'b', hostname: 'B', outcome: 'CancelledBeforeDelivery' },
          { deviceId: 'c', hostname: 'C', outcome: 'CancelRequested' },
          { deviceId: 'd', hostname: 'D', outcome: 'Unsupported' },
        ],
        true,
      ),
    ).toBe('Cancelled before the device saw it for 2 devices; 1 cancel sent to the device; 1 cannot be cancelled by this agent.')

    expect(describeCancelResult([{ deviceId: 'a', hostname: 'A', outcome: 'Excluded' }], false)).toBe('Cancelled for 1 device.')
    expect(describeCancelResult([], true)).toBe('The restart was cancelled. Nothing had reached any device.')
  })
})

describe('api paths', () => {
  it('builds the routes the server maps, with ids escaped', () => {
    expect(restartSchedulePaths.create).toBe('/admin/v1/restart-schedules')
    expect(restartSchedulePaths.forGroup('g 1')).toBe('/admin/v1/restart-schedules/groups/g%201')
    expect(restartSchedulePaths.schedule('s')).toBe('/admin/v1/restart-schedules/s')
    expect(restartSchedulePaths.cancel('s/x')).toBe('/admin/v1/restart-schedules/s%2Fx/cancel')
  })
})
