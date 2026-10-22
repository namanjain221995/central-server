import type { ChromeExtensionRule, ChromeExtensionRuleMode } from '../api/client'
import type { RowActionsMenuItem } from '../components/RowActionsMenu'
import type { Tone } from './chromeView'

/**
 * The rules behind Chrome extension control, kept out of the components so each
 * is testable on its own (chromeView.ts does the same for the read-only side).
 * Nothing here talks to the server: these decide how a rule reads, which
 * actions a row offers, and whether a typed extension ID can possibly be one.
 */

/**
 * A Chrome Web Store extension ID is exactly 32 characters, each a letter from
 * a to p: it is a hash rendered in base-16 over those letters instead of 0-9a-f.
 * Anything else cannot name a Web Store extension, so it is refused before the
 * server ever sees it.
 */
const EXTENSION_ID_PATTERN = /^[a-p]{32}$/

/**
 * What a pasted ID is compared and submitted as. Trimmed because IDs arrive by
 * copy-paste, and lowercased because the letters are case-insensitive in the
 * store URL but Chrome's policy wants them lowercase.
 */
export function normalizeExtensionId(input: string): string {
  return input.trim().toLowerCase()
}

export function isValidExtensionId(input: string): boolean {
  return EXTENSION_ID_PATTERN.test(normalizeExtensionId(input))
}

/** The message under the ID field, or null when the input is a valid ID. */
export function extensionIdError(input: string): string | null {
  const id = normalizeExtensionId(input)
  if (!id) return 'Enter the extension ID from its Chrome Web Store URL.'
  if (!EXTENSION_ID_PATTERN.test(id)) {
    return 'An extension ID is exactly 32 letters, each between a and p — the last part of its Chrome Web Store URL.'
  }
  return null
}

/** How a rule's mode reads on a badge. */
export function ruleModeLabel(mode: ChromeExtensionRuleMode): string {
  switch (mode) {
    case 'ForceInstalled':
      return 'Forced'
    case 'Blocked':
      return 'Blocked'
    case 'Allowed':
      return 'Allowed'
    case 'Removed':
      return 'Removed'
  }
}

/**
 * Blocked is amber, matching how a disabled extension is toned: a state the
 * administrator chose, not a fault. Removed is red because it is the one mode
 * that destroys something on the device. Allowed enforces nothing beyond
 * permission, so it stays neutral.
 */
export function ruleModeTone(mode: ChromeExtensionRuleMode): Tone {
  switch (mode) {
    case 'ForceInstalled':
      return 'ok'
    case 'Blocked':
      return 'warn'
    case 'Removed':
      return 'crit'
    case 'Allowed':
      return 'neutral'
  }
}

/** The device's rule for one extension, or null when none is set. */
export function ruleForExtension(
  rules: readonly ChromeExtensionRule[] | null,
  extensionId: string,
): ChromeExtensionRule | null {
  return rules?.find((r) => r.extensionId === extensionId) ?? null
}

export type ChromeExtensionRuleAction = 'block' | 'allow' | 'remove' | 'clear'

/** The rule mode a menu action asks for. `clear` is a delete, not a mode. */
export function ruleModeForAction(action: Exclude<ChromeExtensionRuleAction, 'clear'>): ChromeExtensionRuleMode {
  switch (action) {
    case 'block':
      return 'Blocked'
    case 'allow':
      return 'Allowed'
    case 'remove':
      return 'Removed'
  }
}

/**
 * The per-extension action menu. Every entry is always present so the menu's
 * shape is steady row to row; an entry that cannot run says why instead of
 * vanishing. A Chrome component is not policy-controllable — Chrome's
 * extension policies do not apply to its own built-ins — so its mutating
 * entries are disabled rather than offered as controls that would do nothing;
 * Clear stays judged only by whether a rule exists, so a stray rule on a
 * component can still be removed.
 */
export function extensionRuleMenuItems(
  currentMode: ChromeExtensionRuleMode | null,
  isComponent: boolean,
): RowActionsMenuItem<ChromeExtensionRuleAction>[] {
  const componentReason = isComponent ? 'Part of Chrome itself; policy cannot change it' : null
  return [
    {
      key: 'block',
      label: 'Block',
      enabled: !isComponent && currentMode !== 'Blocked',
      reason: componentReason ?? (currentMode === 'Blocked' ? 'Already blocked' : null),
    },
    {
      key: 'allow',
      label: 'Allow',
      enabled: !isComponent && currentMode !== 'Allowed',
      reason: componentReason ?? (currentMode === 'Allowed' ? 'Already allowed' : null),
    },
    {
      key: 'remove',
      label: 'Remove',
      enabled: !isComponent && currentMode !== 'Removed',
      reason: componentReason ?? (currentMode === 'Removed' ? 'Already removed' : null),
      destructive: true,
    },
    {
      key: 'clear',
      label: 'Clear rule',
      enabled: currentMode !== null,
      reason: currentMode === null ? 'No rule is set' : null,
    },
  ]
}

/** What a rule names itself as in labels: the name when known, else the ID. */
function ruleSubject(name: string | null, extensionId: string): string {
  const trimmed = name?.trim()
  return trimmed ? trimmed : extensionId
}

/** The tracked task's label, e.g. `Block "uBlock Origin"`. */
export function ruleActionLabel(mode: ChromeExtensionRuleMode, name: string | null, extensionId: string): string {
  const verb =
    mode === 'ForceInstalled' ? 'Force-install' : mode === 'Blocked' ? 'Block' : mode === 'Allowed' ? 'Allow' : 'Remove'
  return `${verb} "${ruleSubject(name, extensionId)}"`
}

/** The tracked task's label for deleting a rule. */
export function clearRuleLabel(name: string | null, extensionId: string): string {
  return `Clear rule for "${ruleSubject(name, extensionId)}"`
}

/**
 * Named rules first, alphabetically, then nameless ones by ID, so the managed
 * list reads by what an operator knows the extensions as and the order is
 * stable between reloads.
 */
export function compareRules(a: ChromeExtensionRule, b: ChromeExtensionRule): number {
  if ((a.displayName === null) !== (b.displayName === null)) return a.displayName === null ? 1 : -1
  const byName = (a.displayName ?? '').localeCompare(b.displayName ?? '', undefined, { sensitivity: 'base' })
  return byName !== 0 ? byName : a.extensionId.localeCompare(b.extensionId)
}
