import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  getAuditLog,
  getConnectedUsbDevices,
  getDeviceCounts,
  getFleetReport,
  getReadiness,
  getServiceInfo,
  type AuditLogEntry,
  type ConnectedUsbDevice,
  type DeviceCounts,
  type FleetReport,
  type HealthReport,
  type ServiceInfo,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { auditResultTone, describeAuditAction, describeAuditSubject, formatWhen } from './auditView'

type Tone = 'ok' | 'warn' | 'crit' | 'muted' | undefined

/** How many audit events "Recent activity" shows; the Audit Logs page has the rest. */
const RECENT_ACTIVITY_LIMIT = 10

/**
 * Device tiles show live counts (Phase 1). The remaining tiles keep em-dashes
 * rather than zeroes: "0 pending updates" would be a claim about the estate,
 * and those collectors do not exist yet (Phases 8/12).
 */
const PLACEHOLDER_STATS: StatTile[] = [
  { label: 'Pending Updates', value: '—', note: 'Collector not implemented' },
  { label: 'Security Alerts', value: '—', note: 'Collector not implemented' },
]

interface StatTile {
  label: string
  value: string
  tone?: Tone
  note?: string
  /** Where the tile leads: the list its number counts. */
  to?: string
}

function StatCard({ label, value, tone, note, to }: StatTile) {
  const unknown = value === '—'
  const className = `card stat-card${tone && !unknown ? ` tone-${tone}` : ''}`
  const body = (
    <>
      <div className="stat-label">{label}</div>
      <div className={`stat-value${unknown ? ' unknown' : ''}`}>{value}</div>
      {note && <div className="stat-note">{note}</div>}
    </>
  )

  return to && !unknown ? (
    <Link to={to} className={className} aria-label={`${label}: ${value}. Show the list.`}>
      {body}
    </Link>
  ) : (
    <div className={className}>{body}</div>
  )
}

function Metric({ label, value, tone, to }: { label: string; value: string; tone?: Tone; to?: string }) {
  const className = `metric${tone ? ` tone-${tone}` : ''}`
  const body = (
    <>
      <div className="metric-label">{label}</div>
      <div className="metric-value">{value}</div>
    </>
  )

  return to ? (
    <Link to={to} className={className} aria-label={`${label}: ${value}. Show the list.`}>
      {body}
    </Link>
  ) : (
    <div className={className}>{body}</div>
  )
}

function statusBadgeClass(status: string): string {
  switch (status) {
    case 'Healthy':
      return 'badge ok'
    case 'Degraded':
      return 'badge warn'
    default:
      return 'badge crit'
  }
}

function RecentActivity({ entries, canView }: { entries: AuditLogEntry[] | null; canView: boolean }) {
  if (!canView) {
    return (
      <div className="empty-state">
        <Icon name="audit" size={40} strokeWidth={1.25} className="icon" />
        <div className="title">Activity is not available to your role</div>
        <div>Reading the audit log needs the audit.view permission.</div>
      </div>
    )
  }

  if (entries === null) {
    return <div className="loading">Loading recent activity…</div>
  }

  if (entries.length === 0) {
    return (
      <div className="empty-state">
        <Icon name="audit" size={40} strokeWidth={1.25} className="icon" />
        <div className="title">No activity yet</div>
        <div>Audit events will appear here once devices enroll and administrators act.</div>
      </div>
    )
  }

  return (
    <ul className="activity-list">
      {entries.map((entry) => {
        const subject = describeAuditSubject(entry)
        return (
          <li key={entry.id}>
            <span className="activity-time" title={new Date(entry.occurredAt).toLocaleString()}>
              {formatWhen(entry.occurredAt)}
            </span>
            <span className="activity-what">
              <strong>{describeAuditAction(entry.action)}</strong>
              {subject &&
                (entry.deviceId ? (
                  <>
                    {' · '}
                    <Link to={`/devices/${entry.deviceId}`}>{subject}</Link>
                  </>
                ) : (
                  ` · ${subject}`
                ))}
              <span className="activity-who"> — {entry.actorDisplay}</span>
            </span>
            {entry.result !== 'Success' && (
              <span className={`badge ${auditResultTone(entry.result)}`} title={entry.failureReason ?? undefined}>
                {entry.result}
              </span>
            )}
          </li>
        )
      })}
    </ul>
  )
}

export function DashboardPage() {
  const { hasPermission } = useAuth()
  const canViewUsb = hasPermission('usb.view')
  const canViewAudit = hasPermission('audit.view')

  const [serviceInfo, setServiceInfo] = useState<ServiceInfo | null>(null)
  const [health, setHealth] = useState<HealthReport | null>(null)
  const [counts, setCounts] = useState<DeviceCounts | null>(null)
  const [report, setReport] = useState<FleetReport | null>(null)
  const [usb, setUsb] = useState<ConnectedUsbDevice[] | null>(null)
  const [activity, setActivity] = useState<AuditLogEntry[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false

    async function load() {
      try {
        const [info, readiness, deviceCounts, fleetReport, connectedUsb, recent] = await Promise.all([
          getServiceInfo(),
          getReadiness(),
          getDeviceCounts(),
          getFleetReport().catch(() => null),
          // Optional panels: a failure here leaves the tile showing a dash rather
          // than taking the whole dashboard down with it.
          canViewUsb ? getConnectedUsbDevices().catch(() => null) : Promise.resolve(null),
          canViewAudit ? getAuditLog(RECENT_ACTIVITY_LIMIT).catch(() => null) : Promise.resolve(null),
        ])
        if (!cancelled) {
          setServiceInfo(info)
          setHealth(readiness)
          setCounts(deviceCounts)
          setReport(fleetReport)
          setUsb(connectedUsb)
          setActivity((previous) => recent ?? previous ?? [])
          setError(null)
        }
      } catch {
        if (!cancelled) {
          setError(
            'The Admin API is unreachable. Check that the backend is running and that ' +
              'the dev proxy target matches its port (see docs/development.md).',
          )
        }
      } finally {
        if (!cancelled) {
          setLoading(false)
        }
      }
    }

    void load()
    const timer = setInterval(() => void load(), 30_000)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [canViewUsb, canViewAudit])

  // Tones describe the estate, not the tile. "Total" and "Retired" are facts
  // rather than states, so they stay neutral; offline machines only turn amber
  // when there actually are some.
  const deviceStats: StatTile[] = [
    { label: 'Total Devices', value: counts ? String(counts.total) : '—', to: '/devices?view=All' },
    { label: 'Online', value: counts ? String(counts.online) : '—', tone: 'ok', to: '/devices?connectivity=online' },
    {
      label: 'Offline',
      value: counts ? String(counts.offline) : '—',
      tone: counts && counts.offline > 0 ? 'warn' : undefined,
      to: '/devices?connectivity=offline',
    },
    {
      label: 'Retired',
      value: counts ? String(counts.retired) : '—',
      tone: 'muted',
      to: '/devices?view=Retired',
    },
  ]

  const reportStats: StatTile[] = report
    ? [
        {
          label: 'Reboot Pending',
          value: String(report.updates.rebootPending),
          tone: report.updates.rebootPending > 0 ? 'warn' : undefined,
          to: '/updates?show=reboot',
        },
        {
          label: 'Needs Attention',
          value: String(report.security.needsAttention + report.security.critical),
          tone: report.security.needsAttention + report.security.critical > 0 ? 'crit' : undefined,
          to: '/security?show=attention',
        },
      ]
    : PLACEHOLDER_STATS

  // Storage and phones only: hubs, keyboards and mice are inventory, not what
  // "connected USB devices" means to someone watching data leave the building.
  const phones = usb?.filter((u) => u.isPortableDevice).length
  const usbStats: StatTile[] = canViewUsb
    ? [
        {
          label: 'USB Devices Connected',
          value: usb ? String(usb.length) : '—',
          to: '/usb-access?show=connected',
        },
        {
          label: 'Phones Connected',
          value: phones === undefined ? '—' : String(phones),
          to: '/usb-access?show=phones',
        },
      ]
    : []

  return (
    <>
      {error && (
        <div className="error-banner">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      <div className="stat-grid">
        {[...deviceStats, ...reportStats, ...usbStats].map((stat) => (
          <StatCard key={stat.label} {...stat} />
        ))}
      </div>

      {report && (
        <div className="card card-section">
          <h2>Fleet report</h2>
          <div className="metric-grid">
            <Metric
              label="Avg security score"
              value={report.security.averageScore == null ? '—' : `${report.security.averageScore}%`}
              to="/security"
            />
            <Metric
              label="Devices w/ failed updates"
              value={String(report.updates.withFailedUpdates)}
              tone={report.updates.withFailedUpdates > 0 ? 'crit' : undefined}
              to="/updates?show=failed"
            />
            <Metric label="Enabled policies" value={String(report.policies.enabledPolicies)} to="/policies" />
            <Metric
              label="Non-compliant results"
              value={String(report.policies.nonCompliantResults)}
              tone={report.policies.nonCompliantResults > 0 ? 'warn' : undefined}
              to="/policies"
            />
            <Metric label="Tasks queued" value={String(report.tasks.queued)} to="/tasks?status=Queued" />
            <Metric
              label="Tasks succeeded"
              value={String(report.tasks.succeeded)}
              tone="ok"
              to="/tasks?status=Succeeded"
            />
            <Metric
              label="Tasks failed"
              value={String(report.tasks.failed)}
              tone={report.tasks.failed > 0 ? 'crit' : undefined}
              to="/tasks?status=Failed"
            />
            <Metric label="Active packages" value={String(report.activePackages)} to="/software?section=packages" />
          </div>
        </div>
      )}

      <div className="card card-section">
        <h2>Platform status</h2>
        {loading && <div className="loading">Checking platform status…</div>}
        {!loading && serviceInfo && health && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Component</th>
                  <th>Status</th>
                  <th>Detail</th>
                </tr>
              </thead>
              <tbody>
                <tr>
                  <td>Admin API</td>
                  <td>
                    <span className="badge ok">Reachable</span>
                  </td>
                  <td>
                    <code>{serviceInfo.service}</code> v{serviceInfo.version} ·{' '}
                    {serviceInfo.environment}
                  </td>
                </tr>
                {health.checks.map((check) => (
                  <tr key={check.name}>
                    <td style={{ textTransform: 'capitalize' }}>{check.name}</td>
                    <td>
                      <span className={statusBadgeClass(check.status)}>{check.status}</span>
                    </td>
                    <td>{check.durationMs.toFixed(1)} ms</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        {!loading && !serviceInfo && !error && <div className="loading">No status available.</div>}
      </div>

      <div className="card card-section">
        <div className="section-head">
          <h2>Recent activity</h2>
          {canViewAudit && activity && activity.length > 0 && <Link to="/audit">View all</Link>}
        </div>
        <RecentActivity entries={activity} canView={canViewAudit} />
      </div>
    </>
  )
}
