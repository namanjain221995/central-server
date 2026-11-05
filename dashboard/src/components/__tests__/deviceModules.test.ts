import { describe, expect, it } from 'vitest'
import { MODULES, isModule } from '../../pages/deviceModules'

/**
 * The device page's module routing.
 *
 * These exist because of a real bug: the Drivers and BitLocker cards were added
 * to the tab type and to the feature grid, but not to the runtime whitelist the
 * URL guard checks. Clicking either card set `?m=drivers`, the guard rejected
 * it, the page fell back to the grid, and the click appeared to do nothing.
 *
 * TypeScript could not catch it. The whitelist was typed `Tab[]`, and a list
 * that omits some members of a union is still a valid array of that union — the
 * type said "everything here is a Tab", never "every Tab is here". The fix
 * derives the type from the list so the two cannot disagree; these tests hold
 * the runtime half of that guarantee.
 */
describe('device module routing', () => {
  it('accepts every module it declares', () => {
    for (const key of MODULES) {
      expect(isModule(key)).toBe(true)
    }
  })

  /**
   * Named explicitly rather than relying on the loop above. If someone removes
   * these from the list the loop still passes — it would simply iterate over a
   * shorter array — and the original bug returns silently.
   */
  it.each(['drivers', 'bitlocker'])('routes to the %s module', (key) => {
    expect(isModule(key)).toBe(true)
    expect(MODULES).toContain(key)
  })

  /** Every module the device page renders a panel for must be reachable. */
  it.each([
    'overview', 'hardware', 'network', 'users', 'groups', 'software',
    'security', 'usb', 'drivers', 'bitlocker', 'updates', 'services',
    'processes', 'files', 'actions', 'tasks',
  ])('keeps the %s module reachable', (key) => {
    expect(isModule(key)).toBe(true)
  })

  /**
   * The value comes from the address bar, so anything unrecognised must fall
   * back to the grid rather than being trusted into the render path.
   */
  it.each([null, '', 'nope', 'Drivers', 'DRIVERS', 'drivers ', '../etc/passwd', '__proto__'])(
    'rejects %j',
    (value) => {
      expect(isModule(value)).toBe(false)
    },
  )

  it('lists each module exactly once', () => {
    expect(new Set(MODULES).size).toBe(MODULES.length)
  })
})
