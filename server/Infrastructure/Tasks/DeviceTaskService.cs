using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EndpointPlatform.Infrastructure.Tasks;

public enum TaskCancelResult
{
    Success,
    NotFound,
    /// <summary>Already delivered to the agent, or already terminal.</summary>
    NotCancellable,
}

/// <summary>
/// Queues typed tasks for administrators, delivers them to agents on poll, and
/// records their results. The one place tasks are created, so every task is
/// permission-checked (by the caller) and audited (here).
/// </summary>
public sealed partial class DeviceTaskService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<DeviceTaskService> logger)
{
    /// <summary>Cap on tasks handed out per poll, so one backlogged agent cannot pull thousands at once.</summary>
    public const int MaxTasksPerPoll = 20;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<DeviceTaskService> _logger = logger;

    /// <summary>
    /// Whether a failed save was the database refusing a duplicate where only
    /// one row is allowed -- in practice a second restart for a device that
    /// already has one in flight
    /// (<c>ux_device_tasks_active_restart_per_device</c>).
    /// </summary>
    /// <remarks>
    /// Lives here because Npgsql does, and because the caller that needs it is
    /// an API endpoint that should not take a database driver reference to ask
    /// one question. The endpoint checks for an active restart before queueing,
    /// so this is the losing side of a race rather than the ordinary path.
    /// </remarks>
    public static bool IsDuplicateActiveTask(DbUpdateException exception) =>
        exception?.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// Queues a task for a device after the caller's permission has already been
    /// checked at the endpoint. Returns null if the device does not exist in the
    /// caller's organization or is retired.
    /// </summary>
    public async Task<DeviceTask?> QueueAsync(
        Guid organizationId,
        Guid deviceId,
        DeviceTaskType type,
        object? payload,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default,
        TimeSpan? timeToLive = null)
    {
        var device = await _dbContext.Devices
            .SingleOrDefaultAsync(
                d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);

        if (device is null || device.Status == DeviceStatus.Retired)
        {
            return null;
        }

        var definition = DeviceTaskCatalog.Require(type);

        // Refused before the row exists rather than after the endpoint fails it.
        // An agent without the executor would claim the task and report an unknown
        // type, which reads in the console exactly like the operation genuinely
        // failing -- so the operator would retry a thing that cannot work.
        if (!DeviceTaskCatalog.IsSupportedBy(definition, device.AgentVersion))
        {
            _logger.LogInformation(
                "Refusing to queue {Type} for device {DeviceId}: agent {Reported} is below the required {Required}.",
                type, device.Id, device.AgentVersion, definition.MinimumAgentVersion);

            return null;
        }

        var payloadJson = payload is null ? null : JsonSerializer.Serialize(payload, JsonOptions);
        var now = _timeProvider.GetUtcNow();

        // A caller may shorten the deadline (a scheduled restart is pointless
        // after its moment), never lengthen it past the catalogue's ceiling.
        var ceiling = TimeSpan.FromSeconds(definition.DefaultTimeToLiveSeconds);
        var ttl = timeToLive is { } requested && requested > TimeSpan.Zero && requested < ceiling ? requested : ceiling;

        var task = DeviceTask.Create(
            organizationId, deviceId, type, payloadJson, actorId, actorDisplay, now, ttl);

        _dbContext.DeviceTasks.Add(task);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: $"task.queue.{type.ToString().ToLowerInvariant()}",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .OnTarget("device_task", task.Id.ToString(), type.ToString())
                .Requiring(definition.RequiredPermission)
                .WithStateChange(null, payloadJson));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Task {TaskId} ({Type}) queued for device {DeviceId} by {Actor}.",
            task.Id, type, device.Id, actorDisplay);

        return task;
    }

    /// <summary>
    /// Claims queued tasks for a device and marks them Delivered. Called by the
    /// authenticated agent poll. Expired-while-queued tasks are swept here too.
    /// </summary>
    public async Task<IReadOnlyList<DeviceTask>> ClaimForDeviceAsync(
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var candidates = await _dbContext.DeviceTasks
            .Where(t => t.DeviceId == deviceId && t.Status == DeviceTaskStatus.Queued)
            .OrderBy(t => t.CreatedAt)
            .Take(MaxTasksPerPoll)
            .ToListAsync(cancellationToken);

        var delivered = new List<DeviceTask>(candidates.Count);

        foreach (var task in candidates)
        {
            if (task.TryDeliver(now))
            {
                // Long work gets its execution grace from the moment it is claimed,
                // so the sweeper cannot expire it while the device is still doing
                // it. The agent receives the extended deadline with the task.
                if (DeviceTaskCatalog.All.TryGetValue(task.Type, out var definition)
                    && definition.ExecutionGraceSeconds > 0)
                {
                    task.ExtendForExecution(now + TimeSpan.FromSeconds(definition.ExecutionGraceSeconds));
                }

                delivered.Add(task);
            }
        }

        if (candidates.Count > 0)
        {
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Another poll claimed some of these first. Return only what we know
                // committed cleanly by re-reading; simplest safe behaviour is to
                // return nothing this round and let the agent poll again.
                _dbContext.ChangeTracker.Clear();
                _logger.LogDebug("Concurrent task claim for device {DeviceId}; deferring to next poll.", deviceId);
                return [];
            }
        }

        return delivered;
    }

    /// <summary>
    /// Expires tasks whose deadline has passed while still Queued or Delivered,
    /// across all devices. Called by a background sweeper so that tasks for an
    /// offline device (which never polls) still transition to Expired rather than
    /// lingering as Queued forever. Returns the number expired.
    /// </summary>
    /// <remarks>
    /// The expiry message says whether the device ever claimed the task. A driver
    /// install the device claimed and never answered also gets a follow-up scan
    /// queued, so the console learns the machine's real state without anyone asking
    /// -- and its message then says what actually became of that scan (queued,
    /// already pending, or could not be queued), never a blanket "queued".
    /// </remarks>
    public async Task<int> SweepExpiredAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var stale = await _dbContext.DeviceTasks
            .Where(t =>
                (t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Delivered)
                && t.ExpiresAt <= now)
            .OrderBy(t => t.ExpiresAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        var expired = 0;
        var silentInstalls = new List<DeviceTask>();
        foreach (var task in stale)
        {
            // A driver install the device claimed and then went silent on may well
            // have finished: the platform's answer is to look, so the first thing it
            // learns when the device is back is what is actually installed.
            var silentInstall = task.Type == DeviceTaskType.InstallDriverUpdate
                && task.Status == DeviceTaskStatus.Delivered;

            // No follow-up note yet: what the platform did about it is only known
            // once the scan has been asked for, below.
            if (task.TryExpire(now))
            {
                expired++;
                if (silentInstall)
                {
                    silentInstalls.Add(task);
                }
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (expired > 0)
        {
            _logger.LogInformation("Task expiry sweep expired {Count} task(s).", expired);
        }

        // After the expiries are committed: a failure to queue one scan must not
        // roll back the sweep, and each install is followed up on its own. The scan
        // must be claimed after the install was to see what it did; the expiry
        // message is then worded from what actually happened.
        foreach (var install in silentInstalls)
        {
            var (outcome, _) = await TryQueueFollowUpScanAsync(
                install, install.DeliveredAt ?? now, "the install expired without a result", cancellationToken);
            await NoteExpiryFollowUpAsync(install.Id, outcome, cancellationToken);
        }

        return expired;
    }

    /// <summary>
    /// Cancels a task that is still Queued. Delivered tasks cannot be cancelled:
    /// the agent may already be acting on one, and pretending to stop work that
    /// is mid-flight on a Windows machine would make Cancelled a lie.
    /// </summary>
    /// <remarks>
    /// The practical case is an action queued against an offline device — a
    /// mistaken shutdown would otherwise sit Queued until its TTL expires, with
    /// the administrator unable to do anything but wait for it to fire whenever
    /// the machine next checks in.
    /// </remarks>
    public async Task<TaskCancelResult> CancelAsync(
        Guid organizationId,
        Guid deviceId,
        Guid taskId,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.DeviceTasks
            .SingleOrDefaultAsync(
                t => t.Id == taskId && t.DeviceId == deviceId && t.OrganizationId == organizationId,
                cancellationToken);

        if (task is null)
        {
            return TaskCancelResult.NotFound;
        }

        var now = _timeProvider.GetUtcNow();

        if (!task.TryCancel(now, $"Cancelled by {actorDisplay}."))
        {
            return TaskCancelResult.NotCancellable;
        }

        var definition = DeviceTaskCatalog.Require(task.Type);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: $"task.cancel.{task.Type.ToString().ToLowerInvariant()}",
            AuditResult.Success,
            audit => audit
                .OnDevice(deviceId, deviceId.ToString())
                .OnTarget("device_task", task.Id.ToString(), task.Type.ToString())
                .Requiring(definition.RequiredPermission));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The agent claimed it in the same instant. The claim won, so the task
            // is genuinely on its way to the machine — report it as uncancellable
            // rather than pretending the race went the administrator's way.
            _dbContext.ChangeTracker.Clear();
            return TaskCancelResult.NotCancellable;
        }

        _logger.LogInformation("Task {TaskId} ({Type}) cancelled by {Actor}.", taskId, task.Type, actorDisplay);
        return TaskCancelResult.Success;
    }

    /// <summary>
    /// Applies an agent-reported result. Returns false if the task is not awaiting
    /// one: unknown to this device, never delivered, or already settled.
    /// </summary>
    /// <remarks>
    /// A delivered driver install the sweeper already expired still takes its
    /// result, marked as late (<see cref="DeviceTask.TryCompleteLate"/>), and goes
    /// through exactly the same ingestion as an on-time one -- the device's report
    /// is the truth, whenever it lands. Every other task type keeps refusing a late
    /// result: for those an expiry has been a settled answer since day one (a late
    /// restart "success" in particular must not resurrect an expired restart), and
    /// widening that is its own decision, not a side effect of the driver fix.
    /// </remarks>
    public async Task<bool> CompleteAsync(
        Guid deviceId,
        Guid taskId,
        bool succeeded,
        string? message,
        string? resultJson,
        CancellationToken cancellationToken = default)
    {
        var task = await _dbContext.DeviceTasks
            .SingleOrDefaultAsync(t => t.Id == taskId && t.DeviceId == deviceId, cancellationToken);

        if (task is null)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();

        if (!task.TryComplete(succeeded, message, resultJson, now))
        {
            if (task.Type != DeviceTaskType.InstallDriverUpdate
                || !task.TryCompleteLate(succeeded, message, resultJson, now))
            {
                _logger.LogWarning(
                    "Rejected result for task {TaskId}: status was {Status}, not Delivered.", taskId, task.Status);
                return false;
            }

            _logger.LogInformation(
                "Task {TaskId} ({Type}) had expired after delivery; its late result was accepted.", taskId, task.Type);
        }

        _auditWriter.Stage(
            task.OrganizationId,
            AuditActorType.Agent,
            deviceId,
            deviceId.ToString(),
            action: $"task.result.{task.Type.ToString().ToLowerInvariant()}",
            succeeded ? AuditResult.Success : AuditResult.Failure,
            audit => audit
                .OnDevice(deviceId, deviceId.ToString())
                .OnTarget("device_task", task.Id.ToString(), task.Type.ToString())
                .WithFailureReason(succeeded ? null : message));

        if (task.Type == DeviceTaskType.CancelRestart && succeeded)
        {
            await MarkRestartCancelledAsync(task, deviceId, now, cancellationToken);
        }

        if (task.Type == DeviceTaskType.ScanDriverUpdates && succeeded)
        {
            await ApplyDriverUpdateScanAsync(task, deviceId, resultJson, now, cancellationToken);
        }

        // Success AND failure: a failed install still reports per-update codes, and
        // a failure is as much a fact to remember as a success.
        FollowUpScanNeed? installFollowUp = null;
        if (task.Type == DeviceTaskType.InstallDriverUpdate)
        {
            installFollowUp = await ApplyDriverUpdateInstallAsync(task, deviceId, resultJson, now, cancellationToken);
        }

        if (task.Type == DeviceTaskType.ScanSecurityUpdates && succeeded)
        {
            await ApplySecurityUpdateScanAsync(deviceId, resultJson, now, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Task {TaskId} ({Type}) completed: {Outcome}.",
            taskId, task.Type, succeeded ? "success" : "failure");

        // An install result that did not carry a fresh offered list (every agent
        // before 1.27.0, and a 1.27.0 whose verification search failed) leaves the
        // pending list describing the machine before the install; a reboot-pending
        // result that landed after the restart needs a scan from after that restart.
        // The server asks for the truth itself -- never again dependent on a browser
        // tab staying open. After the save, so the result is recorded whatever
        // happens here.
        if (installFollowUp is { } need)
        {
            await TryQueueFollowUpScanAsync(task, need.ObserveAfter, need.Why, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// The device aborted the countdown a restart task had started, so that
    /// restart is now Cancelled: "Succeeded" meant Windows had accepted it, and
    /// that is no longer the truth. The restart is named in the cancellation's
    /// payload and must belong to the same device -- a device can only ever
    /// undo its own restart.
    /// </summary>
    private async Task MarkRestartCancelledAsync(
        DeviceTask cancellation, Guid deviceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid restartTaskId;
        string requestedBy;
        try
        {
            var payload = JsonSerializer.Deserialize<TaskPayloads.CancelRestart>(cancellation.PayloadJson ?? "", JsonOptions);
            if (payload is null)
            {
                return;
            }

            (restartTaskId, requestedBy) = (payload.RestartTaskId, payload.RequestedBy);
        }
        catch (JsonException)
        {
            _logger.LogWarning("Cancel-restart task {TaskId} carries no readable restart id; no restart marked cancelled.", cancellation.Id);
            return;
        }

        var restart = await _dbContext.DeviceTasks
            .SingleOrDefaultAsync(t => t.Id == restartTaskId && t.DeviceId == deviceId, cancellationToken);
        if (restart is null || !restart.TryCancelAcceptedRestart(now, $"Cancelled by {requestedBy}: the device aborted the restart before it happened."))
        {
            return;
        }

        _auditWriter.Stage(
            restart.OrganizationId,
            AuditActorType.Agent,
            deviceId,
            deviceId.ToString(),
            action: "task.cancel.restartdevice",
            AuditResult.Success,
            audit => audit
                .OnDevice(deviceId, deviceId.ToString())
                .OnTarget("device_task", restart.Id.ToString(), restart.Type.ToString())
                .WithStateChange(
                    JsonSerializer.Serialize(new { status = "Succeeded" }),
                    JsonSerializer.Serialize(new { status = "Cancelled", cancelTaskId = cancellation.Id, requestedBy })));

        _logger.LogInformation(
            "Restart task {RestartTaskId} marked Cancelled: device {DeviceId} aborted it on cancel task {CancelTaskId}.",
            restart.Id, deviceId, cancellation.Id);
    }

    /// <summary>Most pending driver updates one scan may report, to bound a hostile result.</summary>
    private const int MaxDriverUpdatesPerScan = 500;

    /// <summary>
    /// A driver-update scan succeeded, so the device's pending-update list is now
    /// whatever the scan reported. Replaced wholesale, like a driver or software
    /// inventory section: a scan that found nothing clears the rows rather than
    /// leaving a stale list that reads as "still pending".
    /// </summary>
    /// <remarks>
    /// The result is agent-supplied, so nothing in it is trusted: an item with no
    /// update id or title is dropped, string lengths are bounded to what the store
    /// accepts, the first of any duplicate update id wins, and the list is capped. A
    /// result that will not parse leaves the previous list untouched and is logged,
    /// not thrown -- a malformed scan should not fail the task completion that
    /// carried it. Once the list is replaced, the device's install memory is
    /// reconciled against it (<see cref="ReconcileInstallRecordsAsync"/>).
    /// </remarks>
    private async Task ApplyDriverUpdateScanAsync(
        DeviceTask scan, Guid deviceId, string? resultJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        DriverUpdateScanReport? report;
        try
        {
            report = string.IsNullOrWhiteSpace(resultJson)
                ? null
                : JsonSerializer.Deserialize<DriverUpdateScanReport>(resultJson, JsonOptions);
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "Driver-update scan for device {DeviceId} returned an unreadable result; pending list left unchanged.",
                deviceId);
            return;
        }

        if (report is null)
        {
            _logger.LogWarning(
                "Driver-update scan for device {DeviceId} returned no result body; pending list left unchanged.",
                deviceId);
            return;
        }

        var pending = await ReplacePendingRowsAsync(deviceId, report, now, cancellationToken);

        // The scan ran when the device claimed it, on the server's clock; that is
        // when the offered list it reports was true. It judges only attempts
        // strictly before that: an install claimed in the same batch shares the
        // claim time and may have run after the scan.
        await ReconcileInstallRecordsAsync(
            deviceId, pending, observedAt: scan.DeliveredAt ?? now, now, records: null, ownInstallTaskId: null, cancellationToken);
    }

    /// <summary>
    /// Replaces the device's pending driver-update rows with what a scan-shaped
    /// report says Windows Update offers. The one implementation behind both a
    /// <c>ScanDriverUpdates</c> result and the verification list an
    /// <c>InstallDriverUpdate</c> result carries (<c>postScan</c>), so the two can
    /// never disagree on what they trust. Returns the new rows (not yet saved).
    /// </summary>
    private async Task<IReadOnlyList<DeviceDriverUpdate>> ReplacePendingRowsAsync(
        Guid deviceId, DriverUpdateScanReport report, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.DeviceDriverUpdates
            .Where(u => u.DeviceId == deviceId)
            .ToListAsync(cancellationToken);
        _dbContext.DeviceDriverUpdates.RemoveRange(existing);

        var collectedAt = report.ScannedAt ?? now;
        var reported = new List<DeviceDriverUpdate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in report.Updates ?? [])
        {
            if (reported.Count >= MaxDriverUpdatesPerScan)
            {
                _logger.LogWarning(
                    "Driver-update scan for device {DeviceId} reported more than {Max} updates; the rest were dropped.",
                    deviceId, MaxDriverUpdatesPerScan);
                break;
            }

            // The update id is Windows Update's identity for the update and is a
            // GUID, so an absent one or one too long to be a GUID is a malformed
            // item, not an update. A missing title likewise -- there is nothing to
            // show the operator.
            if (string.IsNullOrWhiteSpace(item.UpdateId) || item.UpdateId.Length > 64
                || string.IsNullOrWhiteSpace(item.Title))
            {
                continue;
            }

            // A repeat of an update id is a malformed result rather than two
            // updates; keep the first and drop the rest, as the inventory ingestion
            // does with a repeated device instance id.
            if (!seen.Add(item.UpdateId))
            {
                continue;
            }

            reported.Add(new DeviceDriverUpdate(
                deviceId,
                item.UpdateId,
                item.Revision,
                Truncate(item.Title, 512)!,
                Truncate(item.DriverClass, 128),
                Truncate(item.DriverProvider, 256),
                item.DriverVerDate,
                item.IsPreview,
                item.SizeBytes,
                collectedAt));
        }

        _dbContext.DeviceDriverUpdates.AddRange(reported);

        _logger.LogInformation(
            "Driver-update scan for device {DeviceId}: {Count} update(s) pending.", deviceId, reported.Count);

        return reported;
    }

    /// <summary>Most pending security updates one scan may report, to bound a hostile result.</summary>
    private const int MaxSecurityUpdatesPerScan = 500;

    /// <summary>
    /// A security-update scan succeeded, so the device's pending-update list is now
    /// whatever the scan reported. Replaced wholesale, like a driver or software
    /// inventory section: a scan that found nothing clears the rows rather than
    /// leaving a stale list that reads as "still pending".
    /// </summary>
    /// <remarks>
    /// The result is agent-supplied, so nothing in it is trusted: an item with no
    /// update id or title is dropped, string lengths are bounded to what the store
    /// accepts, the first of any duplicate update id wins, and the list is capped. A
    /// result that will not parse leaves the previous list untouched and is logged,
    /// not thrown -- a malformed scan should not fail the task completion that
    /// carried it.
    /// </remarks>
    private async Task ApplySecurityUpdateScanAsync(
        Guid deviceId, string? resultJson, DateTimeOffset now, CancellationToken cancellationToken)
    {
        SecurityUpdateScanReport? report;
        try
        {
            report = string.IsNullOrWhiteSpace(resultJson)
                ? null
                : JsonSerializer.Deserialize<SecurityUpdateScanReport>(resultJson, JsonOptions);
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "Security-update scan for device {DeviceId} returned an unreadable result; pending list left unchanged.",
                deviceId);
            return;
        }

        if (report is null)
        {
            _logger.LogWarning(
                "Security-update scan for device {DeviceId} returned no result body; pending list left unchanged.",
                deviceId);
            return;
        }

        var existing = await _dbContext.DeviceSecurityUpdates
            .Where(u => u.DeviceId == deviceId)
            .ToListAsync(cancellationToken);
        _dbContext.DeviceSecurityUpdates.RemoveRange(existing);

        var collectedAt = report.ScannedAt ?? now;
        var reported = new List<DeviceSecurityUpdate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in report.Updates ?? [])
        {
            if (reported.Count >= MaxSecurityUpdatesPerScan)
            {
                _logger.LogWarning(
                    "Security-update scan for device {DeviceId} reported more than {Max} updates; the rest were dropped.",
                    deviceId, MaxSecurityUpdatesPerScan);
                break;
            }

            // The update id is Windows Update's identity for the update and is a
            // GUID, so an absent one or one too long to be a GUID is a malformed
            // item, not an update. A missing title likewise -- there is nothing to
            // show the operator.
            if (string.IsNullOrWhiteSpace(item.UpdateId) || item.UpdateId.Length > 64
                || string.IsNullOrWhiteSpace(item.Title))
            {
                continue;
            }

            // A repeat of an update id is a malformed result rather than two
            // updates; keep the first and drop the rest, as the inventory ingestion
            // does with a repeated device instance id.
            if (!seen.Add(item.UpdateId))
            {
                continue;
            }

            reported.Add(new DeviceSecurityUpdate(
                deviceId,
                item.UpdateId,
                item.Revision,
                Truncate(item.Title, 512)!,
                Truncate(item.KbIds, 128),
                Truncate(item.Severity, 32),
                item.IsPreview,
                item.SizeBytes,
                collectedAt));
        }

        _dbContext.DeviceSecurityUpdates.AddRange(reported);

        _logger.LogInformation(
            "Security-update scan for device {DeviceId}: {Count} update(s) pending.", deviceId, reported.Count);
    }

    /// <summary>Caps a string to <paramref name="max"/> characters, preserving null.</summary>
    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
