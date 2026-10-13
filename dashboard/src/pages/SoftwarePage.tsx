import { useCallback, useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { PackagesPanel } from './PackagesPanel'
import { DeploymentsPanel } from './DeploymentsPanel'
import {
  getSoftwareInstallations,
  getSoftwarePublishers,
  getSoftwareTitles,
  type SoftwareInstallationPage,
  type SoftwareTitle,
  type SoftwareTitlePage,
} from '../api/client'
import { Icon } from '../components/Icon'
import {
  RUNNING_FILTERS,
  categoryLabel,
  confidenceLabel,
  confidenceTone,
  installationsSummary,
  isSameTitle,
  registryViewLabel,
  scopeLabel,
  signerLabel,
  titleKey,
  type RunningFilter,
} from './softwareView'

const PAGE_SIZE = 30
const DEVICE_PAGE_SIZE = 50

type Section = 'installed' | 'packages' | 'deployments'

const SECTIONS: { key: Section; label: string }[] = [
  { key: 'installed', label: 'Installed apps' },
  { key: 'packages', label: 'Managed packages' },
  { key: 'deployments', label: 'Deployments' },
]

export function SoftwarePage() {
  // ?section=packages opens a section directly (the dashboard's Active packages tile).
  const [searchParams] = useSearchParams()
  const [section, setSection] = useState<Section>(
    () => SECTIONS.find((s) => s.key === searchParams.get('section'))?.key ?? 'installed',
  )
  const [focusDeployment, setFocusDeployment] = useState<string | null>(null)
  const [data, setData] = useState<SoftwareTitlePage | null>(null)
  const [publishers, setPublishers] = useState<string[]>([])
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [publisher, setPublisher] = useState('')
  // Frameworks, inbox apps and system components are collected and categorised
  // on the endpoint but hidden here by default: they are not what an operator
  // scrolls a software list for. The server applies the same rule, so paging
  // stays correct.
  const [showSystem, setShowSystem] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  // The title whose devices are being shown. Null closes the drill-down.
  const [selected, setSelected] = useState<SoftwareTitle | null>(null)
  const [installs, setInstalls] = useState<SoftwareInstallationPage | null>(null)
  const [installsError, setInstallsError] = useState<string | null>(null)
  const [installsLoading, setInstallsLoading] = useState(false)
  // Applied on the server, like the other filters, so the count and the page
  // stay honest; "all" is the absence of a filter and is not sent.
  const [runningFilter, setRunningFilter] = useState<RunningFilter>('all')

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await getSoftwareTitles(page, PAGE_SIZE, search, publisher, showSystem ? 'all' : 'applications'))
      setError(null)
    } catch {
      setError('Could not load software inventory.')
    } finally {
      setLoading(false)
    }
  }, [page, search, publisher, showSystem])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    getSoftwarePublishers()
      .then(setPublishers)
      .catch(() => setPublishers([]))
  }, [])

  // Fetched on demand, not with the table: the device list for every title on a
  // page would be a request per row and most are never opened.
  useEffect(() => {
    if (selected === null) {
      setInstalls(null)
      setInstallsError(null)
      return
    }

    let cancelled = false
    setInstallsLoading(true)
    setInstallsError(null)

    getSoftwareInstallations(
      selected.name, selected.version, selected.publisher, 1, DEVICE_PAGE_SIZE, runningFilter,
    )
      .then((result) => {
        // A slower earlier request must not overwrite a newer selection.
        if (!cancelled) setInstalls(result)
      })
      .catch(() => {
        if (!cancelled) setInstallsError('Could not load the devices for this application.')
      })
      .finally(() => {
        if (!cancelled) setInstallsLoading(false)
      })

    return () => {
      cancelled = true
    }
  }, [selected, runningFilter])

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / PAGE_SIZE)) : 1

  return (
    <>
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      {/* Three distinct things, kept visibly distinct: what is installed, what
          may be deployed, and what was deployed. Collapsing them is how a
          catalogue entry starts being mistaken for endpoint state. */}
      <div className="segmented" role="tablist" aria-label="Software sections">
        {SECTIONS.map((s) => (
          <button
            key={s.key}
            type="button"
            role="tab"
            aria-selected={section === s.key}
            className={section === s.key ? 'active' : undefined}
            onClick={() => setSection(s.key)}
          >
            {s.label}
          </button>
        ))}
      </div>

      {section === 'packages' && (
        <PackagesPanel
          onDeployed={(deploymentId) => {
            setFocusDeployment(deploymentId)
            setSection('deployments')
          }}
        />
      )}

      {section === 'deployments' && <DeploymentsPanel focusDeploymentId={focusDeployment} />}

      <div className="card" hidden={section !== 'installed'}>
        <h2>Installed software</h2>
        <div className="toolbar">
          <div className="input-search">
            <Icon name="search" size={15} className="search-icon" />
            <input
              type="search"
              placeholder="Search software…"
              aria-label="Search software by name"
              value={search}
              onChange={(e) => {
                setPage(1)
                setSearch(e.target.value)
              }}
            />
          </div>
          <select
            aria-label="Filter by publisher"
            style={{ width: 'auto', minWidth: 180 }}
            value={publisher}
            onChange={(e) => {
              setPage(1)
              setPublisher(e.target.value)
            }}
          >
            <option value="">All publishers</option>
            {publishers.map((p) => (
              <option key={p} value={p}>
                {p}
              </option>
            ))}
          </select>
          <label className="checkbox-inline">
            <input
              type="checkbox"
              checked={showSystem}
              onChange={(e) => {
                setPage(1)
                setShowSystem(e.target.checked)
              }}
            />
            Show Windows components
          </label>
        </div>

        {loading && !data && <div className="loading">Loading software…</div>}

        {data && data.items.length === 0 && (
          <div className="empty-state">
            <Icon name="software" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No software found</div>
            <div>
              Software appears here once devices report an inventory that includes installed
              applications.
            </div>
          </div>
        )}

        {data && data.items.length > 0 && (
          <>
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Application</th>
                    <th>Version</th>
                    <th>Publisher</th>
                    <th>Type</th>
                    <th>Installs</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((t) => (
                    <tr
                      key={titleKey(t)}
                      className={isSameTitle(selected, t) ? 'row-selected' : undefined}
                    >
                      <td>
                        {/* A button, not a click handler on the row: the drill-down
                            has to be reachable from the keyboard like every other
                            control on this page. */}
                        <button
                          type="button"
                          className="link-button"
                          aria-expanded={isSameTitle(selected, t)}
                          onClick={() => setSelected(isSameTitle(selected, t) ? null : t)}
                        >
                          {t.name}
                        </button>
                        {/* Said next to the name, because it changes what the
                            name means: nothing installed this, a process is the
                            only witness, and it may be gone at the next inventory. */}
                        {t.confidence === 'Observed' && (
                          <span className="badge warn" style={{ marginLeft: 8 }}>
                            {confidenceLabel(t.confidence)}
                          </span>
                        )}
                      </td>
                      <td>{t.version ?? '—'}</td>
                      <td>{t.publisher ?? '—'}</td>
                      <td>{categoryLabel(t.category)}</td>
                      <td>
                        {/* A count, not a state — square marker keeps it from
                            reading as a status pill. */}
                        <span className="badge neutral plain">{t.installCount}</span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="pagination">
              <span>
                {data.totalCount} distinct title{data.totalCount === 1 ? '' : 's'}
              </span>
              <span className="pager">
                <button
                  type="button"
                  className="btn-sm"
                  disabled={page <= 1}
                  onClick={() => setPage(page - 1)}
                >
                  Previous
                </button>
                <span>
                  Page {page} of {totalPages}
                </span>
                <button
                  type="button"
                  className="btn-sm"
                  disabled={page >= totalPages}
                  onClick={() => setPage(page + 1)}
                >
                  Next
                </button>
              </span>
            </div>
          </>
        )}
      </div>

      {selected && section === 'installed' && (
        <div className="card">
          <div className="card-header">
            <h2>{selected.name}</h2>
            <button type="button" className="btn-sm" onClick={() => setSelected(null)}>
              Close
            </button>
          </div>

          <dl className="detail-grid">
            <div>
              <dt>Version</dt>
              <dd>{selected.version ?? 'Not reported'}</dd>
            </div>
            <div>
              <dt>Publisher</dt>
              <dd>{selected.publisher ?? 'Not reported'}</dd>
            </div>
            <div>
              <dt>Installations</dt>
              {/* Deliberately not read off the list below: that list is
                  filtered on the server before it is counted, so under
                  "Running" its total is a count of running installations and
                  not of the installed footprint. The wording of both halves
                  lives in softwareView. */}
              <dd>{installationsSummary(selected, runningFilter, installs)}</dd>
            </div>
            <div>
              <dt>Type</dt>
              <dd>{categoryLabel(selected.category)}</dd>
            </div>
            <div>
              <dt>Presence</dt>
              <dd>
                <span className={`badge ${confidenceTone(selected.confidence)}`}>
                  {confidenceLabel(selected.confidence)}
                </span>
              </dd>
            </div>
          </dl>

          {/* A select rather than a tab strip: the section switch above is
              already one, and two of them on a page would read as nested
              sections rather than a filter on a list. */}
          <div className="toolbar">
            <select
              aria-label="Filter by running state"
              style={{ width: 'auto', minWidth: 150 }}
              value={runningFilter}
              onChange={(e) => setRunningFilter(e.target.value as RunningFilter)}
            >
              {RUNNING_FILTERS.map((f) => (
                <option key={f.key} value={f.key}>
                  {f.label}
                </option>
              ))}
            </select>
            <span className="muted" style={{ fontSize: 12 }}>
              Running state is as of each device’s last inventory.
            </span>
          </div>

          {installsError && (
            <div className="error-banner" role="alert">
              <Icon name="alert" size={15} />
              <span>{installsError}</span>
            </div>
          )}

          {installsLoading && !installs && <div className="loading">Loading devices…</div>}

          {installs && installs.items.length === 0 && !installsError && (
            <div className="empty-state">
              <Icon name="devices" size={40} strokeWidth={1.25} className="icon" />
              <div className="title">
                {runningFilter === 'all' ? 'No devices in scope' : 'No devices match this filter'}
              </div>
              <div>
                {runningFilter === 'all'
                  ? 'This application is not installed on any device you have access to.'
                  : 'No device in scope reported this application in that state at its last inventory.'}
              </div>
            </div>
          )}

          {installs && installs.items.length > 0 && (
            <>
              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Device</th>
                      <th>Status</th>
                      <th>Installed for</th>
                      <th>Presence</th>
                      <th>Signer</th>
                      <th>Found in</th>
                      <th>Reported</th>
                    </tr>
                  </thead>
                  <tbody>
                    {installs.items.map((i) => (
                      // Keyed by device AND user: one device legitimately appears
                      // once per user who has the application installed.
                      <tr key={`${i.deviceId}|${i.installedForUser ?? ''}`}>
                        <td>
                          <Link to={`/devices/${i.deviceId}`}>{i.displayName ?? i.hostname}</Link>
                          {/* Only the positive is badged: "not running" is the
                              absence of a process at one moment, and a badge
                              would make it look like a finding. */}
                          {i.isRunning === true && (
                            <span className="badge warn" style={{ marginLeft: 8 }}>
                              Running
                            </span>
                          )}
                        </td>
                        <td>
                          <span
                            className={`badge ${i.deviceStatus === 'Active' ? 'ok' : 'neutral'}`}
                          >
                            {i.deviceStatus}
                          </span>
                        </td>
                        <td>{scopeLabel(i)}</td>
                        <td>
                          <span className={`badge ${confidenceTone(i.confidence)}`}>
                            {confidenceLabel(i.confidence)}
                          </span>
                        </td>
                        {/* Presence of a signature, not trust in it: the label
                            names who signed, and says nothing about the chain. */}
                        <td title={i.signerSubject ?? undefined}>{signerLabel(i.signerSubject, i.signatureStatus)}</td>
                        <td>{registryViewLabel(i.architecture)}</td>
                        <td>{new Date(i.collectedAt).toLocaleString()}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              {installs.totalCount > installs.items.length && (
                <div className="pagination">
                  <span>
                    Showing the first {installs.items.length} of {installs.totalCount} installations
                  </span>
                </div>
              )}
            </>
          )}
        </div>
      )}
    </>
  )
}
