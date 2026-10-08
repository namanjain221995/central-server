using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Tasks;

/// <summary>Lifecycle of a queued device task.</summary>
public enum DeviceTaskStatus
{
    /// <summary>Created, waiting for the agent to claim it on its next poll.</summary>
    Queued = 0,

    /// <summary>Handed to the agent; awaiting a result.</summary>
    Delivered = 1,

    Succeeded = 2,
    Failed = 3,

    /// <summary>Never claimed before its deadline. A dead agent must not leave work "pending" forever.</summary>
    Expired = 4,

    /// <summary>Cancelled by an administrator before delivery.</summary>
    Cancelled = 5,
}

/// <summary>
/// One unit of work queued for an endpoint and pulled by its agent.
/// </summary>
/// <remarks>
/// <para>
/// Pull-based: the agent claims queued tasks on its authenticated poll and posts
/// results back. The server never connects to an agent (spec: no unauthenticated
/// server-to-agent execution). The payload is a typed, server-validated JSON
/// document specific to <see cref="Type"/>; there is no free-form command field.
/// </para>
/// <para>
/// State transitions are one-way and guarded: Queued -&gt; Delivered -&gt;
/// (Succeeded|Failed), or Queued -&gt; (Cancelled|Expired). A result for a task
/// not in Delivered is rejected, so a replayed or stale result cannot rewrite a
/// terminal outcome.
/// </para>
/// </remarks>
public sealed class DeviceTask : AuditableEntity
{
    private DeviceTask()
    {
        CreatedByDisplay = null!;
    }

    private DeviceTask(
        Guid organizationId,
        Guid deviceId,
        DeviceTaskType type,
        string? payloadJson,
        Guid createdByUserId,
        string createdByDisplay,
        DateTimeOffset now,
        TimeSpan timeToLive)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        DeviceId = Guard.NotEmpty(deviceId);
        Type = type;
        PayloadJson = payloadJson;
        CreatedByUserId = Guard.NotEmpty(createdByUserId);
        CreatedByDisplay = Guard.NotNullOrWhiteSpace(createdByDisplay, nameof(createdByDisplay), maxLength: 320);
        Status = DeviceTaskStatus.Queued;
        ExpiresAt = now + timeToLive;
    }

    public Guid OrganizationId { get; private set; }

    public Guid DeviceId { get; private set; }

    public DeviceTaskType Type { get; private set; }

    /// <summary>Typed, validated JSON payload for <see cref="Type"/>; null for payload-free tasks.</summary>
    public string? PayloadJson { get; private set; }

    public DeviceTaskStatus Status { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string CreatedByDisplay { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Agent-reported result document (jsonb), redacted by the agent before sending.</summary>
    public string? ResultJson { get; private set; }

    /// <summary>Short human-readable outcome or failure reason.</summary>
    public string? ResultMessage { get; private set; }

    public bool IsTerminal =>
        Status is DeviceTaskStatus.Succeeded or DeviceTaskStatus.Failed
            or DeviceTaskStatus.Expired or DeviceTaskStatus.Cancelled;

    public static DeviceTask Create(
        Guid organizationId,
        Guid deviceId,
        DeviceTaskType type,
        string? payloadJson,
        Guid createdByUserId,
        string createdByDisplay,
        DateTimeOffset now,
        TimeSpan timeToLive) =>
        new(organizationId, deviceId, type, payloadJson, createdByUserId, createdByDisplay, now, timeToLive);

    /// <summary>Marks the task claimed by the agent. Idempotent for the same poll retry.</summary>
    public bool TryDeliver(DateTimeOffset now)
    {
        if (Status != DeviceTaskStatus.Queued)
        {
            return false;
        }

        if (now >= ExpiresAt)
        {
            Status = DeviceTaskStatus.Expired;
            CompletedAt = now;
            ResultMessage = "Task expired before an agent claimed it.";
            return false;
        }

        Status = DeviceTaskStatus.Delivered;
        DeliveredAt = now;
        return true;
    }

    /// <summary>
    /// Applies an agent-reported result. Rejected unless the task is Delivered, so a
    /// stale or replayed result cannot overwrite a terminal outcome.
    /// </summary>
    public bool TryComplete(bool succeeded, string? resultMessage, string? resultJson, DateTimeOffset now)
    {
        if (Status != DeviceTaskStatus.Delivered)
        {
            return false;
        }

        Status = succeeded ? DeviceTaskStatus.Succeeded : DeviceTaskStatus.Failed;
        CompletedAt = now;
        ResultMessage = Guard.OptionalMaxLength(resultMessage, 1024);
        ResultJson = resultJson;
        return true;
    }

    /// <summary>Cancels a not-yet-delivered task.</summary>
    public bool TryCancel(DateTimeOffset now, string? reason)
    {
        if (Status != DeviceTaskStatus.Queued)
        {
            return false;
        }

        Status = DeviceTaskStatus.Cancelled;
        CompletedAt = now;
        ResultMessage = Guard.OptionalMaxLength(reason, 1024);
        return true;
    }

    /// <summary>Expires a task whose deadline passed while queued (swept by a background job).</summary>
    /// <summary>
    /// Marks a restart that the device had already accepted as Cancelled,
    /// because a <see cref="DeviceTaskType.CancelRestart"/> task later aborted
    /// the countdown. The one transition out of a settled state, and only for
    /// a restart: "Succeeded" meant Windows accepted the request, and once the
    /// request has been withdrawn that is no longer the truth about this task.
    /// </summary>
    public bool TryCancelAcceptedRestart(DateTimeOffset now, string? reason)
    {
        if (Type is not (DeviceTaskType.RestartDevice or DeviceTaskType.ScheduleRestart)
            || Status is not (DeviceTaskStatus.Delivered or DeviceTaskStatus.Succeeded))
        {
            return false;
        }

        Status = DeviceTaskStatus.Cancelled;
        CompletedAt = now;
        ResultMessage = Guard.OptionalMaxLength(reason, 1024);
        return true;
    }

    public bool TryExpire(DateTimeOffset now)
    {
        if (Status is DeviceTaskStatus.Queued or DeviceTaskStatus.Delivered && now >= ExpiresAt)
        {
            Status = DeviceTaskStatus.Expired;
            CompletedAt = now;
            ResultMessage ??= "Task expired.";
            return true;
        }

        return false;
    }
}
