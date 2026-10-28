using EndpointPlatform.Domain.Common;

namespace EndpointPlatform.Domain.Identity;

/// <summary>
/// Grants one platform administrator authority over one device group.
/// </summary>
/// <remarks>
/// <para>
/// Permission answers "may this operator do X at all"; scope answers "on which
/// machines". Both must pass. A permission grant alone reaches nothing: an
/// administrator with <c>user.change_type</c> but no scope can change the account
/// type on zero devices.
/// </para>
/// <para>
/// The authorizer is deliberately deny-by-default: an administrator with no scope
/// rows and <see cref="PlatformUser.HasAllDeviceScope"/> false acts on nothing, so
/// "no rows" never has to mean "unlimited". Accounts are nevertheless CREATED with
/// all-device scope (PlatformUserService.CreateAsync): the role says what an
/// administrator may do, and until there is a surface for granting scope, an account
/// born without it would be unusable. Restricting an administrator to specific
/// groups means clearing the flag and adding rows here. Administrators that predate
/// this model were migrated to all-devices explicitly, and the
/// BackfillAllDeviceScope migration did the same for accounts left with neither.
/// </para>
/// </remarks>
public sealed class AdminDeviceScope : AuditableEntity
{
    private AdminDeviceScope()
    {
    }

    public AdminDeviceScope(Guid platformUserId, Guid deviceGroupId)
    {
        PlatformUserId = Guard.NotEmpty(platformUserId);
        DeviceGroupId = Guard.NotEmpty(deviceGroupId);
    }

    public Guid PlatformUserId { get; private set; }

    public Guid DeviceGroupId { get; private set; }
}
