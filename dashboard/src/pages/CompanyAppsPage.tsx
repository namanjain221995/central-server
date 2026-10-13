import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { CatalogPanel } from './CatalogPanel'
import { CatalogInstallsPanel } from './CatalogInstallsPanel'

type Section = 'catalog' | 'installs'

const SECTIONS: { key: Section; label: string }[] = [
  { key: 'catalog', label: 'Catalog' },
  { key: 'installs', label: 'Install requests' },
]

/**
 * The Company Portal: what employees may install themselves, and what they
 * asked to install.
 */
export function CompanyAppsPage() {
  // ?section=installs opens a section directly, as the Software page allows.
  const [searchParams] = useSearchParams()
  const [section, setSection] = useState<Section>(
    () => SECTIONS.find((s) => s.key === searchParams.get('section'))?.key ?? 'catalog',
  )

  return (
    <>
      {/* A rule and a record, kept visibly apart: the catalogue says what may
          be installed, the requests say what was asked for. */}
      <div className="segmented" role="tablist" aria-label="Company Apps sections">
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

      {section === 'catalog' && <CatalogPanel />}
      {section === 'installs' && <CatalogInstallsPanel />}
    </>
  )
}
