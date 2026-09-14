using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EndpointPlatform.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Keeps the two group invariants true on every insert: every organization has
/// its "All Devices" group, and every new device is placed in one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why at the persistence boundary.</b> Devices are created by enrollment, by
/// re-enrollment of a retired machine (which makes a new row), and by every test
/// and future code path that constructs one. Asking each of those to remember a
/// group is how one of them eventually forgets. Doing it here, once, means the
/// rule holds for all of them -- and the column is a non-null foreign key, so a
/// context built without this interceptor fails its first device insert loudly
/// instead of quietly creating a device in no group.
/// </para>
/// <para>
/// <b>Only fills a gap; never overrides.</b> A device that already names a group
/// keeps it. The interceptor supplies a default and makes no decision an
/// administrator could have made.
/// </para>
/// <para>
/// <b>Never automatic grouping.</b> The default is always the built-in group.
/// Nothing here reads a hostname, operating system, agent version or anything
/// else about the device; membership outside "All Devices" changes only through
/// an explicit administrator action.
/// </para>
/// </remarks>
public sealed class DeviceGroupAssignmentInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } context)
        {
            AssignAsync(context, CancellationToken.None, synchronous: true).GetAwaiter().GetResult();
        }

        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            await AssignAsync(context, cancellationToken, synchronous: false);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static async Task AssignAsync(DbContext context, CancellationToken cancellationToken, bool synchronous)
    {
        // Snapshot first: adding entities while enumerating the tracker is unsafe.
        var tracked = context.ChangeTracker.Entries().ToList();

        var builtInByOrganization = tracked
            .Where(e => e.Entity is DeviceGroup { IsBuiltIn: true } && e.State != EntityState.Deleted)
            .Select(e => (DeviceGroup)e.Entity)
            .GroupBy(g => g.OrganizationId)
            .ToDictionary(g => g.Key, g => g.First().Id);

        // A new organization gets its built-in group in the same save.
        foreach (var organization in tracked
                     .Where(e => e.State == EntityState.Added && e.Entity is Organization)
                     .Select(e => (Organization)e.Entity))
        {
            if (!builtInByOrganization.ContainsKey(organization.Id))
            {
                var allDevices = DeviceGroup.CreateAllDevices(organization.Id);
                context.Add(allDevices);
                builtInByOrganization[organization.Id] = allDevices.Id;
            }
        }

        var unassigned = tracked
            .Where(e => e.State == EntityState.Added && e.Entity is Device { DeviceGroupId: var id } && id == Guid.Empty)
            .Select(e => (Device)e.Entity)
            .ToList();

        foreach (var device in unassigned)
        {
            if (!builtInByOrganization.TryGetValue(device.OrganizationId, out var groupId))
            {
                var query = context.Set<DeviceGroup>()
                    .AsNoTracking()
                    .Where(g => g.OrganizationId == device.OrganizationId && g.IsBuiltIn)
                    .Select(g => g.Id);

                groupId = synchronous
                    ? query.SingleOrDefault()
                    : await query.SingleOrDefaultAsync(cancellationToken);

                if (groupId == Guid.Empty)
                {
                    // Every organization is given one on creation and the migration
                    // backfilled the rest, so this is a broken database, not a race.
                    // Refusing is right: a device with no group would be a device no
                    // scoped administrator could ever be granted.
                    throw new InvalidOperationException(
                        $"Organization {device.OrganizationId} has no \"{DeviceGroup.AllDevicesName}\" group; " +
                        "a device cannot be created without one.");
                }

                builtInByOrganization[device.OrganizationId] = groupId;
            }

            device.MoveToGroup(groupId);
        }
    }
}
