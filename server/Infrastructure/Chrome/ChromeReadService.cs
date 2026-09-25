using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Chrome;

/// <summary>
/// Read side of Chrome Management: what the fleet reported about Chrome, by
/// group, by device and by profile. Read-only in this phase; nothing here
/// touches an endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Group membership is the Groups page's, not a Chrome one.</b> "All Devices"
/// is a real built-in group row, and a device belongs to exactly one group
/// (<see cref="Device.DeviceGroupId"/>), so the built-in group lists only devices
/// in no custom group and a custom group lists only its members. The membership
/// predicate is restated from <c>DeviceGroupService</c> verbatim rather than
/// widened: a Chrome view that showed a device in two groups would contradict the
/// page that administrators use to decide who may act on it.
/// </para>
/// <para>
/// <b>Out of scope reads as not found.</b> Every device- and group-scoped read
/// answers null, which the endpoint turns into 404, so an administrator who cannot
/// reach a device does not learn that it exists. Fleet counts narrow instead of
/// refusing, for the same reason.
/// </para>
/// <para>
/// <b>Nothing is fabricated.</b> Whether an installation is current cannot be
/// computed until a version reference exists, so the update status is
/// <see cref="UpdateStatusUnknown"/> and the fleet count of devices with an update
/// available is null -- never zero, which would read as "everything is current".
/// </para>
/// </remarks>
public sealed class ChromeReadService(
    EndpointPlatformDbContext dbContext,
    DeviceScopeAuthorizer scope,
    TimeProvider timeProvider,
    IOptions<AgentServerOptions> agentServerOptions)
{
    /// <summary>
    /// The update status every installation carries until a later phase can
    /// compare the reported version against a reference. A string rather than an
    /// enum so the later phase adds values without changing this contract.
    /// </summary>
    public const string UpdateStatusUnknown = "Unknown";

    /// <summary>
    /// Bounds one group listing. The Groups page lists a group unpaged, and this
    /// view mirrors it; the ceiling exists so a pathological group cannot turn a
    /// read into an unbounded response, not to page.
    /// </summary>
    public const int MaxDevicesPerGroupListing = 5000;

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly DeviceScopeAuthorizer _scope = scope
        ?? throw new ArgumentNullException(nameof(scope));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(
        (agentServerOptions ?? throw new ArgumentNullException(nameof(agentServerOptions))).Value.OfflineAfterSeconds);

    // --------------------------------------------------------------- overview

    /// <summary>
    /// Fleet-wide counts, narrowed to what the caller may see. Groups follow the
    /// caller's group scope and devices their device scope, exactly as the Groups
    /// and Devices pages narrow theirs.
    /// </summary>
    public async Task<ChromeOverview> GetOverviewAsync(
        Guid organizationId, Guid actorId, CancellationToken cancellationToken = default)
    {
        var visibleGroups = await _scope.ScopedGroupIdsOrNullAsync(actorId, organizationId, cancellationToken);
        var scopedDevices = await _scope.ScopedDeviceIdsOrNullAsync(actorId, organizationId, cancellationToken);
        var onlineSince = _timeProvider.GetUtcNow() - _staleAfter;

        var groups = _dbContext.DeviceGroups.AsNoTracking().Where(g => g.OrganizationId == organizationId);
        if (visibleGroups is not null)
        {
            groups = groups.Where(g => visibleGroups.Contains(g.Id));
        }

        var devices = ActiveDevices(organizationId);
        if (scopedDevices is not null)
        {
            devices = devices.Where(d => scopedDevices.Contains(d.Id));
        }

        var totalGroups = await groups.CountAsync(cancellationToken);
        var totalDevices = await devices.CountAsync(cancellationToken);
        var onlineDevices = await devices.CountAsync(
            d => d.LastSeenAt != null && d.LastSeenAt >= onlineSince, cancellationToken);

        // "Reporting" is the existence of the installation row, whatever it says:
        // a device that reported NotInstalled has still reported. Only a device
        // whose agent predates the section has no row.
        var reportingChrome = await devices.CountAsync(
            d => _dbContext.ChromeInstallations.Any(i => i.DeviceId == d.Id), cancellationToken);
        // "With Chrome" is the entity's own rule, ChromeInstallation.IsInstalled,
        // restated in SQL because a computed property does not translate:
        // Available AND a version. The agent reports Available with no entry when
        // it found an installation whose version it could not read; that device
        // has reported, and counts above, but the console cannot say Chrome is
        // present on it, so it does not count here.
        var withChrome = await devices.CountAsync(
            d => _dbContext.ChromeInstallations.Any(i =>
                i.DeviceId == d.Id && i.Status == ChromeReportStatus.Available && i.Version != null),
            cancellationToken);

        // Profile and extension rows are joined back to the scoped device set
        // rather than counted by organization, so a scoped administrator's totals
        // describe only the devices they can open.
        var totalProfiles = await _dbContext.ChromeProfiles.AsNoTracking()
            .CountAsync(p => devices.Any(d => d.Id == p.DeviceId), cancellationToken);
        var totalExtensions = await NonComponentExtensions()
            .CountAsync(e => devices.Any(d => d.Id == e.DeviceId), cancellationToken);

        return new ChromeOverview(
            totalGroups,
            totalDevices,
            onlineDevices,
            reportingChrome,
            withChrome,
            totalProfiles,
            totalExtensions,
            // Not computable until the update reference exists. Null, never 0: a
            // zero here would tell an operator that every installation is current.
            DevicesWithUpdatesAvailable: null);
    }

    // ------------------------------------------------------------------ group

    /// <summary>
    /// One group's active devices with their Chrome summary, or null when the
    /// caller may not see the group. Membership is the Groups page predicate.
    /// </summary>
    public async Task<ChromeGroupDevices?> ListGroupDevicesAsync(
        Guid organizationId, Guid actorId, Guid groupId, string? search,
        CancellationToken cancellationToken = default)
    {
        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, groupId, cancellationToken))
        {
            return null;
        }

        var group = await _dbContext.DeviceGroups.AsNoTracking()
            .Where(g => g.Id == groupId && g.OrganizationId == organizationId)
            .Select(g => new { g.Id, g.Name, g.IsBuiltIn })
            .SingleOrDefaultAsync(cancellationToken);

        if (group is null)
        {
            return null;
        }

        // The Groups page predicate, verbatim. DeviceGroupId is the single group a
        // device is in, so the built-in group lists exactly the devices that are in
        // no custom group.
        var members = _dbContext.Devices.AsNoTracking()
            .Where(d => d.DeviceGroupId == groupId && d.OrganizationId == organizationId && d.Status == DeviceStatus.Active);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Escaped so the LIKE wildcards in what an operator types match
            // literally, as the device list search does.
            var escaped = search.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
            members = members.Where(d =>
                EF.Functions.ILike(d.Hostname, $"%{escaped}%", @"\")
                || (d.DisplayName != null && EF.Functions.ILike(d.DisplayName, $"%{escaped}%", @"\")));
        }

        var now = _timeProvider.GetUtcNow();
        var onlineSince = now - _staleAfter;

        // Left join: a device that has never reported the section is still a
        // member of the group and is listed with no Chrome facts at all, so the
        // console can tell "no data" from "no Chrome".
        var rows = await (
            from d in members
            join i in _dbContext.ChromeInstallations.AsNoTracking() on d.Id equals i.DeviceId into installations
            from i in installations.DefaultIfEmpty()
            orderby d.Hostname
            select new
            {
                d.Id,
                d.Hostname,
                d.DisplayName,
                d.LastSeenAt,
                Installation = i,
                ProfileCount = _dbContext.ChromeProfiles.Count(p => p.DeviceId == d.Id),
                ExtensionCount = _dbContext.ChromeExtensions.Count(e =>
                    e.DeviceId == d.Id
                    && e.InstallType != ChromeExtensionInstallType.Component
                    && e.InstallType != ChromeExtensionInstallType.ExternalComponent),
            })
            .Take(MaxDevicesPerGroupListing)
            .ToListAsync(cancellationToken);

        var devices = rows
            .Select(r => new ChromeDeviceRow(
                r.Id,
                r.Hostname,
                r.DisplayName,
                IsOnline: r.LastSeenAt is { } seen && seen >= onlineSince,
                r.LastSeenAt,
                ChromeStatus: r.Installation?.Status.ToString(),
                ChromeVersion: r.Installation?.Version,
                Channel: r.Installation?.Channel,
                r.ProfileCount,
                r.ExtensionCount,
                UpdateStatus: UpdateStatusUnknown,
                CollectedAt: r.Installation?.CollectedAt))
            .ToList();

        return new ChromeGroupDevices(group.Id, group.Name, group.IsBuiltIn, devices);
    }

    // ----------------------------------------------------------------- device

    /// <summary>
    /// One device's Chrome section: the installation as last reported and its
    /// profiles with extension counts. Null when the caller may not reach the
    /// device or it is not in their organization.
    /// </summary>
    public async Task<ChromeDeviceDetail?> GetDeviceAsync(
        Guid organizationId, Guid actorId, Guid deviceId, CancellationToken cancellationToken = default)
    {
        if (!await _scope.CanActOnDeviceAsync(actorId, organizationId, deviceId, cancellationToken))
        {
            return null;
        }

        var device = await _dbContext.Devices.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == deviceId && d.OrganizationId == organizationId, cancellationToken);

        if (device is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();

        var installation = await _dbContext.ChromeInstallations.AsNoTracking()
            .SingleOrDefaultAsync(i => i.DeviceId == deviceId, cancellationToken);

        var profiles = await _dbContext.ChromeProfiles.AsNoTracking()
            .Where(p => p.DeviceId == deviceId)
            .Select(p => new
            {
                Profile = p,
                ExtensionCount = _dbContext.ChromeExtensions.Count(e =>
                    e.ChromeProfileId == p.Id
                    && e.InstallType != ChromeExtensionInstallType.Component
                    && e.InstallType != ChromeExtensionInstallType.ExternalComponent),
                // Also without components, so the managed count can never exceed
                // the extension count it sits beside on the console.
                ManagedExtensionCount = _dbContext.ChromeExtensions.Count(e =>
                    e.ChromeProfileId == p.Id
                    && e.IsManaged
                    && e.InstallType != ChromeExtensionInstallType.Component
                    && e.InstallType != ChromeExtensionInstallType.ExternalComponent),
            })
            .Take(InventoryChrome.MaxProfiles)
            .ToListAsync(cancellationToken);

        var profileRows = profiles
            .Select(r => new ChromeProfileRow(
                r.Profile.Id,
                r.Profile.UserSid,
                r.Profile.UserAccount,
                r.Profile.ProfileKey,
                r.Profile.ProfileName,
                r.Profile.ProfilePath,
                r.Profile.IsManaged,
                r.Profile.LastActiveAt,
                r.ExtensionCount,
                r.ManagedExtensionCount,
                r.Profile.CollectedAt))
            .OrderBy(r => r.UserAccount, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ProfileKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ChromeDeviceDetail(
            device.Id,
            device.Hostname,
            device.DisplayName,
            IsOnline: device.IsOnline(now, _staleAfter),
            InventoryRefreshPending: device.IsInventoryRefreshPending,
            Installation: installation is null
                ? null
                : new ChromeInstallationView(
                    installation.Status.ToString(),
                    installation.Version,
                    installation.ExecutablePath,
                    installation.Architecture,
                    installation.Channel,
                    installation.InstallationScope,
                    installation.InstalledForUser,
                    installation.UpdaterVersion,
                    installation.LastUpdateCheck,
                    UpdateStatus: UpdateStatusUnknown,
                    installation.CollectedAt),
            profileRows);
    }

    // ---------------------------------------------------------------- profile

    /// <summary>
    /// The extensions Chrome recorded for one profile. Null when the caller may not
    /// reach the device, or when the profile is not that device's: a profile id
    /// under the wrong device is answered exactly as a profile that does not exist.
    /// </summary>
    public async Task<IReadOnlyList<ChromeExtensionRow>?> ListProfileExtensionsAsync(
        Guid organizationId, Guid actorId, Guid deviceId, Guid profileId,
        CancellationToken cancellationToken = default)
    {
        if (!await _scope.CanActOnDeviceAsync(actorId, organizationId, deviceId, cancellationToken))
        {
            return null;
        }

        var profileBelongsToDevice = await _dbContext.ChromeProfiles.AsNoTracking()
            .AnyAsync(p => p.Id == profileId && p.DeviceId == deviceId, cancellationToken);

        if (!profileBelongsToDevice)
        {
            return null;
        }

        var extensions = await _dbContext.ChromeExtensions.AsNoTracking()
            .Where(e => e.ChromeProfileId == profileId && e.DeviceId == deviceId)
            .Take(InventoryChromeProfile.MaxExtensions)
            .ToListAsync(cancellationToken);

        // Components last: they are Chrome's own, and an operator opening a profile
        // is looking for what somebody installed. Nameless rows after named ones.
        return extensions
            .Select(e => new ChromeExtensionRow(
                e.Id,
                e.ExtensionId,
                e.Name,
                e.Version,
                e.ManifestVersion,
                e.Enabled,
                e.InstallType.ToString(),
                e.IsManaged,
                e.IsComponent,
                e.FromWebStore,
                e.UpdateUrl,
                e.InstalledAt,
                // Chrome's own last-update time, not the row's persistence stamp.
                UpdatedAt: e.ExtensionUpdatedAt))
            .OrderBy(r => r.IsComponent)
            .ThenBy(r => r.Name is null)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ExtensionId, StringComparer.Ordinal)
            .ToList();
    }

    // ---------------------------------------------------------------- helpers

    private IQueryable<Device> ActiveDevices(Guid organizationId) =>
        _dbContext.Devices.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.Status == DeviceStatus.Active);

    /// <summary>
    /// Extension rows that count as "extensions" on the console. Filtered on the
    /// install type in SQL because <see cref="ChromeExtension.IsComponent"/> is a
    /// computed property and does not translate.
    /// </summary>
    private IQueryable<ChromeExtension> NonComponentExtensions() =>
        _dbContext.ChromeExtensions.AsNoTracking()
            .Where(e => e.InstallType != ChromeExtensionInstallType.Component
                        && e.InstallType != ChromeExtensionInstallType.ExternalComponent);
}

/// <summary>Fleet-wide Chrome counts, narrowed to the caller's scope.</summary>
/// <param name="TotalGroups">Groups the caller may see.</param>
/// <param name="TotalDevices">Active devices in the caller's scope.</param>
/// <param name="OnlineDevices">Of those, the ones heard from within the offline threshold.</param>
/// <param name="DevicesReportingChrome">Devices that have reported the Chrome section at all, whatever it said.</param>
/// <param name="DevicesWithChrome">Devices whose last report found Chrome installed and named its version: <see cref="ChromeInstallation.IsInstalled"/>, restated in SQL.</param>
/// <param name="TotalProfiles">Chrome profiles across the scoped devices.</param>
/// <param name="TotalExtensions">Extensions across the scoped devices, without Chrome's own components.</param>
/// <param name="DevicesWithUpdatesAvailable">Null until a later phase can compute it; never 0 in its place.</param>
public sealed record ChromeOverview(
    int TotalGroups,
    int TotalDevices,
    int OnlineDevices,
    int DevicesReportingChrome,
    int DevicesWithChrome,
    int TotalProfiles,
    int TotalExtensions,
    int? DevicesWithUpdatesAvailable);

/// <summary>One group's active devices with their Chrome summary, ordered by hostname.</summary>
public sealed record ChromeGroupDevices(
    Guid GroupId,
    string GroupName,
    bool IsBuiltIn,
    IReadOnlyList<ChromeDeviceRow> Devices);

/// <summary>
/// One device in a group listing. The Chrome fields are null when the device has
/// never reported the section; the counts are then 0 because there is nothing to
/// count, not because Chrome is absent.
/// </summary>
public sealed record ChromeDeviceRow(
    Guid DeviceId,
    string Hostname,
    string? DisplayName,
    bool IsOnline,
    DateTimeOffset? LastSeenAt,
    string? ChromeStatus,
    string? ChromeVersion,
    string? Channel,
    int ProfileCount,
    int ExtensionCount,
    string UpdateStatus,
    DateTimeOffset? CollectedAt);

/// <summary>One device's Chrome section as last reported.</summary>
public sealed record ChromeDeviceDetail(
    Guid DeviceId,
    string Hostname,
    string? DisplayName,
    bool IsOnline,
    bool InventoryRefreshPending,
    ChromeInstallationView? Installation,
    IReadOnlyList<ChromeProfileRow> Profiles);

/// <summary>The installation row as the console reads it. Null fields were not recorded.</summary>
public sealed record ChromeInstallationView(
    string Status,
    string? Version,
    string? ExecutablePath,
    string? Architecture,
    string? Channel,
    string? InstallationScope,
    string? InstalledForUser,
    string? UpdaterVersion,
    DateTimeOffset? LastUpdateCheck,
    string UpdateStatus,
    DateTimeOffset CollectedAt);

/// <summary>One Chrome profile on a device with its extension counts (components excluded).</summary>
public sealed record ChromeProfileRow(
    Guid ProfileId,
    string UserSid,
    string? UserAccount,
    string ProfileKey,
    string? ProfileName,
    string ProfilePath,
    bool? IsManaged,
    DateTimeOffset? LastActiveAt,
    int ExtensionCount,
    int ManagedExtensionCount,
    DateTimeOffset CollectedAt);

/// <summary>
/// One extension in one profile, as Chrome recorded it. <paramref name="ExtensionRowId"/>
/// is this platform's row; <paramref name="ExtensionId"/> is Chrome's identifier,
/// which is what an operator searches the fleet for. <paramref name="UpdateUrl"/>
/// is agent-reported text bounded by length only: it is for display, is not
/// validated as a URL, and must never be rendered as a link target.
/// </summary>
public sealed record ChromeExtensionRow(
    Guid ExtensionRowId,
    string ExtensionId,
    string? Name,
    string? Version,
    int? ManifestVersion,
    bool? Enabled,
    string InstallType,
    bool IsManaged,
    bool IsComponent,
    bool? FromWebStore,
    string? UpdateUrl,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? UpdatedAt);
