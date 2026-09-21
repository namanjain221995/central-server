import { useEffect, useMemo, useState } from 'react'
import { getAccessLevels, type AccessLevelsResponse } from '../api/client'
import { Icon } from '../components/Icon'

/**
 * What each access level can and cannot do.
 *
 * Read-only by design. The four built-in roles are reconciled from code on every
 * deployment and cannot be edited through the console, so this page exists to
 * answer "what does this role actually grant?" — the question an administrator
 * has to answer before handing someone an account.
 *
 * Everything rendered here comes from the server. No permission key, category or
 * denial list is written in TypeScript: a second copy of the permission model
 * would drift from the one the API enforces, and nothing would catch it.
 */
export function AccessLevelsPanel() {
  const [data, setData] = useState<AccessLevelsResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [selectedKey, setSelectedKey] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    getAccessLevels()
      .then((response) => {
        if (cancelled) return
        setData(response)
        setSelectedKey((current) => current ?? response.accessLevels[0]?.key ?? null)
      })
      .catch((cause: unknown) => {
        if (!cancelled) setError(cause instanceof Error ? cause.message : 'Could not load access levels.')
      })

    return () => {
      cancelled = true
    }
  }, [])

  const selected = useMemo(
    () => data?.accessLevels.find((level) => level.key === selectedKey) ?? null,
    [data, selectedKey],
  )

  // A set, so each of the 41 rows is a lookup rather than a scan of the array.
  const granted = useMemo(
    () => new Set(selected?.permissionKeys ?? []),
    [selected],
  )

  if (error) {
    return (
      <div className="card">
        <div className="empty-state">
          <div className="icon" aria-hidden="true"><Icon name="alert" size={22} /></div>
          <div className="title">Could not load access levels</div>
          <p>{error}</p>
        </div>
      </div>
    )
  }

  if (!data || !selected) {
    return <div className="card"><div className="loading" style={{ padding: 24 }}>Loading…</div></div>
  }

  return (
    <div className="split">
      <div className="card split-aside">
        <h2>Access levels</h2>
        <p className="lede" style={{ marginBottom: 12 }}>
          {data.accessLevels.length} built-in levels. These are fixed: they are reconciled
          from the platform's own definition on every deployment and cannot be edited here.
        </p>

        {data.accessLevels.map((level) => (
          <button
            key={level.key}
            type="button"
            className="list-item"
            aria-selected={level.key === selectedKey}
            onClick={() => setSelectedKey(level.key)}
          >
            <span style={{ fontWeight: 600 }}>{level.displayName}</span>
            <span className="badge neutral plain">
              {level.holdsEveryPermission ? 'all' : `${level.grantedCount}/${data.totalPermissions}`}
            </span>
          </button>
        ))}
      </div>

      <div className="card split-main">
        <div className="card-header">
          <div>
            <h2 style={{ marginBottom: 4 }}>{selected.displayName}</h2>
            <p className="lede" style={{ margin: 0 }}>{selected.description}</p>
          </div>
        </div>

        {selected.holdsEveryPermission ? (
          // Stated rather than shown as 41 ticks. This role's permissions are
          // computed as "the whole catalogue", precisely so a permission added in
          // future can never be accidentally withheld from it — a tick list would
          // quietly stop being the whole story the next time one is added.
          <div className="warn-banner" role="status">
            <strong>Holds every permission, by definition.</strong> This level is defined as
            the entire catalogue rather than as a fixed list, so any capability added to
            the platform in future is granted to it automatically.
          </div>
        ) : (
          <p className="lede">
            Grants <strong>{selected.grantedCount}</strong> of {data.totalPermissions} permissions.
            Denied <strong>{selected.deniedCount}</strong>.
          </p>
        )}

        {data.categories.map((category) => (
          <section key={category.name} className="card-section">
            <h3 style={{ fontSize: 13, margin: '0 0 8px' }}>{category.name}</h3>

            <div className="table-wrap">
              <table className="table">
                <caption className="sr-only">
                  {category.name} permissions, and whether {selected.displayName} holds each one
                </caption>
                <thead>
                  <tr>
                    <th scope="col" style={{ width: 96 }}>Access</th>
                    <th scope="col">Capability</th>
                    <th scope="col" style={{ width: 92 }}>Risk</th>
                  </tr>
                </thead>
                <tbody>
                  {category.permissions.map((permission) => {
                    const held = granted.has(permission.key)

                    return (
                      <tr key={permission.key}>
                        <td>
                          {/*
                            The word, not a tick or a colour. It has to survive
                            greyscale and be read aloud by a screen reader, which is
                            the same rule SecurityPage follows for Pass/Fail.
                          */}
                          <span className={held ? 'badge ok' : 'badge neutral'}>
                            {held ? 'Allowed' : 'Denied'}
                          </span>
                        </td>
                        <td>
                          <div>{permission.description}</div>
                          <code style={{ fontSize: 11, color: 'var(--color-text-muted)' }}>
                            {permission.key}
                          </code>
                        </td>
                        <td>
                          {permission.highRisk ? (
                            <span className="badge warn">High</span>
                          ) : (
                            <span className="badge neutral plain">Normal</span>
                          )}
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          </section>
        ))}
      </div>
    </div>
  )
}
