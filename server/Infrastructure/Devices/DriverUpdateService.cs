using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Queues Windows Update driver-update work: a scan (find what is pending) or an
/// install (apply the chosen updates), on one device or across the fleet.
/// </summary>
/// <remarks>
/// <para>
/// A thin layer over <see cref="DeviceTaskService"/>. It exists so the two
/// fleet-wide actions have one place to resolve which devices to act on -- active,
/// in the caller's scope, running an agent new enough -- and so a single-device
/// caller can be told which of "no such device" and "agent too old" it hit, which
/// matter here because the whole fleet is below the required agent until the 1.21.0
/// rollout reaches it.
/// </para>
/// <para>
/// Nothing here installs anything or decides what Windows Update offers. A scan is
/// read-only on the endpoint; an install names update ids the device re-verifies
/// against Windows Update for itself, so a stale or tampered id simply does not get
/// installed. Hiding an update is platform-side only: it changes what the console
/// counts as actionable and what install-all queues, never anything on Windows.
/// </para>
/// </remarks>
public sealed class DriverUpdateService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    DriverUpdateReadService readService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<DriverUpdateService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly DriverUpdateReadService _readService = readService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<DriverUpdateService> _logger = logger;

    /// <summary>Queues a driver-update scan on one device the caller has already scoped-checked.</summary>
    public async Task<DriverUpdateQueueResult> QueueScanAsync(
        Guid organizationId, Guid deviceId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        if (await AgentGate(organizationId, deviceId, DeviceTaskType.ScanDriverUpdates, cancellationToken)
            is { } refusal)
        {
            return refusal;
        }

        var payload = new TaskPayloads.ScanDriverUpdates(_timeProvider.GetUtcNow());
        var task = await _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.ScanDriverUpdates, payload, actorId, actorDisplay, cancellationToken);

        return task is null
            ? new DriverUpdateQueueResult(DriverUpdateQueueStatus.DeviceNotFound, null, "The device could not accept the task.")
            : new DriverUpdateQueueResult(DriverUpdateQueueStatus.Queued, task, null);
    }

    /// <summary>
    /// Queues an install of the chosen updates on one device the caller has already
    /// scoped-checked. The requested ids are narrowed to the device's currently
    /// pending updates, so a stale selection cannot install something unexpected and
    /// an empty result is reported as nothing to install rather than queued.
    /// </summary>
    public async Task<DriverUpdateQueueResult> QueueInstallAsync(
        Guid organizationId, Guid deviceId, IReadOnlyCollection<string> updateIds,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        if (await AgentGate(organizationId, deviceId, DeviceTaskType.InstallDriverUpdate, cancellationToken)
            is { } refusal)
        {
            return refusal;
        }

        var requested = updateIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = await _dbContext.DeviceDriverUpdates.AsNoTracking()
            .Where(u => u.DeviceId == deviceId)
            .Select(u => u.UpdateId)
            .ToListAsync(cancellationToken);

        // Only ids the device still reports pending. Nothing stops an operator
        // choosing a preview update deliberately -- the console warns but allows it
        // -- so the filter is "is this still pending", not "is this a preview".
        var toInstall = pending.Where(requested.Contains).ToList();
        if (toInstall.Count == 0)
        {
            return new DriverUpdateQueueResult(DriverUpdateQueueStatus.NothingToInstall, null,
                "None of the selected updates are still pending on this device. Re-scan and try again.");
        }

        var payload = new TaskPayloads.InstallDriverUpdate(toInstall, _timeProvider.GetUtcNow());
        var task = await _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.InstallDriverUpdate, payload, actorId, actorDisplay, cancellationToken);

        return task is null
            ? new DriverUpdateQueueResult(DriverUpdateQueueStatus.DeviceNotFound, null, "The device could not accept the task.")
            : new DriverUpdateQueueResult(DriverUpdateQueueStatus.Queued, task, null);
    }

    /// <summary>
    /// Queues a scan on every active, in-scope device. Devices on an agent too old
    /// to run the scan are skipped and counted, not failed.
    /// </summary>
    public async Task<DriverUpdateBulkResult> ScanAllAsync(
        Guid organizationId, IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var definition = DeviceTaskCatalog.Require(DeviceTaskType.ScanDriverUpdates);
        var devices = await ActiveDevicesAsync(organizationId, scopedDeviceIds, cancellationToken);

        var queued = 0;
        var tooOld = 0;
        foreach (var device in devices)
        {
            if (!DeviceTaskCatalog.IsSupportedBy(definition, device.AgentVersion))
            {
                tooOld++;
                continue;
            }

            var payload = new TaskPayloads.ScanDriverUpdates(_timeProvider.GetUtcNow());
            var task = await _taskService.QueueAsync(
                organizationId, device.Id, DeviceTaskType.ScanDriverUpdates, payload, actorId, actorDisplay, cancellationToken);
            if (task is not null)
            {
                queued++;
            }
        }

        _logger.LogInformation(
            "Driver-update scan-all by {Actor}: {Considered} device(s), {Queued} queued, {TooOld} on too-old agents.",
            actorDisplay, devices.Count, queued, tooOld);

        return new DriverUpdateBulkResult(devices.Count, queued, tooOld, DevicesSkippedNothing: 0, TotalUpdates: 0);
    }

    /// <summary>
    /// Queues an install of every pending non-preview update on every active,
    /// in-scope device that has one. Preview updates are never auto-selected here;
    /// devices with nothing non-preview pending, and devices on too-old agents, are
    /// skipped and counted.
    /// </summary>
    /// <remarks>
    /// Per device it also leaves out what install-all must not keep throwing at a
    /// machine: updates an operator hid, updates Windows Update re-offered after an
    /// install (retrying the same channel cannot complete them), updates whose last
    /// install is waiting on a restart or on its verification scan (they are not
    /// done failing or succeeding yet -- re-sending one only gets "installer busy /
    /// restart pending" back, and used to overwrite the "needs restart" memory with a
    /// failure), and updates an install already queued or running names (no
    /// double-queuing while one is in flight, nor in the short window before its
    /// follow-up scan settles). So repeated use converges instead of thrashing on
    /// firmware. The single-device install stays permissive: an operator who ticks
    /// one of these explicitly is acting deliberately, as with a preview.
    /// </remarks>
    public async Task<DriverUpdateBulkResult> InstallAllAsync(
        Guid organizationId, IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var definition = DeviceTaskCatalog.Require(DeviceTaskType.InstallDriverUpdate);
        var devices = await ActiveDevicesAsync(organizationId, scopedDeviceIds, cancellationToken);
        var deviceIds = devices.Select(d => d.Id).ToList();

        var excludedByDevice = await NotForInstallAllAsync(deviceIds, cancellationToken);

        // The pending non-preview update ids, grouped by device, in one query, minus
        // what this device must not be sent.
        var pendingByDevice = (await _dbContext.DeviceDriverUpdates.AsNoTracking()
                .Where(u => deviceIds.Contains(u.DeviceId) && !u.IsPreview)
                .Select(u => new { u.DeviceId, u.UpdateId })
                .ToListAsync(cancellationToken))
            .Where(u => !(excludedByDevice.TryGetValue(u.DeviceId, out var excluded) && excluded.Contains(u.UpdateId)))
            .GroupBy(u => u.DeviceId)
            .ToDictionary(g => g.Key, g => g.Select(u => u.UpdateId).ToList());

        var now = _timeProvider.GetUtcNow();
        var queued = 0;
        var tooOld = 0;
        var nothing = 0;
        var totalUpdates = 0;

        foreach (var device in devices)
        {
            if (!pendingByDevice.TryGetValue(device.Id, out var ids) || ids.Count == 0)
            {
                nothing++;
                continue;
            }

            if (!DeviceTaskCatalog.IsSupportedBy(definition, device.AgentVersion))
            {
                tooOld++;
                continue;
            }

            var payload = new TaskPayloads.InstallDriverUpdate(ids, now);
            var task = await _taskService.QueueAsync(
                organizationId, device.Id, DeviceTaskType.InstallDriverUpdate, payload, actorId, actorDisplay, cancellationToken);
            if (task is not null)
            {
                queued++;
                totalUpdates += ids.Count;
            }
        }

        _logger.LogInformation(
            "Driver-update install-all by {Actor}: {Considered} device(s), {Queued} queued ({Total} updates), "
            + "{TooOld} too-old agents, {Nothing} with nothing pending.",
            actorDisplay, devices.Count, queued, totalUpdates, tooOld, nothing);

        return new DriverUpdateBulkResult(devices.Count, queued, tooOld, nothing, totalUpdates);
    }

    /// <summary>
    /// Hides one update on one device the caller has already scope-checked: it
    /// leaves the actionable list and install-all never queues it. Idempotent -- a
    /// second hide changes nothing and writes nothing.
    /// </summary>
    /// <remarks>
    /// Platform-side only; nothing is sent to the device. The title is taken from
    /// the pending row (or the install memory) so the hidden list still reads after
    /// Windows stops offering the update. Audited as <c>driverupdate.suppress</c>
    /// with the update id and title in the state change.
    /// </remarks>
    public async Task<DriverUpdateSuppressionResult> SuppressAsync(
        Guid organizationId, Guid deviceId, string updateId, string? reason,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var device = await _dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);
        if (device is null || device.Status == DeviceStatus.Retired)
        {
            return DriverUpdateSuppressionResult.DeviceNotFound;
        }

        var id = updateId.Trim();
        if (await FindSuppressionAsync(deviceId, id, cancellationToken) is not null)
        {
            return DriverUpdateSuppressionResult.Unchanged;
        }

        var title = await TitleOfAsync(deviceId, id, cancellationToken);
        var suppression = new DeviceDriverUpdateSuppression(deviceId, id, title, reason, actorId, actorDisplay);
        _dbContext.DeviceDriverUpdateSuppressions.Add(suppression);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: "driverupdate.suppress",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .OnTarget("device_driver_update", id, title)
                .Requiring(Permissions.Driver.Manage)
                .WithStateChange(null, JsonSerializer.Serialize(new { updateId = id, title, reason = suppression.Reason, hidden = true })));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another operator hid the same update in the same instant. Theirs
            // stands, and the outcome the caller asked for is true either way.
            _dbContext.ChangeTracker.Clear();
            return DriverUpdateSuppressionResult.Unchanged;
        }

        _logger.LogInformation(
            "Driver update {UpdateId} hidden on device {DeviceId} by {Actor}.", id, deviceId, actorDisplay);
        return DriverUpdateSuppressionResult.Changed;
    }

    /// <summary>
    /// Un-hides one update on one device the caller has already scope-checked.
    /// Idempotent -- un-hiding a visible update changes nothing and writes nothing.
    /// Audited as <c>driverupdate.unsuppress</c>.
    /// </summary>
    public async Task<DriverUpdateSuppressionResult> UnsuppressAsync(
        Guid organizationId, Guid deviceId, string updateId,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var device = await _dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);
        if (device is null || device.Status == DeviceStatus.Retired)
        {
            return DriverUpdateSuppressionResult.DeviceNotFound;
        }

        var suppression = await FindSuppressionAsync(deviceId, updateId.Trim(), cancellationToken);
        if (suppression is null)
        {
            return DriverUpdateSuppressionResult.Unchanged;
        }

        _dbContext.DeviceDriverUpdateSuppressions.Remove(suppression);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: "driverupdate.unsuppress",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .OnTarget("device_driver_update", suppression.UpdateId, suppression.Title)
                .Requiring(Permissions.Driver.Manage)
                .WithStateChange(
                    JsonSerializer.Serialize(new
                    {
                        updateId = suppression.UpdateId,
                        title = suppression.Title,
                        reason = suppression.Reason,
                        hiddenBy = suppression.CreatedByDisplay,
                        hidden = true,
                    }),
                    JsonSerializer.Serialize(new { updateId = suppression.UpdateId, title = suppression.Title, hidden = false })));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Un-hidden by someone else in the same instant: already the outcome asked for.
            _dbContext.ChangeTracker.Clear();
            return DriverUpdateSuppressionResult.Unchanged;
        }

        _logger.LogInformation(
            "Driver update {UpdateId} un-hidden on device {DeviceId} by {Actor}.", suppression.UpdateId, deviceId, actorDisplay);
        return DriverUpdateSuppressionResult.Changed;
    }

    /// <summary>
    /// The device's suppression for an update id, matched without regard to case
    /// (Windows Update ids are GUIDs, and the agent normalises them to lower case).
    /// </summary>
    private async Task<DeviceDriverUpdateSuppression?> FindSuppressionAsync(
        Guid deviceId, string updateId, CancellationToken cancellationToken)
    {
        var lowered = updateId.ToLowerInvariant();
        return await _dbContext.DeviceDriverUpdateSuppressions
            .Where(s => s.DeviceId == deviceId && s.UpdateId.ToLower() == lowered)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The update's title from the pending list, else the install memory, else its id.</summary>
    private async Task<string> TitleOfAsync(Guid deviceId, string updateId, CancellationToken cancellationToken)
    {
        var lowered = updateId.ToLowerInvariant();

        var pending = await _dbContext.DeviceDriverUpdates.AsNoTracking()
            .Where(u => u.DeviceId == deviceId && u.UpdateId.ToLower() == lowered)
            .Select(u => u.Title)
            .FirstOrDefaultAsync(cancellationToken);
        if (pending is not null)
        {
            return pending;
        }

        var remembered = await _dbContext.DeviceDriverUpdateInstallRecords.AsNoTracking()
            .Where(r => r.DeviceId == deviceId && r.UpdateId.ToLower() == lowered)
            .Select(r => r.Title)
            .FirstOrDefaultAsync(cancellationToken);

        return remembered ?? updateId;
    }

    /// <summary>
    /// Per device, the update ids install-all must leave alone: hidden; re-offered
    /// after an install; waiting on a restart (PendingReboot) or on a verification
    /// scan (InstalledUnverified); or named by an install still queued or running.
    /// Three queries for the whole fleet, not three per device.
    /// </summary>
    private async Task<Dictionary<Guid, HashSet<string>>> NotForInstallAllAsync(
        List<Guid> deviceIds, CancellationToken cancellationToken)
    {
        var excluded = new Dictionary<Guid, HashSet<string>>();

        void Exclude(Guid deviceId, string updateId)
        {
            if (!excluded.TryGetValue(deviceId, out var ids))
            {
                ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                excluded[deviceId] = ids;
            }

            ids.Add(updateId);
        }

        var hidden = await _dbContext.DeviceDriverUpdateSuppressions.AsNoTracking()
            .Where(s => deviceIds.Contains(s.DeviceId))
            .Select(s => new { s.DeviceId, s.UpdateId })
            .ToListAsync(cancellationToken);
        foreach (var row in hidden)
        {
            Exclude(row.DeviceId, row.UpdateId);
        }

        // Re-offered: retrying cannot complete it. Reboot-pending / unverified: the
        // last install is not finished being judged -- a restart or the follow-up
        // scan will settle it, and retrying in the meantime only fails.
        var unsettled = await _dbContext.DeviceDriverUpdateInstallRecords.AsNoTracking()
            .Where(r => deviceIds.Contains(r.DeviceId)
                        && (r.Status == DriverUpdateInstallStatus.ReOffered
                            || r.Status == DriverUpdateInstallStatus.PendingReboot
                            || r.Status == DriverUpdateInstallStatus.InstalledUnverified))
            .Select(r => new { r.DeviceId, r.UpdateId })
            .ToListAsync(cancellationToken);
        foreach (var row in unsettled)
        {
            Exclude(row.DeviceId, row.UpdateId);
        }

        var inFlight = await _dbContext.DeviceTasks.AsNoTracking()
            .Where(t => deviceIds.Contains(t.DeviceId)
                        && t.Type == DeviceTaskType.InstallDriverUpdate
                        && (t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Delivered))
            .Select(t => new { t.DeviceId, t.PayloadJson })
            .ToListAsync(cancellationToken);
        foreach (var task in inFlight)
        {
            foreach (var id in DriverUpdateInstallTasks.UpdateIdsOf(task.PayloadJson))
            {
                Exclude(task.DeviceId, id);
            }
        }

        return excluded;
    }

    /// <summary>
    /// A refusal if the device is missing or its agent is too old for the task,
    /// otherwise null. Keeps the two single-device callers' agent-gate identical.
    /// </summary>
    private async Task<DriverUpdateQueueResult?> AgentGate(
        Guid organizationId, Guid deviceId, DeviceTaskType type, CancellationToken cancellationToken)
    {
        var device = await _dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);

        if (device is null || device.Status == DeviceStatus.Retired)
        {
            return new DriverUpdateQueueResult(DriverUpdateQueueStatus.DeviceNotFound, null, "No such device.");
        }

        var definition = DeviceTaskCatalog.Require(type);
        if (!DeviceTaskCatalog.IsSupportedBy(definition, device.AgentVersion))
        {
            return new DriverUpdateQueueResult(DriverUpdateQueueStatus.AgentTooOld, null,
                $"This device runs agent {device.AgentVersion}; driver updates need "
                + $"{definition.MinimumAgentVersion} or newer. Update the agent first.");
        }

        return null;
    }

    private async Task<List<ActiveDevice>> ActiveDevicesAsync(
        Guid organizationId, IReadOnlyCollection<Guid>? scopedDeviceIds, CancellationToken cancellationToken)
    {
        var query = _dbContext.Devices.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.Status == DeviceStatus.Active);

        if (scopedDeviceIds is not null)
        {
            var visible = scopedDeviceIds.ToList();
            query = query.Where(d => visible.Contains(d.Id));
        }

        return await query
            .OrderBy(d => d.Hostname)
            .Select(d => new ActiveDevice(d.Id, d.AgentVersion))
            .ToListAsync(cancellationToken);
    }

    private sealed record ActiveDevice(Guid Id, string? AgentVersion);
}

/// <summary>What a hide or un-hide came to.</summary>
public enum DriverUpdateSuppressionResult
{
    /// <summary>The update was hidden (or un-hidden) by this call.</summary>
    Changed,

    /// <summary>It already was; nothing was written.</summary>
    Unchanged,

    /// <summary>No such device in the caller's organization, or it is retired.</summary>
    DeviceNotFound,
}

public enum DriverUpdateQueueStatus
{
    Queued,
    DeviceNotFound,
    AgentTooOld,
    NothingToInstall,
}

/// <summary>The outcome of queuing a scan or install on one device.</summary>
public sealed record DriverUpdateQueueResult(DriverUpdateQueueStatus Status, DeviceTask? Task, string? Error);

/// <summary>What a fleet-wide scan-all or install-all came to.</summary>
/// <param name="DevicesConsidered">Active, in-scope devices looked at.</param>
/// <param name="DevicesQueued">Devices that got a task.</param>
/// <param name="DevicesSkippedAgentTooOld">Devices skipped because their agent is below 1.21.0.</param>
/// <param name="DevicesSkippedNothing">Devices skipped because they had nothing to install (install-all only).</param>
/// <param name="TotalUpdates">Total updates queued across all devices (install-all only).</param>
public sealed record DriverUpdateBulkResult(
    int DevicesConsidered,
    int DevicesQueued,
    int DevicesSkippedAgentTooOld,
    int DevicesSkippedNothing,
    int TotalUpdates);
