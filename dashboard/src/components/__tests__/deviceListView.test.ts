import { describe, expect, it } from 'vitest'
import {
  defaultDeviceListView,
  deviceListViews,
  describeDeviceCount,
  statusFilterFor,
  connectivityFromParam,
  viewFromParam,
  type DeviceListView,
} from '../../pages/deviceListView'

/**
 * The lifecycle filter on the devices list.
 *
 * Small surface, but the failure it guards against is quiet: "All" is not a status
 * the API understands, it is the absence of one. Sending it literally returns an
 * empty page, which on a device list reads as "no devices" rather than as a bug --
 * and since retiring devices is what puts rows into the Retired view, an operator
 * would most likely hit it just after retiring something and conclude the record
 * had been deleted.
 */

describe('lifecycle filter', () => {
  it('sends Active and Retired through as-is', () => {
    expect(statusFilterFor('Active')).toBe('Active')
    expect(statusFilterFor('Retired')).toBe('Retired')
  })

  /** The one that matters: absence of a filter, not a filter for "All". */
  it('sends no status at all for All', () => {
    expect(statusFilterFor('All')).toBeUndefined()
  })

  it('offers exactly the three lifecycle views', () => {
    expect(deviceListViews).toEqual(['Active', 'Retired', 'All'])
  })

  /** Retired machines are history; the day-to-day estate is the default. */
  it('defaults to Active', () => {
    expect(defaultDeviceListView).toBe('Active')
    expect(statusFilterFor(defaultDeviceListView)).toBe('Active')
  })

  it('maps every offered view without falling through', () => {
    for (const view of deviceListViews) {
      const filter = statusFilterFor(view)
      expect(view === 'All' ? filter === undefined : filter === view).toBe(true)
    }
  })
})

describe('count line', () => {
  it('names the lifecycle state it is counting', () => {
    expect(describeDeviceCount('Active', 3)).toBe('3 active devices')
    expect(describeDeviceCount('Retired', 2)).toBe('2 retired devices')
  })

  /** "All" has no adjective — "3 all devices" would be nonsense. */
  it('drops the adjective for All', () => {
    expect(describeDeviceCount('All', 5)).toBe('5 devices')
  })

  it('singularises', () => {
    expect(describeDeviceCount('Active', 1)).toBe('1 active device')
    expect(describeDeviceCount('All', 1)).toBe('1 device')
  })

  it('handles an empty estate', () => {
    expect(describeDeviceCount('Retired', 0)).toBe('0 retired devices')
  })
})

describe('type safety', () => {
  it('accepts only the three views', () => {
    const views: DeviceListView[] = ['Active', 'Retired', 'All']
    expect(views.every((v) => deviceListViews.includes(v))).toBe(true)
  })
})

describe('filters from a link', () => {
  it('opens the view a dashboard tile asked for, in any case', () => {
    expect(viewFromParam('Retired')).toBe('Retired')
    expect(viewFromParam('all')).toBe('All')
  })

  it('opens the normal list for a missing or mistyped view, never an empty one', () => {
    expect(viewFromParam(null)).toBe(defaultDeviceListView)
    expect(viewFromParam('Deleted')).toBe(defaultDeviceListView)
  })

  it('reads online and offline, and nothing else', () => {
    expect(connectivityFromParam('online')).toBe('online')
    expect(connectivityFromParam('OFFLINE')).toBe('offline')
    expect(connectivityFromParam('sometimes')).toBe('')
    expect(connectivityFromParam(null)).toBe('')
  })
})
