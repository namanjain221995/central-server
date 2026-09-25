using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Flags devices whose inventory has gone stale so the next heartbeat asks the
/// agent for a fresh upload.
/// </summary>
/// <remarks>
/// <para>
/// It sets the same mark an administrator's Refresh button sets
/// (<see cref="Device.RequestInventoryRefresh"/>), so nothing changes on the
/// agent side: the heartbeat answers <c>InventoryRequested</c> through the
/// existing mechanism and the agent uploads in its own time. The sweep never
/// contacts a device and never writes inventory itself - it only asks.
/// </para>
/// <para>
/// One audit entry per organization per batch, not one per device. A thousand
/// "the platform asked for inventory" rows every day would bury the events an
/// auditor actually looks for, while a single row still records that the system,
/// not a person, made the request and how many devices it covered.
/// </para>
/// </remarks>
public sealed class InventoryRefreshSweepService(
    EndpointPlatformDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<InventoryRefreshOptions> options,
    AuditWriter auditWriter,
    ILogger<InventoryRefreshSweepService> logger)
{
    /// <summary>
    /// Action key of the audit entry staged per organization in a batch. Sits
    /// beside the manual <c>device.refresh_inventory</c> so a filter on the
    /// device actions finds the automatic requests as well as the by-hand ones.
    /// </summary>
    public const string AuditAction = "device.refresh_inventory.sweep";

    /// <summary>
    /// Actor display recorded on the audit entry; no person is behind it. Named
    /// the way the other system sweepers are.
    /// </summary>
    public const string ActorDisplay = "inventory refresh sweeper";

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly IOptions<InventoryRefreshOptions> _options = options;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly ILogger<InventoryRefreshSweepService> _logger = logger;

    /// <summary>
    /// Requests a fresh inventory from up to <paramref name="batchSize"/> Active
    /// devices whose last upload is older than the configured threshold, oldest
    /// first. Returns how many were asked; 0 when the sweep is disabled.
    /// </summary>
    public async Task<int> SweepAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        var hours = _options.Value.RefreshAfterHours;
        if (hours == 0)
        {
            return 0; // Disabled by configuration; manual refresh is unaffected.
        }

        var now = _timeProvider.GetUtcNow();
        var cutoff = now - TimeSpan.FromHours(hours);

        // A device that has never uploaded is already pending (the flag is derived
        // from a null InventoryCollectedAt), and one whose request post-dates its
        // last upload is pending too; asking either again would only churn the
        // row. Retired devices no longer heartbeat, so a request to one would sit
        // unanswered forever and inflate every audit summary.
        var stale = await _dbContext.Devices
            .Where(d =>
                d.Status == DeviceStatus.Active
                && d.InventoryCollectedAt != null
                && d.InventoryCollectedAt < cutoff
                && (d.InventoryRequestedAt == null || d.InventoryRequestedAt <= d.InventoryCollectedAt))
            .OrderBy(d => d.InventoryCollectedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        foreach (var device in stale)
        {
            device.RequestInventoryRefresh(now);
        }

        foreach (var perOrganization in stale.GroupBy(d => d.OrganizationId))
        {
            var requested = perOrganization.Count();

            _auditWriter.Stage(
                perOrganization.Key,
                AuditActorType.System,
                actorId: null,
                ActorDisplay,
                AuditAction,
                AuditResult.Success,
                a => a.WithStateChange(null, JsonSerializer.Serialize(new { requested, olderThanHours = hours })));
        }

        // The device rows and their audit entries commit together: a request
        // without its record, or a record for a request that rolled back, would
        // both be wrong.
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Inventory refresh sweep asked {Count} device(s) with inventory older than {Hours} h for a fresh upload.",
            stale.Count,
            hours);

        return stale.Count;
    }
}
