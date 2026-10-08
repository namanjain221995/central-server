using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Restarts;

/// <summary>What happened to one device when its department's restart was dispatched.</summary>
public enum RestartDispatchOutcome
{
    /// <summary>A restart task was queued; <see cref="RestartScheduleDevice.RestartTaskId"/> is it.</summary>
    Queued = 0,

    /// <summary>An administrator had taken this device out of the schedule.</summary>
    Excluded = 1,

    /// <summary>Offline at the moment, so not targeted -- the rule every group action follows.</summary>
    Offline = 2,

    /// <summary>The device already had a restart queued or delivered from elsewhere.</summary>
    AlreadyInProgress = 3,

    /// <summary>Retired since, or its agent cannot run the task.</summary>
    NotEligible = 4,

    /// <summary>The administrator who scheduled it no longer had authority over the device.</summary>
    NotAuthorized = 5,

    /// <summary>
    /// The server could not send it in time (it was not running at the moment),
    /// and a restart was not sent late. Devices that were armed in advance are
    /// unaffected: they restart on their own.
    /// </summary>
    Missed = 6,
}

/// <summary>What happened when a cancellation was asked for after dispatch.</summary>
public enum RestartCancelOutcome
{
    /// <summary>The restart task was still Queued and was cancelled before any device saw it.</summary>
    CancelledBeforeDelivery = 0,

    /// <summary>A <c>CancelRestart</c> task was queued; <see cref="RestartScheduleDevice.CancelTaskId"/> is it.</summary>
    Requested = 1,

    /// <summary>The device's agent predates cancellation; the countdown could not be reached.</summary>
    Unsupported = 2,

    /// <summary>There was nothing left to cancel: the restart had already happened, failed or expired.</summary>
    NotApplicable = 3,
}

/// <summary>
/// One device's part in a dispatched schedule: how dispatch went for it and,
/// if an administrator later cancelled, how that went. Written once at
/// dispatch, amended once by a cancellation; the live status of the restart
/// itself is the task the row points at.
/// </summary>
public sealed class RestartScheduleDevice : Entity
{
    private RestartScheduleDevice()
    {
        Hostname = null!;
    }

    public RestartScheduleDevice(
        Guid scheduleId, Guid deviceId, string hostname, RestartDispatchOutcome dispatchOutcome, Guid? restartTaskId)
    {
        ScheduleId = Guard.NotEmpty(scheduleId);
        DeviceId = Guard.NotEmpty(deviceId);
        Hostname = Guard.NotNullOrWhiteSpace(hostname, nameof(hostname), 255);
        DispatchOutcome = dispatchOutcome;

        if ((dispatchOutcome == RestartDispatchOutcome.Queued) != restartTaskId.HasValue)
        {
            throw new ArgumentException("A queued outcome carries its task id, and only a queued outcome does.", nameof(restartTaskId));
        }

        RestartTaskId = restartTaskId;
    }

    public Guid ScheduleId { get; private set; }

    public Guid DeviceId { get; private set; }

    public string Hostname { get; private set; }

    public RestartDispatchOutcome DispatchOutcome { get; private set; }

    public Guid? RestartTaskId { get; private set; }

    public RestartCancelOutcome? CancelOutcome { get; private set; }

    public Guid? CancelTaskId { get; private set; }

    public DateTimeOffset? CancelRequestedAt { get; private set; }

    public string? CancelRequestedByDisplay { get; private set; }

    /// <summary>
    /// Records the latest cancellation attempt. Deliberately not "once": a
    /// cancellation the device refused, or that an older agent could not run,
    /// may be tried again later, and the row keeps only the latest word.
    /// Whether another attempt is worth making is judged by the service from
    /// the live tasks, which the row does not see.
    /// </summary>
    public void RecordCancel(RestartCancelOutcome outcome, Guid? cancelTaskId, DateTimeOffset now, string byDisplay)
    {
        if ((outcome == RestartCancelOutcome.Requested) != cancelTaskId.HasValue)
        {
            throw new ArgumentException("A requested cancellation carries its task id, and only that outcome does.", nameof(cancelTaskId));
        }

        CancelOutcome = outcome;
        CancelTaskId = cancelTaskId;
        CancelRequestedAt = now;
        CancelRequestedByDisplay = Guard.NotNullOrWhiteSpace(byDisplay, nameof(byDisplay), RestartSchedule.MaxDisplayLength);
    }
}
