import { describe, expect, it } from 'vitest'
import { waitForFreshInventory, type InventorySyncDeps } from '../inventorySync'
import { decideSettlement, SYNCING_STAGE } from '../useTaskTracker'

/**
 * The auto-refresh behaviour, tested at its two load-bearing joints:
 *
 * 1. `waitForFreshInventory` — the chase that turns "task succeeded" into
 *    "the view shows the new state": request a refresh, poll the pending flag,
 *    stop the moment fresh data has landed, give up honestly on timeout.
 * 2. `decideSettlement` — the transition table deciding when a tracked task is
 *    done and whether its success must wait for inventory before the UI calls
 *    it finished.
 *
 * Both are pure with injected dependencies, so these tests run the real logic
 * the dashboard ships — no mocked React, no fake DOM.
 */

function deps(pendingSequence: boolean[]) {
  const calls = { refresh: 0, polls: 0, sleeps: [] as number[] }
  const d: InventorySyncDeps = {
    requestRefresh: async () => {
      calls.refresh++
    },
    isRefreshPending: async () => {
      calls.polls++
      // Past the scripted sequence, stay pending forever.
      return pendingSequence[calls.polls - 1] ?? true
    },
    sleep: async (ms) => {
      calls.sleeps.push(ms)
    },
  }
  return { d, calls }
}

describe('waitForFreshInventory', () => {
  it('requests a refresh first, then polls until the device has uploaded', async () => {
    // Pending twice, then fresh data lands.
    const { d, calls } = deps([true, true, false])

    const fresh = await waitForFreshInventory('dev-1', { deps: d, intervalMs: 10_000 })

    expect(fresh).toBe(true)
    expect(calls.refresh).toBe(1)
    expect(calls.polls).toBe(3)
  })

  it('stops on the first poll when the inventory is already fresh', async () => {
    const { d, calls } = deps([false])

    const fresh = await waitForFreshInventory('dev-1', { deps: d })

    expect(fresh).toBe(true)
    expect(calls.polls).toBe(1)
  })

  it('gives up honestly on timeout instead of pretending the view is current', async () => {
    const { d, calls } = deps([]) // pending forever

    const fresh = await waitForFreshInventory('dev-1', {
      deps: d,
      timeoutMs: 30_000,
      intervalMs: 10_000,
    })

    expect(fresh).toBe(false)
    expect(calls.polls).toBe(3)
  })

  it('treats a failed poll as a blip, not a verdict', async () => {
    let polls = 0
    const d: InventorySyncDeps = {
      requestRefresh: async () => {},
      isRefreshPending: async () => {
        polls++
        if (polls === 1) throw new Error('502 from a mid-deploy proxy')
        return false
      },
      sleep: async () => {},
    }

    const fresh = await waitForFreshInventory('dev-1', { deps: d })

    expect(fresh).toBe(true)
    expect(polls).toBe(2)
  })
})

describe('decideSettlement', () => {
  it('a queued or delivered task is not settled and triggers no refresh', () => {
    expect(decideSettlement('Queued', null, true)).toEqual({ kind: 'wait' })
    expect(decideSettlement('Delivered', null, true)).toEqual({ kind: 'running' })
  })

  it('a success that changed device state must chase inventory before finishing', () => {
    const d = decideSettlement('Succeeded', "Service 'Spooler' stop completed.", true)

    expect(d).toEqual({
      kind: 'settled',
      succeeded: true,
      stage: 'Succeeded',
      message: "Service 'Spooler' stop completed.",
      chaseInventory: true,
    })
  })

  it('a success that changed nothing inventory-visible settles immediately', () => {
    const d = decideSettlement('Succeeded', 'Restart scheduled in 30s.', false)

    expect(d).toMatchObject({ kind: 'settled', succeeded: true, chaseInventory: false })
  })

  /**
   * A restart Windows accepted is a success the machine has not acted on yet.
   * The banner must say "Scheduled" and when, not "Succeeded" — an operator
   * looking at a device that is still up would rightly call the latter a lie.
   */
  it('a restart Windows accepted settles as Scheduled while the countdown runs', () => {
    const restartAt = new Date(Date.now() + 5 * 60_000).toISOString()
    const task = {
      type: 'RestartDevice',
      resultJson: `{"graceSeconds":300,"restartAt":"${restartAt}","outcome":"Scheduled","code":null}`,
    }

    const d = decideSettlement('Succeeded', 'Restart accepted by Windows.', false, task)

    expect(d).toMatchObject({ kind: 'settled', succeeded: true, chaseInventory: false, message: null })
    expect((d as { stage: string }).stage).toMatch(/^Scheduled — Windows will restart the device at /)
  })

  /**
   * The banner must not go green while the machine is still up. The task has
   * succeeded — the server will say nothing more about it — but the work has
   * not happened, and a tick beside a device that is plainly still running is
   * the kind of small lie an operator stops trusting the console over.
   */
  it('a restart still counting down reads as pending, not as success', () => {
    const restartAt = new Date(Date.now() + 5 * 60_000).toISOString()
    const task = {
      type: 'RestartDevice',
      resultJson: `{"graceSeconds":300,"restartAt":"${restartAt}","outcome":"Scheduled","code":null}`,
    }

    expect(decideSettlement('Succeeded', null, false, task)).toMatchObject({ succeeded: true, tone: 'pending' })
  })

  it('the same restart reads as success once Windows was due to act', () => {
    const restartAt = new Date(Date.now() - 60_000).toISOString()
    const task = {
      type: 'RestartDevice',
      resultJson: `{"graceSeconds":30,"restartAt":"${restartAt}","outcome":"Scheduled","code":null}`,
    }

    expect(decideSettlement('Succeeded', null, false, task)).toMatchObject({ succeeded: true, tone: 'success' })
  })

  it('every other settled task leaves the tone to its success flag', () => {
    expect((decideSettlement('Succeeded', 'done', false) as { tone?: string }).tone).toBeUndefined()
    expect((decideSettlement('Failed', 'nope', false) as { tone?: string }).tone).toBeUndefined()
  })

  it('a restart whose moment has passed settles as Succeeded without claiming the device came back', () => {
    const restartAt = new Date(Date.now() - 60_000).toISOString()
    const task = {
      type: 'RestartDevice',
      resultJson: `{"graceSeconds":30,"restartAt":"${restartAt}","outcome":"Scheduled","code":null}`,
    }

    const d = decideSettlement('Succeeded', null, false, task)

    expect((d as { stage: string }).stage).toBe(
      'Succeeded — Windows accepted the restart; the device’s next heartbeat confirms it came back',
    )
  })

  it('a restart success from an older server, with no structured result, keeps the generic wording', () => {
    const d = decideSettlement('Succeeded', 'Restart scheduled in 30s.', false, { type: 'RestartDevice', resultJson: null })

    expect(d).toMatchObject({ kind: 'settled', succeeded: true, stage: 'Succeeded', message: 'Restart scheduled in 30s.' })
  })

  it('a refused restart is a failure with the agent’s reason, never Scheduled', () => {
    const task = {
      type: 'RestartDevice',
      resultJson: '{"graceSeconds":0,"restartAt":null,"outcome":"Expired","code":null}',
    }

    const d = decideSettlement('Failed', 'Restart refused: the task expired before the device executed it.', false, task)

    expect(d).toMatchObject({ kind: 'settled', succeeded: false, stage: 'Failed' })
  })

  it('a failure never chases inventory — the machine was not changed', () => {
    // Even when the caller asked for a sync: a failed stop left the service
    // running, and waiting for "fresh" data would just delay saying so.
    const d = decideSettlement('Failed', 'Malformed service-control payload.', true)

    expect(d).toMatchObject({
      kind: 'settled',
      succeeded: false,
      stage: 'Failed',
      chaseInventory: false,
    })
  })

  it('expired and cancelled settle without a chase, with honest wording', () => {
    expect(decideSettlement('Expired', null, true)).toMatchObject({
      kind: 'settled',
      succeeded: false,
      chaseInventory: false,
      message: 'The device never picked this up before the task expired.',
    })
    expect(decideSettlement('Cancelled', 'Cancelled by admin.', true)).toMatchObject({
      kind: 'settled',
      succeeded: false,
      chaseInventory: false,
    })
  })

  it('the syncing stage tells the user what the wait is for', () => {
    // Pinned because this string is the UI's promise: success is not announced
    // while the table below it still shows the old state.
    expect(SYNCING_STAGE).toContain('report its new state')
  })
})
