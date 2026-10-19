using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.ScreenLocks;

/// <summary>How a screen lock chose its audience. Stored as text, like every persisted enum.</summary>
public enum ScreenLockTargetType
{
    /// <summary>A device group, resolved to its active members when the lock is engaged.</summary>
    Group = 0,

    /// <summary>An explicit selection of devices.</summary>
    Devices = 1,
}

/// <summary>
/// An administrator's decision to put a set of machines behind a persistent
/// full-screen lock: what the locked screen says, who it was aimed at, and who
/// engaged it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The record is the decision, not the live state.</b> One row per engage; what
/// each targeted machine is actually doing about it lives on its
/// <see cref="ScreenLockDevice"/> rows and the <c>EngageScreenLock</c> /
/// <c>ReleaseScreenLock</c> tasks they point at. Whether a device is "locked" right
/// now is DERIVED from those tasks and the agent's heartbeat, never stored on this
/// row -- the same "derive, don't store" rule the Device entity follows for
/// online/offline, because a lock the agent has already lifted locally (the IT
/// recovery code) must not read as still engaged just because no release task has
/// been queued.
/// </para>
/// <para>
/// <b>Plain text, tightly bounded.</b> The message and contact line travel verbatim
/// to an agent that paints them on a lock screen, so the limits here are the
/// contract the agent relies on -- never markup, and never a control character it
/// would refuse.
/// </para>
/// </remarks>
public sealed class ScreenLock : AuditableEntity
{
    public const int MaxMessageLength = 500;

    public const int MaxContactLineLength = 200;

    public const int MaxDisplayLength = 256;

    private ScreenLock()
    {
        Message = null!;
        CreatedByDisplay = null!;
    }

    private ScreenLock(
        Guid organizationId, string message, string? contactLine, ScreenLockTargetType targetType,
        Guid? deviceGroupId, Guid createdByUserId, string createdByDisplay)
    {
        if ((targetType == ScreenLockTargetType.Group) != deviceGroupId.HasValue)
        {
            throw new ArgumentException(
                "A group lock names its group, and only a group lock does.", nameof(deviceGroupId));
        }

        OrganizationId = Guard.NotEmpty(organizationId);
        Message = PlainText(Guard.NotNullOrWhiteSpace(message, nameof(message), MaxMessageLength), nameof(message));

        // Optional: blank normalises to null (no second line), a present value is
        // trimmed, length-checked and held to the same plain-text rule as the message.
        var normalizedContact = Guard.OptionalMaxLength(contactLine, MaxContactLineLength);
        ContactLine = normalizedContact is null ? null : PlainText(normalizedContact, nameof(contactLine));

        TargetType = targetType;
        DeviceGroupId = deviceGroupId is { } groupId ? Guard.NotEmpty(groupId) : null;
        CreatedByUserId = Guard.NotEmpty(createdByUserId);
        CreatedByDisplay = Guard.NotNullOrWhiteSpace(createdByDisplay, nameof(createdByDisplay), MaxDisplayLength);
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>What the locked screen shows: why the machine is locked.</summary>
    public string Message { get; private set; }

    /// <summary>An optional second line -- who to contact to get unlocked -- or null.</summary>
    public string? ContactLine { get; private set; }

    public ScreenLockTargetType TargetType { get; private set; }

    /// <summary>
    /// The group a <see cref="ScreenLockTargetType.Group"/> lock named. A historical
    /// reference, not a dependency: the group's name is resolved live when the
    /// history is read, and goes blank if the group is ever deleted.
    /// </summary>
    public Guid? DeviceGroupId { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string CreatedByDisplay { get; private set; }

    /// <summary>
    /// Whether text may travel as a lock message or contact line: no control
    /// characters other than newlines and tabs.
    /// </summary>
    /// <remarks>
    /// The server-side mirror of the agent's lock-screen protocol (the counterpart
    /// of the announcement path's <c>SessionNoticeProtocol</c>), and it must stay in
    /// step with it: the executor refuses what this accepts, so a mismatch would
    /// queue a lock fleet-wide that then fails on every device.
    /// </remarks>
    public static bool IsPlainText(string? text) =>
        text is not null && !text.Any(static c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'));

    private static string PlainText(string value, string paramName) =>
        IsPlainText(value)
            ? value
            : throw new ArgumentException(
                "Screen lock text is plain text: control characters other than line breaks and tabs are not allowed.",
                paramName);

    public static ScreenLock ToGroup(
        Guid organizationId, string message, string? contactLine, Guid deviceGroupId,
        Guid createdByUserId, string createdByDisplay) =>
        new(organizationId, message, contactLine, ScreenLockTargetType.Group, deviceGroupId,
            createdByUserId, createdByDisplay);

    public static ScreenLock ToDevices(
        Guid organizationId, string message, string? contactLine,
        Guid createdByUserId, string createdByDisplay) =>
        new(organizationId, message, contactLine, ScreenLockTargetType.Devices, deviceGroupId: null,
            createdByUserId, createdByDisplay);
}

/// <summary>
/// One device's part in a screen lock: the task that carried the lock to it (and,
/// once lifted, the task that released it), or why it was deliberately sent
/// nothing.
/// </summary>
/// <remarks>
/// The task ids are plain values, never foreign keys -- this history outlives the
/// tasks it points at, and the live state of the lock on each device is read from
/// those tasks while they exist, combined with what the agent reports on its
/// heartbeat. The device reference, by contrast, cascades exactly as
/// <c>announcement_devices</c> does: deleting a device takes its per-device lock
/// rows with it, and the lock row and its audit entry are the history that remains.
/// The hostname is copied here as the machine's name at engage time, immune to
/// later renames.
/// </remarks>
public sealed class ScreenLockDevice : AuditableEntity
{
    public const int MaxHostnameLength = 256;

    public const int MaxSkipReasonLength = 64;

    /// <summary>The device was targeted while retired; nothing is ever queued for a retired device.</summary>
    public const string SkipReasonRetired = "Retired";

    /// <summary>The device's agent predates the lock executor (the catalogue's minimum version).</summary>
    public const string SkipReasonAgentTooOld = "AgentTooOld";

    private ScreenLockDevice()
    {
        Hostname = null!;
    }

    public ScreenLockDevice(
        Guid screenLockId, Guid deviceId, string hostname, Guid? engageTaskId, string? skipReason)
    {
        if (engageTaskId.HasValue == (skipReason is not null))
        {
            throw new ArgumentException(
                "A queued row carries its engage task id and a skipped row its reason -- exactly one of the two.",
                nameof(engageTaskId));
        }

        ScreenLockId = Guard.NotEmpty(screenLockId);
        DeviceId = Guard.NotEmpty(deviceId);
        Hostname = Guard.NotNullOrWhiteSpace(hostname, nameof(hostname), MaxHostnameLength);
        EngageTaskId = engageTaskId is { } id ? Guard.NotEmpty(id) : null;
        SkipReason = skipReason is null
            ? null
            : Guard.NotNullOrWhiteSpace(skipReason, nameof(skipReason), MaxSkipReasonLength);
    }

    public Guid ScreenLockId { get; private set; }

    public Guid DeviceId { get; private set; }

    public string Hostname { get; private set; }

    /// <summary>The EngageScreenLock task queued for this device, or null when it was skipped.</summary>
    public Guid? EngageTaskId { get; private set; }

    /// <summary>
    /// The ReleaseScreenLock task that lifted this device's lock, once one was
    /// queued; null while the lock is still in force (or was never engaged).
    /// </summary>
    public Guid? ReleaseTaskId { get; private set; }

    /// <summary>Why nothing was queued, or null when an engage task was.</summary>
    public string? SkipReason { get; private set; }

    /// <summary>Records the release task queued to lift this device's lock.</summary>
    /// <remarks>
    /// Only an engaged row (one that carries an engage task) can be released, and it
    /// is released at most once: a second release of the same row is a no-op, so a
    /// repeated unlock cannot overwrite the task that actually lifted the lock.
    /// </remarks>
    public bool TryMarkReleased(Guid releaseTaskId)
    {
        if (EngageTaskId is null || ReleaseTaskId is not null)
        {
            return false;
        }

        ReleaseTaskId = Guard.NotEmpty(releaseTaskId);
        return true;
    }
}
