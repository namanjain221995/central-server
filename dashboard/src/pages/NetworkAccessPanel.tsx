import { useEffect, useState } from 'react'
import {
  ApiError,
  getNetworkAccessPolicy,
  updateNetworkAccessPolicy,
  type NetworkAccessPolicy,
} from '../api/client'
import { Icon } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import {
  callerCoverage,
  describeLastChanged,
  normalizeEntry,
  validateCidrInput,
} from './networkAccessView'

/**
 * Restrict this console to approved networks.
 *
 * The policy is the server's: when enabled, the admin console and Admin API
 * answer only from the listed IPv4/IPv6 ranges, plus the server's own loopback
 * as break-glass. Managed devices and the Agent API are never affected — this
 * page says so outright, because "network allowlist" otherwise reads like it
 * could cut a fleet off.
 *
 * Two lockout checks guard the save. This panel refuses to save a policy whose
 * list verifiably misses the caller's own IPv4 address, with a one-click way to
 * add it; the server re-checks for real (including IPv6, which this client only
 * warns about) and its 400 problem detail — which names the caller's address —
 * is shown verbatim. The whole panel hides silently on 403, the same convention
 * as the fleet-accounts card: the tab is already permission-gated, so a 403
 * here means the client's idea of the permission is stale.
 */
export function NetworkAccessPanel() {
  const [policy, setPolicy] = useState<NetworkAccessPolicy | null>(null)
  const [forbidden, setForbidden] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)

  // The draft being edited, seeded from the server copy on load and re-seeded
  // from every successful save, so "dirty" is always relative to what is live.
  const [enabled, setEnabled] = useState(false)
  const [entries, setEntries] = useState<string[]>([])

  const [newEntry, setNewEntry] = useState('')
  const [addError, setAddError] = useState<string | null>(null)

  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [confirming, setConfirming] = useState(false)

  useEffect(() => {
    let cancelled = false
    getNetworkAccessPolicy()
      .then((loaded) => {
        if (cancelled) return
        setPolicy(loaded)
        setEnabled(loaded.enabled)
        setEntries(loaded.allowedCidrs)
      })
      .catch((cause) => {
        if (cancelled) return
        if (cause instanceof ApiError && cause.status === 403) {
          setForbidden(true)
        } else {
          setLoadError('Could not load the network access policy.')
        }
      })
    return () => { cancelled = true }
  }, [])

  if (forbidden) return null

  if (loadError) {
    return (
      <div className="card">
        <div className="empty-state">
          <div className="icon" aria-hidden="true"><Icon name="alert" size={22} /></div>
          <div className="title">Could not load network access</div>
          <p>{loadError}</p>
        </div>
      </div>
    )
  }

  if (policy === null) {
    return <div className="loading">Loading network access…</div>
  }

  const coverage = callerCoverage(entries, policy.callerIp)
  // Only a verifiable lockout blocks the save: the caller's IPv4 address is in
  // no entry. IPv6 gets a warning instead — the server still refuses for real.
  const blocked = enabled && coverage === 'not-covered'
  const dirty =
    enabled !== policy.enabled ||
    entries.length !== policy.allowedCidrs.length ||
    entries.some((entry, index) => entry !== policy.allowedCidrs[index])

  const saveProblem = blocked
    ? 'Add your current address first, or saving would lock you out.'
    : !dirty
      ? 'No changes to save.'
      : null

  const lastChanged = describeLastChanged(policy.updatedAtUtc, policy.updatedBy)

  function addEntry(raw: string) {
    const entry = normalizeEntry(raw)
    const problem = validateCidrInput(entry)
    if (problem !== null) {
      setAddError(problem)
      return
    }
    if (entries.some((existing) => normalizeEntry(existing).toLowerCase() === entry.toLowerCase())) {
      setAddError('That network is already in the list.')
      return
    }
    setEntries((current) => [...current, entry])
    setNewEntry('')
    setAddError(null)
    setNotice(null)
  }

  function removeEntry(index: number) {
    setEntries((current) => current.filter((_, i) => i !== index))
    setNotice(null)
  }

  async function save() {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      const saved = await updateNetworkAccessPolicy({
        enabled,
        allowedCidrs: entries.map(normalizeEntry).filter((entry) => entry.length > 0),
      })
      setPolicy(saved)
      setEnabled(saved.enabled)
      setEntries(saved.allowedCidrs)
      setNotice(
        saved.enabled
          ? 'Saved. The console now answers only from the allowed networks.'
          : 'Saved. The console accepts sign-ins from any network.',
      )
    } catch (cause) {
      if (cause instanceof ApiError && cause.status === 403) {
        setForbidden(true)
        return
      }
      // The server's own refusal, verbatim — on a lockout it names the
      // caller's address, which is exactly what the operator needs to see.
      setError(
        cause instanceof ApiError
          ? (cause.detail ?? cause.message)
          : 'The policy could not be saved.',
      )
    } finally {
      setBusy(false)
    }
  }

  function onSave() {
    if (busy || saveProblem !== null) return
    // Turning the restriction on is the moment connections start being refused,
    // so that transition alone is confirmed; edits while it is already on, and
    // turning it off, save directly.
    if (enabled && !policy?.enabled) {
      setConfirming(true)
    } else {
      void save()
    }
  }

  return (
    <>
      {error && (
        <div className="error-banner" role="alert"><Icon name="alert" size={15} /><span>{error}</span></div>
      )}
      {notice && (
        <div className="notice-banner" role="status"><Icon name="check" size={15} /><span>{notice}</span></div>
      )}

      <div className="page-header">
        <div className="lede">
          Restrict this admin console — sign-in and the admin API — to approved networks.
          Managed devices and their agents are never affected, and signing in on the server
          itself (loopback) always works as break-glass.
        </div>
      </div>

      <div className="card card-section">
        <p style={{ marginTop: 0 }}>
          <Icon name="info" size={15} /> Your current address:{' '}
          <strong><code>{policy.callerIp}</code></strong>
        </p>

        <div className="field full">
          <label className="check-row">
            <input
              type="checkbox"
              checked={enabled}
              onChange={(e) => { setEnabled(e.target.checked); setNotice(null) }}
            />
            {' '}Restrict console to allowed networks
          </label>
          {!enabled && (
            <span className="field-hint">
              Off: the console answers from any network. The list below is kept, but not enforced.
            </span>
          )}
        </div>

        {blocked && (
          <div className="warn-banner" role="alert">
            <p style={{ marginTop: 0 }}>
              Your current address <code>{policy.callerIp}</code> is not in the allowed list.
              Saving this would lock you out of the console, so Save is disabled until the
              list covers you.
            </p>
            <button type="button" className="btn-primary btn-sm" onClick={() => addEntry(policy.callerIp)}>
              <Icon name="plus" size={14} /> Add my current address
            </button>
          </div>
        )}

        {enabled && coverage === 'unverifiable' && (
          <div className="warn-banner" role="status">
            Could not verify that your current address <code>{policy.callerIp}</code> is covered —
            IPv6 ranges are not checked here. The server will still refuse a change that would
            lock you out.
          </div>
        )}

        <div className="field full">
          <span className="field-label">Allowed networks</span>
          <span className="field-hint">
            IPv4 or IPv6, as a CIDR range (e.g. 203.0.113.0/24) or a single address
            (e.g. 203.0.113.7, stored as /32).
          </span>

          <div className="toolbar" style={{ flexWrap: 'wrap' }}>
            <input
              value={newEntry}
              autoComplete="off"
              aria-label="Network to allow"
              placeholder="e.g. 203.0.113.0/24"
              style={{ maxWidth: 280 }}
              onChange={(e) => { setNewEntry(e.target.value); setAddError(null) }}
              onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addEntry(newEntry) } }}
            />
            <button type="button" className="btn-ghost btn-sm" onClick={() => addEntry(newEntry)}>
              <Icon name="plus" size={14} /> Add
            </button>
          </div>
          {addError && <span className="field-message">{addError}</span>}

          {entries.length === 0 ? (
            <p className="muted" style={{ marginBottom: 0 }}>
              No networks in the list{enabled ? ' — with the restriction on, only the server itself could connect' : ''}.
            </p>
          ) : (
            <div className="table-wrap" style={{ maxHeight: 280, overflowY: 'auto' }}>
              <table className="table">
                <caption className="sr-only">Allowed networks</caption>
                <thead>
                  <tr>
                    <th scope="col">Network</th>
                    <th scope="col" style={{ width: 90 }} aria-label="Actions" />
                  </tr>
                </thead>
                <tbody>
                  {entries.map((entry, index) => (
                    <tr key={`${entry}-${index}`}>
                      <td><code>{entry}</code></td>
                      <td>
                        <button
                          type="button"
                          className="btn-ghost btn-sm"
                          onClick={() => removeEntry(index)}
                        >
                          <Icon name="trash" size={14} /> Remove
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>

        <div className="toolbar" style={{ marginTop: 12 }}>
          {lastChanged !== null && <span className="muted">{lastChanged}</span>}
          <span className="spacer" />
          <button
            type="button"
            className={`btn-accent${busy ? ' btn-loading' : ''}`}
            disabled={busy || saveProblem !== null}
            title={saveProblem ?? undefined}
            onClick={onSave}
          >
            Save
          </button>
        </div>
      </div>

      {confirming && (
        <ConfirmDialog
          title="Restrict console access?"
          confirmLabel="Restrict console"
          onCancel={() => setConfirming(false)}
          onConfirm={() => { setConfirming(false); void save() }}
        >
          Sign-ins and admin API calls from outside the {entries.length} allowed{' '}
          {entries.length === 1 ? 'network' : 'networks'} will be refused, for every
          administrator, as soon as this is saved. Managed devices and their agents are
          not affected, and loopback on the server keeps working as break-glass.
        </ConfirmDialog>
      )}
    </>
  )
}
