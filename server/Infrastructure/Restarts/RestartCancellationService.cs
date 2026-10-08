using System.Text.Json;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Restarts;

/// <summary>What one attempt to cancel a restart did.</summary>
public enum RestartCancelAttemptOutcome
{
    /// <summary>The restart was still Queued and was cancelled where it sat. Nothing reached the device.</summary>
    CancelledBeforeDelivery,

    /// <summary>The device has the restart; a <c>CancelRestart</c> task was queued and the device confirms on its next check-in.</summary>
    CancelRequested,

    /// <summary>A cancellation for this restart is already on its way to the device.</summary>
    AlreadyRequested,

    /// <summary>The restart is already Cancelled.</summary>
    AlreadyCancelled,

    /// <summary>The device is retired, or its agent predates cancellation; the countdown cannot be reached.</summary>
    Unsupported,

    /// <summary>Nothing left to cancel: not a restart, already happened, failed or expired.</summary>
    NothingToCancel,
}

/// <param name="CancelTaskId">The <c>CancelRestart</c> task, for <see cref="RestartCancelAttemptOutcome.CancelRequested"/> and <see cref="RestartCancelAttemptOutcome.AlreadyRequested"/>.</param>
public sealed record RestartCancelAttempt(RestartCancelAttemptOutcome Outcome, Guid? CancelTaskId);

/// <summary>
/// Cancels one restart by whatever means its state allows: where it sits while
/// it is still Queued, through the device once the device has it, and not at
/// all once its moment has passed.
/// </summary>
/// <remarks>
/// <para>
/// The one place this decision is made. The single-device route, the group
/// action and Restart Management all come here, so "cancel" means the same
/// thing wherever an administrator presses it, and the honest answers --
/// requested rather than done, unsupported on an old agent, too late once the
/// machine is going down -- are the same answers everywhere.
/// </para>
/// <para>
/// A restart the device has accepted is still cancellable until Windows acts:
/// <c>CancelRestart</c> (agent 1.14.0 and later) aborts the countdown and
/// reports back, and only that report turns the restart task Cancelled. Until
/// then the answer is <see cref="RestartCancelAttemptOutcome.CancelRequested"/>,
/// never "cancelled": the device has not confirmed it.
/// </para>
/// <para>
/// Holds nothing in the change tracker across its calls into
/// <see cref="DeviceTaskService"/>, which saves as it goes and clears the
/// tracker when it loses a race. A caller with tracked entities of its own
/// re-reads them after calling this.
/// </para>
/// </remarks>
public sealed class RestartCancellationService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    TimeProvider timeProvider,
    ILogger<RestartCancellationService> logger)
{
    private static readonly DeviceTaskDefinition CancelDefinition = DeviceTaskCatalog.Require(DeviceTaskType.CancelRestart);

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<RestartCancellationService> _logger = logger;

    /// <summary>
    /// Cancels the restart task <paramref name="restartTaskId"/> on
    /// <paramref name="deviceId"/>. The caller has already checked the actor's
    /// permission and scope over the device.
    /// </summary>
    public async Task<RestartCancelAttempt> CancelAsync(
        Guid organizationId, Guid deviceId, Guid restartTaskId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var restart = await LoadAsync(restartTaskId, deviceId, organizationId, cancellationToken);
        if (restart is null)
        {
            return new(RestartCancelAttemptOutcome.NothingToCancel, null);
        }

        if (restart.Status == DeviceTaskStatus.Queued)
        {
            var cancelled = await _taskService.CancelAsync(
                organizationId, deviceId, restartTaskId, actorId, actorDisplay, cancellationToken);
            switch (cancelled)
            {
                case TaskCancelResult.Success:
                    return new(RestartCancelAttemptOutcome.CancelledBeforeDelivery, null);
                case TaskCancelResult.NotFound:
                    return new(RestartCancelAttemptOutcome.NothingToCancel, null);
            }

            // Claimed in the same instant: the device has it now. Re-read and
            // carry on as for a delivered one.
            restart = await LoadAsync(restartTaskId, deviceId, organizationId, cancellationToken);
            if (restart is null)
            {
                return new(RestartCancelAttemptOutcome.NothingToCancel, null);
            }
        }

        if (restart.Status == DeviceTaskStatus.Cancelled)
        {
            return new(RestartCancelAttemptOutcome.AlreadyCancelled, null);
        }

        var now = _timeProvider.GetUtcNow();
        if (!IsPendingOnDevice(restart, now))
        {
            // Already restarted, failed or expired: nothing a device can undo.
            return new(RestartCancelAttemptOutcome.NothingToCancel, null);
        }

        // A cancellation already on its way for this very restart: do not
        // queue a second, report the first.
        var live = await _dbContext.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId
                        && t.Type == DeviceTaskType.CancelRestart
                        && (t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Delivered))
            .ToListAsync(cancellationToken);
        var existing = live.FirstOrDefault(t => RestartTaskIdOf(t) == restartTaskId);
        if (existing is not null)
        {
            return new(RestartCancelAttemptOutcome.AlreadyRequested, existing.Id);
        }

        var device = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => new { d.Status, d.AgentVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (device is null || device.Status != DeviceStatus.Active || !DeviceTaskCatalog.IsSupportedBy(CancelDefinition, device.AgentVersion))
        {
            _logger.LogInformation(
                "Restart {RestartTaskId} on device {DeviceId} cannot be cancelled through the device: agent {Agent} (needs {Required}).",
                restartTaskId, deviceId, device?.AgentVersion ?? "retired", CancelDefinition.MinimumAgentVersion);
            return new(RestartCancelAttemptOutcome.Unsupported, null);
        }

        var task = await _taskService.QueueAsync(
            organizationId, deviceId, DeviceTaskType.CancelRestart,
            new TaskPayloads.CancelRestart(restartTaskId, actorDisplay), actorId, actorDisplay, cancellationToken);

        return task is null
            ? new(RestartCancelAttemptOutcome.Unsupported, null)
            : new(RestartCancelAttemptOutcome.CancelRequested, task.Id);
    }

    /// <summary>
    /// Whether the restart is still ahead of the device: delivered and not yet
    /// reported, or accepted with Windows' moment still in the future.
    /// </summary>
    public static bool IsPendingOnDevice(DeviceTask restart, DateTimeOffset now) =>
        restart.Status == DeviceTaskStatus.Delivered
        || (restart.Status == DeviceTaskStatus.Succeeded && RestartAtOf(restart) is { } at && at > now);

    /// <summary>When Windows said it would act, from the restart task's result; null until the device reported.</summary>
    public static DateTimeOffset? RestartAtOf(DeviceTask restart)
    {
        if (restart.ResultJson is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(restart.ResultJson);
            return doc.RootElement.TryGetProperty("restartAt", out var at) && at.ValueKind == JsonValueKind.String
                && at.TryGetDateTimeOffset(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The restart a CancelRestart task was queued to undo, from its payload.</summary>
    public static Guid? RestartTaskIdOf(DeviceTask cancel)
    {
        if (cancel.PayloadJson is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(cancel.PayloadJson);
            return doc.RootElement.TryGetProperty("restartTaskId", out var id) && id.TryGetGuid(out var guid) ? guid : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task<DeviceTask?> LoadAsync(Guid restartTaskId, Guid deviceId, Guid organizationId, CancellationToken cancellationToken) =>
        _dbContext.DeviceTasks.AsNoTracking()
            .SingleOrDefaultAsync(
                t => t.Id == restartTaskId && t.DeviceId == deviceId && t.OrganizationId == organizationId
                     && (t.Type == DeviceTaskType.RestartDevice || t.Type == DeviceTaskType.ScheduleRestart),
                cancellationToken);
}
