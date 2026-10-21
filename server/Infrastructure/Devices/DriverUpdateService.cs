using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
/// installed.
/// </para>
/// </remarks>
public sealed class DriverUpdateService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    DriverUpdateReadService readService,
    TimeProvider timeProvider,
    ILogger<DriverUpdateService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly DriverUpdateReadService _readService = readService;
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
    public async Task<DriverUpdateBulkResult> InstallAllAsync(
        Guid organizationId, IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay, CancellationToken cancellationToken = default)
    {
        var definition = DeviceTaskCatalog.Require(DeviceTaskType.InstallDriverUpdate);
        var devices = await ActiveDevicesAsync(organizationId, scopedDeviceIds, cancellationToken);
        var deviceIds = devices.Select(d => d.Id).ToList();

        // The pending non-preview update ids, grouped by device, in one query.
        var pendingByDevice = (await _dbContext.DeviceDriverUpdates.AsNoTracking()
                .Where(u => deviceIds.Contains(u.DeviceId) && !u.IsPreview)
                .Select(u => new { u.DeviceId, u.UpdateId })
                .ToListAsync(cancellationToken))
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
