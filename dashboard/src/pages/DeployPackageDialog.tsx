import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  createDeployment,
  getDevices,
  getGroups,
  previewDeployment,
  type DeploymentPlan,
} from '../api/client'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'
import {
  hasWorkToDo,
  planLines,
  selectableTargets,
  type TargetCandidate,
} from './deploymentView'

const DEVICE_PAGE_SIZE = 100

/**
 * Deploy a managed package to devices or groups.
 *
 * Replaces a `window.prompt` that asked for a hostname. Hostnames are display
 * text — they repeat across a fleet and they change — so targeting now selects
 * DeviceIds and GroupIds, which is also what the server authorizes against.
 *
 * Nothing here is authoritative. The server re-resolves every id against the
 * caller's organization and device scope, re-checks the package is deployable,
 * excludes retired devices, and decides eligibility itself. This dialog only
 * shows the operator what that decision will be before they commit to it.
 */
export function DeployPackageDialog({
  packageId,
  packageLabel,
  onCancel,
  onDeployed,
}: {
  packageId: string
  packageLabel: string
  onCancel: () => void
  onDeployed: (deploymentId: string) => void
}) {
  useDialogDismiss(onCancel)

  const [mode, setMode] = useState<'devices' | 'groups'>('devices')
  const [devices, setDevices] = useState<TargetCandidate[] | null>(null)
  const [groups, setGroups] = useState<{ id: string; name: string; deviceCount: number }[]>([])
  const [selectedDevices, setSelectedDevices] = useState<string[]>([])
  const [selectedGroups, setSelectedGroups] = useState<string[]>([])
  const [search, setSearch] = useState('')
  const [deviceTotal, setDeviceTotal] = useState(0)

  const [plan, setPlan] = useState<DeploymentPlan | null>(null)
  const [planning, setPlanning] = useState(false)
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Searched and filtered server-side, a page at a time. Pulling the whole fleet
  // into the browser to filter it here would be 350 rows for one operator action
  // and would get worse as the estate grows.
  useEffect(() => {
    let cancelled = false

    getDevices(1, DEVICE_PAGE_SIZE, search, 'Active')
      .then((result) => {
        if (cancelled) return
        setDevices(selectableTargets(result.items.map((d) => ({
          id: d.id,
          hostname: d.hostname,
          displayName: d.displayName,
          status: d.status,
          agentVersion: d.agentVersion,
          lastSeenAt: d.lastSeenAt,
        }))))
        setDeviceTotal(result.totalCount)
      })
      .catch(() => {
        if (!cancelled) setError('Could not load devices.')
      })

    return () => {
      cancelled = true
    }
  }, [search])

  useEffect(() => {
    getGroups()
      .then((rows) => setGroups(rows.map((g) => ({
        id: g.id, name: g.name, deviceCount: g.deviceCount,
      }))))
      .catch(() => setGroups([]))
  }, [])

  const deviceIds = mode === 'devices' ? selectedDevices : []
  const groupIds = mode === 'groups' ? selectedGroups : []
  const hasSelection = deviceIds.length > 0 || groupIds.length > 0

  // Re-planned whenever the selection changes: the numbers an operator commits
  // to must describe the selection in front of them, not an earlier one.
  const refreshPlan = useCallback(async () => {
    if (!hasSelection) {
      setPlan(null)
      return
    }

    setPlanning(true)
    try {
      setPlan(await previewDeployment(packageId, deviceIds, groupIds))
      setError(null)
    } catch {
      setPlan(null)
      setError('Could not work out what this deployment would do.')
    } finally {
      setPlanning(false)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [packageId, hasSelection, deviceIds.join(','), groupIds.join(',')])

  useEffect(() => {
    void refreshPlan()
  }, [refreshPlan])

  const visible = useMemo(() => devices ?? [], [devices])

  function toggle(list: string[], id: string, set: (next: string[]) => void) {
    set(list.includes(id) ? list.filter((x) => x !== id) : [...list, id])
  }

  async function onSubmit() {
    setSubmitting(true)
    try {
      const created = await createDeployment(packageId, deviceIds, groupIds)
      onDeployed(created.deploymentId)
    } catch {
      setError('The deployment could not be created.')
      setSubmitting(false)
    }
  }

  return (
    <div className="overlay">
      <div className="dialog" role="dialog" aria-modal="true" aria-label="Deploy software">
        <div className="dialog-header">
          <h2>Deploy software</h2>
          <div className="sub">{packageLabel}</div>
        </div>

        <div className="dialog-body">
          {error && (
            <div className="error-banner" role="alert">
              <Icon name="alert" size={15} />
              <span>{error}</span>
            </div>
          )}

          <div className="field">
            <span className="label">Target</span>
            <div className="radio-row">
              <label>
                <input
                  type="radio"
                  name="target-mode"
                  checked={mode === 'devices'}
                  onChange={() => setMode('devices')}
                />
                Devices
              </label>
              <label>
                <input
                  type="radio"
                  name="target-mode"
                  checked={mode === 'groups'}
                  onChange={() => setMode('groups')}
                />
                Groups
              </label>
            </div>
          </div>

          {mode === 'devices' && (
            <>
              <div className="input-search">
                <Icon name="search" size={15} className="search-icon" />
                <input
                  type="search"
                  placeholder="Search devices…"
                  aria-label="Search devices"
                  value={search}
                  onChange={(e) => setSearch(e.target.value)}
                />
              </div>

              {devices === null && <div className="loading">Loading devices…</div>}

              {devices !== null && visible.length === 0 && (
                <div className="empty-state">
                  <div className="title">No devices match</div>
                  <div>Retired devices are not shown: they cannot receive a deployment.</div>
                </div>
              )}

              {visible.length > 0 && (
                <div className="table-wrap" style={{ maxHeight: 280, overflowY: 'auto' }}>
                  <table className="table">
                    <thead>
                      <tr>
                        <th style={{ width: 32 }}></th>
                        <th>Device</th>
                        <th>Agent</th>
                        <th>Last seen</th>
                      </tr>
                    </thead>
                    <tbody>
                      {visible.map((d) => (
                        <tr key={d.id}>
                          <td>
                            <input
                              type="checkbox"
                              aria-label={`Select ${d.hostname}`}
                              checked={selectedDevices.includes(d.id)}
                              onChange={() => toggle(selectedDevices, d.id, setSelectedDevices)}
                            />
                          </td>
                          <td>{d.displayName ?? d.hostname}</td>
                          <td>{d.agentVersion}</td>
                          <td>{d.lastSeenAt ? new Date(d.lastSeenAt).toLocaleDateString() : '—'}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}

              {deviceTotal > visible.length && (
                <p className="group-note">
                  Showing {visible.length} of {deviceTotal} active devices. Narrow the search to
                  reach the rest, or target a group.
                </p>
              )}
            </>
          )}

          {mode === 'groups' && (
            <div className="table-wrap" style={{ maxHeight: 280, overflowY: 'auto' }}>
              <table className="table">
                <thead>
                  <tr>
                    <th style={{ width: 32 }}></th>
                    <th>Group</th>
                    <th>Devices</th>
                  </tr>
                </thead>
                <tbody>
                  {groups.map((g) => (
                    <tr key={g.id}>
                      <td>
                        <input
                          type="checkbox"
                          aria-label={`Select ${g.name}`}
                          checked={selectedGroups.includes(g.id)}
                          onChange={() => toggle(selectedGroups, g.id, setSelectedGroups)}
                        />
                      </td>
                      <td>{g.name}</td>
                      <td>{g.deviceCount}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {/* What the server would actually do. Computed by the server, so it
              cannot promise something the deployment then declines to do. */}
          {hasSelection && (
            <div className="callout" style={{ marginTop: 16 }}>
              <div className="label">Resolution</div>
              {planning && !plan && <div className="loading">Working out what is needed…</div>}
              {plan && (
                <dl className="detail-grid" style={{ margin: '10px 0 0' }}>
                  {planLines(plan).map((line) => (
                    <div key={line.label}>
                      <dt>{line.label}</dt>
                      <dd>{line.value}</dd>
                    </div>
                  ))}
                </dl>
              )}
              {plan && !hasWorkToDo(plan) && (
                <p className="group-note" style={{ marginTop: 10 }}>
                  Every selected device already has this version. Nothing would be installed.
                </p>
              )}
            </div>
          )}
        </div>

        <div className="dialog-footer">
          <button type="button" className="btn-sm" onClick={onCancel} disabled={submitting}>
            Cancel
          </button>
          <button
            type="button"
            className="btn-primary"
            disabled={submitting || planning || !hasWorkToDo(plan)}
            onClick={() => void onSubmit()}
          >
            {submitting ? 'Deploying…' : `Deploy to ${plan?.needsInstall ?? 0} device${plan?.needsInstall === 1 ? '' : 's'}`}
          </button>
        </div>
      </div>
    </div>
  )
}
