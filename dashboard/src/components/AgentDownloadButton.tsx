import { useCallback, useEffect, useState } from 'react'
import { downloadAgentRelease, getLatestAgentRelease, type LatestAgentRelease } from '../api/client'
import { Icon } from './Icon'

/**
 * How often the topbar re-asks for the current published release.
 *
 * Slow on purpose. A release is published by a human, so this only has to notice
 * within a few minutes — and it runs on every page, for every signed-in
 * administrator. It also refreshes when the tab regains focus, which is what
 * actually makes it feel current after someone publishes in another tab.
 */
const POLL_MS = 5 * 60_000

interface Props {
  /**
   * Whether the signed-in administrator holds `software.view`. Without it the
   * request is a guaranteed 403, so it is never made and nothing is rendered —
   * the same reasoning as the pending-enrollment badge in AppShell.
   */
  canView: boolean
}

/**
 * Downloads the current published agent installer, from anywhere in the console.
 *
 * Renders nothing at all when there is no published release, rather than a
 * disabled button. An empty state in the topbar is a permanent decoration on
 * every page; the Agent page is where that story is told, and it explains how to
 * upload and publish one. Nothing is rendered on failure either, for the same
 * reason — a background poll must not put an error banner above every page.
 *
 * The click is a plain navigation handled by the browser's download manager
 * (see `downloadAgentRelease`), so a 31 MB transfer does not sit in a fetch
 * promise with no progress.
 */
export function AgentDownloadButton({ canView }: Props) {
  const [latest, setLatest] = useState<LatestAgentRelease | null>(null)

  const refresh = useCallback(async (signal: { cancelled: boolean }) => {
    try {
      const result = await getLatestAgentRelease()
      if (!signal.cancelled) setLatest(result)
    } catch {
      // Deliberately ignored; see the note above. The previous value is kept
      // rather than cleared, so a transient failure does not make the button
      // disappear from under a cursor that is about to click it.
    }
  }, [])

  useEffect(() => {
    if (!canView) {
      setLatest(null)
      return
    }

    const signal = { cancelled: false }
    void refresh(signal)

    const timer = setInterval(() => void refresh(signal), POLL_MS)
    // Publishing usually happens in another tab; coming back to this one is the
    // moment an administrator expects to see the new version.
    const onFocus = () => void refresh(signal)
    window.addEventListener('focus', onFocus)

    return () => {
      signal.cancelled = true
      clearInterval(timer)
      window.removeEventListener('focus', onFocus)
    }
  }, [canView, refresh])

  if (!canView || !latest?.available || !latest.releaseId) {
    return null
  }

  const version = latest.version ?? 'agent'
  const size = formatSize(latest.sizeBytes)

  return (
    <div className="topbar-agent">
      <button
        type="button"
        className="btn-ghost btn-sm"
        onClick={() => downloadAgentRelease(latest.releaseId!, latest.fileName ?? '')}
        // The accessible name carries what the visible label abbreviates, and
        // what the tooltip would otherwise be the only place to find.
        title={`Download the agent installer — version ${version}${size ? `, ${size}` : ''}`}
      >
        <Icon name="download" size={14} />
        Agent
        <span className="agent-version">{version}</span>
      </button>
    </div>
  )
}

/** 32505856 -> "31.0 MB". Returns null for an absent or nonsensical size. */
function formatSize(bytes: number | undefined): string | null {
  if (typeof bytes !== 'number' || !Number.isFinite(bytes) || bytes <= 0) return null
  const mb = bytes / (1024 * 1024)
  return mb >= 1 ? `${mb.toFixed(1)} MB` : `${Math.round(bytes / 1024)} KB`
}
