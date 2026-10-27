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

    /// <summary>
    /// Not answered before its deadline: never claimed, or claimed and never
    /// reported. A dead agent must not leave work "pending" forever.
    /// </summary>
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
/// (Succeeded|Failed), or Queued -&gt; (Cancelled|Expired), or Delivered -&gt;
/// Expired when the device goes silent. A result for a task not in Delivered is
/// rejected, so a replayed or stale result cannot rewrite a terminal outcome --
/// with one deliberate exception: a delivered task the sweeper expired accepts
/// the device's late result (<see cref="TryCompleteLate"/>), because "Expired"
/// was only ever a guess about a device that did have the task.
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
    /// Pushes a just-delivered task's deadline out to cover the time the work itself
    /// takes on the endpoint. Only ever extends, and only while Delivered.
    /// </summary>
    /// <remarks>
    /// A task's lifetime was written for "how long may it wait to be claimed". For
    /// long work -- a driver install downloading hundreds of megabytes -- a claim
    /// late in that window left the sweeper free to expire the task mid-install and
    /// the device's honest result was then refused. The catalogue's execution grace
    /// (<see cref="DeviceTaskDefinition.ExecutionGraceSeconds"/>) is applied here at
    /// claim time, so a claimed install cannot be expired while it is still running.
    /// </remarks>
    public bool ExtendForExecution(DateTimeOffset deadline)
    {
        if (Status != DeviceTaskStatus.Delivered || deadline <= ExpiresAt)
        {
            return false;
        }

        ExpiresAt = deadline;
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

    /// <summary>Appended to the message of a result accepted after its task had expired.</summary>
    public const string LateResultSuffix = " (result arrived after the deadline)";

    /// <summary>
    /// Applies a result that arrived after the sweeper had already expired the task
    /// -- allowed only when the device provably had the task (it was delivered), so
    /// "Expired" was a guess about a silent device rather than the truth.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The symmetric case to <see cref="TryCancelAcceptedRestart"/>: the codebase
    /// already sanctions one transition out of a settled state when the settled
    /// state stops being true. "Expired" stops being true the moment the device
    /// reports what actually happened -- for a driver install that may be "installed
    /// everything" -- and discarding that report would leave the console wrong for good.
    /// </para>
    /// <para>
    /// Replay safety is unchanged: Succeeded, Failed and Cancelled stay unwritable, a
    /// task that expired unclaimed stays Expired (nothing ran), and only the task's own
    /// device can post a result at all (the agent endpoint scopes the task id to the
    /// authenticated device). The suffix keeps the late arrival visible.
    /// </para>
    /// </remarks>
    public bool TryCompleteLate(bool succeeded, string? resultMessage, string? resultJson, DateTimeOffset now)
    {
        if (Status != DeviceTaskStatus.Expired || DeliveredAt is null)
        {
            return false;
        }

        var reported = string.IsNullOrWhiteSpace(resultMessage)
            ? (succeeded ? "Completed" : "Failed")
            : resultMessage.Trim();
        var room = 1024 - LateResultSuffix.Length;
        if (reported.Length > room)
        {
            reported = reported[..room];
        }

        Status = succeeded ? DeviceTaskStatus.Succeeded : DeviceTaskStatus.Failed;
        CompletedAt = now;
        ResultMessage = Guard.OptionalMaxLength(reported + LateResultSuffix, 1024);
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

    /// <summary>The expiry message for a task no device ever claimed: nothing ran.</summary>
    public const string ExpiredUnclaimedMessage = "The device never picked this task up before it expired.";

    /// <summary>
    /// The expiry message for a task the device claimed and never answered. It may
    /// have done the work; it went silent, and a result can still be accepted late.
    /// </summary>
    public const string ExpiredUnreportedMessage =
        "The device received this task but never reported a result (it may have gone offline mid-task)";

    /// <summary>
    /// Expires a task whose deadline passed while queued or delivered (swept by a
    /// background job), saying which of the two it was.
    /// </summary>
    /// <param name="now">The sweep time.</param>
    /// <param name="followUp">
    /// What the platform did about a delivered task going silent, e.g. "a
    /// verification scan has been queued", appended to the message only for a task
    /// the device had claimed. The caller says it because only the caller knows.
    /// </param>
    /// <remarks>
    /// The two cases are different facts. "Never picked up" means nothing ran on the
    /// machine; "received but never reported" means something may well have -- a
    /// driver install may have finished while the result was lost -- and reading the
    /// second as the first sends an operator to retry work that is already done.
    /// </remarks>
    public bool TryExpire(DateTimeOffset now, string? followUp = null)
    {
        if (Status is DeviceTaskStatus.Queued or DeviceTaskStatus.Delivered && now >= ExpiresAt)
        {
            var message = DeliveredAt is null
                ? ExpiredUnclaimedMessage
                : string.IsNullOrWhiteSpace(followUp)
                    ? ExpiredUnreportedMessage + "."
                    : $"{ExpiredUnreportedMessage}; {followUp.Trim().TrimEnd('.')}.";

            Status = DeviceTaskStatus.Expired;
            CompletedAt = now;
            ResultMessage ??= Guard.OptionalMaxLength(message.Length > 1024 ? message[..1024] : message, 1024);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Adds what the platform did about a delivered task going silent to its expiry
    /// message, once the caller actually knows -- e.g. "a verification scan has been
    /// queued", or that one could not be. Applies once, and only to a task that
    /// expired after delivery with the plain "never reported" message.
    /// </summary>
    /// <remarks>
    /// The sweeper expires first and acts after (so a failure to act cannot roll the
    /// expiry back); the note therefore follows the expiry rather than being guessed
    /// at it. A task that has since taken its late result, or that already carries a
    /// note, is left alone and false is returned.
    /// </remarks>
    public bool NoteExpiryFollowUp(string followUp)
    {
        if (Status != DeviceTaskStatus.Expired
            || DeliveredAt is null
            || string.IsNullOrWhiteSpace(followUp)
            || ResultMessage != ExpiredUnreportedMessage + ".")
        {
            return false;
        }

        var message = $"{ExpiredUnreportedMessage}; {followUp.Trim().TrimEnd('.')}.";
        ResultMessage = Guard.OptionalMaxLength(message.Length > 1024 ? message[..1024] : message, 1024);
        return true;
    }
}
