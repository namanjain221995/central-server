using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Groups;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Persistence.Configurations;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EndpointPlatform.Infrastructure.Groups;

/// <summary>
/// Manages device groups and which group each device is in. Every operation is
/// scoped, transactional and audited.
/// </summary>
/// <remarks>
/// <para>
/// <b>Moving a device is an authorization decision.</b> An administrator's
/// device scope is the set of groups they are scoped to, so putting a device in
/// a group decides who may act on it. Every move therefore requires authority
/// over the device where it is now <em>and</em> over the group it is going to.
/// Without the first, a scoped administrator could pull any device into their
/// own group and so grant themselves authority over it -- which the previous
/// implementation allowed. Without the second, they could push devices onto
/// administrators who never agreed to manage them. The rule is the same for add,
/// remove and delete, because all three are moves: remove and delete go to
/// "All Devices".
/// </para>
/// <para>
/// <b>Moves are conditional on the state that was authorized.</b> Each is
/// <c>UPDATE ... WHERE id = @device AND device_group_id = @authorizedSource</c>.
/// If another request moved the device in between -- possibly into a group this
/// administrator has no authority over -- the update touches nothing and the
/// device is reported as having changed, rather than being moved out of a group
/// the caller was never checked against.
/// </para>
/// <para>
/// <b>Out of scope reads as not found.</b> A device or group the caller cannot
/// act on is indistinguishable from one that does not exist, as on every device
/// endpoint.
/// </para>
/// </remarks>
public sealed class DeviceGroupService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    DeviceScopeAuthorizer scope,
    IOptions<AgentServerOptions> agentServerOptions)
{
    /// <summary>Bounds a single membership request, so an oversized body is refused rather than run.</summary>
    public const int MaxDevicesPerRequest = 500;

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly DeviceScopeAuthorizer _scope = scope;
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);

    // ------------------------------------------------------------------ reads

    /// <summary>The groups the caller may act on, "All Devices" first, with device counts.</summary>
    public async Task<IReadOnlyList<DeviceGroupSummary>> ListAsync(
        Guid organizationId, Guid actorId, CancellationToken cancellationToken = default)
    {
        var visible = await _scope.ScopedGroupIdsOrNullAsync(actorId, organizationId, cancellationToken);
        var onlineSince = _timeProvider.GetUtcNow() - _staleAfter;

        var query = _dbContext.DeviceGroups.AsNoTracking().Where(g => g.OrganizationId == organizationId);
        if (visible is not null)
        {
            query = query.Where(g => visible.Contains(g.Id));
        }

        var rows = await query
            .Select(g => new
            {
                g.Id,
                g.Name,
                g.Description,
                g.IsBuiltIn,
                DeviceCount = _dbContext.Devices.Count(d => d.DeviceGroupId == g.Id && d.Status == DeviceStatus.Active),
                OnlineCount = _dbContext.Devices.Count(d =>
                    d.DeviceGroupId == g.Id
                    && d.Status == DeviceStatus.Active
                    && d.LastSeenAt != null
                    && d.LastSeenAt >= onlineSince),
            })
            .ToListAsync(cancellationToken);

        return rows
            .OrderByDescending(r => r.IsBuiltIn)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new DeviceGroupSummary(r.Id, r.Name, r.Description, r.IsBuiltIn, r.DeviceCount, r.OnlineCount))
            .ToList();
    }

    /// <summary>One group and its active devices, or null when the caller may not see it.</summary>
    public async Task<DeviceGroupDetail?> GetAsync(
        Guid organizationId, Guid actorId, Guid groupId, CancellationToken cancellationToken = default)
    {
        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, groupId, cancellationToken))
        {
            return null;
        }

        var group = await _dbContext.DeviceGroups.AsNoTracking()
            .SingleAsync(g => g.Id == groupId, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var devices = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.DeviceGroupId == groupId && d.OrganizationId == organizationId && d.Status == DeviceStatus.Active)
            .OrderBy(d => d.Hostname)
            .Select(d => new { d.Id, d.Hostname, d.DisplayName, d.AgentVersion, d.OperatingSystem, d.LastSeenAt, d.Status })
            .ToListAsync(cancellationToken);

        var members = devices
            .Select(d => new DeviceGroupMember(
                d.Id, d.Hostname, d.DisplayName, d.AgentVersion, d.OperatingSystem, d.LastSeenAt,
                IsOnline: d.Status == DeviceStatus.Active && d.LastSeenAt is { } seen && now - seen <= _staleAfter))
            .ToList();

        return new DeviceGroupDetail(
            group.Id, group.Name, group.Description, group.IsBuiltIn, members.Count, members.Count(m => m.IsOnline), members);
    }

    /// <summary>
    /// Devices the caller could place in the group, each with the group it is in
    /// now -- so the console can say plainly that adding one moves it.
    /// </summary>
    public async Task<IReadOnlyList<DeviceGroupCandidate>?> CandidatesAsync(
        Guid organizationId, Guid actorId, Guid groupId, CancellationToken cancellationToken = default)
    {
        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, groupId, cancellationToken))
        {
            return null;
        }

        var visible = await _scope.ScopedGroupIdsOrNullAsync(actorId, organizationId, cancellationToken);
        var now = _timeProvider.GetUtcNow();

        var query =
            from d in _dbContext.Devices.AsNoTracking()
            join g in _dbContext.DeviceGroups.AsNoTracking() on d.DeviceGroupId equals g.Id
            where d.OrganizationId == organizationId && d.Status == DeviceStatus.Active
            select new { d.Id, d.Hostname, d.DisplayName, d.LastSeenAt, d.AgentVersion, GroupId = g.Id, GroupName = g.Name, g.IsBuiltIn };

        // A device in a group the caller cannot act on is not a candidate at all:
        // listing it would reveal it, and moving it would be refused anyway.
        if (visible is not null)
        {
            query = query.Where(r => visible.Contains(r.GroupId));
        }

        var rows = await query.OrderBy(r => r.Hostname).ToListAsync(cancellationToken);

        return rows
            .Select(r => new DeviceGroupCandidate(
                r.Id, r.Hostname, r.DisplayName, r.AgentVersion,
                IsOnline: r.LastSeenAt is { } seen && now - seen <= _staleAfter,
                CurrentGroupId: r.GroupId, CurrentGroupName: r.GroupName, CurrentGroupIsBuiltIn: r.IsBuiltIn,
                InThisGroup: r.GroupId == groupId))
            .ToList();
    }

    // ----------------------------------------------------------------- writes

    /// <summary>
    /// Creates a custom group and moves the given devices into it, in one
    /// transaction. A device that cannot be moved is reported, not fatal: the
    /// group is still created with the ones that could.
    /// </summary>
    public async Task<GroupCreateResult> CreateAsync(
        Guid organizationId, Guid actorId, string actorDisplay,
        string name, string? description, IReadOnlyCollection<Guid> deviceIds,
        CancellationToken cancellationToken = default)
    {
        string validName;
        try
        {
            validName = DeviceGroup.ValidateCustomName(name);
        }
        catch (ArgumentException ex)
        {
            return GroupCreateResult.Invalid(ex.Message);
        }

        if (deviceIds.Count > MaxDevicesPerRequest)
        {
            return GroupCreateResult.Invalid($"At most {MaxDevicesPerRequest} devices may be added at once.");
        }

        // Only an administrator with all-device scope may create a group. Scope is
        // granted per group, and nothing grants the creator scope over a group
        // that did not exist a moment ago -- so a scoped administrator would make
        // a group they could neither see, fill nor delete.
        var unrestricted = await _scope.ScopedGroupIdsOrNullAsync(actorId, organizationId, cancellationToken) is null;
        if (!unrestricted)
        {
            return GroupCreateResult.Forbidden();
        }

        DeviceGroup group;
        try
        {
            group = new DeviceGroup(organizationId, validName, description, DeviceGroupType.Static);
        }
        catch (ArgumentException ex)
        {
            return GroupCreateResult.Invalid(ex.Message);
        }

        try
        {
            var results = await InTransactionAsync(async staged =>
            {
                _dbContext.DeviceGroups.Add(group);
                staged.Add(group);

                staged.Add(_auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
                    action: "group.create", AuditResult.Success,
                    a => a.OnTarget("device_group", group.Id.ToString(), group.Name)
                          .Requiring(Permissions.Group.Manage)
                          .WithStateChange(null, JsonSerializer.Serialize(new { name = group.Name, devicesRequested = deviceIds.Count }))));

                // The group row must exist before a device can point at it.
                await _dbContext.SaveChangesAsync(cancellationToken);

                return await MoveCoreAsync(organizationId, actorId, actorDisplay, deviceIds, group, staged, cancellationToken);
            }, cancellationToken);

            return GroupCreateResult.Created(group.Id, results);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, DeviceGroupConfiguration.UniqueNameIndex))
        {
            return GroupCreateResult.Duplicate();
        }
    }

    /// <summary>Renames a custom group. "All Devices" is refused.</summary>
    public async Task<GroupChangeStatus> RenameAsync(
        Guid organizationId, Guid actorId, string actorDisplay, Guid groupId, string name, string? description,
        CancellationToken cancellationToken = default)
    {
        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, groupId, cancellationToken))
        {
            return GroupChangeStatus.NotFound;
        }

        try
        {
            return await InTransactionAsync(async staged =>
            {
                var group = await _dbContext.DeviceGroups.SingleAsync(g => g.Id == groupId, cancellationToken);
                staged.Add(group);

                if (group.IsBuiltIn)
                {
                    return GroupChangeStatus.BuiltIn;
                }

                var previous = group.Name;
                try
                {
                    group.Rename(name, description);
                }
                catch (ArgumentException)
                {
                    return GroupChangeStatus.Invalid;
                }

                staged.Add(_auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
                    action: "group.rename", AuditResult.Success,
                    a => a.OnTarget("device_group", group.Id.ToString(), group.Name)
                          .Requiring(Permissions.Group.Manage)
                          .WithStateChange(
                              JsonSerializer.Serialize(new { name = previous }),
                              JsonSerializer.Serialize(new { name = group.Name }))));

                return GroupChangeStatus.Ok;
            }, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, DeviceGroupConfiguration.UniqueNameIndex))
        {
            return GroupChangeStatus.DuplicateName;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Deleted by another request between loading and saving the rename.
            return GroupChangeStatus.NotFound;
        }
        catch (InvalidOperationException)
        {
            if (await GroupExistsAsync(organizationId, groupId, cancellationToken))
            {
                throw;
            }

            return GroupChangeStatus.NotFound;
        }
    }

    /// <summary>
    /// Deletes a custom group, moving every device in it to "All Devices" first,
    /// in one transaction. Devices are never deleted.
    /// </summary>
    public async Task<GroupDeleteResult> DeleteAsync(
        Guid organizationId, Guid actorId, string actorDisplay, Guid groupId,
        CancellationToken cancellationToken = default)
    {
        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, groupId, cancellationToken))
        {
            return GroupDeleteResult.Of(GroupChangeStatus.NotFound);
        }

        var allDevicesId = await BuiltInGroupIdAsync(organizationId, cancellationToken);

        // Deleting moves the members to "All Devices", so it is a move like any
        // other and needs authority over the destination too.
        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, allDevicesId, cancellationToken))
        {
            return GroupDeleteResult.Of(GroupChangeStatus.DestinationOutOfScope);
        }

        try
        {
            return await InTransactionAsync(async staged =>
            {
                var group = await _dbContext.DeviceGroups.SingleAsync(g => g.Id == groupId, cancellationToken);
                staged.Add(group);

                if (group.IsBuiltIn)
                {
                    return GroupDeleteResult.Of(GroupChangeStatus.BuiltIn);
                }

                var now = _timeProvider.GetUtcNow();

                // Every device, active or retired: deletion must leave none of them
                // pointing at a group that no longer exists.
                var moved = await _dbContext.Devices
                    .Where(d => d.DeviceGroupId == groupId && d.OrganizationId == organizationId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.DeviceGroupId, allDevicesId)
                        .SetProperty(d => d.UpdatedAt, now), cancellationToken);

                _dbContext.DeviceGroups.Remove(group);

                staged.Add(_auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
                    action: "group.delete", AuditResult.Success,
                    a => a.OnTarget("device_group", group.Id.ToString(), group.Name)
                          .Requiring(Permissions.Group.Manage)
                          .WithStateChange(
                              JsonSerializer.Serialize(new { name = group.Name }),
                              JsonSerializer.Serialize(new { devicesMovedTo = DeviceGroup.AllDevicesName, devicesMoved = moved }))));

                // A device moved into this group after the update above makes this
                // delete fail on the foreign key and roll the whole thing back,
                // rather than leaving that device pointing at nothing.
                return GroupDeleteResult.Deleted(moved);
            }, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex))
        {
            return GroupDeleteResult.Of(GroupChangeStatus.Conflict);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request deleted this group between this one loading it and
            // removing it, so the DELETE affected no row. The group is gone, which
            // is what was asked for; the other request moved its devices.
            return GroupDeleteResult.Of(GroupChangeStatus.NotFound);
        }
        catch (InvalidOperationException)
        {
            // SingleAsync found nothing: deleted by another request before this one
            // loaded it. Anything else is not ours to swallow.
            if (await GroupExistsAsync(organizationId, groupId, cancellationToken))
            {
                throw;
            }

            return GroupDeleteResult.Of(GroupChangeStatus.NotFound);
        }
    }

    /// <summary>Moves devices into the group, each out of whichever group it was in.</summary>
    public async Task<GroupMembershipResult> AddDevicesAsync(
        Guid organizationId, Guid actorId, string actorDisplay, Guid groupId, IReadOnlyCollection<Guid> deviceIds,
        CancellationToken cancellationToken = default)
    {
        if (deviceIds.Count is 0 or > MaxDevicesPerRequest)
        {
            return GroupMembershipResult.Of(GroupChangeStatus.Invalid);
        }

        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, groupId, cancellationToken))
        {
            return GroupMembershipResult.Of(GroupChangeStatus.NotFound);
        }

        var results = await InTransactionAsync(async staged =>
        {
            var destination = await _dbContext.DeviceGroups.AsNoTracking().SingleAsync(g => g.Id == groupId, cancellationToken);
            return await MoveCoreAsync(organizationId, actorId, actorDisplay, deviceIds, destination, staged, cancellationToken);
        }, cancellationToken);

        return GroupMembershipResult.Done(results);
    }

    /// <summary>
    /// Removes devices from a custom group, returning each to "All Devices" so it
    /// is never left in no group. Removing from "All Devices" itself is refused:
    /// there is nowhere to return a device to.
    /// </summary>
    public async Task<GroupMembershipResult> RemoveDevicesAsync(
        Guid organizationId, Guid actorId, string actorDisplay, Guid groupId, IReadOnlyCollection<Guid> deviceIds,
        CancellationToken cancellationToken = default)
    {
        if (deviceIds.Count is 0 or > MaxDevicesPerRequest)
        {
            return GroupMembershipResult.Of(GroupChangeStatus.Invalid);
        }

        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, groupId, cancellationToken))
        {
            return GroupMembershipResult.Of(GroupChangeStatus.NotFound);
        }

        var allDevicesId = await BuiltInGroupIdAsync(organizationId, cancellationToken);
        if (groupId == allDevicesId)
        {
            return GroupMembershipResult.Of(GroupChangeStatus.BuiltIn);
        }

        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, allDevicesId, cancellationToken))
        {
            return GroupMembershipResult.Of(GroupChangeStatus.DestinationOutOfScope);
        }

        var results = await InTransactionAsync(async staged =>
        {
            var destination = await _dbContext.DeviceGroups.AsNoTracking().SingleAsync(g => g.Id == allDevicesId, cancellationToken);

            // Only devices actually in this group may be removed from it; anything
            // else in the request is reported and left where it is. Without this
            // "remove from Developers" could be used to move a device out of any
            // other group the caller happens to control.
            var inThisGroup = await _dbContext.Devices.AsNoTracking()
                .Where(d => deviceIds.Contains(d.Id) && d.DeviceGroupId == groupId && d.OrganizationId == organizationId)
                .Select(d => d.Id)
                .ToListAsync(cancellationToken);

            var notMembers = deviceIds.Distinct().Except(inThisGroup)
                .Select(id => new GroupDeviceResult(id, null, GroupDeviceOutcome.NotInGroup, null))
                .ToList();

            var moved = await MoveCoreAsync(organizationId, actorId, actorDisplay, inThisGroup, destination, staged, cancellationToken);
            return (IReadOnlyList<GroupDeviceResult>)[.. moved, .. notMembers];
        }, cancellationToken);

        return GroupMembershipResult.Done(results);
    }

    // --------------------------------------------------------------- internals

    /// <summary>
    /// Moves each device into <paramref name="destination"/>, authorizing its
    /// current group and applying the move only if the device is still there.
    /// Must run inside <see cref="InTransactionAsync{T}"/>; the caller has
    /// already authorized the destination.
    /// </summary>
    private async Task<IReadOnlyList<GroupDeviceResult>> MoveCoreAsync(
        Guid organizationId, Guid actorId, string actorDisplay, IReadOnlyCollection<Guid> deviceIds,
        DeviceGroup destination, List<object> staged, CancellationToken cancellationToken)
    {
        var requested = deviceIds.Distinct().ToList();
        if (requested.Count == 0)
        {
            return [];
        }

        var visible = await _scope.ScopedGroupIdsOrNullAsync(actorId, organizationId, cancellationToken);

        var devices = await (
            from d in _dbContext.Devices.AsNoTracking()
            join g in _dbContext.DeviceGroups.AsNoTracking() on d.DeviceGroupId equals g.Id
            where requested.Contains(d.Id) && d.OrganizationId == organizationId
            select new { d.Id, d.Hostname, SourceId = g.Id, SourceName = g.Name })
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var results = new List<GroupDeviceResult>(requested.Count);

        foreach (var id in requested)
        {
            // Absent, another tenant's, or in a group the caller cannot act on:
            // all the same answer, so scope never reveals a device exists.
            if (!devices.TryGetValue(id, out var device) || (visible is not null && !visible.Contains(device.SourceId)))
            {
                results.Add(new GroupDeviceResult(id, null, GroupDeviceOutcome.NotFound, null));
                continue;
            }

            if (device.SourceId == destination.Id)
            {
                results.Add(new GroupDeviceResult(id, device.Hostname, GroupDeviceOutcome.AlreadyInGroup, device.SourceId));
                continue;
            }

            // Conditional on the group that was just authorized. Zero rows means
            // another request moved the device first; it is not moved out of a
            // group this caller was never checked against.
            var rows = await _dbContext.Devices
                .Where(d => d.Id == id && d.DeviceGroupId == device.SourceId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.DeviceGroupId, destination.Id)
                    .SetProperty(d => d.UpdatedAt, now), cancellationToken);

            if (rows == 0)
            {
                results.Add(new GroupDeviceResult(id, device.Hostname, GroupDeviceOutcome.ChangedConcurrently, device.SourceId));
                continue;
            }

            staged.Add(_auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
                action: "group.move_device", AuditResult.Success,
                a => a.OnDevice(id, device.Hostname)
                      .OnTarget("device_group", destination.Id.ToString(), destination.Name)
                      .Requiring(Permissions.Group.Manage)
                      .WithStateChange(
                          JsonSerializer.Serialize(new { group = device.SourceName }),
                          JsonSerializer.Serialize(new { group = destination.Name }))));

            results.Add(new GroupDeviceResult(id, device.Hostname, GroupDeviceOutcome.Moved, device.SourceId));
        }

        return results;
    }

    private Task<bool> GroupExistsAsync(Guid organizationId, Guid groupId, CancellationToken cancellationToken) =>
        _dbContext.DeviceGroups.AsNoTracking()
            .AnyAsync(g => g.Id == groupId && g.OrganizationId == organizationId, cancellationToken);

    private async Task<Guid> BuiltInGroupIdAsync(Guid organizationId, CancellationToken cancellationToken) =>
        await _dbContext.DeviceGroups.AsNoTracking()
            .Where(g => g.OrganizationId == organizationId && g.IsBuiltIn)
            .Select(g => g.Id)
            .SingleAsync(cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> and a final save in one transaction, under the
    /// context's retrying execution strategy.
    /// </summary>
    /// <remarks>
    /// Anything the work tracks is recorded in the list it is handed. If an
    /// attempt fails those entries are detached, so a retry starts clean instead
    /// of re-saving a previous attempt's audit rows beside its own.
    /// </remarks>
    private Task<T> InTransactionAsync<T>(Func<List<object>, Task<T>> work, CancellationToken cancellationToken)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            var staged = new List<object>();
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await work(staged);
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                foreach (var entity in staged)
                {
                    _dbContext.Entry(entity).State = EntityState.Detached;
                }

                throw;
            }
        });
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraint) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && string.Equals(pg.ConstraintName, constraint, StringComparison.Ordinal);

    private static bool IsForeignKeyViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };
}

/// <summary>A group as listed.</summary>
public sealed record DeviceGroupSummary(
    Guid Id, string Name, string Description, bool IsBuiltIn, int DeviceCount, int OnlineCount);

/// <summary>A group and its active devices.</summary>
public sealed record DeviceGroupDetail(
    Guid Id, string Name, string Description, bool IsBuiltIn, int DeviceCount, int OnlineCount,
    IReadOnlyList<DeviceGroupMember> Devices);

/// <summary>A device as a group shows it.</summary>
public sealed record DeviceGroupMember(
    Guid Id, string Hostname, string? DisplayName, string AgentVersion, string? OperatingSystem,
    DateTimeOffset? LastSeenAt, bool IsOnline);

/// <summary>A device that could be placed in a group, and where it is now.</summary>
public sealed record DeviceGroupCandidate(
    Guid Id, string Hostname, string? DisplayName, string AgentVersion, bool IsOnline,
    Guid CurrentGroupId, string CurrentGroupName, bool CurrentGroupIsBuiltIn, bool InThisGroup);

/// <summary>How a group-level change ended.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<GroupChangeStatus>))]
public enum GroupChangeStatus
{
    Ok,
    NotFound,
    BuiltIn,
    Invalid,
    DuplicateName,
    DestinationOutOfScope,
    Conflict,
    Forbidden,
}

/// <summary>What happened to one device in a membership change.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<GroupDeviceOutcome>))]
public enum GroupDeviceOutcome
{
    Moved,
    AlreadyInGroup,
    NotInGroup,
    NotFound,
    ChangedConcurrently,
}

/// <summary>One device's outcome, with the group it came from when that is known and visible.</summary>
public sealed record GroupDeviceResult(Guid DeviceId, string? Hostname, GroupDeviceOutcome Outcome, Guid? PreviousGroupId);

public sealed record GroupCreateResult(
    GroupChangeStatus Status, Guid? GroupId, string? Error, IReadOnlyList<GroupDeviceResult> Devices)
{
    public static GroupCreateResult Created(Guid id, IReadOnlyList<GroupDeviceResult> devices) =>
        new(GroupChangeStatus.Ok, id, null, devices);

    public static GroupCreateResult Invalid(string error) => new(GroupChangeStatus.Invalid, null, error, []);

    public static GroupCreateResult Forbidden() =>
        new(GroupChangeStatus.Forbidden, null, "Creating a group requires all-device scope.", []);

    public static GroupCreateResult Duplicate() =>
        new(GroupChangeStatus.DuplicateName, null, "A group with that name already exists.", []);
}

public sealed record GroupDeleteResult(GroupChangeStatus Status, int DevicesMoved)
{
    public static GroupDeleteResult Deleted(int moved) => new(GroupChangeStatus.Ok, moved);

    public static GroupDeleteResult Of(GroupChangeStatus status) => new(status, 0);
}

public sealed record GroupMembershipResult(GroupChangeStatus Status, IReadOnlyList<GroupDeviceResult> Devices)
{
    public static GroupMembershipResult Done(IReadOnlyList<GroupDeviceResult> devices) => new(GroupChangeStatus.Ok, devices);

    public static GroupMembershipResult Of(GroupChangeStatus status) => new(status, []);
}
