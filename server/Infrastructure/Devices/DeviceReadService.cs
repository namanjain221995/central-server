using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.ScreenLocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Read-side queries for the device list and detail views.
/// </summary>
/// <remarks>
/// Projects straight to DTO shapes - list pages never materialise full entities.
/// Online/offline is computed here, per query, from heartbeat staleness; it is
/// not a stored column (see the Device entity remarks).
/// </remarks>
public sealed class DeviceReadService(
    EndpointPlatformDbContext dbContext,
    IOptions<AgentServerOptions> agentServerOptions,
    TimeProvider timeProvider)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly AgentServerOptions _options = agentServerOptions?.Value
        ?? throw new ArgumentNullException(nameof(agentServerOptions));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<DevicePage> ListAsync(
        Guid organizationId,
        int page,
        int pageSize,
        string? search,
        Domain.Devices.DeviceStatus? status = null,
        CancellationToken cancellationToken = default,
        bool? online = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var now = _timeProvider.GetUtcNow();
        var staleAfter = TimeSpan.FromSeconds(_options.OfflineAfterSeconds);
        var onlineThreshold = now - staleAfter;

        var query = _dbContext.Devices
            .AsNoTracking()
            .Where(d => d.OrganizationId == organizationId);

        // The normal working view is Active machines; removed (retired) devices
        // keep their rows and history and stay reachable through the Retired
        // view rather than cluttering the estate an administrator manages daily.
        if (status is { } wanted)
        {
            query = query.Where(d => d.Status == wanted);
        }

        // Online and offline exactly as CountsAsync counts them, so the dashboard's
        // "Online 2" tile opens a list of two. Both describe Active devices only: a
        // retired machine is neither.
        if (online is true)
        {
            query = query.Where(d =>
                d.Status == Domain.Devices.DeviceStatus.Active
                && d.LastSeenAt != null
                && d.LastSeenAt >= onlineThreshold);
        }
        else if (online is false)
        {
            query = query.Where(d =>
                d.Status == Domain.Devices.DeviceStatus.Active
                && (d.LastSeenAt == null || d.LastSeenAt < onlineThreshold));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            // Parameterised by EF; the pattern characters in user input are treated
            // as literals only if escaped - escape them so "50%" matches literally.
            var escaped = term.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            // Search both names. An administrator who labelled a machine "TAM0149"
            // will search for that; one who knows it as LAPTOP-LVCHEQ2H will search
            // for the hostname. Matching only one of them makes devices findable by
            // whichever name the searcher happens not to be using.
            query = query.Where(d =>
                EF.Functions.ILike(d.Hostname, $"%{escaped}%", @"\")
                || (d.DisplayName != null && EF.Functions.ILike(d.DisplayName, $"%{escaped}%", @"\")));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        // One lookup serves every row. Every enrolled device is windows/x64 by
        // construction (the MSI refuses anything else), so the latest published
        // windows/x64 release is comparable to all of them; a future second
        // platform would carry its arch on the device row and filter here.
        var latestRelease = await _dbContext.AgentReleases
            .AsNoTracking()
            .Where(r =>
                r.Platform == "windows"
                && r.Architecture == "x64"
                && r.Status == Domain.Agents.AgentReleaseStatus.Published)
            .Select(r => r.Version)
            .ToListAsync(cancellationToken);
        var latestVersion = latestRelease
            .Where(v => Domain.Agents.AgentVersionNumber.TryParse(v, out _))
            .OrderByDescending(v =>
            {
                Domain.Agents.AgentVersionNumber.TryParse(v, out var parsed);
                return parsed;
            })
            .FirstOrDefault();

        // Plain columns only inside the SQL projection; the version comparison
        // is .NET code EF cannot translate, so it runs on the materialised page.
        var rows = await query
            .OrderByDescending(d => d.LastSeenAt)
            .ThenBy(d => d.Hostname)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new
            {
                d.Id,
                d.Hostname,
                d.DisplayName,
                d.OperatingSystem,
                d.LoggedOnUser,
                d.AgentVersion,
                d.Status,
                d.LastSeenAt,
                d.EnrolledAt,
                d.ScreenLockedReported,
            })
            .ToListAsync(cancellationToken);

        // "Locked" is derived, never stored (like online/offline): the latest
        // engage/release tasks per device combined with what the agent last
        // reported. One extra query for the page's devices, computed in memory the
        // way the derivation rule requires -- a device with no screen-lock task is
        // simply not locked and never appears here.
        var pageDeviceIds = rows.Select(d => d.Id).ToList();
        var lockTasks = await _dbContext.DeviceTasks
            .AsNoTracking()
            .Where(t => pageDeviceIds.Contains(t.DeviceId)
                        && (t.Type == DeviceTaskType.EngageScreenLock
                            || t.Type == DeviceTaskType.ReleaseScreenLock))
            .Select(t => new { t.DeviceId, t.Type, t.Status, t.CreatedAt })
            .ToListAsync(cancellationToken);
        var lockTasksByDevice = lockTasks
            .ToLookup(
                t => t.DeviceId,
                t => new ScreenLockTaskSnapshot(t.Type, t.Status, t.CreatedAt));

        var items = rows
            .Select(d => new DeviceListItem(
                d.Id,
                d.Hostname,
                d.DisplayName,
                d.OperatingSystem,
                d.LoggedOnUser,
                d.AgentVersion,
                d.Status.ToString(),
                d.LastSeenAt,
                d.Status == Domain.Devices.DeviceStatus.Active
                    && d.LastSeenAt != null
                    && d.LastSeenAt >= onlineThreshold,
                d.EnrolledAt,
                latestVersion,
                // Retired devices are not update candidates; the comparison
                // itself fails closed on anything unparseable.
                d.Status == Domain.Devices.DeviceStatus.Active
                    && latestVersion != null
                    && Domain.Agents.AgentVersionNumber.IsNewer(latestVersion, d.AgentVersion),
                ScreenLockService.DeriveDeviceLocked(d.ScreenLockedReported, lockTasksByDevice[d.Id])))
            .ToList();

        return new DevicePage(items, totalCount, page, pageSize);
    }

    public async Task<DeviceCounts> CountsAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var onlineThreshold = now - TimeSpan.FromSeconds(_options.OfflineAfterSeconds);

        var counts = await _dbContext.Devices
            .AsNoTracking()
            .Where(d => d.OrganizationId == organizationId)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Online = g.Count(d =>
                    d.Status == Domain.Devices.DeviceStatus.Active
                    && d.LastSeenAt != null
                    && d.LastSeenAt >= onlineThreshold),
                Retired = g.Count(d => d.Status == Domain.Devices.DeviceStatus.Retired),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (counts is null)
        {
            return new DeviceCounts(0, 0, 0, 0);
        }

        var offline = counts.Total - counts.Online - counts.Retired;
        return new DeviceCounts(counts.Total, counts.Online, offline, counts.Retired);
    }
}

public sealed record DeviceListItem(
    Guid Id,
    /// <summary>The Windows computer name, as reported by the agent.</summary>
    string Hostname,
    /// <summary>The administrator's console label, or null when none is set.</summary>
    string? DisplayName,
    string? OperatingSystem,
    /// <summary>
    /// Who was signed in at the last inventory, or null when the machine has
    /// never reported one. List pickers (announcements above all) lead with it:
    /// "whose screen is this" is the question a broadcast is asked about.
    /// </summary>
    string? LoggedOnUser,
    string AgentVersion,
    string Status,
    DateTimeOffset? LastSeenAt,
    bool IsOnline,
    DateTimeOffset EnrolledAt,
    /// <summary>Newest published agent version for this device's platform, or null when none is.</summary>
    string? LatestAgentVersion,
    /// <summary>True when a strictly newer published agent exists for this device.</summary>
    bool AgentUpdateAvailable,
    /// <summary>
    /// True when the device is behind a persistent full-screen lock right now.
    /// Derived from its engage/release tasks and the agent's reported lock state,
    /// never stored -- the same way <see cref="IsOnline"/> is derived.
    /// </summary>
    bool ScreenLocked);

public sealed record DevicePage(
    IReadOnlyList<DeviceListItem> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record DeviceCounts(int Total, int Online, int Offline, int Retired);
