using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Chat;

/// <summary>Where a live-chat session stands. Stored as text, like every persisted enum.</summary>
public enum ChatSessionStatus
{
    /// <summary>Open: the admin and the employee can still exchange messages.</summary>
    Active = 0,

    /// <summary>Closed. No further message is accepted on either side.</summary>
    Ended = 1,
}

/// <summary>
/// Why a session ended, recorded so the history reads the same as what happened.
/// Stored as text, like every persisted enum.
/// </summary>
public enum ChatEndedReason
{
    /// <summary>Still active: no end reason yet.</summary>
    None = 0,

    /// <summary>An administrator deliberately ended the chat.</summary>
    AdminEnded = 1,

    /// <summary>Closed after a long stretch with no message from either side.</summary>
    IdleTimeout = 2,

    /// <summary>The device stopped checking in, so the chat could no longer be two-way.</summary>
    DeviceWentAway = 3,

    /// <summary>The session outlived its maximum lifetime.</summary>
    Expired = 4,
}

/// <summary>
/// One live IT-support chat between an administrator and the person at a managed
/// device. The session is the conversation; the lines said in it are its
/// <see cref="ChatMessage"/> rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>At most one active session per device.</b> A device is talking to one IT
/// person at a time, enforced by a partial unique index on
/// <c>chat_sessions(device_id)</c> where the status is Active -- the database
/// refuses a second open session rather than leaving the endpoint to reconcile two
/// conversations. Once a session is Ended the device is free to start another.
/// </para>
/// <para>
/// <b>Started by an administrator.</b> Phase 1 is admin-initiated: the admin opens
/// the chat against a device, which queues the task that tells the endpoint to
/// surface the chat window. <see cref="LastActivityAt"/> advances on every message
/// either side appends, so an idle-timeout sweep has a single field to read.
/// </para>
/// </remarks>
public sealed class ChatSession : AuditableEntity
{
    public const int MaxStartedByDisplayLength = 320;

    private ChatSession()
    {
        StartedByDisplay = null!;
    }

    public ChatSession(
        Guid organizationId,
        Guid deviceId,
        Guid startedByUserId,
        string startedByDisplay,
        DateTimeOffset now)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        DeviceId = Guard.NotEmpty(deviceId);
        StartedByUserId = Guard.NotEmpty(startedByUserId);
        StartedByDisplay = Guard.NotNullOrWhiteSpace(startedByDisplay, nameof(startedByDisplay), MaxStartedByDisplayLength);
        Status = ChatSessionStatus.Active;
        EndedReason = ChatEndedReason.None;
        StartedAt = now;
        LastActivityAt = now;
    }

    public Guid OrganizationId { get; private set; }

    public Guid DeviceId { get; private set; }

    public Guid StartedByUserId { get; private set; }

    public string StartedByDisplay { get; private set; }

    public ChatSessionStatus Status { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    public ChatEndedReason EndedReason { get; private set; }

    /// <summary>
    /// The last time a message was appended by either side. The single field an
    /// idle-timeout sweep reads, advanced by <see cref="RecordActivity"/>.
    /// </summary>
    public DateTimeOffset LastActivityAt { get; private set; }

    /// <summary>Advances <see cref="LastActivityAt"/>; a no-op on an ended session.</summary>
    public void RecordActivity(DateTimeOffset now)
    {
        if (Status == ChatSessionStatus.Ended)
        {
            return;
        }

        LastActivityAt = now;
    }

    /// <summary>
    /// Ends the session with a reason. Idempotent: ending an already-ended session
    /// changes nothing and returns false, so a caller can tell a real close from a
    /// repeat.
    /// </summary>
    public bool End(ChatEndedReason reason, DateTimeOffset now)
    {
        if (reason == ChatEndedReason.None)
        {
            throw new ArgumentException("An ended session must carry a reason other than None.", nameof(reason));
        }

        if (Status == ChatSessionStatus.Ended)
        {
            return false;
        }

        Status = ChatSessionStatus.Ended;
        EndedReason = reason;
        EndedAt = now;
        LastActivityAt = now;
        return true;
    }
}
