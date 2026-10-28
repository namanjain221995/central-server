using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Software;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Software;

/// <summary>
/// The administrator's side of the Company Portal: which approved packages are
/// offered to end users, how they are presented, and to whom.
/// </summary>
/// <remarks>
/// <para>
/// Every change is audited under <c>software.catalog.*</c> and requires
/// <c>software.deploy</c>. Publishing an app is, in effect, pre-approving an
/// install on every device that can see it, which is the same authority as
/// deploying the package -- so it is gated by the same permission.
/// </para>
/// <para>
/// <b>And by the same scope.</b> An entry's visibility is the set of devices that
/// may install it as SYSTEM, so a group-scoped administrator may only offer an app
/// to groups they hold, may never choose "all devices", and may not change, publish,
/// unpublish or delete an entry that already reaches beyond their groups. Every
/// write takes the caller's scoped group ids (null for all-device scope, which
/// changes nothing) and refuses with <see cref="CatalogAppStatus.OutOfScope"/>,
/// the catalog's equivalent of the 403 a scoped administrator gets for creating a
/// device group. Entries and their group ids are already visible to every
/// <c>software.view</c> holder, so the refusal reveals nothing the list does not.
/// </para>
/// </remarks>
public sealed class CatalogAppService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly AuditWriter _auditWriter = auditWriter;

    public async Task<IReadOnlyList<CatalogAppSummary>> ListAsync(
        Guid organizationId, CancellationToken cancellationToken = default)
    {
        var rows = await (
            from app in _dbContext.CatalogApps.AsNoTracking()
            join package in _dbContext.SoftwarePackages.AsNoTracking() on app.PackageId equals package.Id
            where app.OrganizationId == organizationId
            orderby app.DisplayName
            select new
            {
                app.Id,
                app.PackageId,
                package.Name,
                package.Version,
                package.IsWithdrawn,
                app.DisplayName,
                app.Description,
                app.Category,
                HasIcon = app.Icon != null,
                app.Visibility,
                app.IsPublished,
                app.CreatedByDisplay,
                app.PublishedByDisplay,
                app.CreatedAt,
                app.UpdatedAt,
            })
            .ToListAsync(cancellationToken);

        var appIds = rows.Select(r => r.Id).ToList();
        var groups = await _dbContext.CatalogAppGroups.AsNoTracking()
            .Where(g => appIds.Contains(g.CatalogAppId))
            .Select(g => new { g.CatalogAppId, g.DeviceGroupId })
            .ToListAsync(cancellationToken);

        var groupsByApp = groups
            .GroupBy(g => g.CatalogAppId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(x => x.DeviceGroupId).ToList());

        return rows.Select(r => new CatalogAppSummary(
                r.Id, r.PackageId, r.Name, r.Version, r.IsWithdrawn, r.DisplayName, r.Description, r.Category,
                r.HasIcon, r.Visibility, groupsByApp.TryGetValue(r.Id, out var ids) ? ids : [],
                r.IsPublished, r.CreatedByDisplay, r.PublishedByDisplay, r.CreatedAt, r.UpdatedAt))
            .ToList();
    }

    public async Task<CatalogAppResult> CreateAsync(
        Guid organizationId,
        CatalogAppInput input,
        bool publish,
        IReadOnlyCollection<Guid>? scopedGroupIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        if (InputOutsideScope(input, scopedGroupIds) is { } refused)
        {
            return refused;
        }

        // Any package of the organization, withdrawn or not: an administrator
        // may set an entry up ahead of restoring the package. The portal itself
        // never offers a withdrawn package, whatever the catalog says.
        var package = await _dbContext.SoftwarePackages.AsNoTracking()
            .Where(p => p.Id == input.PackageId && p.OrganizationId == organizationId)
            .Select(p => new { p.Id, p.Name, p.Version, p.IsWithdrawn })
            .SingleOrDefaultAsync(cancellationToken);
        if (package is null)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.PackageNotFound, "The package does not exist.");
        }

        if (publish && package.IsWithdrawn)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.PackageWithdrawn, WithdrawnMessage);
        }

        var duplicate = await _dbContext.CatalogApps
            .AnyAsync(a => a.OrganizationId == organizationId && a.PackageId == input.PackageId, cancellationToken);
        if (duplicate)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.DuplicatePackage, "This package is already in the catalog.");
        }

        var groupIds = await ResolveGroupsAsync(organizationId, input, cancellationToken);
        if (groupIds is null)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.InvalidGroups, "Select at least one group that belongs to this organization.");
        }

        CatalogApp app;
        try
        {
            app = new CatalogApp(
                organizationId, input.PackageId, input.DisplayName, input.Description, input.Category,
                input.Visibility, actorId, actorDisplay);
        }
        catch (ArgumentException ex)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.Invalid, ex.Message);
        }

        if (publish)
        {
            app.Publish(actorId, actorDisplay);
        }

        _dbContext.CatalogApps.Add(app);
        foreach (var groupId in groupIds)
        {
            _dbContext.CatalogAppGroups.Add(new CatalogAppGroup(app.Id, groupId));
        }

        Audit(organizationId, actorId, actorDisplay, "software.catalog.create", app,
            Describe(app, groupIds, $"{package.Name} {package.Version}"));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, CatalogAppConfiguration.UniquePackageIndex))
        {
            // Two administrators adding the same package in the same instant:
            // the check above let both through, the index let one in. The
            // second gets the same answer it would have got a second later.
            return CatalogAppResult.Failed(CatalogAppStatus.DuplicatePackage, "This package is already in the catalog.");
        }

        return CatalogAppResult.Succeeded(app);
    }

    public async Task<CatalogAppResult> UpdateAsync(
        Guid organizationId,
        Guid appId,
        CatalogAppInput input,
        IReadOnlyCollection<Guid>? scopedGroupIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var app = await FindAsync(organizationId, appId, cancellationToken);
        if (app is null)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.NotFound, "The catalog app does not exist.");
        }

        // Both ends of the change must be within scope: narrowing an entry that
        // reaches other groups would withdraw it from devices the caller has no
        // authority over, and widening one would offer it to them.
        if (await AppOutsideScopeAsync(app, scopedGroupIds, cancellationToken) is { } beyond)
        {
            return beyond;
        }

        if (InputOutsideScope(input, scopedGroupIds) is { } refused)
        {
            return refused;
        }

        if (app.PackageId != input.PackageId)
        {
            // The package is the one thing an entry cannot change. "Zoom" that
            // starts installing something else tomorrow is exactly what a user
            // reading the tile cannot defend against; remove and recreate instead.
            return CatalogAppResult.Failed(CatalogAppStatus.Invalid, "The package of a catalog app cannot be changed.");
        }

        var groupIds = await ResolveGroupsAsync(organizationId, input, cancellationToken);
        if (groupIds is null)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.InvalidGroups, "Select at least one group that belongs to this organization.");
        }

        var existing = await _dbContext.CatalogAppGroups
            .Where(g => g.CatalogAppId == app.Id)
            .ToListAsync(cancellationToken);
        var before = Describe(app, existing.Select(g => g.DeviceGroupId).ToList(), null);

        try
        {
            app.Describe(input.DisplayName, input.Description, input.Category);
        }
        catch (ArgumentException ex)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.Invalid, ex.Message);
        }

        app.SetVisibility(input.Visibility);

        var wanted = groupIds.ToHashSet();
        _dbContext.CatalogAppGroups.RemoveRange(existing.Where(g => !wanted.Contains(g.DeviceGroupId)));
        foreach (var groupId in wanted.Except(existing.Select(g => g.DeviceGroupId)))
        {
            _dbContext.CatalogAppGroups.Add(new CatalogAppGroup(app.Id, groupId));
        }

        Audit(organizationId, actorId, actorDisplay, "software.catalog.update", app,
            Describe(app, groupIds, null), before);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return CatalogAppResult.Succeeded(app);
    }

    public async Task<CatalogAppResult> SetIconAsync(
        Guid organizationId,
        Guid appId,
        byte[]? png,
        IReadOnlyCollection<Guid>? scopedGroupIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var app = await FindAsync(organizationId, appId, cancellationToken);
        if (app is null)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.NotFound, "The catalog app does not exist.");
        }

        // The icon is what an employee trusts when choosing what to install, so
        // restyling an entry seen beyond the caller's groups is the same change of
        // someone else's catalog as editing its text.
        if (await AppOutsideScopeAsync(app, scopedGroupIds, cancellationToken) is { } beyond)
        {
            return beyond;
        }

        try
        {
            app.SetIcon(png);
        }
        catch (ArgumentException ex)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.Invalid, ex.Message);
        }

        Audit(organizationId, actorId, actorDisplay, "software.catalog.icon", app,
            JsonSerializer.Serialize(new { iconBytes = png?.Length ?? 0 }));

        await _dbContext.SaveChangesAsync(cancellationToken);

        return CatalogAppResult.Succeeded(app);
    }

    public async Task<byte[]?> GetIconAsync(
        Guid organizationId, Guid appId, CancellationToken cancellationToken = default) =>
        await _dbContext.CatalogApps.AsNoTracking()
            .Where(a => a.Id == appId && a.OrganizationId == organizationId)
            .Select(a => a.Icon)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Publishing an entry whose package is withdrawn is refused rather than
    /// recorded: the portal would hide it anyway, and "published" on the
    /// Company Apps page must mean employees can see it.
    /// </summary>
    public async Task<CatalogAppResult> SetPublishedAsync(
        Guid organizationId,
        Guid appId,
        bool published,
        IReadOnlyCollection<Guid>? scopedGroupIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var app = await FindAsync(organizationId, appId, cancellationToken);
        if (app is null)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.NotFound, "The catalog app does not exist.");
        }

        // Publishing an entry that reaches beyond the caller's groups would
        // pre-approve an install on devices they have no authority over;
        // unpublishing it would withdraw it from them.
        if (await AppOutsideScopeAsync(app, scopedGroupIds, cancellationToken) is { } beyond)
        {
            return beyond;
        }

        if (published)
        {
            var withdrawn = await _dbContext.SoftwarePackages.AsNoTracking()
                .Where(p => p.Id == app.PackageId)
                .Select(p => p.IsWithdrawn)
                .SingleAsync(cancellationToken);
            if (withdrawn)
            {
                return CatalogAppResult.Failed(CatalogAppStatus.PackageWithdrawn, WithdrawnMessage);
            }

            app.Publish(actorId, actorDisplay);
        }
        else
        {
            app.Unpublish();
        }

        Audit(organizationId, actorId, actorDisplay,
            published ? "software.catalog.publish" : "software.catalog.unpublish", app, null);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return CatalogAppResult.Succeeded(app);
    }

    /// <summary>
    /// Removes the entry. Installs already made from it are untouched, and their
    /// request history keeps the app's name.
    /// </summary>
    public async Task<CatalogAppResult> DeleteAsync(
        Guid organizationId,
        Guid appId,
        IReadOnlyCollection<Guid>? scopedGroupIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var app = await FindAsync(organizationId, appId, cancellationToken);
        if (app is null)
        {
            return CatalogAppResult.Failed(CatalogAppStatus.NotFound, "The catalog app does not exist.");
        }

        if (await AppOutsideScopeAsync(app, scopedGroupIds, cancellationToken) is { } beyond)
        {
            return beyond;
        }

        _dbContext.CatalogApps.Remove(app);

        Audit(organizationId, actorId, actorDisplay, "software.catalog.delete", app, null);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return CatalogAppResult.Succeeded(app);
    }

    /// <summary>Newest first, with the device and the task's current status.</summary>
    public async Task<CatalogInstallRequestPage> ListInstallRequestsAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query =
            from request in _dbContext.CatalogInstallRequests.AsNoTracking()
            join device in _dbContext.Devices.AsNoTracking() on request.DeviceId equals device.Id
            join task in _dbContext.DeviceTasks.AsNoTracking() on request.TaskId equals task.Id into taskJoin
            from task in taskJoin.DefaultIfEmpty()
            where request.OrganizationId == organizationId
            select new { request, device.Hostname, TaskStatus = (DeviceTaskStatus?)task.Status };

        if (scopedDeviceIds is not null)
        {
            // Materialised first: EF translates Contains on a List, not on every
            // IReadOnlyCollection a caller might hand over.
            var scoped = scopedDeviceIds.ToList();
            query = query.Where(x => scoped.Contains(x.request.DeviceId));
        }

        var total = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(x => x.request.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new CatalogInstallRequestPage(
            rows.Select(x => new CatalogInstallRequestRow(
                x.request.Id, x.request.DeviceId, x.Hostname, x.request.CatalogAppId, x.request.AppName,
                x.request.RequestedBy, x.request.Outcome, x.request.TaskId, x.TaskStatus, x.request.CreatedAt))
                .ToList(),
            total, page, pageSize);
    }

    private const string WithdrawnMessage = "The package is withdrawn. Restore it before publishing the app.";

    /// <summary>
    /// Refuses a requested visibility that reaches beyond the caller's groups, or
    /// null when it does not. Checked before the group ids are resolved, so an
    /// unknown group and another administrator's group get the same answer.
    /// </summary>
    private static CatalogAppResult? InputOutsideScope(
        CatalogAppInput input, IReadOnlyCollection<Guid>? scopedGroupIds)
    {
        if (scopedGroupIds is null)
        {
            return null;
        }

        // "All devices" is every device in the organization, not the built-in
        // "All Devices" group, so even scope over that group does not reach it.
        if (input.Visibility == CatalogVisibility.AllDevices)
        {
            return CatalogAppResult.Failed(
                CatalogAppStatus.OutOfScope, "Offering an app to all devices requires all-device scope.");
        }

        var scoped = scopedGroupIds.ToHashSet();
        return (input.GroupIds ?? []).All(scoped.Contains)
            ? null
            : CatalogAppResult.Failed(
                CatalogAppStatus.OutOfScope, "An app can only be offered to groups within your device scope.");
    }

    /// <summary>
    /// Refuses a change to an entry that is already offered beyond the caller's
    /// groups, or null when every device it reaches is theirs.
    /// </summary>
    private async Task<CatalogAppResult?> AppOutsideScopeAsync(
        CatalogApp app, IReadOnlyCollection<Guid>? scopedGroupIds, CancellationToken cancellationToken)
    {
        if (scopedGroupIds is null)
        {
            return null;
        }

        var withinScope = false;
        if (app.Visibility != CatalogVisibility.AllDevices)
        {
            var groups = await _dbContext.CatalogAppGroups.AsNoTracking()
                .Where(g => g.CatalogAppId == app.Id)
                .Select(g => g.DeviceGroupId)
                .ToListAsync(cancellationToken);

            var scoped = scopedGroupIds.ToHashSet();
            withinScope = groups.All(scoped.Contains);
        }

        return withinScope
            ? null
            : CatalogAppResult.Failed(
                CatalogAppStatus.OutOfScope,
                "This app is offered to devices outside your scope; only an administrator with authority over all of them can change it.");
    }

    private static bool IsUniqueViolation(DbUpdateException exception, string constraint) =>
        exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation } pg
        && string.Equals(pg.ConstraintName, constraint, StringComparison.Ordinal);

    private Task<CatalogApp?> FindAsync(Guid organizationId, Guid appId, CancellationToken cancellationToken) =>
        _dbContext.CatalogApps.SingleOrDefaultAsync(
            a => a.Id == appId && a.OrganizationId == organizationId, cancellationToken);

    /// <summary>
    /// The groups an entry should be linked to: none for AllDevices, the
    /// organization's own matching groups otherwise. Null means the input named a
    /// group that is not the organization's, or none at all.
    /// </summary>
    private async Task<IReadOnlyList<Guid>?> ResolveGroupsAsync(
        Guid organizationId, CatalogAppInput input, CancellationToken cancellationToken)
    {
        if (input.Visibility == CatalogVisibility.AllDevices)
        {
            return [];
        }

        var wanted = (input.GroupIds ?? []).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return null;
        }

        var found = await _dbContext.DeviceGroups.AsNoTracking()
            .Where(g => wanted.Contains(g.Id) && g.OrganizationId == organizationId)
            .Select(g => g.Id)
            .ToListAsync(cancellationToken);

        return found.Count == wanted.Count ? found : null;
    }

    private void Audit(
        Guid organizationId, Guid actorId, string actorDisplay, string action, CatalogApp app,
        string? newState, string? previousState = null) =>
        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action, AuditResult.Success,
            a => a.OnTarget("catalog_app", app.Id.ToString(), app.DisplayName)
                  .Requiring(Permissions.Software.Deploy)
                  .WithStateChange(previousState, newState));

    private static string Describe(CatalogApp app, IReadOnlyList<Guid> groupIds, string? package) =>
        JsonSerializer.Serialize(new
        {
            package,
            app.DisplayName,
            app.Category,
            visibility = app.Visibility.ToString(),
            groups = groupIds,
            app.IsPublished,
        });
}

/// <summary>Everything an administrator sets on a catalog entry, apart from the icon.</summary>
public sealed record CatalogAppInput(
    Guid PackageId,
    string DisplayName,
    string? Description,
    string? Category,
    CatalogVisibility Visibility,
    IReadOnlyCollection<Guid>? GroupIds);

public enum CatalogAppStatus
{
    Success,
    NotFound,
    PackageNotFound,
    DuplicatePackage,
    PackageWithdrawn,
    InvalidGroups,
    Invalid,

    /// <summary>
    /// The caller lacks all-device scope and the change would reach devices outside
    /// their groups. A 403, mirroring a scoped administrator creating a group.
    /// </summary>
    OutOfScope,
}

public sealed record CatalogAppResult(CatalogAppStatus Status, CatalogApp? App, string? Error)
{
    public static CatalogAppResult Succeeded(CatalogApp app) => new(CatalogAppStatus.Success, app, null);

    public static CatalogAppResult Failed(CatalogAppStatus status, string error) => new(status, null, error);
}

public sealed record CatalogAppSummary(
    Guid Id,
    Guid PackageId,
    string PackageName,
    string PackageVersion,
    bool PackageWithdrawn,
    string DisplayName,
    string? Description,
    string? Category,
    bool HasIcon,
    CatalogVisibility Visibility,
    IReadOnlyList<Guid> GroupIds,
    bool IsPublished,
    string CreatedByDisplay,
    string PublishedByDisplay,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CatalogInstallRequestRow(
    Guid Id,
    Guid DeviceId,
    string Hostname,
    Guid CatalogAppId,
    string AppName,
    string? RequestedBy,
    CatalogInstallOutcome Outcome,
    Guid? TaskId,
    DeviceTaskStatus? TaskStatus,
    DateTimeOffset RequestedAt);

public sealed record CatalogInstallRequestPage(
    IReadOnlyList<CatalogInstallRequestRow> Items, int Total, int Page, int PageSize);
