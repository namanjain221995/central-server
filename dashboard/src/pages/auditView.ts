import type { AuditLogEntry } from '../api/client'

/**
 * Wording for the audit log on the dashboard and the Audit Logs page.
 *
 * The server records actions as dotted identifiers (`usb.access.grant`). The
 * common ones get a sentence an operator recognises at a glance; anything else is
 * spelled out from its identifier rather than hidden, so a new action is still
 * readable before anyone adds wording for it.
 */
const ACTION_LABELS: Record<string, string> = {
  'auth.sign_in': 'Signed in',
  'auth.sign_out': 'Signed out',
  'device.enroll': 'Device enrolled',
  'device.retire': 'Device removed',
  'device.rename': 'Device renamed',
  'usb.storage.connected': 'USB storage connected',
  'usb.portable_device.connected': 'Phone connected',
  'usb.access.grant': 'USB access granted',
  'usb.access.revoke': 'USB access revoked',
  'usb.access.expire': 'USB access expired',
  'usb.access.enable': 'USB access enabled',
  'agent_release.created': 'Agent release uploaded',
  'agent_release.artifact_replaced': 'Agent release file replaced',
  'agent_release.published': 'Agent release published',
  'restart_schedule.create': 'Restart scheduled',
  'restart_schedule.cancel': 'Scheduled restart cancelled',
  'restart_schedule.dispatch': 'Scheduled restart sent',
  'restart_schedule.missed': 'Scheduled restart missed',
  'policy.create': 'Policy created',
  'policy.assign': 'Policy assigned',
  'software.deployment.create': 'Software deployment started',
  'software.deployment.cancel': 'Software deployment cancelled',
  'software.package.create': 'Software package added',
  'platform.user.created': 'Administrator created',
  'platform.user.disabled': 'Administrator disabled',
  'platform.user.enabled': 'Administrator enabled',
  'platform.user.password_changed': 'Password changed',
  'platform.user.password_reset': 'Password reset',
  'platform.user.mfa_reset': 'MFA reset',
}

/** "USB access granted", or "Software application force stop" for an unlisted action. */
export function describeAuditAction(action: string): string {
  const known = ACTION_LABELS[action]
  if (known) return known

  const words = action
    .split(/[._]/)
    .filter((part) => part.length > 0)
    .map((part) => (part === 'usb' ? 'USB' : part))
    .join(' ')

  return words.length === 0 ? action : words.charAt(0).toUpperCase() + words.slice(1)
}

/** What the action was done to: the device, the named target, or both. Null when neither was recorded. */
export function describeAuditSubject(entry: AuditLogEntry): string | null {
  const parts = [entry.deviceDisplay, entry.targetDisplay].filter(
    (part, index, all): part is string => !!part && all.indexOf(part) === index,
  )
  return parts.length === 0 ? null : parts.join(' · ')
}

/** "just now", "5 min ago", "3 h ago", or the date for anything older than a day. */
export function formatWhen(occurredAt: string, now: Date = new Date()): string {
  const at = new Date(occurredAt)
  if (Number.isNaN(at.getTime())) return '—'

  const seconds = Math.max(0, Math.round((now.getTime() - at.getTime()) / 1000))
  if (seconds < 60) return 'just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)} h ago`
  return at.toLocaleDateString()
}

/** Tone of the result badge: a denial or failure is worth noticing in a list of successes. */
export function auditResultTone(result: string): 'ok' | 'warn' | 'crit' {
  switch (result) {
    case 'Success':
      return 'ok'
    case 'Denied':
      return 'warn'
    default:
      return 'crit'
  }
}
