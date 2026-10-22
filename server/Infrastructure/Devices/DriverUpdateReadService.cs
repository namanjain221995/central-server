using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Reads the pending Windows Update driver updates: the fleet overview of which
/// machines have updates waiting, and one machine's pending list.
/// </summary>
/// <remarks>
/// <para>
/// Both reads are scoped. A scoped administrator is handed the device ids they may
/// see (null means "all"), and the overview is computed over those devices only, so
/// the page can never name a machine outside the caller's scope -- the same rule the
/// announcement history and the deployment reads follow.
/// </para>
/// <para>
/// The reboot-pending flag is the general Windows reboot-required signal the last
/// inventory reported (<see cref="DeviceUpdateStatus.RebootRequired"/>), the same
/// source the Windows Update overview uses. It is a best-effort hint for the
/// overview; the live, driver-install-specific signal is the install task's own
/// result, which the console reads per device.
/// </para>
/// </remarks>
public sealed class DriverUpdateReadService(
    EndpointPlatformDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<AgentServerOptions> agentServerOptions)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>The console's one definition of offline: no heartbeat for this long.</summary>
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);

    /// <summary>
    /// Every active device that has at least one pending driver update, with its
    /// counts, most updates first.
    /// </summary>
    public async Task<DriverUpdateOverview> GetOverviewAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var visible = scopedDeviceIds?.ToList();

        var query =
            from u in _dbContext.DeviceDriverUpdates.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on u.DeviceId equals d.Id
            where d.OrganizationId == organizationId && d.Status == DeviceStatus.Active
            select new { u.DeviceId, d.Hostname, d.DisplayName, d.LastSeenAt, u.IsPreview, u.CollectedAt };

        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.DeviceId));
        }

        var rows = await query.ToListAsync(cancellationToken);

        var deviceIds = rows.Select(r => r.DeviceId).Distinct().ToList();
        var rebootById = await _dbContext.DeviceUpdateStatus.AsNoTracking()
            .Where(s => deviceIds.Contains(s.DeviceId))
            .Select(s => new { s.DeviceId, s.RebootRequired })
            .ToDictionaryAsync(s => s.DeviceId, s => s.RebootRequired, cancellationToken);

        var now = _timeProvider.GetUtcNow();

        var devices = rows
            .GroupBy(r => r.DeviceId)
            .Select(g =>
            {
                var first = g.First();
                return new DriverUpdateDeviceSummary(
                    g.Key,
                    first.Hostname,
                    first.DisplayName,
                    IsOnline: first.LastSeenAt is { } seen && now - seen <= _staleAfter,
                    first.LastSeenAt,
                    PendingCount: g.Count(),
                    PreviewCount: g.Count(r => r.IsPreview),
                    LastScanAt: g.Max(r => r.CollectedAt),
                    RebootPending: rebootById.GetValueOrDefault(g.Key));
            })
            .OrderByDescending(d => d.PendingCount - d.PreviewCount)
            .ThenByDescending(d => d.PendingCount)
            .ThenBy(d => d.Hostname)
            .ToList();

        var summary = new DriverUpdateSummary(
            DevicesWithPending: devices.Count,
            TotalPending: devices.Sum(d => d.PendingCount),
            DevicesRebootPending: devices.Count(d => d.RebootPending));

        return new DriverUpdateOverview(summary, devices);
    }

    /// <summary>
    /// One device's pending driver updates, non-preview first then by title, with
    /// when it was last scanned and whether a reboot is pending. The caller has
    /// already confirmed the device is in scope.
    /// </summary>
    public async Task<DeviceDriverUpdateList> GetForDeviceAsync(
        Guid deviceId, CancellationToken cancellationToken = default)
    {
        var updates = await _dbContext.DeviceDriverUpdates.AsNoTracking()
            .Where(u => u.DeviceId == deviceId)
            .OrderBy(u => u.IsPreview)
            .ThenBy(u => u.Title)
            .Select(u => new DriverUpdateRow(
                u.UpdateId,
                u.Revision,
                u.Title,
                u.DriverClass,
                u.DriverProvider,
                u.DriverVersionDate,
                u.IsPreview,
                u.SizeBytes))
            .ToListAsync(cancellationToken);

        // The rows all carry the same scan time; null when nothing is pending, which
        // the console reads as "no pending updates" rather than a scan age.
        var lastScanAt = await _dbContext.DeviceDriverUpdates.AsNoTracking()
            .Where(u => u.DeviceId == deviceId)
            .MaxAsync(u => (DateTimeOffset?)u.CollectedAt, cancellationToken);

        var rebootPending = await _dbContext.DeviceUpdateStatus.AsNoTracking()
            .Where(s => s.DeviceId == deviceId)
            .Select(s => (bool?)s.RebootRequired)
            .SingleOrDefaultAsync(cancellationToken) ?? false;

        return new DeviceDriverUpdateList(lastScanAt, rebootPending, updates);
    }
}

/// <summary>One device in the driver-update overview, with its pending counts.</summary>
/// <param name="RebootPending">The general Windows reboot-required flag from the last inventory.</param>
public sealed record DriverUpdateDeviceSummary(
    Guid DeviceId,
    string Hostname,
    string? DisplayName,
    bool IsOnline,
    DateTimeOffset? LastSeenAt,
    int PendingCount,
    int PreviewCount,
    DateTimeOffset? LastScanAt,
    bool RebootPending);

/// <summary>Fleet totals across every device with pending driver updates.</summary>
public sealed record DriverUpdateSummary(int DevicesWithPending, int TotalPending, int DevicesRebootPending);

public sealed record DriverUpdateOverview(
    DriverUpdateSummary Summary, IReadOnlyList<DriverUpdateDeviceSummary> Devices);

/// <summary>One device's pending driver updates and the state the console shows around them.</summary>
public sealed record DeviceDriverUpdateList(
    DateTimeOffset? LastScanAt, bool RebootPending, IReadOnlyList<DriverUpdateRow> Updates);

/// <summary>One pending driver update as the per-device list shows it.</summary>
public sealed record DriverUpdateRow(
    string UpdateId,
    int Revision,
    string Title,
    string? DriverClass,
    string? DriverProvider,
    DateTimeOffset? DriverVerDate,
    bool IsPreview,
    long? SizeBytes);
