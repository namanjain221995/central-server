using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Software;

/// <summary>Who may see a catalog app in the Company Portal.</summary>
public enum CatalogVisibility
{
    /// <summary>Every device in the organization.</summary>
    AllDevices = 0,

    /// <summary>
    /// Only devices in the listed groups. Not the same as listing the built-in
    /// "All Devices" group: that group holds only devices in no other group, so it
    /// would hide the app from everyone who has been placed in a department.
    /// </summary>
    SelectedGroups = 1,
}

/// <summary>
/// An approved software package offered to end users in the Company Portal, so
/// they can install it themselves without an administrator queueing it.
/// </summary>
/// <remarks>
/// <para>
/// Publishing an app is the administrator's approval. A user clicking Install
/// does not decide anything: the portal names a catalog app, and the server
/// checks that this app is published, visible to that device and backed by a
/// deployable package before it queues the same InstallPackage task an
/// administrator would. The agent then applies the same hash and signer pins.
/// </para>
/// <para>
/// The row is presentation and policy only. What gets installed is the
/// referenced <see cref="SoftwarePackage"/>, pinned by content hash -- an app
/// cannot point at bytes the package catalogue has not already accepted.
/// </para>
/// </remarks>
public sealed class CatalogApp : AuditableEntity
{
    /// <summary>Largest icon accepted. A portal tile needs nothing near this.</summary>
    public const int MaxIconBytes = 64 * 1024;

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private CatalogApp()
    {
    }

    public CatalogApp(
        Guid organizationId,
        Guid packageId,
        string displayName,
        string? description,
        string? category,
        CatalogVisibility visibility,
        Guid createdByUserId,
        string createdByDisplay)
    {
        OrganizationId = Guard.NotEmpty(organizationId, nameof(organizationId));
        PackageId = Guard.NotEmpty(packageId, nameof(packageId));
        CreatedByUserId = Guard.NotEmpty(createdByUserId, nameof(createdByUserId));
        CreatedByDisplay = Guard.NotNullOrWhiteSpace(createdByDisplay, nameof(createdByDisplay), maxLength: 256);
        Describe(displayName, description, category);
        Visibility = visibility;
        PublishedByUserId = CreatedByUserId;
        PublishedByDisplay = CreatedByDisplay;
    }

    public Guid OrganizationId { get; private set; }

    /// <summary>The package installed when a user picks this app.</summary>
    public Guid PackageId { get; private set; }

    public string DisplayName { get; private set; } = null!;

    public string? Description { get; private set; }

    public string? Category { get; private set; }

    /// <summary>PNG bytes, or null for the portal's default tile.</summary>
    public byte[]? Icon { get; private set; }

    public CatalogVisibility Visibility { get; private set; }

    /// <summary>
    /// Only a published app is shown in the portal or installable from it. New
    /// apps start unpublished so an administrator can finish setting one up
    /// before anyone sees it.
    /// </summary>
    public bool IsPublished { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public string CreatedByDisplay { get; private set; } = null!;

    /// <summary>
    /// The administrator who last published the app. Self-service installs are
    /// queued in their name, because publishing is the approval the install
    /// rests on; the requesting user is recorded alongside.
    /// </summary>
    public Guid PublishedByUserId { get; private set; }

    public string PublishedByDisplay { get; private set; } = null!;

    public void Describe(string displayName, string? description, string? category)
    {
        DisplayName = Guard.NotNullOrWhiteSpace(displayName, nameof(displayName), maxLength: 128).Trim();
        Description = NullIfBlank(Guard.OptionalMaxLength(description, 1000, nameof(description)));
        Category = NullIfBlank(Guard.OptionalMaxLength(category, 64, nameof(category)));
    }

    public void SetVisibility(CatalogVisibility visibility) => Visibility = visibility;

    public void Publish(Guid userId, string userDisplay)
    {
        PublishedByUserId = Guard.NotEmpty(userId, nameof(userId));
        PublishedByDisplay = Guard.NotNullOrWhiteSpace(userDisplay, nameof(userDisplay), maxLength: 256);
        IsPublished = true;
    }

    public void Unpublish() => IsPublished = false;

    /// <summary>
    /// Replaces the icon. PNG only: the portal draws it with no image codec
    /// beyond PNG, and refusing everything else up front means a hostile file
    /// never reaches a user's desktop as "the Slack icon".
    /// </summary>
    public void SetIcon(byte[]? png)
    {
        if (png is null)
        {
            Icon = null;
            return;
        }

        if (png.Length == 0 || png.Length > MaxIconBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(png), $"Icon must be 1 byte to {MaxIconBytes / 1024} KB.");
        }

        if (!png.AsSpan().StartsWith(PngSignature))
        {
            throw new ArgumentException("Icon must be a PNG image.", nameof(png));
        }

        Icon = png;
    }

    /// <summary>Whether a device in <paramref name="deviceGroupId"/> may see this app.</summary>
    public bool IsVisibleTo(Guid deviceGroupId, IReadOnlyCollection<Guid> allowedGroupIds) =>
        IsVisibleTo(IsPublished, Visibility, deviceGroupId, allowedGroupIds);

    /// <summary>
    /// The rule itself, for a caller that projects the columns rather than
    /// loading the entity (the portal list must not pull every icon). One
    /// definition, so the entity and the query can never disagree.
    /// </summary>
    public static bool IsVisibleTo(
        bool isPublished, CatalogVisibility visibility, Guid deviceGroupId, IReadOnlyCollection<Guid> allowedGroupIds) =>
        isPublished
        && (visibility == CatalogVisibility.AllDevices || allowedGroupIds.Contains(deviceGroupId));

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One group a <see cref="CatalogVisibility.SelectedGroups"/> app is offered to.</summary>
public sealed class CatalogAppGroup : Entity
{
    private CatalogAppGroup()
    {
    }

    public CatalogAppGroup(Guid catalogAppId, Guid deviceGroupId)
    {
        CatalogAppId = Guard.NotEmpty(catalogAppId, nameof(catalogAppId));
        DeviceGroupId = Guard.NotEmpty(deviceGroupId, nameof(deviceGroupId));
    }

    public Guid CatalogAppId { get; private set; }

    public Guid DeviceGroupId { get; private set; }
}
