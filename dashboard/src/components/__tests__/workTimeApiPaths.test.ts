import { afterEach, describe, expect, it, vi } from 'vitest'
import {
  downloadWorkTimeReportCsv,
  getWorkTimeReport,
  getWorkTimeSettings,
  getServerStorage,
} from '../../api/client'

/**
 * The work-time report takes an optional date range and group filter. These
 * assert the query string is built correctly and that every path carries exactly
 * one `/api` prefix — the same property apiPaths.test.ts guards for the rest of
 * the client, which a real `/api/api/...` regression once broke in production.
 */

function captureFetchUrl(): { urls: string[] } {
  const urls: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      urls.push(typeof input === 'string' ? input : input.toString())
      return new Response(JSON.stringify({}), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }),
  )
  return { urls }
}

/** Minimal anchor + document, so the CSV download (which navigates, not fetches) is testable in node. */
function captureAnchorHref(): { hrefs: string[] } {
  const hrefs: string[] = []
  const anchor = {
    href: '',
    download: '',
    click() { hrefs.push(this.href) },
    remove() { /* no-op */ },
  }
  vi.stubGlobal('document', {
    createElement: () => anchor,
    body: { appendChild: () => undefined, removeChild: () => undefined },
  })
  return { hrefs }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('work-time API paths', () => {
  it.each([
    ['getWorkTimeSettings', () => getWorkTimeSettings(), '/api/admin/v1/settings/work-time'],
    ['getServerStorage', () => getServerStorage(), '/api/admin/v1/server-storage'],
    ['getWorkTimeReport (no filter)', () => getWorkTimeReport(), '/api/admin/v1/work-time/report'],
  ])('%s is singly prefixed', async (_name, call, expected) => {
    const { urls } = captureFetchUrl()
    await call().catch(() => null)
    expect(urls[0]).toBe(expected)
    expect(urls[0].split('/api/').length - 1).toBe(1)
  })

  it('builds the report query from the range and group', async () => {
    const { urls } = captureFetchUrl()
    await getWorkTimeReport({ from: '2026-11-01', to: '2026-11-05', groupId: 'g-1' }).catch(() => null)
    expect(urls[0]).toBe('/api/admin/v1/work-time/report?from=2026-11-01&to=2026-11-05&groupId=g-1')
  })

  it('omits empty query parameters', async () => {
    const { urls } = captureFetchUrl()
    await getWorkTimeReport({ from: '2026-11-01' }).catch(() => null)
    expect(urls[0]).toBe('/api/admin/v1/work-time/report?from=2026-11-01')
  })

  it('downloads the CSV from the report.csv route with the same query', () => {
    const { hrefs } = captureAnchorHref()
    downloadWorkTimeReportCsv({ from: '2026-11-01', to: '2026-11-05', groupId: 'g-1' })
    expect(hrefs[0]).toBe('/api/admin/v1/work-time/report.csv?from=2026-11-01&to=2026-11-05&groupId=g-1')
    expect(hrefs[0].split('/api/').length - 1).toBe(1)
  })
})
