import { useCallback, useEffect, useRef, useState } from 'react'
import { getDeviceTasks, type DeviceTaskItem } from '../api/client'
import { describeAcceptedRestart, restartResult } from '../pages/restartView'
import { waitForFreshInventory } from './inventorySync'

const POLL_MS = 3_000

/** The states the tracker reports, in the order a task moves through them. */
export interface TrackedTask {
  taskId: string
  /** What the administrator asked for, e.g. `Restart` or `Stop "Spooler"`. */
  label: string
  /** Human description of where the task is right now. */
  stage: string
  /** Set once nothing more will happen for this task, including any data sync. */
  terminal: boolean
  succeeded: boolean | null
  /** The agent's result message, when there is one. */
  message: string | null
}

/**
 * What should happen to a tracked task given the server's current view of it.
 * Pure so the transition table is testable without React.
 */
export type SettleDecision =
  | { kind: 'wait' }
  | { kind: 'running' }
  | {
      kind: 'settled'
      succeeded: boolean
      stage: string
      message: string | null
      /**
       * True when the task changed device state that only fresh inventory can
       * show — the banner then stays in its pending style, saying so, until the
       * device has reported back and the view has been reloaded.
       */
      chaseInventory: boolean
    }

export function decideSettlement(
  status: DeviceTaskItem['status'],
  resultMessage: string | null,
  syncInventoryRequested: boolean,
  /**
   * The task's type and structured result, when the caller has them. A restart
   * that Windows accepted is a success the machine has not acted on yet, and
   * the stage must say so rather than "Succeeded"; without these two it falls
   * back to the generic wording, which is what older servers get.
   */
  task?: Pick<DeviceTaskItem, 'type' | 'resultJson'>,
  now: Date = new Date(),
): SettleDecision {
  switch (status) {
    case 'Queued':
      return { kind: 'wait' }
    case 'Delivered':
      return { kind: 'running' }
    case 'Succeeded': {
      const accepted = task ? restartResult(task) : null
      if (accepted?.outcome === 'Scheduled') {
        return {
          kind: 'settled',
          succeeded: true,
          stage: describeAcceptedRestart(accepted, now),
          message: null,
          chaseInventory: false,
        }
      }
      return {
        kind: 'settled',
        succeeded: true,
        stage: 'Succeeded',
        message: resultMessage,
        // Only a success can have changed the machine; a failed or expired task
        // left it as it was, so there is nothing fresher to wait for.
        chaseInventory: syncInventoryRequested,
      }
    }
    case 'Failed':
      return { kind: 'settled', succeeded: false, stage: 'Failed', message: resultMessage, chaseInventory: false }
    case 'Expired':
      return {
        kind: 'settled',
        succeeded: false,
        stage: 'Expired',
        message: resultMessage ?? 'The device never picked this up before the task expired.',
        chaseInventory: false,
      }
    case 'Cancelled':
      return { kind: 'settled', succeeded: false, stage: 'Cancelled', message: resultMessage, chaseInventory: false }
    default:
      return { kind: 'wait' }
  }
}

/** Shown while a succeeded task waits for the device to report its new state. */
export const SYNCING_STAGE = 'Succeeded on the device — waiting for it to report its new state…'

/**
 * Follows queued tasks to their terminal state so the UI can report what
 * actually happened on Windows, not merely that the server accepted a request.
 *
 * `onRefreshed` is called whenever the view's data should be reloaded — once
 * per task settling, and for inventory-changing tasks only after the device has
 * uploaded fresh inventory. That last part is what makes "Stop Spooler →
 * Succeeded" and a services table saying Running impossible to show together:
 * the success banner appears only once the table it sits above has caught up.
 *
 * `offline` softens the Queued wording: a task for an offline machine is not
 * about to run, and saying "waiting for the device to check in" without
 * qualification would imply it is.
 */
export function useTaskTracker(
  deviceId: string,
  offline: boolean,
  onRefreshed?: () => void | Promise<void>,
) {
  const [tracked, setTracked] = useState<TrackedTask[]>([])
  const offlineRef = useRef(offline)
  offlineRef.current = offline
  const onRefreshedRef = useRef(onRefreshed)
  onRefreshedRef.current = onRefreshed

  /** Tasks that asked for an inventory chase on success. */
  const syncRequested = useRef(new Set<string>())
  /** Tasks whose settlement has been handled, so a poll tick can't repeat it. */
  const settled = useRef(new Set<string>())
  /**
   * One chase serves every task that settles while it runs: a single fresh
   * inventory is fresh for all of them, and stacking chases would just hammer
   * the detail endpoint.
   */
  const chase = useRef<Promise<boolean> | null>(null)

  const track = useCallback((taskId: string, label: string, opts?: { syncInventory?: boolean }) => {
    if (opts?.syncInventory) syncRequested.current.add(taskId)
    setTracked((current) => [
      ...current.filter((t) => t.taskId !== taskId),
      {
        taskId,
        label,
        stage: offlineRef.current
          ? 'Queued — device offline; runs when the agent reconnects.'
          : 'Queued — waiting for the device to check in…',
        terminal: false,
        succeeded: null,
        message: null,
      },
    ])
  }, [])

  const dismiss = useCallback((taskId: string) => {
    syncRequested.current.delete(taskId)
    settled.current.delete(taskId)
    setTracked((current) => current.filter((t) => t.taskId !== taskId))
  }, [])

  const finalize = useCallback((taskId: string, decision: Extract<SettleDecision, { kind: 'settled' }>) => {
    setTracked((current) =>
      current.map((t) =>
        t.taskId === taskId
          ? { ...t, terminal: true, succeeded: decision.succeeded, stage: decision.stage, message: decision.message }
          : t,
      ),
    )
  }, [])

  const settle = useCallback(
    (taskId: string, decision: Extract<SettleDecision, { kind: 'settled' }>) => {
      if (settled.current.has(taskId)) return
      settled.current.add(taskId)

      if (!decision.chaseInventory) {
        finalize(taskId, decision)
        void onRefreshedRef.current?.()
        return
      }

      // Hold the banner in its pending style while the device catches up, so a
      // green Succeeded never sits above a table still showing the old state.
      setTracked((current) =>
        current.map((t) => (t.taskId === taskId ? { ...t, stage: SYNCING_STAGE } : t)),
      )

      chase.current ??= waitForFreshInventory(deviceId).finally(() => {
        chase.current = null
      })

      void chase.current.then(async (fresh) => {
        await onRefreshedRef.current?.()
        finalize(taskId, {
          ...decision,
          // A timeout is not certainty: the reload happened, but the device may
          // still be reporting old state, and the banner must not claim otherwise.
          message: fresh
            ? decision.message
            : `${decision.message ?? 'Done'} (the device has not reported fresh inventory yet — data may lag)`,
        })
      })
    },
    [deviceId, finalize],
  )

  const anyPending = tracked.some((t) => !t.terminal)

  useEffect(() => {
    if (!anyPending) return

    let cancelled = false

    const timer = setInterval(async () => {
      try {
        const tasks = await getDeviceTasks(deviceId)
        if (cancelled) return

        for (const t of tracked) {
          if (t.terminal || settled.current.has(t.taskId)) continue
          const task = tasks.find((x) => x.id === t.taskId)
          if (!task) continue

          const decision = decideSettlement(task.status, task.resultMessage, syncRequested.current.has(t.taskId), task)
          if (decision.kind === 'running') {
            setTracked((current) =>
              current.map((x) => (x.taskId === t.taskId ? { ...x, stage: 'Running on Windows…' } : x)),
            )
          } else if (decision.kind === 'settled') {
            settle(t.taskId, decision)
          }
        }
      } catch {
        // A failed poll is not a failed task — keep the last known state and
        // try again on the next tick.
      }
    }, POLL_MS)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [anyPending, deviceId, tracked, settle])

  return { tracked, track, dismiss }
}
