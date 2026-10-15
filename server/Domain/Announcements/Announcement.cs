using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Announcements;

/// <summary>How an announcement chose its audience. Stored as text, like every persisted enum.</summary>
public enum AnnouncementTargetType
{
    /// <summary>A device group, resolved to its active members at send time.</summary>
    Group = 0,

    /// <summary>An explicit selection of devices.</summary>
    Devices = 1,
}

/// <summary>
/// A short plain-text notice an administrator broadcast to device screens: what
/// was said, who it was aimed at, and who sent it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The record is the send, not the delivery.</b> One row per broadcast; what
/// each targeted machine did about it is on its <see cref="AnnouncementDevice"/>
/// rows and the <c>ShowAnnouncement</c> tasks they point at. The announcement
/// itself never changes after it is written.
/// </para>
/// <para>
/// <b>Plain text, tightly bounded.</b> The title and message travel verbatim to
/// an agent that displays them on every signed-in session, so the limits here
/// are the contract the agent relies on -- nothing longer than a glance's worth
/// of text, and never markup.
/// </para>
/// </remarks>
public sealed class Announcement : AuditableEntity
{
    public const int MaxTitleLength = 80;

    public const int MaxMessageLength = 500;

    public const int MaxDisplayLength = 256;

    private Announcement()
    {
        Title = null!;
        Message = null!;
        CreatedByDisplay = null!;
    }

    private Announcement(
        Guid organizationId, string title, string message, AnnouncementTargetType targetType,
        Guid? deviceGroupId, Guid createdByUserId, string createdByDisplay)
    {
        if ((targetType == AnnouncementTargetType.Group) != deviceGroupId.HasValue)
        {
            throw new ArgumentException(
                "A group announcement names its group, and only a group announcement does.", nameof(deviceGroupId));
        }

        OrganizationId = Guard.NotEmpty(organizationId);
        Title = PlainText(Guard.NotNullOrWhiteSpace(title, nameof(title), MaxTitleLength), nameof(title));
        Message = PlainText(Guard.NotNullOrWhiteSpace(message, nameof(message), MaxMessageLength), nameof(message));
        TargetType = targetType;
        DeviceGroupId = deviceGroupId is { } groupId ? Guard.NotEmpty(groupId) : null;
        CreatedByUserId = Guard.NotEmpty(createdByUserId);
        CreatedByDisplay = Guard.NotNullOrWhiteSpace(createdByDisplay, nameof(createdByDisplay), MaxDisplayLength);
    }

    public Guid OrganizationId { get; private set; }

    public string Title { get; private set; }

    public string Message { get; private set; }

    public AnnouncementTargetType TargetType { get; private set; }

    /// <summary>
    /// The group a <see cref="AnnouncementTargetType.Group"/> announcement named.
    /// A historical reference, not a dependency: the group's name is resolved live
    /// when the history is read, and goes blank if the group is ever deleted.
    /// </summary>
    public Guid? DeviceGroupId { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string CreatedByDisplay { get; private set; }

    /// <summary>
    /// Whether text may travel as an announcement title or message: no control
    /// characters other than newlines and tabs.
    /// </summary>
    /// <remarks>
    /// The server-side mirror of the agent's
    /// <c>SessionNoticeProtocol.IsValidTitle</c>/<c>IsValidMessage</c>
    /// (<c>agent/EndpointAgent.Core/SessionNotice/RestartNotice.cs</c>), and it
    /// must stay in step with them: the executor refuses what this accepts, so a
    /// mismatch queues a broadcast fleet-wide that then fails on every device.
    /// </remarks>
    public static bool IsPlainText(string? text) =>
        text is not null && !text.Any(static c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    private static string PlainText(string value, string paramName) =>
        IsPlainText(value)
            ? value
            : throw new ArgumentException(
                "Announcement text is plain text: control characters other than line breaks and tabs are not allowed.",
                paramName);

    public static Announcement ToGroup(
        Guid organizationId, string title, string message, Guid deviceGroupId,
        Guid createdByUserId, string createdByDisplay) =>
        new(organizationId, title, message, AnnouncementTargetType.Group, deviceGroupId,
            createdByUserId, createdByDisplay);

    public static Announcement ToDevices(
        Guid organizationId, string title, string message, Guid createdByUserId, string createdByDisplay) =>
        new(organizationId, title, message, AnnouncementTargetType.Devices, deviceGroupId: null,
            createdByUserId, createdByDisplay);
}

/// <summary>
/// One device's part in an announcement: the task that carries the notice to it,
/// or why it was deliberately sent nothing.
/// </summary>
/// <remarks>
/// The task id is a plain value, never a foreign key -- this history outlives the
/// tasks it points at, and the live state of each delivery is read from the task
/// while it exists. The device reference, by contrast, cascades exactly as
/// <c>restart_schedule_devices</c> does: deleting a device takes its per-device
/// delivery rows with it, and the announcement row and its audit entry are the
/// history that remains. The hostname is copied here as the machine's name at
/// send time, immune to later renames.
/// </remarks>
public sealed class AnnouncementDevice : AuditableEntity
{
    public const int MaxHostnameLength = 256;

    public const int MaxSkipReasonLength = 64;

    /// <summary>The device was targeted while retired; nothing is ever queued for a retired device.</summary>
    public const string SkipReasonRetired = "Retired";

    /// <summary>The device's agent predates the display executor (the catalogue's minimum version).</summary>
    public const string SkipReasonAgentTooOld = "AgentTooOld";

    private AnnouncementDevice()
    {
        Hostname = null!;
    }

    public AnnouncementDevice(Guid announcementId, Guid deviceId, string hostname, Guid? taskId, string? skipReason)
    {
        if (taskId.HasValue == (skipReason is not null))
        {
            throw new ArgumentException(
                "A queued row carries its task id and a skipped row its reason -- exactly one of the two.",
                nameof(taskId));
        }

        AnnouncementId = Guard.NotEmpty(announcementId);
        DeviceId = Guard.NotEmpty(deviceId);
        Hostname = Guard.NotNullOrWhiteSpace(hostname, nameof(hostname), MaxHostnameLength);
        TaskId = taskId is { } id ? Guard.NotEmpty(id) : null;
        SkipReason = skipReason is null
            ? null
            : Guard.NotNullOrWhiteSpace(skipReason, nameof(skipReason), MaxSkipReasonLength);
    }

    public Guid AnnouncementId { get; private set; }

    public Guid DeviceId { get; private set; }

    public string Hostname { get; private set; }

    /// <summary>The ShowAnnouncement task queued for this device, or null when it was skipped.</summary>
    public Guid? TaskId { get; private set; }

    /// <summary>Why nothing was queued, or null when a task was.</summary>
    public string? SkipReason { get; private set; }
}
