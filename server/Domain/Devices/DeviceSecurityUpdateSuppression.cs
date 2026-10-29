using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Devices;

/// <summary>
/// An operator's decision to hide one Windows Update security update on one device:
/// it stays out of the actionable pending list and "install all" never queues it.
/// </summary>
/// <remarks>
/// <para>
/// Per device, because Windows Update offers are per device. Platform-side only:
/// nothing on the endpoint changes. Setting WUA's own <c>IsHidden</c> would also hide
/// the update from the laptop's Settings page -- a real change on the machine that
/// outlives the platform's view of it and surprises whoever services the laptop
/// later -- so hiding here is a statement about the console, not about Windows.
/// </para>
/// <para>
/// Unlike a hidden driver, a hidden security patch must say why: leaving a machine
/// unpatched on purpose is a decision someone will be asked to account for, so the
/// reason is required (and audited), never optional.
/// </para>
/// <para>
/// Its own table rather than a flag on <see cref="DeviceSecurityUpdate"/>: the pending
/// rows are replaced by every scan, and a decision an operator made must survive that.
/// </para>
/// </remarks>
public sealed class DeviceSecurityUpdateSuppression : AuditableEntity
{
    /// <summary>Longest reason an operator may record.</summary>
    public const int MaxReasonLength = 200;

    private DeviceSecurityUpdateSuppression()
    {
        UpdateId = null!;
        Title = null!;
        Reason = null!;
        CreatedByDisplay = null!;
    }

    public DeviceSecurityUpdateSuppression(
        Guid deviceId,
        string updateId,
        string title,
        string reason,
        Guid createdByUserId,
        string createdByDisplay)
    {
        DeviceId = Guard.NotEmpty(deviceId);
        UpdateId = Guard.NotNullOrWhiteSpace(updateId, nameof(updateId), maxLength: 64);
        Title = Guard.NotNullOrWhiteSpace(title, nameof(title), maxLength: 512);
        Reason = Guard.NotNullOrWhiteSpace(reason, nameof(reason), maxLength: MaxReasonLength);
        CreatedByUserId = Guard.NotEmpty(createdByUserId);
        CreatedByDisplay = Guard.NotNullOrWhiteSpace(createdByDisplay, nameof(createdByDisplay), maxLength: 320);
    }

    public Guid DeviceId { get; private set; }

    /// <summary>The Windows Update update id (a GUID) hidden on this device.</summary>
    public string UpdateId { get; private set; }

    /// <summary>The update's title when it was hidden, so the hidden list reads even after the offer is gone.</summary>
    public string Title { get; private set; }

    /// <summary>Why the operator hid it. Always present: hiding a security patch must be justified.</summary>
    public string Reason { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string CreatedByDisplay { get; private set; }
}
