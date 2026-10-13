import type { CatalogAppRow } from '../api/client'
import { iconUrl, placeholderLetter } from '../pages/catalogView'

/**
 * A catalogue app's icon as the Company Portal shows it: a square tile. An app
 * with no icon shows its initial in the same box, so a column of apps keeps one
 * shape whether or not every icon has been uploaded.
 *
 * Decorative: the name is always written beside it.
 */
export function AppTileIcon({
  app,
  large = false,
}: {
  app: Pick<CatalogAppRow, 'id' | 'updatedAt' | 'hasIcon' | 'displayName'>
  large?: boolean
}) {
  return (
    <span className={large ? 'app-tile-icon is-large' : 'app-tile-icon'} aria-hidden="true">
      {app.hasIcon ? <img src={iconUrl(app)} alt="" /> : placeholderLetter(app.displayName)}
    </span>
  )
}
