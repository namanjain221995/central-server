using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Restarts;

public enum RestartScheduleStatus
{
    /// <summary>Held by the server. Nothing has reached a device.</summary>
    Pending = 0,

    /// <summary>The restart tasks were queued; each device's own task says how it went.</summary>
    Dispatched = 1,

    /// <summary>Cancelled while still Pending. Nothing was ever sent.</summary>
    Cancelled = 2,

    /// <summary>
    /// The dispatch moment passed by more than the tolerance while nothing was
    /// dispatching -- the server was down -- and the restart was not sent late.
    /// </summary>
    Missed = 3,
}

/// <summary>
/// A department-wide restart the server will send at a set moment: which group,
/// when, with what warning, and who asked.
/// </summary>
/// <remarks>
/// <para>
/// <b>The group is resolved when the restart goes out, not when it is
/// scheduled.</b> A device moved into the department before the moment is
/// included; one moved out is not; one that is offline then is skipped, as a
/// group restart skips it. The schedule names a group, never devices -- the
/// same rule as every group action -- and per-device exceptions are recorded
/// separately as <see cref="RestartScheduleExclusion"/>.
/// </para>
/// <para>
/// <b>Status is about the schedule, not the devices.</b> Dispatched means the
/// tasks exist; whether each machine restarted, failed, or was cancelled is on
/// its <see cref="RestartScheduleDevice"/> row and the task it points at. A
/// cancellation after dispatch is recorded here as who asked and when, and
/// carried out per device.
/// </para>
/// </remarks>
public sealed class RestartSchedule : AuditableEntity
{
    public const int MaxDisplayLength = 320;

    private RestartSchedule()
    {
        CreatedByDisplay = null!;
    }

    private RestartSchedule(
        Guid organizationId, Guid deviceGroupId, int requestedDelaySeconds, int warningSeconds,
        Guid createdByUserId, string createdByDisplay, DateTimeOffset now)
    {
        if (!RestartScheduleDelay.IsAccepted(requestedDelaySeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedDelaySeconds), requestedDelaySeconds,
                $"A scheduled restart is {RestartScheduleDelay.MinimumSeconds}..{RestartScheduleDelay.MaximumSeconds} seconds away.");
        }

        if (warningSeconds <= 0 || warningSeconds > requestedDelaySeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(warningSeconds), warningSeconds,
                "The warning is positive and never longer than the delay itself.");
        }

        OrganizationId = Guard.NotEmpty(organizationId);
        DeviceGroupId = Guard.NotEmpty(deviceGroupId);
        RequestedDelaySeconds = requestedDelaySeconds;
        WarningSeconds = warningSeconds;
        RestartAt = now.AddSeconds(requestedDelaySeconds);
        DispatchAt = RestartAt.AddSeconds(-warningSeconds);
        CreatedByUserId = Guard.NotEmpty(createdByUserId);
        CreatedByDisplay = Guard.NotNullOrWhiteSpace(createdByDisplay, nameof(createdByDisplay), MaxDisplayLength);
        Status = RestartScheduleStatus.Pending;
    }

    public Guid OrganizationId { get; private set; }

    public Guid DeviceGroupId { get; private set; }

    /// <summary>What the administrator chose, in seconds: the restart is this far from when it was created.</summary>
    public int RequestedDelaySeconds { get; private set; }

    /// <summary>The grace period handed to Windows; the restart task goes out this long before <see cref="RestartAt"/>.</summary>
    public int WarningSeconds { get; private set; }

    /// <summary>When the devices restart.</summary>
    public DateTimeOffset RestartAt { get; private set; }

    /// <summary>When the restart tasks are queued. Before this, nothing has reached a device.</summary>
    public DateTimeOffset DispatchAt { get; private set; }

    public RestartScheduleStatus Status { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string CreatedByDisplay { get; private set; }

    public DateTimeOffset? DispatchedAt { get; private set; }

    /// <summary>
    /// When cancellation was asked for, and by whom. For a Pending schedule this
    /// accompanies <see cref="RestartScheduleStatus.Cancelled"/>; for a Dispatched
    /// one it records that a department-wide cancellation was sent to the devices.
    /// </summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    public string? CancelledByDisplay { get; private set; }

    public DateTimeOffset? MissedAt { get; private set; }

    public static RestartSchedule Create(
        Guid organizationId, Guid deviceGroupId, int requestedDelaySeconds, int warningSeconds,
        Guid createdByUserId, string createdByDisplay, DateTimeOffset now) =>
        new(organizationId, deviceGroupId, requestedDelaySeconds, warningSeconds, createdByUserId, createdByDisplay, now);

    /// <summary>Whether the restart tasks should go out now.</summary>
    public bool IsDue(DateTimeOffset now) => Status == RestartScheduleStatus.Pending && now >= DispatchAt;

    /// <summary>Cancels a schedule nothing has been sent for. False once it is no longer Pending.</summary>
    public bool TryCancel(DateTimeOffset now, Guid byUserId, string byDisplay)
    {
        if (Status != RestartScheduleStatus.Pending)
        {
            return false;
        }

        Status = RestartScheduleStatus.Cancelled;
        RecordCancelledBy(now, byUserId, byDisplay);
        return true;
    }

    /// <summary>
    /// Records that the administrator asked for the whole department's restart
    /// to be cancelled after the tasks had gone out. The status stays
    /// Dispatched: what each device did about it is on its own row.
    /// </summary>
    public void RecordCancelRequested(DateTimeOffset now, Guid byUserId, string byDisplay)
    {
        if (Status != RestartScheduleStatus.Dispatched)
        {
            throw new InvalidOperationException("Only a dispatched schedule can have a cancellation requested.");
        }

        RecordCancelledBy(now, byUserId, byDisplay);
    }

    public void MarkDispatched(DateTimeOffset now)
    {
        if (Status != RestartScheduleStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot dispatch a schedule that is {Status}.");
        }

        Status = RestartScheduleStatus.Dispatched;
        DispatchedAt = now;
    }

    public void MarkMissed(DateTimeOffset now)
    {
        if (Status != RestartScheduleStatus.Pending)
        {
            throw new InvalidOperationException($"Cannot miss a schedule that is {Status}.");
        }

        Status = RestartScheduleStatus.Missed;
        MissedAt = now;
    }

    private void RecordCancelledBy(DateTimeOffset now, Guid byUserId, string byDisplay)
    {
        CancelledAt = now;
        CancelledByUserId = Guard.NotEmpty(byUserId);
        CancelledByDisplay = Guard.NotNullOrWhiteSpace(byDisplay, nameof(byDisplay), MaxDisplayLength);
    }
}
