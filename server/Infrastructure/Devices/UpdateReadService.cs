using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>Fleet Windows Update overview: reboot-pending and failed-update counts.</summary>
public sealed class UpdateReadService(EndpointPlatformDbContext dbContext)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    /// <param name="scopedDeviceIds">
    /// The devices the caller may see, or null for all of them -- the value
    /// <c>DeviceScopeAuthorizer.ScopedDeviceIdsOrNullAsync</c> returns. Required, not
    /// defaulted, for the reason given on <see cref="SecurityReadService.GetOverviewAsync"/>,
    /// and applied in SQL before the rows are materialised or counted.
    /// </param>
    public async Task<UpdateOverview> GetOverviewAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var query =
            from u in _dbContext.DeviceUpdateStatus.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on u.DeviceId equals d.Id
            where d.OrganizationId == organizationId && d.Status == DeviceStatus.Active
            select new { Status = u, d.Hostname };

        if (scopedDeviceIds?.ToList() is { } visible)
        {
            query = query.Where(x => visible.Contains(x.Status.DeviceId));
        }

        var rows = await query
            .Select(x => new DeviceUpdateSummary(
                x.Status.DeviceId, x.Hostname, x.Status.RebootRequired, x.Status.FailedUpdateCount, x.Status.CollectedAt))
            .ToListAsync(cancellationToken);

        var summary = new UpdateSummary(
            rows.Count,
            rows.Count(r => r.RebootRequired),
            rows.Count(r => r.FailedUpdateCount > 0));

        return new UpdateOverview(summary, rows
            .OrderByDescending(r => r.RebootRequired)
            .ThenByDescending(r => r.FailedUpdateCount)
            .ThenBy(r => r.Hostname)
            .ToList());
    }
}

public sealed record DeviceUpdateSummary(
    Guid DeviceId, string Hostname, bool RebootRequired, int FailedUpdateCount, DateTimeOffset CollectedAt);

public sealed record UpdateSummary(int DevicesReporting, int RebootPending, int WithFailedUpdates);

public sealed record UpdateOverview(UpdateSummary Summary, IReadOnlyList<DeviceUpdateSummary> Devices);
