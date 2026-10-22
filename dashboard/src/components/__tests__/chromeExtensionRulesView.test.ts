import { describe, expect, it } from 'vitest'
import type { ChromeExtensionRule } from '../../api/client'
import {
  clearRuleLabel,
  compareRules,
  extensionIdError,
  extensionRuleMenuItems,
  isValidExtensionId,
  normalizeExtensionId,
  ruleActionLabel,
  ruleForExtension,
  ruleModeForAction,
  ruleModeLabel,
  ruleModeTone,
} from '../../pages/chromeExtensionRulesView'

const UBLOCK = 'cjpalhdlnbpafiamejdnhcphjbkeiagm'

function rule(overrides: Partial<ChromeExtensionRule>): ChromeExtensionRule {
  return {
    extensionId: UBLOCK,
    mode: 'Blocked',
    displayName: 'uBlock Origin',
    updatedAt: '2026-10-22T10:00:00Z',
    ...overrides,
  }
}

describe('extension id validation', () => {
  it('accepts a canonical 32-letter a–p id', () => {
    expect(isValidExtensionId(UBLOCK)).toBe(true)
    expect(extensionIdError(UBLOCK)).toBeNull()
  })

  it('normalizes pasted whitespace and case before judging', () => {
    expect(normalizeExtensionId(`  ${UBLOCK.toUpperCase()} `)).toBe(UBLOCK)
    expect(isValidExtensionId(`  ${UBLOCK.toUpperCase()} `)).toBe(true)
  })

  it.each([
    ['31 characters', UBLOCK.slice(0, 31)],
    ['33 characters', `${UBLOCK}a`],
    ['a letter past p', `${UBLOCK.slice(0, 31)}q`],
    ['a digit', `${UBLOCK.slice(0, 31)}1`],
    ['a full Web Store URL', `https://chromewebstore.google.com/detail/${UBLOCK}`],
  ])('rejects %s', (_name, input) => {
    expect(isValidExtensionId(input)).toBe(false)
    expect(extensionIdError(input)).toMatch(/32 letters/)
  })

  it('asks for an id rather than calling emptiness malformed', () => {
    expect(extensionIdError('   ')).toMatch(/Enter the extension ID/)
  })
})

describe('rule badges', () => {
  it('labels every mode', () => {
    expect(ruleModeLabel('ForceInstalled')).toBe('Forced')
    expect(ruleModeLabel('Blocked')).toBe('Blocked')
    expect(ruleModeLabel('Allowed')).toBe('Allowed')
    expect(ruleModeLabel('Removed')).toBe('Removed')
  })

  it('tones Blocked amber like a disabled extension, and only Removed red', () => {
    expect(ruleModeTone('ForceInstalled')).toBe('ok')
    expect(ruleModeTone('Blocked')).toBe('warn')
    expect(ruleModeTone('Allowed')).toBe('neutral')
    expect(ruleModeTone('Removed')).toBe('crit')
  })
})

describe('ruleForExtension', () => {
  it('finds the device rule for an extension and null otherwise', () => {
    const rules = [rule({})]
    expect(ruleForExtension(rules, UBLOCK)?.mode).toBe('Blocked')
    expect(ruleForExtension(rules, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')).toBeNull()
    expect(ruleForExtension(null, UBLOCK)).toBeNull()
  })
})

describe('ruleModeForAction', () => {
  it('maps each menu action to the mode it asks for', () => {
    expect(ruleModeForAction('block')).toBe('Blocked')
    expect(ruleModeForAction('allow')).toBe('Allowed')
    expect(ruleModeForAction('remove')).toBe('Removed')
  })
})

describe('extensionRuleMenuItems', () => {
  function item(items: ReturnType<typeof extensionRuleMenuItems>, key: string) {
    const found = items.find((i) => i.key === key)
    if (!found) throw new Error(`no ${key} item`)
    return found
  }

  it('offers all four actions, with Clear disabled when no rule exists', () => {
    const items = extensionRuleMenuItems(null, false)
    expect(items.map((i) => i.key)).toEqual(['block', 'allow', 'remove', 'clear'])
    expect(item(items, 'block').enabled).toBe(true)
    expect(item(items, 'allow').enabled).toBe(true)
    expect(item(items, 'remove').enabled).toBe(true)
    expect(item(items, 'clear').enabled).toBe(false)
    expect(item(items, 'clear').reason).toBe('No rule is set')
  })

  it('disables the action the extension is already in, and says so', () => {
    const items = extensionRuleMenuItems('Blocked', false)
    expect(item(items, 'block').enabled).toBe(false)
    expect(item(items, 'block').reason).toBe('Already blocked')
    expect(item(items, 'allow').enabled).toBe(true)
    expect(item(items, 'clear').enabled).toBe(true)
  })

  it('marks Remove destructive so it does not read like Refresh', () => {
    expect(item(extensionRuleMenuItems(null, false), 'remove').destructive).toBe(true)
  })

  it('disables mutating actions on a Chrome component but still lets a stray rule be cleared', () => {
    const items = extensionRuleMenuItems('Blocked', true)
    expect(item(items, 'block').enabled).toBe(false)
    expect(item(items, 'allow').enabled).toBe(false)
    expect(item(items, 'remove').enabled).toBe(false)
    expect(item(items, 'block').reason).toMatch(/Part of Chrome itself/)
    expect(item(items, 'clear').enabled).toBe(true)
  })
})

describe('task labels', () => {
  it('names the action and the extension, falling back to its id', () => {
    expect(ruleActionLabel('Blocked', 'uBlock Origin', UBLOCK)).toBe('Block "uBlock Origin"')
    expect(ruleActionLabel('Allowed', 'uBlock Origin', UBLOCK)).toBe('Allow "uBlock Origin"')
    expect(ruleActionLabel('Removed', null, UBLOCK)).toBe(`Remove "${UBLOCK}"`)
    expect(ruleActionLabel('ForceInstalled', '  ', UBLOCK)).toBe(`Force-install "${UBLOCK}"`)
    expect(clearRuleLabel('uBlock Origin', UBLOCK)).toBe('Clear rule for "uBlock Origin"')
    expect(clearRuleLabel(null, UBLOCK)).toBe(`Clear rule for "${UBLOCK}"`)
  })
})

describe('compareRules', () => {
  it('orders named rules alphabetically before nameless ones, then by id', () => {
    const sorted = [
      rule({ extensionId: 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', displayName: null }),
      rule({ extensionId: 'cccccccccccccccccccccccccccccccc', displayName: 'zeta' }),
      rule({ extensionId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', displayName: null }),
      rule({ extensionId: 'dddddddddddddddddddddddddddddddd', displayName: 'Alpha' }),
    ].sort(compareRules)

    expect(sorted.map((r) => r.displayName ?? r.extensionId)).toEqual([
      'Alpha',
      'zeta',
      'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
    ])
  })
})
