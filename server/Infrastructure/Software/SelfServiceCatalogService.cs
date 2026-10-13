using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Software;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Software;

/// <summary>
/// The device's side of the Company Portal: what this machine may see, and what
/// happens when its user asks for something.
/// </summary>
/// <remarks>
/// <para>
/// The device is the only identity here. It authenticated with its credential,
/// its group decides what it may see, and a request from it can only name a
/// catalog app id. Everything else -- the package, its bytes, the task -- is
/// resolved on the server from what an administrator published. The reported
/// user name is kept for the record and decides nothing.
/// </para>
/// <para>
/// Refusals are uniform. Unpublished, invisible, withdrawn and non-existent all
/// come back as <see cref="CatalogInstallOutcome.Refused"/>, so the portal cannot
/// be used to enumerate what other groups are offered.
/// </para>
/// </remarks>
public sealed class SelfServiceCatalogService(
    EndpointPlatformDbContext dbContext,
    SoftwareDeploymentService deploymentService,
    AuditWriter auditWriter,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Requests per device per minute before the server stops recording them.
    /// A person clicks a tile a few times; anything beyond this is a loop or a
    /// probe, and neither deserves a row per attempt.
    /// </summary>
    internal const int MaxRequestsPerMinute = 10;

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly SoftwareDeploymentService _deploymentService = deploymentService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>The published apps this device may see, each with its state on the device.</summary>
    public async Task<IReadOnlyList<CatalogEntryForDevice>> ListForDeviceAsync(
        Device device, CancellationToken cancellationToken = default)
    {
        var apps = await VisibleAppsAsync(device, cancellationToken);
        if (apps.Count == 0)
        {
            return [];
        }

        var installed = await InstalledOnAsync(device.Id, cancellationToken);
        var outstanding = (await _deploymentService.OutstandingInstallsAsync([device.Id], null, cancellationToken))
            .Select(o => o.PackageId)
            .ToHashSet();

        return apps
            .Select(a => new CatalogEntryForDevice(
                a.Id, a.DisplayName, a.Description, a.Category, a.HasIcon,
                a.PackageVersion, a.PackagePublisher, a.UpdatedAt,
                StateOf(a, installed, outstanding)))
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<byte[]?> GetIconAsync(
        Device device, Guid appId, CancellationToken cancellationToken = default)
    {
        var visible = (await VisibleAppsAsync(device, cancellationToken)).Any(a => a.Id == appId);
        if (!visible)
        {
            return null;
        }

        // Fetched by id, alone: the visibility pass above projects HasIcon and
        // never the bytes, so listing never drags every icon out of the table.
        return await _dbContext.CatalogApps.AsNoTracking()
            .Where(a => a.Id == appId)
            .Select(a => a.Icon)
            .SingleOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Queues the install if the device may have the app and needs it.
    /// </summary>
    /// <remarks>
    /// One transaction around the decision, the task and the request record.
    /// The task service commits the task as it queues it, so without the
    /// transaction a failure after that point would leave an install on its way
    /// to the device with no record of who asked for it -- the one thing the
    /// record exists to prevent. Run under the execution strategy because the
    /// production connection retries transient failures, and a strategy that
    /// retries refuses a transaction it does not own.
    /// </remarks>
    public Task<CatalogInstallDecision> RequestInstallAsync(
        Device device, Guid appId, string? requestedBy, CancellationToken cancellationToken = default)
    {
        // Normalised once: blank is "unknown", and what is stored, audited and
        // shown must be the same value.
        var who = requestedBy?.Trim() is { Length: > 0 } name ? name : null;

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() => RequestInstallInTransactionAsync(device, appId, who, cancellationToken));
    }

    private async Task<CatalogInstallDecision> RequestInstallInTransactionAsync(
        Device device, Guid appId, string? requestedBy, CancellationToken cancellationToken)
    {
        // A retried attempt starts clean; nothing from a failed one may be re-saved.
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        if (await OverLimitAsync(device.Id, cancellationToken))
        {
            return new CatalogInstallDecision(CatalogInstallOutcome.Refused, null);
        }

        var visible = (await VisibleAppsAsync(device, cancellationToken)).SingleOrDefault(a => a.Id == appId);
        if (visible is null)
        {
            // Recorded under the id that was asked for, with no name: the name
            // would say whether the id is real.
            var refused = await RecordAsync(device, appId, "(not offered)", requestedBy,
                CatalogInstallOutcome.Refused, null, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return refused;
        }

        var package = await _dbContext.SoftwarePackages.AsNoTracking()
            .SingleAsync(p => p.Id == visible.PackageId, cancellationToken);

        var display = $"Company Portal ({requestedBy ?? device.Hostname})";
        if (display.Length > 256)
        {
            display = display[..256];
        }

        var result = await _deploymentService.CreateSelfServiceAsync(
            device.OrganizationId, package, device.Id, visible.PublishedByUserId, display, cancellationToken);

        var outcome = result switch
        {
            null => CatalogInstallOutcome.Refused,
            { TaskId: not null } => CatalogInstallOutcome.Queued,
            // Not comparable means the product is there in a version the
            // server cannot read; it is installed, as far as the user is
            // concerned, and the tile says so.
            { Eligibility: SoftwareEligibility.AlreadyInstalled
                or SoftwareEligibility.NewerInstalled
                or SoftwareEligibility.VersionNotComparable }
                => CatalogInstallOutcome.AlreadyInstalled,
            { Eligibility: SoftwareEligibility.AlreadyInProgress } => CatalogInstallOutcome.AlreadyInProgress,
            _ => CatalogInstallOutcome.Refused,
        };

        var decision = await RecordAsync(device, appId, visible.DisplayName, requestedBy, outcome,
            result?.TaskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return decision;
    }

    private async Task<CatalogInstallDecision> RecordAsync(
        Device device, Guid appId, string appName, string? requestedBy,
        CatalogInstallOutcome outcome, Guid? taskId, CancellationToken cancellationToken)
    {
        _dbContext.CatalogInstallRequests.Add(new CatalogInstallRequest(
            device.OrganizationId, device.Id, appId, appName, requestedBy, outcome, taskId));

        _auditWriter.Stage(
            device.OrganizationId,
            AuditActorType.Agent,
            device.Id,
            device.Hostname,
            action: "software.catalog.request",
            outcome == CatalogInstallOutcome.Refused ? AuditResult.Denied : AuditResult.Success,
            a => a.OnDevice(device.Id, device.Hostname)
                  .OnTarget("catalog_app", appId.ToString(), appName)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      requestedBy,
                      outcome = outcome.ToString(),
                      taskId,
                  })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        return new CatalogInstallDecision(outcome, taskId);
    }

    private async Task<bool> OverLimitAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var since = _timeProvider.GetUtcNow().AddMinutes(-1);
        var recent = await _dbContext.CatalogInstallRequests.AsNoTracking()
            .CountAsync(r => r.DeviceId == deviceId && r.CreatedAt >= since, cancellationToken);
        return recent >= MaxRequestsPerMinute;
    }

    /// <summary>
    /// Published, backed by a deployable package, and visible to the device's
    /// group. A projection, not the entities: the icon column stays in the
    /// database until one icon is asked for by id.
    /// </summary>
    private async Task<IReadOnlyList<VisibleApp>> VisibleAppsAsync(Device device, CancellationToken cancellationToken)
    {
        var rows = await (
            from app in _dbContext.CatalogApps.AsNoTracking()
            join package in _dbContext.SoftwarePackages.AsNoTracking() on app.PackageId equals package.Id
            where app.OrganizationId == device.OrganizationId && app.IsPublished && !package.IsWithdrawn
            select new VisibleApp(
                app.Id, app.DisplayName, app.Description, app.Category, app.Icon != null,
                app.Visibility, app.IsPublished, app.PublishedByUserId, app.UpdatedAt,
                package.Id, package.Name, package.Version, package.Publisher, package.MsiProductCode))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var restricted = rows
            .Where(r => r.Visibility == CatalogVisibility.SelectedGroups)
            .Select(r => r.Id)
            .ToList();

        var links = restricted.Count == 0
            ? []
            : await _dbContext.CatalogAppGroups.AsNoTracking()
                .Where(g => restricted.Contains(g.CatalogAppId))
                .Select(g => new { g.CatalogAppId, g.DeviceGroupId })
                .ToListAsync(cancellationToken);

        var allowedByApp = links
            .GroupBy(l => l.CatalogAppId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<Guid>)g.Select(l => l.DeviceGroupId).ToList());

        return rows
            .Where(r => CatalogApp.IsVisibleTo(
                r.IsPublished, r.Visibility, device.DeviceGroupId,
                allowedByApp.TryGetValue(r.Id, out var allowed) ? allowed : []))
            .ToList();
    }

    private async Task<IReadOnlyList<InstalledApplication>> InstalledOnAsync(
        Guid deviceId, CancellationToken cancellationToken) =>
        await _dbContext.DeviceSoftware.AsNoTracking()
            .Where(s => s.DeviceId == deviceId)
            .Select(s => new InstalledApplication(s.Name, s.Version, s.Publisher, s.ProductCode))
            .ToListAsync(cancellationToken);

    private static CatalogEntryState StateOf(
        VisibleApp app, IReadOnlyList<InstalledApplication> installed, HashSet<Guid> outstanding)
    {
        if (outstanding.Contains(app.PackageId))
        {
            return CatalogEntryState.Installing;
        }

        var eligibility = SoftwareEligibilityEvaluator.Evaluate(
            new DeployableSoftware(app.PackageName, app.PackageVersion, app.PackagePublisher, app.MsiProductCode),
            installed);

        // Mirrors the install decision exactly: a tile must never offer Install
        // for something a request would then answer "already installed".
        return eligibility switch
        {
            SoftwareEligibility.AlreadyInstalled
                or SoftwareEligibility.NewerInstalled
                or SoftwareEligibility.VersionNotComparable => CatalogEntryState.Installed,
            SoftwareEligibility.UpdateRequired => CatalogEntryState.UpdateAvailable,
            _ => CatalogEntryState.Available,
        };
    }

    /// <summary>A catalog entry as the device-facing queries project it: no icon bytes.</summary>
    private sealed record VisibleApp(
        Guid Id,
        string DisplayName,
        string? Description,
        string? Category,
        bool HasIcon,
        CatalogVisibility Visibility,
        bool IsPublished,
        Guid PublishedByUserId,
        DateTimeOffset UpdatedAt,
        Guid PackageId,
        string PackageName,
        string PackageVersion,
        string? PackagePublisher,
        string MsiProductCode);
}

/// <summary>What the portal shows on a tile.</summary>
public enum CatalogEntryState
{
    Available,
    UpdateAvailable,
    Installing,
    Installed,
}

public sealed record CatalogEntryForDevice(
    Guid Id,
    string DisplayName,
    string? Description,
    string? Category,
    bool HasIcon,
    string Version,
    string? Publisher,
    DateTimeOffset UpdatedAt,
    CatalogEntryState State);

public sealed record CatalogInstallDecision(CatalogInstallOutcome Outcome, Guid? TaskId);
