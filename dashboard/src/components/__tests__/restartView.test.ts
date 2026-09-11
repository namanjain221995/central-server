import { describe, expect, it } from 'vitest'
import {
  RESTART_DELAY_OPTIONS,
  RESTART_MAX_DELAY_SECONDS,
  RESTART_MIN_DELAY_SECONDS,
  describeAcceptedRestart,
  describeDuration,
  describeRestartTiming,
  isAcceptedRestartDelay,
  restartResult,
  restartStage,
} from '../../pages/restartView'

/**
 * The Restart Device dialog's rules and the way a restart task is described
 * afterwards. All pure. The server decides; what is guarded here is that the
 * console never offers a delay the server refuses, never claims a restart has
 * happened when Windows has merely accepted it, and words the timer so that
 * "in 10 minutes" is understood to start when the device gets the task.
 */

describe('accepted delays', () => {
  it('mirrors the server: zero, or thirty seconds to one hour inclusive', () => {
    expect(RESTART_MIN_DELAY_SECONDS).toBe(30)
    expect(RESTART_MAX_DELAY_SECONDS).toBe(3600)
    expect(isAcceptedRestartDelay(0)).toBe(true)
    expect(isAcceptedRestartDelay(30)).toBe(true)
    expect(isAcceptedRestartDelay(3600)).toBe(true)
  })

  it('refuses negative, sub-floor, over-ceiling and non-integer values', () => {
    for (const bad of [-1, -3600, 1, 29, 3601, 86_400, 1.5, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(isAcceptedRestartDelay(bad), String(bad)).toBe(false)
    }
  })

  it('offers only delays the server accepts, starting with now', () => {
    expect(RESTART_DELAY_OPTIONS[0]).toEqual({ seconds: 0, label: 'Now' })
    for (const o of RESTART_DELAY_OPTIONS) {
      expect(isAcceptedRestartDelay(o.seconds), o.label).toBe(true)
    }
    // Ascending, no duplicates, ceiling is the last option.
    const seconds = RESTART_DELAY_OPTIONS.map((o) => o.seconds)
    expect([...seconds].sort((a, b) => a - b)).toEqual(seconds)
    expect(new Set(seconds).size).toBe(seconds.length)
    expect(seconds.at(-1)).toBe(RESTART_MAX_DELAY_SECONDS)
  })
})

describe('wording', () => {
  it('describes durations in plain words', () => {
    expect(describeDuration(30)).toBe('30 seconds')
    expect(describeDuration(1)).toBe('1 second')
    expect(describeDuration(60)).toBe('1 minute')
    expect(describeDuration(90)).toBe('1 minute 30 seconds')
    expect(describeDuration(300)).toBe('5 minutes')
    expect(describeDuration(3600)).toBe('1 hour')
  })

  it('says exactly what an immediate restart does', () => {
    expect(describeRestartTiming(0)).toBe(
      'Restart the device immediately. The signed-in user gets a 30-second warning.',
    )
  })

  it('says when a timed restart counts from', () => {
    expect(describeRestartTiming(600)).toBe(
      'Restart the device in 10 minutes, counted from when the device receives the task.',
    )
    expect(describeRestartTiming(3600)).toBe(
      'Restart the device in 1 hour, counted from when the device receives the task.',
    )
  })
})

describe('restart result', () => {
  const accepted = '{"graceSeconds":300,"restartAt":"2026-09-12T10:05:00+00:00","outcome":"Scheduled","code":null}'

  it('reads the agent’s structured result', () => {
    expect(restartResult({ type: 'RestartDevice', resultJson: accepted })).toEqual({
      graceSeconds: 300,
      restartAt: '2026-09-12T10:05:00+00:00',
      outcome: 'Scheduled',
    })
  })

  it('is null for other task types, for a task with no result, and for unreadable JSON', () => {
    expect(restartResult({ type: 'Ping', resultJson: accepted })).toBeNull()
    expect(restartResult({ type: 'RestartDevice', resultJson: null })).toBeNull()
    expect(restartResult({ type: 'RestartDevice', resultJson: undefined })).toBeNull()
    expect(restartResult({ type: 'RestartDevice', resultJson: '{not json' })).toBeNull()
    expect(restartResult({ type: 'RestartDevice', resultJson: '{"outcome":"Scheduled"}' })).toBeNull()
  })

  it('tolerates a refusal with no restart time', () => {
    const refused = '{"graceSeconds":0,"restartAt":null,"outcome":"Expired","code":null}'
    expect(restartResult({ type: 'RestartDevice', resultJson: refused })).toEqual({
      graceSeconds: 0,
      restartAt: null,
      outcome: 'Expired',
    })
  })
})

describe('restart stage', () => {
  const at = '2026-09-12T10:05:00+00:00'
  const accepted = `{"graceSeconds":300,"restartAt":"${at}","outcome":"Scheduled","code":null}`
  const before = new Date('2026-09-12T10:00:00+00:00')
  const after = new Date('2026-09-12T10:06:00+00:00')

  it('maps the server’s states to the stages the page names', () => {
    expect(restartStage({ type: 'RestartDevice', status: 'Queued', resultJson: null }, before)).toBe('Queued')
    expect(restartStage({ type: 'RestartDevice', status: 'Delivered', resultJson: null }, before)).toBe('Executing')
    expect(restartStage({ type: 'RestartDevice', status: 'Failed', resultJson: null }, before)).toBe('Failed')
    expect(restartStage({ type: 'RestartDevice', status: 'Expired', resultJson: null }, before)).toBe('Expired')
    expect(restartStage({ type: 'RestartDevice', status: 'Cancelled', resultJson: null }, before)).toBe('Cancelled')
  })

  /**
   * The stage the generic model lacks: Windows accepted the request and the
   * machine is still up. That is "Scheduled", not "Succeeded".
   */
  it('is Scheduled while the moment Windows will act is still ahead', () => {
    expect(restartStage({ type: 'RestartDevice', status: 'Succeeded', resultJson: accepted }, before)).toBe('Scheduled')
  })

  it('is Succeeded once that moment has passed', () => {
    expect(restartStage({ type: 'RestartDevice', status: 'Succeeded', resultJson: accepted }, after)).toBe('Succeeded')
  })

  it('is Succeeded for a success from an older server with no structured result', () => {
    expect(restartStage({ type: 'RestartDevice', status: 'Succeeded', resultJson: null }, before)).toBe('Succeeded')
  })

  it('never claims the device restarted', () => {
    const parsed = restartResult({ type: 'RestartDevice', resultJson: accepted })!
    expect(describeAcceptedRestart(parsed, before)).toMatch(/^Scheduled — Windows will restart the device at .* \(5 minutes after it received the task\)$/)
    expect(describeAcceptedRestart(parsed, after)).toBe(
      'Succeeded — Windows accepted the restart; the device’s next heartbeat confirms it came back',
    )
  })
})
