import { describe, expect, it } from 'vitest'
import {
  callerCoverage,
  cidrContainsIpv4,
  describeLastChanged,
  ipv4ToInt,
  isIpv6Entry,
  normalizeEntry,
  validateCidrInput,
} from '../../pages/networkAccessView'

describe('ipv4ToInt', () => {
  it('converts a dotted quad to the unsigned 32-bit value', () => {
    expect(ipv4ToInt('0.0.0.0')).toBe(0)
    expect(ipv4ToInt('0.0.0.1')).toBe(1)
    expect(ipv4ToInt('192.168.8.96')).toBe(192 * 256 ** 3 + 168 * 256 ** 2 + 8 * 256 + 96)
    expect(ipv4ToInt('255.255.255.255')).toBe(4294967295)
  })

  it('keeps the comparison unsigned: 128.0.0.0 sits above 127.255.255.255', () => {
    expect(ipv4ToInt('128.0.0.0')!).toBeGreaterThan(ipv4ToInt('127.255.255.255')!)
  })

  it('answers null, never a throw, for anything that is not a plain IPv4 address', () => {
    expect(ipv4ToInt('')).toBeNull()
    expect(ipv4ToInt('1.2.3')).toBeNull()
    expect(ipv4ToInt('1.2.3.4.5')).toBeNull()
    expect(ipv4ToInt('1.2.3.256')).toBeNull()
    expect(ipv4ToInt('1.2.3.x')).toBeNull()
    // parseInt would wave these through; the strict digit check must not.
    expect(ipv4ToInt('1.2.3.1e1')).toBeNull()
    expect(ipv4ToInt('1.2.3.-4')).toBeNull()
    expect(ipv4ToInt('1.2.3.0004')).toBeNull()
    expect(ipv4ToInt('2001:db8::1')).toBeNull()
    expect(ipv4ToInt('not an ip')).toBeNull()
  })
})

describe('cidrContainsIpv4', () => {
  it('matches inside the range and refuses outside it', () => {
    expect(cidrContainsIpv4('203.0.113.0/24', '203.0.113.7')).toBe(true)
    expect(cidrContainsIpv4('203.0.113.0/24', '203.0.113.255')).toBe(true)
    expect(cidrContainsIpv4('203.0.113.0/24', '203.0.114.1')).toBe(false)
    expect(cidrContainsIpv4('10.0.0.0/8', '10.255.0.1')).toBe(true)
    expect(cidrContainsIpv4('10.0.0.0/8', '11.0.0.1')).toBe(false)
  })

  it('treats a bare IP as the single-host range the server stores it as', () => {
    expect(cidrContainsIpv4('198.51.100.7', '198.51.100.7')).toBe(true)
    expect(cidrContainsIpv4('198.51.100.7', '198.51.100.8')).toBe(false)
    expect(cidrContainsIpv4('198.51.100.7/32', '198.51.100.7')).toBe(true)
  })

  it('handles the edge prefixes: /0 covers everything, /31 covers the pair', () => {
    expect(cidrContainsIpv4('0.0.0.0/0', '8.8.8.8')).toBe(true)
    expect(cidrContainsIpv4('192.0.2.0/31', '192.0.2.1')).toBe(true)
    expect(cidrContainsIpv4('192.0.2.0/31', '192.0.2.2')).toBe(false)
  })

  it('is safe on malformed input: false, never a throw', () => {
    expect(cidrContainsIpv4('', '203.0.113.7')).toBe(false)
    expect(cidrContainsIpv4('garbage', '203.0.113.7')).toBe(false)
    expect(cidrContainsIpv4('203.0.113.0/', '203.0.113.7')).toBe(false)
    expect(cidrContainsIpv4('203.0.113.0/abc', '203.0.113.7')).toBe(false)
    expect(cidrContainsIpv4('203.0.113.0/33', '203.0.113.7')).toBe(false)
    expect(cidrContainsIpv4('203.0.113.0/24', 'garbage')).toBe(false)
    expect(cidrContainsIpv4('203.0.113.0/24', '')).toBe(false)
    // IPv6 is deliberately out of scope for the client-side check.
    expect(cidrContainsIpv4('2001:db8::/32', '203.0.113.7')).toBe(false)
    expect(cidrContainsIpv4('203.0.113.0/24', '2001:db8::1')).toBe(false)
  })

  it('tolerates surrounding whitespace the way the editor normalizes it', () => {
    expect(cidrContainsIpv4(' 203.0.113.0/24 ', ' 203.0.113.7 ')).toBe(true)
  })
})

describe('validateCidrInput', () => {
  it('accepts plausible IPv4 — bare or CIDR', () => {
    expect(validateCidrInput('203.0.113.7')).toBeNull()
    expect(validateCidrInput('203.0.113.0/24')).toBeNull()
    expect(validateCidrInput('0.0.0.0/0')).toBeNull()
    expect(validateCidrInput('  10.0.0.1 ')).toBeNull()
  })

  it('accepts plausible IPv6 — bare or CIDR — without truly parsing it', () => {
    expect(validateCidrInput('2001:db8::1')).toBeNull()
    expect(validateCidrInput('2001:db8::/32')).toBeNull()
    expect(validateCidrInput('::1')).toBeNull()
    expect(validateCidrInput('fe80::1/64')).toBeNull()
  })

  it('names the problem for what is certainly wrong', () => {
    expect(validateCidrInput('')).toBe('Enter an IP address or CIDR range.')
    expect(validateCidrInput('   ')).toBe('Enter an IP address or CIDR range.')
    expect(validateCidrInput('203.0.113.0/abc')).toBe('The prefix length after "/" must be a number.')
    expect(validateCidrInput('203.0.113.0/')).toBe('The prefix length after "/" must be a number.')
    expect(validateCidrInput('203.0.113.0/33')).toBe('An IPv4 prefix length must be between 0 and 32.')
    expect(validateCidrInput('2001:db8::/129')).toBe('An IPv6 prefix length must be between 0 and 128.')
    expect(validateCidrInput('2001:db8::g')).toBe('That does not look like an IPv6 address or CIDR range.')
    expect(validateCidrInput('2001::db8::1')).toBe('That does not look like an IPv6 address or CIDR range.')
    expect(validateCidrInput('256.1.1.1')).toContain('does not look like an IPv4 or IPv6 address')
    expect(validateCidrInput('office network')).toContain('does not look like an IPv4 or IPv6 address')
  })
})

describe('callerCoverage', () => {
  it('covers an exact match, with or without the single-host prefix spelt out', () => {
    expect(callerCoverage(['203.0.113.7'], '203.0.113.7')).toBe('covered')
    expect(callerCoverage(['203.0.113.7/32'], '203.0.113.7')).toBe('covered')
    expect(callerCoverage(['2001:db8::1'], '2001:DB8::1')).toBe('covered')
    expect(callerCoverage(['2001:db8::1/128'], '2001:db8::1')).toBe('covered')
  })

  it('covers an IPv4 caller inside an IPv4 range', () => {
    expect(callerCoverage(['10.0.0.0/8', '203.0.113.0/24'], '203.0.113.99')).toBe('covered')
  })

  it('blocks when the caller is verifiably outside every entry', () => {
    expect(callerCoverage(['10.0.0.0/8'], '203.0.113.7')).toBe('not-covered')
    expect(callerCoverage(['198.51.100.7'], '203.0.113.7')).toBe('not-covered')
  })

  it('blocks on an empty list for either family — an empty allowlist covers nobody', () => {
    expect(callerCoverage([], '203.0.113.7')).toBe('not-covered')
    expect(callerCoverage(['', '  '], '203.0.113.7')).toBe('not-covered')
    expect(callerCoverage([], '2001:db8::1')).toBe('not-covered')
  })

  it('only warns when IPv6 keeps it from being sure', () => {
    // IPv6 entries this client cannot test against an IPv4 caller…
    expect(callerCoverage(['2001:db8::/32'], '203.0.113.7')).toBe('unverifiable')
    // …and an IPv6 caller with no exact match anywhere.
    expect(callerCoverage(['10.0.0.0/8'], '2001:db8::1')).toBe('unverifiable')
    expect(callerCoverage(['2001:db8::/32'], '2001:db8::1')).toBe('unverifiable')
  })

  it('prefers a certain IPv4 cover over an untestable IPv6 entry', () => {
    expect(callerCoverage(['2001:db8::/32', '203.0.113.0/24'], '203.0.113.7')).toBe('covered')
  })

  it('never covers on a malformed entry', () => {
    expect(callerCoverage(['garbage', '203.0.113.0/33'], '203.0.113.7')).toBe('not-covered')
  })
})

describe('describeLastChanged', () => {
  it('is null until the policy has been written at least once', () => {
    expect(describeLastChanged(null, null)).toBeNull()
    expect(describeLastChanged(null, 'admin@example.com')).toBeNull()
  })

  it('names the author when the server knows one, in the viewer locale', () => {
    const at = '2026-10-24T09:30:00Z'
    const when = new Date(at).toLocaleString()
    expect(describeLastChanged(at, 'admin@example.com')).toBe(`Last changed ${when} by admin@example.com`)
    expect(describeLastChanged(at, null)).toBe(`Last changed ${when}`)
    expect(describeLastChanged(at, '  ')).toBe(`Last changed ${when}`)
  })
})

describe('normalizeEntry / isIpv6Entry', () => {
  it('trims and nothing more; the colon is the IPv6 tell', () => {
    expect(normalizeEntry('  203.0.113.7 ')).toBe('203.0.113.7')
    expect(isIpv6Entry('2001:db8::1')).toBe(true)
    expect(isIpv6Entry(' ::1 ')).toBe(true)
    expect(isIpv6Entry('203.0.113.7')).toBe(false)
  })
})
