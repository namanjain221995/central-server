using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Security;

/// <summary>
/// Answers "may this administrator act on this device?" — the scope half of
/// authorization, enforced server-side alongside the permission check.
/// </summary>
/// <remarks>
/// <para>
/// A permission grant says what an operator may do; scope says where. Both are
/// required, and both are checked on the server: the dashboard hides controls as a
/// courtesy, never as a boundary.
/// </para>
/// <para>
/// The model is deny-by-default. Authority comes from exactly two sources:
/// <see cref="Domain.Identity.PlatformUser.HasAllDeviceScope"/>, or an
/// <see cref="Domain.Identity.AdminDeviceScope"/> row naming a device group the
/// device belongs to. An administrator with neither reaches no device at all — "no
/// scope rows" means nothing, never everything.
/// </para>
/// <para>
/// Organization tenancy is checked here too, so a scoped lookup can never leak a
/// device from another tenant even if a group id were guessed.
/// </para>
/// </remarks>
public sealed class DeviceScopeAuthorizer(EndpointPlatformDbContext dbContext)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    /// <summary>True when the administrator may act on the device.</summary>
    public async Task<bool> CanActOnDeviceAsync(
        Guid platformUserId,
        Guid organizationId,
        Guid deviceId,
        CancellationToken cancellationToken = default)
    {
        // The device must exist in the caller's organization at all.
        var deviceExists = await _dbContext.Devices
            .AnyAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);
        if (!deviceExists)
        {
            return false;
        }

        var hasAllScope = await _dbContext.PlatformUsers
            .Where(u => u.Id == platformUserId && u.OrganizationId == organizationId)
            .Select(u => u.HasAllDeviceScope)
            .SingleOrDefaultAsync(cancellationToken);
        if (hasAllScope)
        {
            return true;
        }

        // Otherwise the device's one group must be a group this administrator is
        // scoped to. Membership is exclusive, so this is a single comparison.
        return await (
            from scope in _dbContext.AdminDeviceScopes
            join device in _dbContext.Devices on scope.DeviceGroupId equals device.DeviceGroupId
            where scope.PlatformUserId == platformUserId
                  && device.Id == deviceId
                  && device.OrganizationId == organizationId
            select scope.Id)
            .AnyAsync(cancellationToken);
    }

    /// <summary>
    /// True when the administrator may act on every device in the group -- which,
    /// because a device belongs to exactly one group, is the same as being scoped
    /// to the group itself.
    /// </summary>
    /// <remarks>
    /// This is what gates group management and group actions. It is deliberately
    /// not "may act on some member": membership decides who may act on a device,
    /// so authority over a group has to mean authority over all of it. The group
    /// must also belong to the caller's organization; a group id from another
    /// tenant answers false rather than revealing that it exists.
    /// </remarks>
    public async Task<bool> CanActOnGroupAsync(
        Guid platformUserId,
        Guid organizationId,
        Guid deviceGroupId,
        CancellationToken cancellationToken = default)
    {
        var groupExists = await _dbContext.DeviceGroups
            .AnyAsync(g => g.Id == deviceGroupId && g.OrganizationId == organizationId, cancellationToken);
        if (!groupExists)
        {
            return false;
        }

        var hasAllScope = await _dbContext.PlatformUsers
            .Where(u => u.Id == platformUserId && u.OrganizationId == organizationId)
            .Select(u => u.HasAllDeviceScope)
            .SingleOrDefaultAsync(cancellationToken);
        if (hasAllScope)
        {
            return true;
        }

        return await _dbContext.AdminDeviceScopes
            .AnyAsync(s => s.PlatformUserId == platformUserId && s.DeviceGroupId == deviceGroupId, cancellationToken);
    }

    /// <summary>
    /// The groups an administrator may see and act on, or null for all of them.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>?> ScopedGroupIdsOrNullAsync(
        Guid platformUserId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var hasAllScope = await _dbContext.PlatformUsers
            .Where(u => u.Id == platformUserId && u.OrganizationId == organizationId)
            .Select(u => u.HasAllDeviceScope)
            .SingleOrDefaultAsync(cancellationToken);

        if (hasAllScope)
        {
            return null;
        }

        return await _dbContext.AdminDeviceScopes
            .Where(s => s.PlatformUserId == platformUserId)
            .Select(s => s.DeviceGroupId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Restricts a device query to those the administrator may see. Used by list/read
    /// endpoints so scope narrows results rather than leaking a device's existence.
    /// </summary>
    public async Task<IReadOnlyCollection<Guid>?> ScopedDeviceIdsOrNullAsync(
        Guid platformUserId, Guid organizationId, CancellationToken cancellationToken = default)
    {
        var hasAllScope = await _dbContext.PlatformUsers
            .Where(u => u.Id == platformUserId && u.OrganizationId == organizationId)
            .Select(u => u.HasAllDeviceScope)
            .SingleOrDefaultAsync(cancellationToken);

        // null means "unrestricted" — the caller applies no additional filter.
        if (hasAllScope)
        {
            return null;
        }

        return await (
            from scope in _dbContext.AdminDeviceScopes
            join device in _dbContext.Devices on scope.DeviceGroupId equals device.DeviceGroupId
            where scope.PlatformUserId == platformUserId && device.OrganizationId == organizationId
            select device.Id)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
