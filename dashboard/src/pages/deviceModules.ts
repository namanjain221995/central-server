/**
 * The device page's modules, and the guard that decides whether a `?m=` value
 * names one.
 *
 * The order here is the order the feature grid renders in.
 *
 * **Why the type is derived from the array rather than declared beside it.**
 * These were previously two separate declarations — a `Tab` union and a
 * `MODULES: Tab[]` whitelist — and adding a module to the union without adding
 * it to the array compiled cleanly, because a subset of `Tab[]` is a perfectly
 * valid `Tab[]`. The result was a card that set `?m=drivers`, a guard that
 * rejected it, and a click that silently did nothing. Deriving `DeviceModule`
 * from `MODULES` makes the array the single source of truth: a module that is
 * not listed here does not exist as a type, so the two cannot drift apart.
 */
export const MODULES = [
  'overview',
  'hardware',
  'network',
  'users',
  'groups',
  'software',
  'security',
  'usb',
  'drivers',
  'bitlocker',
  'updates',
  'services',
  'processes',
  'files',
  'actions',
  'tasks',
] as const

export type DeviceModule = (typeof MODULES)[number]

/**
 * Whether a query-string value names a module.
 *
 * The device page reads the open module from the URL so Back returns to the
 * grid and a module link can be shared. That makes this a boundary: the value
 * is whatever the address bar contains, so an unrecognised one must fall back
 * to the grid rather than being trusted.
 */
export function isModule(value: string | null): value is DeviceModule {
  return value !== null && (MODULES as readonly string[]).includes(value)
}
