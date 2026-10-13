import type { CatalogAppRow, CatalogVisibility, InstallRequestRow } from '../api/client'

/**
 * Pure view logic for the Company Portal catalogue.
 *
 * Kept out of the components so the rules can be tested without a DOM, in the
 * same shape as softwareView and deploymentView.
 */

/** The server's limit on an icon upload. */
export const ICON_MAX_BYTES = 64 * 1024

/** A group as the Visibility column needs it. */
export interface GroupName {
  id: string
  name: string
}

export interface VisibilityDescription {
  /** The cell text: "All devices" or a count of groups. */
  label: string
  /** The group names, for the cell's tooltip; null when there is nothing to expand. */
  title: string | null
}

/**
 * Who can see an app, in words.
 *
 * The count is the cell and the names are the tooltip: thirty apps each listing
 * four department names would not scan. A group id the groups list no longer
 * has is still counted — it is in the app's rule, and the server decides what
 * it means — but is named as unknown rather than silently dropped.
 */
export function describeVisibility(
  app: Pick<CatalogAppRow, 'visibility' | 'groupIds'>,
  groups: readonly GroupName[],
): VisibilityDescription {
  if (app.visibility === 'AllDevices') return { label: 'All devices', title: null }

  const count = app.groupIds.length
  if (count === 0) return { label: 'No groups', title: null }

  const names = app.groupIds.map((id) => groups.find((g) => g.id === id)?.name ?? 'Unknown group')
  return { label: `${count} group${count === 1 ? '' : 's'}`, title: names.join(', ') }
}

/**
 * Where the browser fetches an app's icon. Versioned by updatedAt so a replaced
 * icon is not served from the cache of the one it replaced.
 */
export function iconUrl(app: Pick<CatalogAppRow, 'id' | 'updatedAt'>): string {
  return `/api/admin/v1/catalog/${encodeURIComponent(app.id)}/icon?v=${encodeURIComponent(app.updatedAt)}`
}

/** The letter a placeholder tile shows for an app with no icon. */
export function placeholderLetter(displayName: string): string {
  const first = displayName.trim().charAt(0)
  return first ? first.toUpperCase() : '?'
}

/** Catalogue order: by display name, which is where an employee would look for an app. */
export function sortCatalog(rows: readonly CatalogAppRow[]): CatalogAppRow[] {
  return [...rows].sort(
    (a, b) =>
      a.displayName.localeCompare(b.displayName, undefined, { sensitivity: 'base' }) ||
      a.packageVersion.localeCompare(b.packageVersion, undefined, { numeric: true }),
  )
}

export type OutcomeTone = 'ok' | 'warn' | 'crit' | 'info' | 'neutral'

/**
 * What happened to an install request, in words.
 *
 * Queued is only the start of the story: the task's own status says whether
 * the install then succeeded, so it is appended when the server has it. A task
 * still queued adds nothing and is not repeated.
 */
export function installOutcomeLabel(row: Pick<InstallRequestRow, 'outcome' | 'taskStatus'>): string {
  switch (row.outcome) {
    case 'Queued':
      return row.taskStatus && row.taskStatus !== 'Queued' ? `Queued · ${row.taskStatus}` : 'Queued'
    case 'AlreadyInstalled':
      return 'Already installed'
    case 'AlreadyInProgress':
      return 'Already in progress'
    case 'Refused':
      return 'Refused'
    default:
      return row.outcome
  }
}

/**
 * The badge tone for an install request. A refusal is the finding; a queued
 * request takes the tone of its task, so a failed install reads as a failure
 * and not as something still pending.
 */
export function outcomeTone(row: Pick<InstallRequestRow, 'outcome' | 'taskStatus'>): OutcomeTone {
  switch (row.outcome) {
    case 'Refused':
      return 'crit'
    case 'AlreadyInstalled':
      return 'neutral'
    case 'AlreadyInProgress':
      return 'info'
    case 'Queued':
      return taskTone(row.taskStatus)
    default:
      return 'neutral'
  }
}

function taskTone(status: string | null): OutcomeTone {
  switch (status) {
    case 'Succeeded':
      return 'ok'
    case 'Failed':
      return 'crit'
    case 'Expired':
    case 'Cancelled':
      return 'warn'
    default:
      return 'info'
  }
}

/** What the create/edit dialog has decided on when it saves. */
export interface CatalogFormInput {
  packageId: string
  displayName: string
  visibility: CatalogVisibility
  groupIds: readonly string[]
  /** The chosen icon file, if any. A File satisfies this. */
  icon: { name: string; type: string; size: number } | null
}

/**
 * The first thing wrong with the form, or null when it can be saved.
 *
 * The server validates all of this again and is the authority; this spares an
 * administrator a round trip for the mistakes that can be seen from here.
 */
export function validateCatalogForm(form: CatalogFormInput): string | null {
  if (!form.packageId) return 'Choose a package.'
  if (!form.displayName.trim()) return 'Enter the name employees will see.'
  if (form.visibility === 'SelectedGroups' && form.groupIds.length === 0) {
    return 'Select at least one group, or make the app visible to all devices.'
  }
  if (form.icon) {
    // Some platforms leave a picked file's type blank; the extension then has
    // to stand in for it, and the server still checks the bytes.
    const png = form.icon.type === 'image/png' || (!form.icon.type && /\.png$/i.test(form.icon.name))
    if (!png) return 'The icon must be a PNG file.'
    if (form.icon.size > ICON_MAX_BYTES) return 'The icon must be 64 KB or smaller.'
  }
  return null
}
