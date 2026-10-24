// Pure view-model for the Network access settings panel: light entry validation
// and the client-side "would this lock me out?" check. Kept out of the panel so
// every branch is unit-tested without rendering — mirrors accountsView.
//
// None of this is the enforcement. The server parses every range strictly and
// refuses a policy that would lock its caller out; what lives here only has to
// be good enough to stop the obvious mistake before the round-trip, and to stay
// honest about what it cannot verify (IPv6 containment).

/** Trimmed, ready for comparing and sending. Case is left alone — the server canonicalises. */
export function normalizeEntry(raw: string): string {
  return raw.trim()
}

/**
 * The dotted-quad as an unsigned 32-bit integer, or null when it is not a
 * plain IPv4 address (wrong shape, a part out of range, leading signs, etc.).
 * Unsigned so that 128.0.0.0 compares above 127.255.255.255 like addresses do.
 */
export function ipv4ToInt(ip: string): number | null {
  const parts = ip.split('.')
  if (parts.length !== 4) return null

  let value = 0
  for (const part of parts) {
    // Digits only: parseInt would wave through "1e2", " 5" or "3x".
    if (!/^\d{1,3}$/.test(part)) return null
    const octet = Number(part)
    if (octet > 255) return null
    value = value * 256 + octet
  }

  return value
}

/** An entry split into address and prefix, or null when it is not address[/prefix] shaped. */
function splitPrefix(entry: string): { address: string; prefix: number | null } | null {
  const slash = entry.indexOf('/')
  if (slash === -1) return { address: entry, prefix: null }

  const prefixPart = entry.slice(slash + 1)
  if (!/^\d{1,3}$/.test(prefixPart)) return null

  return { address: entry.slice(0, slash), prefix: Number(prefixPart) }
}

/**
 * Whether one allowlist entry — a bare IPv4 (treated as /32) or an IPv4 CIDR —
 * contains the given IPv4 address. False, never a throw, for anything it cannot
 * parse: a malformed entry, an IPv6 entry, or a non-IPv4 `ip`. This is the whole
 * client-side containment check; IPv6 is deliberately left to the server.
 */
export function cidrContainsIpv4(entry: string, ip: string): boolean {
  const ipValue = ipv4ToInt(normalizeEntry(ip))
  if (ipValue === null) return false

  const parts = splitPrefix(normalizeEntry(entry))
  if (parts === null) return false

  const baseValue = ipv4ToInt(parts.address)
  if (baseValue === null) return false

  const prefix = parts.prefix ?? 32
  if (prefix < 0 || prefix > 32) return false

  // A /0 covers everything; stated outright because <<32 is a no-op shift in JS.
  if (prefix === 0) return true

  // Compare the top `prefix` bits. >>> keeps the arithmetic unsigned.
  const shift = 32 - prefix
  return (ipValue >>> shift) === (baseValue >>> shift)
}

/** True when the entry can only be IPv6 — the one cheap, reliable tell is a colon. */
export function isIpv6Entry(entry: string): boolean {
  return normalizeEntry(entry).includes(':')
}

/**
 * Light validation for a new allowlist entry, as the message under the input —
 * or null when it is plausible. Deliberately forgiving: the server owns exact
 * validation, this only catches what is certainly not an IP or CIDR. IPv6 is
 * checked for shape (hex groups, at most one '::', prefix ≤ 128), not parsed.
 */
export function validateCidrInput(raw: string): string | null {
  const entry = normalizeEntry(raw)
  if (entry.length === 0) {
    return 'Enter an IP address or CIDR range.'
  }

  const parts = splitPrefix(entry)
  if (parts === null) {
    return 'The prefix length after "/" must be a number.'
  }

  if (isIpv6Entry(entry)) {
    if (parts.prefix !== null && parts.prefix > 128) {
      return 'An IPv6 prefix length must be between 0 and 128.'
    }
    if (
      !/^[0-9A-Fa-f:]+$/.test(parts.address) ||
      !parts.address.includes(':') ||
      (parts.address.match(/::/g) ?? []).length > 1 ||
      parts.address.includes(':::')
    ) {
      return 'That does not look like an IPv6 address or CIDR range.'
    }
    return null
  }

  if (ipv4ToInt(parts.address) === null) {
    return 'That does not look like an IPv4 or IPv6 address. Use e.g. 203.0.113.7 or 203.0.113.0/24.'
  }

  if (parts.prefix !== null && parts.prefix > 32) {
    return 'An IPv4 prefix length must be between 0 and 32.'
  }

  return null
}

/**
 * Where the caller stands against a prospective allowlist.
 *
 * - 'covered': an entry certainly includes the caller — an exact match, or an
 *   IPv4 range that contains an IPv4 caller.
 * - 'unverifiable': no entry certainly covers them, but IPv6 is involved (an
 *   IPv6 caller, or IPv6 entries) and this client does not do IPv6 containment —
 *   warn, never block, because the server re-checks for real.
 * - 'not-covered': the caller is IPv4, every entry is IPv4, and none contains
 *   them. Saving this with the restriction on is a lockout; the panel blocks it.
 */
export type CallerCoverage = 'covered' | 'unverifiable' | 'not-covered'

export function callerCoverage(entries: string[], callerIp: string): CallerCoverage {
  const caller = normalizeEntry(callerIp)
  const callerIsIpv6 = isIpv6Entry(caller)

  // An empty list covers nobody of either family — that is certain, so it
  // blocks rather than hiding behind "unverifiable" for an IPv6 caller.
  if (entries.every((raw) => normalizeEntry(raw).length === 0)) return 'not-covered'

  let sawIpv6Entry = false
  for (const raw of entries) {
    const entry = normalizeEntry(raw)
    if (entry.length === 0) continue

    // Exact match covers both families. An explicit single-host prefix is the
    // same statement: the server stores a bare IP as /32 (or /128) anyway.
    if (
      entry.toLowerCase() === caller.toLowerCase() ||
      entry.toLowerCase() === `${caller.toLowerCase()}${callerIsIpv6 ? '/128' : '/32'}`
    ) {
      return 'covered'
    }

    if (isIpv6Entry(entry)) {
      sawIpv6Entry = true
      continue
    }

    if (!callerIsIpv6 && cidrContainsIpv4(entry, caller)) {
      return 'covered'
    }
  }

  // An IPv6 caller we could not match exactly, or IPv6 ranges we cannot test:
  // honesty over a false alarm — the server still refuses a real lockout.
  if (callerIsIpv6 || sawIpv6Entry) return 'unverifiable'

  return 'not-covered'
}

/**
 * The muted "Last changed …" line, or null when the policy has never been
 * written. Shown in the viewer's locale like every other timestamp in the console.
 */
export function describeLastChanged(updatedAtUtc: string | null, updatedBy: string | null): string | null {
  if (updatedAtUtc === null) return null

  const when = new Date(updatedAtUtc).toLocaleString()
  return updatedBy !== null && updatedBy.trim().length > 0
    ? `Last changed ${when} by ${updatedBy}`
    : `Last changed ${when}`
}
