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
/// Queues Windows Update security-update work: a scan (find what is pending) or an
/// install (apply the chosen updates), on one device or across the fleet.
/// </summary>
/// <remarks>
/// <para>
/// A thin layer over <see cref="DeviceTaskService"/>. It exists so the two
/// fleet-wide actions have one place to resolve which devices to act on -- active,
/// in the caller's scope, running an agent new enough -- and so a single-device
/// caller can be told which of "no such device" and "agent too old" it hit, which
/// matter here because the whole fleet is below the required agent until the 1.24.0
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
public sealed class SecurityUpdateService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    SecurityUpdateReadService readService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<SecurityUpdateService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly SecurityUpdateReadService _readService = readService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<SecurityUpdateService> _logger = logger;

    /// <summary>Queues a security-update scan on one device the caller has already scoped-checked.</summary>
    public async Task<SecurityUpdateQueueResult> QueueScanAsync(
        Guid organizationId, Guid deviceId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        if (await AgentGate(organizationId, deviceId, DeviceTaskType.ScanSecurityUpdates, cancellationToken)
            is { } refusal)
        {
            return refusal;
        }

        var payload = new TaskPayloads.ScanSecurityUpdates(_timeProvider.GetUtcNow());
        var task = await _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.ScanSecurityUpdates, payload, actorId, actorDisplay, cancellationToken);

        return task is null
            ? new SecurityUpdateQueueResult(SecurityUpdateQueueStatus.DeviceNotFound, null, "The device could not accept the task.")
            : new SecurityUpdateQueueResult(SecurityUpdateQueueStatus.Queued, task, null);
    }

    /// <summary>
    /// Queues an install of the chosen updates on one device the caller has already
    /// scoped-checked. The requested ids are narrowed to the device's currently
    /// pending updates, so a stale selection cannot install something unexpected and
    /// an empty result is reported as nothing to install rather than queued.
    /// </summary>
    public async Task<SecurityUpdateQueueResult> QueueInstallAsync(
        Guid organizationId, Guid deviceId, IReadOnlyCollection<string> updateIds,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        if (await AgentGate(organizationId, deviceId, DeviceTaskType.InstallSecurityUpdate, cancellationToken)
            is { } refusal)
        {
            return refusal;
        }

        var requested = updateIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pending = await _dbContext.DeviceSecurityUpdates.AsNoTracking()
            .Where(u => u.DeviceId == deviceId)
            .Select(u => u.UpdateId)
            .ToListAsync(cancellationToken);

        // Only ids the device still reports pending. Nothing stops an operator
        // choosing a preview update deliberately -- the console warns but allows it
        // -- so the filter is "is this still pending", not "is this a preview".
        var toInstall = pending.Where(requested.Contains).ToList();
        if (toInstall.Count == 0)
        {
            return new SecurityUpdateQueueResult(SecurityUpdateQueueStatus.NothingToInstall, null,
                "None of the selected updates are still pending on this device. Re-scan and try again.");
        }

        var payload = new TaskPayloads.InstallSecurityUpdate(toInstall, _timeProvider.GetUtcNow());
        var task = await _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.InstallSecurityUpdate, payload, actorId, actorDisplay, cancellationToken);

        return task is null
            ? new SecurityUpdateQueueResult(SecurityUpdateQueueStatus.DeviceNotFound, null, "The device could not accept the task.")
            : new SecurityUpdateQueueResult(SecurityUpdateQueueStatus.Queued, task, null);
    }

    /// <summary>
    /// Queues a scan on every active, in-scope device. Devices on an agent too old
    /// to run the scan are skipped and counted, not failed.
    /// </summary>
    public async Task<SecurityUpdateBulkResult> ScanAllAsync(
        Guid organizationId, IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var definition = DeviceTaskCatalog.Require(DeviceTaskType.ScanSecurityUpdates);
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

            var payload = new TaskPayloads.ScanSecurityUpdates(_timeProvider.GetUtcNow());
            var task = await _taskService.QueueAsync(
                organizationId, device.Id, DeviceTaskType.ScanSecurityUpdates, payload, actorId, actorDisplay, cancellationToken);
            if (task is not null)
            {
                queued++;
            }
        }

        _logger.LogInformation(
            "Security-update scan-all by {Actor}: {Considered} device(s), {Queued} queued, {TooOld} on too-old agents.",
            actorDisplay, devices.Count, queued, tooOld);

        return new SecurityUpdateBulkResult(devices.Count, queued, tooOld, DevicesSkippedNothing: 0, TotalUpdates: 0);
    }

    /// <summary>
    /// Queues an install of every pending non-preview update on every active,
    /// in-scope device that has one. Preview updates are never auto-selected here;
    /// devices with nothing non-preview pending, and devices on too-old agents, are
    /// skipped and counted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per device it also leaves out what install-all must not keep throwing at a
    /// machine: updates an operator hid, updates whose last install is waiting on a
    /// restart or on its verification scan (they are not done failing or succeeding
    /// yet -- re-sending one only gets "installer busy / restart pending" back, and
    /// would overwrite the "needs restart" memory with a failure), and updates an
    /// install already queued or running names. For security updates the restart case
    /// is the common one: a cumulative update that installed is waiting for its
    /// reboot, and install-all must not queue it again on every click until that
    /// reboot happens.
    /// </para>
    /// <para>
    /// A RE-OFFERED security update is deliberately NOT left out -- the one rule where
    /// this differs from drivers. A re-offered driver is typically OEM firmware that
    /// never sticks, so retrying it only thrashes. A re-offered security patch is
    /// typically a cumulative update that installed, rolled back during the restart
    /// ("undoing changes", e.g. 0x800F0922) and is offered again: the machine is
    /// unpatched, and a retry once the cause is fixed usually works. Skipping it would
    /// leave the machine unpatched for good, because nothing but the update
    /// disappearing ever moves that record; retrying moves it, through the next
    /// attempt's result. An operator who really wants it left alone hides it.
    /// </para>
    /// <para>
    /// The single-device install stays permissive, exactly as for drivers: an
    /// operator who ticks one of these explicitly is acting deliberately, as with a
    /// preview.
    /// </para>
    /// </remarks>
    public async Task<SecurityUpdateBulkResult> InstallAllAsync(
        Guid organizationId, IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var definition = DeviceTaskCatalog.Require(DeviceTaskType.InstallSecurityUpdate);
        var devices = await ActiveDevicesAsync(organizationId, scopedDeviceIds, cancellationToken);
        var deviceIds = devices.Select(d => d.Id).ToList();

        var excludedByDevice = await NotForInstallAllAsync(deviceIds, cancellationToken);

        // The pending non-preview update ids, grouped by device, in one query, minus
        // what this device must not be sent.
        var pendingByDevice = (await _dbContext.DeviceSecurityUpdates.AsNoTracking()
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

            var payload = new TaskPayloads.InstallSecurityUpdate(ids, now);
            var task = await _taskService.QueueAsync(
                organizationId, device.Id, DeviceTaskType.InstallSecurityUpdate, payload, actorId, actorDisplay, cancellationToken);
            if (task is not null)
            {
                queued++;
                totalUpdates += ids.Count;
            }
        }

        _logger.LogInformation(
            "Security-update install-all by {Actor}: {Considered} device(s), {Queued} queued ({Total} updates), "
            + "{TooOld} too-old agents, {Nothing} with nothing pending.",
            actorDisplay, devices.Count, queued, totalUpdates, tooOld, nothing);

        return new SecurityUpdateBulkResult(devices.Count, queued, tooOld, nothing, totalUpdates);
    }

    /// <summary>
    /// Hides one update on one device the caller has already scope-checked: it
    /// leaves the actionable list and install-all never queues it. Idempotent -- a
    /// second hide changes nothing and writes nothing (the first reason stands).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Platform-side only; nothing is sent to the device. The title is taken from
    /// the pending row (or the install memory) so the hidden list still reads after
    /// Windows stops offering the update. Audited as <c>securityupdate.suppress</c>
    /// with the update id, title and reason in the state change.
    /// </para>
    /// <para>
    /// The reason is required -- leaving a machine unpatched on purpose must say why
    /// -- and the caller has already checked it (1-200 characters after trimming);
    /// the entity enforces the same rule.
    /// </para>
    /// </remarks>
    public async Task<SecurityUpdateSuppressionResult> SuppressAsync(
        Guid organizationId, Guid deviceId, string updateId, string reason,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var device = await _dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);
        if (device is null || device.Status == DeviceStatus.Retired)
        {
            return SecurityUpdateSuppressionResult.DeviceNotFound;
        }

        var id = updateId.Trim();
        if (await FindSuppressionAsync(deviceId, id, cancellationToken) is not null)
        {
            return SecurityUpdateSuppressionResult.Unchanged;
        }

        var title = await TitleOfAsync(deviceId, id, cancellationToken);
        var suppression = new DeviceSecurityUpdateSuppression(deviceId, id, title, reason, actorId, actorDisplay);
        _dbContext.DeviceSecurityUpdateSuppressions.Add(suppression);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: "securityupdate.suppress",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .OnTarget("device_security_update", id, title)
                .Requiring(Permissions.Update.Manage)
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
            return SecurityUpdateSuppressionResult.Unchanged;
        }

        _logger.LogInformation(
            "Security update {UpdateId} hidden on device {DeviceId} by {Actor}.", id, deviceId, actorDisplay);
        return SecurityUpdateSuppressionResult.Changed;
    }

    /// <summary>
    /// Un-hides one update on one device the caller has already scope-checked.
    /// Idempotent -- un-hiding a visible update changes nothing and writes nothing.
    /// Audited as <c>securityupdate.unsuppress</c>.
    /// </summary>
    public async Task<SecurityUpdateSuppressionResult> UnsuppressAsync(
        Guid organizationId, Guid deviceId, string updateId,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var device = await _dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);
        if (device is null || device.Status == DeviceStatus.Retired)
        {
            return SecurityUpdateSuppressionResult.DeviceNotFound;
        }

        var suppression = await FindSuppressionAsync(deviceId, updateId.Trim(), cancellationToken);
        if (suppression is null)
        {
            return SecurityUpdateSuppressionResult.Unchanged;
        }

        _dbContext.DeviceSecurityUpdateSuppressions.Remove(suppression);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: "securityupdate.unsuppress",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .OnTarget("device_security_update", suppression.UpdateId, suppression.Title)
                .Requiring(Permissions.Update.Manage)
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
            return SecurityUpdateSuppressionResult.Unchanged;
        }

        _logger.LogInformation(
            "Security update {UpdateId} un-hidden on device {DeviceId} by {Actor}.", suppression.UpdateId, deviceId, actorDisplay);
        return SecurityUpdateSuppressionResult.Changed;
    }

    /// <summary>
    /// The device's suppression for an update id, matched without regard to case
    /// (Windows Update ids are GUIDs, and the agent normalises them to lower case).
    /// </summary>
    private async Task<DeviceSecurityUpdateSuppression?> FindSuppressionAsync(
        Guid deviceId, string updateId, CancellationToken cancellationToken)
    {
        var lowered = updateId.ToLowerInvariant();
        return await _dbContext.DeviceSecurityUpdateSuppressions
            .Where(s => s.DeviceId == deviceId && s.UpdateId.ToLower() == lowered)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>The update's title from the pending list, else the install memory, else its id.</summary>
    private async Task<string> TitleOfAsync(Guid deviceId, string updateId, CancellationToken cancellationToken)
    {
        var lowered = updateId.ToLowerInvariant();

        var pending = await _dbContext.DeviceSecurityUpdates.AsNoTracking()
            .Where(u => u.DeviceId == deviceId && u.UpdateId.ToLower() == lowered)
            .Select(u => u.Title)
            .FirstOrDefaultAsync(cancellationToken);
        if (pending is not null)
        {
            return pending;
        }

        var remembered = await _dbContext.DeviceSecurityUpdateInstallRecords.AsNoTracking()
            .Where(r => r.DeviceId == deviceId && r.UpdateId.ToLower() == lowered)
            .Select(r => r.Title)
            .FirstOrDefaultAsync(cancellationToken);

        return remembered ?? updateId;
    }

    /// <summary>
    /// Per device, the update ids install-all must leave alone: hidden; waiting on a
    /// restart (PendingReboot) or on a verification scan (InstalledUnverified); or
    /// named by an install still queued or running. NOT re-offered -- a re-offered
    /// security patch is retried (see <see cref="InstallAllAsync"/>). Three queries
    /// for the whole fleet, not three per device.
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

        var hidden = await _dbContext.DeviceSecurityUpdateSuppressions.AsNoTracking()
            .Where(s => deviceIds.Contains(s.DeviceId))
            .Select(s => new { s.DeviceId, s.UpdateId })
            .ToListAsync(cancellationToken);
        foreach (var row in hidden)
        {
            Exclude(row.DeviceId, row.UpdateId);
        }

        // Reboot-pending / unverified: the last install is not finished being judged --
        // a restart or the follow-up scan will settle it, and retrying in the meantime
        // only fails. Re-offered is NOT here: for a security patch a retry is the fix.
        var unsettled = await _dbContext.DeviceSecurityUpdateInstallRecords.AsNoTracking()
            .Where(r => deviceIds.Contains(r.DeviceId)
                        && (r.Status == SecurityUpdateInstallStatus.PendingReboot
                            || r.Status == SecurityUpdateInstallStatus.InstalledUnverified))
            .Select(r => new { r.DeviceId, r.UpdateId })
            .ToListAsync(cancellationToken);
        foreach (var row in unsettled)
        {
            Exclude(row.DeviceId, row.UpdateId);
        }

        var inFlight = await _dbContext.DeviceTasks.AsNoTracking()
            .Where(t => deviceIds.Contains(t.DeviceId)
                        && t.Type == DeviceTaskType.InstallSecurityUpdate
                        && (t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Delivered))
            .Select(t => new { t.DeviceId, t.PayloadJson })
            .ToListAsync(cancellationToken);
        foreach (var task in inFlight)
        {
            foreach (var id in SecurityUpdateInstallTasks.UpdateIdsOf(task.PayloadJson))
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
    private async Task<SecurityUpdateQueueResult?> AgentGate(
        Guid organizationId, Guid deviceId, DeviceTaskType type, CancellationToken cancellationToken)
    {
        var device = await _dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);

        if (device is null || device.Status == DeviceStatus.Retired)
        {
            return new SecurityUpdateQueueResult(SecurityUpdateQueueStatus.DeviceNotFound, null, "No such device.");
        }

        var definition = DeviceTaskCatalog.Require(type);
        if (!DeviceTaskCatalog.IsSupportedBy(definition, device.AgentVersion))
        {
            return new SecurityUpdateQueueResult(SecurityUpdateQueueStatus.AgentTooOld, null,
                $"This device runs agent {device.AgentVersion}; security updates need "
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
public enum SecurityUpdateSuppressionResult
{
    /// <summary>The update was hidden (or un-hidden) by this call.</summary>
    Changed,

    /// <summary>It already was; nothing was written.</summary>
    Unchanged,

    /// <summary>No such device in the caller's organization, or it is retired.</summary>
    DeviceNotFound,
}

public enum SecurityUpdateQueueStatus
{
    Queued,
    DeviceNotFound,
    AgentTooOld,
    NothingToInstall,
}

/// <summary>The outcome of queuing a scan or install on one device.</summary>
public sealed record SecurityUpdateQueueResult(SecurityUpdateQueueStatus Status, DeviceTask? Task, string? Error);

/// <summary>What a fleet-wide scan-all or install-all came to.</summary>
/// <param name="DevicesConsidered">Active, in-scope devices looked at.</param>
/// <param name="DevicesQueued">Devices that got a task.</param>
/// <param name="DevicesSkippedAgentTooOld">Devices skipped because their agent is below 1.24.0.</param>
/// <param name="DevicesSkippedNothing">Devices skipped because they had nothing to install (install-all only).</param>
/// <param name="TotalUpdates">Total updates queued across all devices (install-all only).</param>
public sealed record SecurityUpdateBulkResult(
    int DevicesConsidered,
    int DevicesQueued,
    int DevicesSkippedAgentTooOld,
    int DevicesSkippedNothing,
    int TotalUpdates);
