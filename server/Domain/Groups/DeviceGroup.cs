using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Groups;

/// <summary>How a group's membership is determined.</summary>
public enum DeviceGroupType
{
    /// <summary>Members are added and removed explicitly.</summary>
    Static = 0,

    /// <summary>Membership is computed from a rule (Phase 13+ / future). Not implemented.</summary>
    Dynamic = 1,
}

/// <summary>
/// A named partition of an organization's devices. Every device belongs to
/// exactly one group; policies, software and tasks can target a group, and an
/// administrator's device scope is expressed as the groups they may act on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exactly one group per device.</b> Membership is not a separate relation any
/// more: it is <see cref="Devices.Device.DeviceGroupId"/>, a non-null foreign key,
/// so the database itself makes "no group" and "two groups" unrepresentable.
/// </para>
/// <para>
/// <b>Every organization has one built-in group, "All Devices".</b> It is where a
/// device lives until an administrator puts it somewhere else, and where it
/// returns when it is removed from a group or its group is deleted. It cannot be
/// renamed or deleted, because the invariant depends on there always being
/// somewhere to put a device.
/// </para>
/// <para>
/// Despite its name, "All Devices" holds only the devices that are in no other
/// group — membership is exclusive, so a device moved into "Developers" leaves
/// it. The name is the product's; the semantics are a fallback partition. In
/// particular an administrator scoped to this group is <em>not</em> scoped to
/// every device: that is <see cref="Identity.PlatformUser.HasAllDeviceScope"/>,
/// and only that.
/// </para>
/// </remarks>
public sealed class DeviceGroup : AuditableEntity
{
    /// <summary>The built-in group's name. Reserved: no custom group may take it.</summary>
    public const string AllDevicesName = "All Devices";

    /// <summary>The longest group name accepted.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The longest description accepted.</summary>
    public const int MaxDescriptionLength = 512;

    private DeviceGroup()
    {
        Name = null!;
        Description = null!;
    }

    private DeviceGroup(Guid organizationId, string name, string description, DeviceGroupType type, bool isBuiltIn)
    {
        OrganizationId = Guard.NotEmpty(organizationId);
        Name = name;
        Description = description;
        Type = type;
        IsBuiltIn = isBuiltIn;
    }

    /// <summary>Creates a custom, administrator-managed group.</summary>
    /// <exception cref="ArgumentException">The name is empty, too long, contains control characters, or is reserved.</exception>
    public DeviceGroup(Guid organizationId, string name, string? description, DeviceGroupType type)
        : this(organizationId, ValidateCustomName(name), NormalizeDescription(description), type, isBuiltIn: false)
    {
    }

    /// <summary>
    /// The organization's "All Devices" group. Created once per organization, by
    /// the persistence layer when the organization is created and by the
    /// migration for organizations that predate it — never from a request.
    /// </summary>
    public static DeviceGroup CreateAllDevices(Guid organizationId) =>
        new(organizationId, AllDevicesName,
            "Every device that has not been placed in another group.",
            DeviceGroupType.Static, isBuiltIn: true);

    public Guid OrganizationId { get; private set; }
    public string Name { get; private set; }
    public string Description { get; private set; }
    public DeviceGroupType Type { get; private set; }

    /// <summary>True for "All Devices": it cannot be renamed or deleted.</summary>
    public bool IsBuiltIn { get; private set; }

    /// <summary>Renames a custom group.</summary>
    /// <exception cref="InvalidOperationException">This is the built-in group.</exception>
    /// <exception cref="ArgumentException">The name is not acceptable.</exception>
    public void Rename(string name, string? description = null)
    {
        if (IsBuiltIn)
        {
            throw new InvalidOperationException($"The \"{AllDevicesName}\" group cannot be renamed.");
        }

        Name = ValidateCustomName(name);
        if (description is not null)
        {
            Description = NormalizeDescription(description);
        }
    }

    /// <summary>
    /// Refuses to let the built-in group be deleted. The caller moves the members
    /// to "All Devices" and removes the row in one transaction; this is the
    /// domain's half of that rule, and the foreign key is the database's.
    /// </summary>
    /// <exception cref="InvalidOperationException">This is the built-in group.</exception>
    public void EnsureDeletable()
    {
        if (IsBuiltIn)
        {
            throw new InvalidOperationException($"The \"{AllDevicesName}\" group cannot be deleted.");
        }
    }

    /// <summary>
    /// The name a custom group may have: trimmed, non-empty, bounded, free of
    /// control characters, and not the reserved built-in name in any casing.
    /// </summary>
    public static string ValidateCustomName(string? name)
    {
        var trimmed = Guard.NotNullOrWhiteSpace(name, nameof(name), maxLength: MaxNameLength);

        // A tab, newline or other control character in a name renders as nothing
        // or breaks a row in every table that shows it, and invites names that
        // look identical to an existing one.
        if (trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("A group name must not contain control characters.", nameof(name));
        }

        // Reserved in any casing: "all devices" beside "All Devices" is exactly
        // the confusion the reservation exists to prevent.
        if (string.Equals(trimmed, AllDevicesName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"\"{AllDevicesName}\" is reserved for the built-in group.", nameof(name));
        }

        return trimmed;
    }

    private static string NormalizeDescription(string? description)
    {
        var value = description?.Trim() ?? string.Empty;
        if (value.Length > MaxDescriptionLength)
        {
            throw new ArgumentException(
                $"A description must be at most {MaxDescriptionLength} characters.", nameof(description));
        }

        if (value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
        {
            throw new ArgumentException("A description must not contain control characters.", nameof(description));
        }

        return value;
    }
}
