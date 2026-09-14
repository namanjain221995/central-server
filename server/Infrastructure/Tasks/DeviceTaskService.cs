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
public sealed class DeviceTaskService(
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
        CancellationToken cancellationToken = default)
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

        var task = DeviceTask.Create(
            organizationId, deviceId, type, payloadJson, actorId, actorDisplay,
            now, TimeSpan.FromSeconds(definition.DefaultTimeToLiveSeconds));

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
        foreach (var task in stale)
        {
            if (task.TryExpire(now))
            {
                expired++;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        if (expired > 0)
        {
            _logger.LogInformation("Task expiry sweep expired {Count} task(s).", expired);
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

    /// <summary>Applies an agent-reported result. Returns false if the task is not awaiting one.</summary>
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
            _logger.LogWarning(
                "Rejected result for task {TaskId}: status was {Status}, not Delivered.", taskId, task.Status);
            return false;
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

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Task {TaskId} ({Type}) completed: {Outcome}.",
            taskId, task.Type, succeeded ? "success" : "failure");

        return true;
    }
}
