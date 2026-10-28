using EndpointPlatform.Domain.Software;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Software;

/// <summary>
/// Reads deployments and their per-device outcomes.
/// </summary>
/// <remarks>
/// Status is <b>derived</b>, never stored. A deployment's progress is entirely a
/// function of the tasks it created, and the task is already the authority on
/// whether an install was delivered, succeeded, failed or expired. Storing a
/// second copy would drift from it, and the console would confidently show
/// progress that never happened.
/// </remarks>
public sealed class SoftwareDeploymentReadService(EndpointPlatformDbContext dbContext)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;

    /// <param name="scopedDeviceIds">
    /// The devices the caller may see, or null for all of them. A deployment is
    /// listed only when at least one of its targets is in scope, and that filter is
    /// applied before counting and paging: a group-scoped administrator must not
    /// learn what was deployed elsewhere, or how much, from the page or its total.
    /// </param>
    public async Task<DeploymentSummaryPage> ListAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var deployments = _dbContext.SoftwareDeployments.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId);

        // Materialised first: EF translates Contains on a List, not on every
        // IReadOnlyCollection a caller might hand over.
        if (scopedDeviceIds?.ToList() is { } visible)
        {
            deployments = deployments.Where(d => _dbContext.SoftwareDeploymentTargets
                .Any(t => t.DeploymentId == d.Id && visible.Contains(t.DeviceId)));
        }

        var totalCount = await deployments.CountAsync(cancellationToken);

        var rows = await deployments
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.PackageId,
                d.PackageName,
                d.PackageVersion,
                d.TargetType,
                d.CreatedByDisplay,
                d.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var ids = rows.Select(r => r.Id).ToList();

        // One query for every listed deployment's targets rather than one per
        // deployment: the list page would otherwise be an N+1 that only bites once
        // there is history to show.
        var states = await TargetStatesAsync(ids, scopedDeviceIds, cancellationToken);

        var items = rows.Select(r =>
        {
            var mine = states.Where(s => s.DeploymentId == r.Id).ToList();
            return new DeploymentSummary(
                r.Id, r.PackageId, r.PackageName, r.PackageVersion, r.TargetType.ToString(),
                r.CreatedByDisplay, r.CreatedAt, Tally(mine));
        }).ToList();

        return new DeploymentSummaryPage(items, totalCount, page, pageSize);
    }

    public async Task<DeploymentDetail?> GetAsync(
        Guid organizationId,
        Guid deploymentId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var deployment = await _dbContext.SoftwareDeployments.AsNoTracking()
            .Where(d => d.Id == deploymentId && d.OrganizationId == organizationId)
            .Select(d => new
            {
                d.Id,
                d.PackageId,
                d.PackageName,
                d.PackageVersion,
                d.TargetType,
                d.CreatedByDisplay,
                d.CreatedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (deployment is null)
        {
            return null;
        }

        // A deployment the list does not show this caller -- no target in their
        // scope -- reads as not found here too, rather than as an empty detail
        // that still names the package and who deployed it.
        if (scopedDeviceIds?.ToList() is { } inScope
            && !await _dbContext.SoftwareDeploymentTargets.AnyAsync(
                t => t.DeploymentId == deploymentId && inScope.Contains(t.DeviceId), cancellationToken))
        {
            return null;
        }

        var query =
            from target in _dbContext.SoftwareDeploymentTargets.AsNoTracking()
            join device in _dbContext.Devices.AsNoTracking() on target.DeviceId equals device.Id
            join task in _dbContext.DeviceTasks.AsNoTracking() on target.TaskId equals task.Id into taskJoin
            from task in taskJoin.DefaultIfEmpty()
            where target.DeploymentId == deploymentId
            select new
            {
                target.DeviceId,
                device.Hostname,
                device.DisplayName,
                DeviceStatus = device.Status,
                device.LastSeenAt,
                target.State,
                target.Reason,
                target.ObservedVersion,
                target.TaskId,
                target.Attempt,
                TaskStatus = (DeviceTaskStatus?)task.Status,
                task.ResultMessage,
                task.CompletedAt,
            };

        if (scopedDeviceIds is not null)
        {
            var visible = scopedDeviceIds.ToHashSet();
            query = query.Where(x => visible.Contains(x.DeviceId));
        }

        var rows = await query.OrderBy(x => x.Hostname).ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;

        var targets = rows.Select(r => new DeploymentDeviceResult(
            r.DeviceId,
            r.Hostname,
            r.DisplayName,
            r.DeviceStatus.ToString(),
            r.LastSeenAt,
            StatusOf(r.State, r.TaskStatus, r.LastSeenAt, now),
            r.Reason.ToString(),
            r.ObservedVersion,
            r.TaskId,
            r.ResultMessage,
            r.CompletedAt,
            r.Attempt)).ToList();

        return new DeploymentDetail(
            deployment.Id, deployment.PackageId, deployment.PackageName, deployment.PackageVersion,
            deployment.TargetType.ToString(), deployment.CreatedByDisplay, deployment.CreatedAt,
            TallyResults(targets), targets);
    }

    private async Task<List<TargetState>> TargetStatesAsync(
        IReadOnlyCollection<Guid> deploymentIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken)
    {
        if (deploymentIds.Count == 0)
        {
            return [];
        }

        var query =
            from target in _dbContext.SoftwareDeploymentTargets.AsNoTracking()
            join task in _dbContext.DeviceTasks.AsNoTracking() on target.TaskId equals task.Id into taskJoin
            from task in taskJoin.DefaultIfEmpty()
            where deploymentIds.Contains(target.DeploymentId)
            join device in _dbContext.Devices.AsNoTracking() on target.DeviceId equals device.Id
            select new
            {
                target.DeploymentId,
                target.DeviceId,
                target.State,
                device.LastSeenAt,
                TaskStatus = (DeviceTaskStatus?)task.Status,
            };

        if (scopedDeviceIds is not null)
        {
            var visible = scopedDeviceIds.ToHashSet();
            query = query.Where(x => visible.Contains(x.DeviceId));
        }

        var rows = await query.ToListAsync(cancellationToken);

        return rows
            .Select(r => new TargetState(
                r.DeploymentId, StatusOf(r.State, r.TaskStatus, r.LastSeenAt, DateTimeOffset.UtcNow)))
            .ToList();
    }

    /// <summary>
    /// The state an operator sees for one device.
    /// </summary>
    /// <remarks>
    /// A skipped target has no task and stays Skipped. Everything else follows the
    /// task, so nothing here can report progress the task does not support: a
    /// queued-but-unclaimed task is Pending, a delivered one is Installing, and an
    /// expired one is Expired rather than being quietly counted as failed.
    /// </remarks>
    /// <summary>
    /// How long a device may be silent before queued work is reported as waiting
    /// on it rather than merely pending.
    /// </summary>
    /// <remarks>
    /// Generous relative to the ~60 s heartbeat: a device is called offline only
    /// once it has missed many beats, so a slow check-in is not mislabelled.
    /// </remarks>
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(15);

    private static string StatusOf(
        DeploymentTargetState state, DeviceTaskStatus? taskStatus, DateTimeOffset? lastSeenAt, DateTimeOffset now)
    {
        if (state == DeploymentTargetState.Skipped)
        {
            return "Skipped";
        }

        return taskStatus switch
        {
            // Queued means the agent has not collected it. Whether that is
            // "any moment now" or "this machine is not running" is the difference
            // between Pending and Offline, and reporting both as Pending leaves an
            // operator waiting on a device that will never answer. Neither is a
            // failure: the task simply waits until its TTL expires.
            DeviceTaskStatus.Queued when lastSeenAt is null || now - lastSeenAt > OfflineAfter
                => "Offline",
            DeviceTaskStatus.Queued => "Pending",
            DeviceTaskStatus.Delivered => "Installing",
            DeviceTaskStatus.Succeeded => "Succeeded",
            DeviceTaskStatus.Failed => "Failed",
            // Never claimed before its deadline -- distinct from Failed, which
            // means the endpoint tried and could not.
            DeviceTaskStatus.Expired => "Expired",
            DeviceTaskStatus.Cancelled => "Cancelled",
            // The task row is gone. Honest about not knowing rather than
            // presenting a default that reads like a real outcome.
            _ => "Unknown",
        };
    }

    private static DeploymentTally Tally(IEnumerable<TargetState> states)
    {
        var list = states.ToList();
        return new DeploymentTally(
            list.Count,
            list.Count(s => s.Status == "Pending"),
            list.Count(s => s.Status == "Installing"),
            list.Count(s => s.Status == "Succeeded"),
            list.Count(s => s.Status == "Failed"),
            list.Count(s => s.Status == "Expired"),
            list.Count(s => s.Status == "Skipped"),
            list.Count(s => s.Status == "Offline"),
            list.Count(s => s.Status == "Cancelled"));
    }

    private static DeploymentTally TallyResults(IEnumerable<DeploymentDeviceResult> results) =>
        Tally(results.Select(r => new TargetState(Guid.Empty, r.Status)));

    private sealed record TargetState(Guid DeploymentId, string Status);
}

public sealed record DeploymentTally(
    int Total, int Pending, int Installing, int Succeeded, int Failed, int Expired, int Skipped,
    int Offline, int Cancelled);

public sealed record DeploymentSummary(
    Guid Id, Guid PackageId, string PackageName, string PackageVersion, string TargetType,
    string CreatedByDisplay, DateTimeOffset CreatedAt, DeploymentTally Tally);

public sealed record DeploymentSummaryPage(
    IReadOnlyList<DeploymentSummary> Items, int TotalCount, int Page, int PageSize);

public sealed record DeploymentDeviceResult(
    Guid DeviceId, string Hostname, string? DisplayName, string DeviceStatus, DateTimeOffset? LastSeenAt,
    string Status, string Reason, string? ObservedVersion, Guid? TaskId, string? ResultMessage,
    DateTimeOffset? CompletedAt, int Attempt);

public sealed record DeploymentDetail(
    Guid Id, Guid PackageId, string PackageName, string PackageVersion, string TargetType,
    string CreatedByDisplay, DateTimeOffset CreatedAt, DeploymentTally Tally,
    IReadOnlyList<DeploymentDeviceResult> Targets);
