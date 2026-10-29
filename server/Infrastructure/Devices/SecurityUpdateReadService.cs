using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Reads the pending Windows Update security updates: the fleet overview of which
/// machines have updates waiting, and one machine's pending list -- each joined with
/// what the platform remembers about installing them.
/// </summary>
/// <remarks>
/// <para>
/// Both reads are scoped. A scoped administrator is handed the device ids they may
/// see (null means "all"), and the overview is computed over those devices only, so
/// the page can never name a machine outside the caller's scope -- the same rule the
/// announcement history and the deployment reads follow.
/// </para>
/// <para>
/// "Pending" in every count is ACTIONABLE pending: offered and not hidden by an
/// operator. Unlike drivers, a security update Windows Update re-offered after an
/// install STAYS actionable: the usual cause is a cumulative update that rolled back
/// during the restart ("undoing changes"), which leaves the machine unpatched and is
/// usually fixed by a retry -- so it keeps counting, and install-all keeps retrying
/// it. "Cannot complete" (re-offered or hidden, each update once) is the separate
/// "needs attention" signal; it overlaps with pending on purpose, for the re-offered
/// ones, which are both still to do and worth a look. The per-update
/// <c>installStatus</c> still says "reOffered" so the console can warn.
/// </para>
/// <para>
/// Reboot-pending prefers the security-install memory -- any install waiting for a
/// restart, the usual state of an installed cumulative update -- and falls back to
/// the general Windows reboot-required flag the last inventory reported
/// (<see cref="DeviceUpdateStatus.RebootRequired"/>), which is what devices with no
/// install memory (older history, other causes) still show.
/// </para>
/// </remarks>
public sealed class SecurityUpdateReadService(
    EndpointPlatformDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<AgentServerOptions> agentServerOptions)
{
    /// <summary>How far back "installed recently" reaches.</summary>
    public static readonly TimeSpan RecentInstallWindow = DriverUpdateReadService.RecentInstallWindow;

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>The console's one definition of offline: no heartbeat for this long.</summary>
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);

    /// <summary>
    /// Every active device that has at least one pending security update or an
    /// install waiting for a restart, with its counts, most actionable updates first.
    /// </summary>
    public async Task<SecurityUpdateOverview> GetOverviewAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken = default)
    {
        var visible = scopedDeviceIds?.ToList();

        var pendingQuery =
            from u in _dbContext.DeviceSecurityUpdates.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on u.DeviceId equals d.Id
            where d.OrganizationId == organizationId && d.Status == DeviceStatus.Active
            select new { u.DeviceId, u.UpdateId, d.Hostname, d.DisplayName, d.LastSeenAt, u.IsPreview, u.CollectedAt };

        // Only the two record states the overview counts. A device can need a
        // restart with nothing left offered (the install went through and only the
        // restart is owed), so these devices are listed even with no pending rows.
        var recordQuery =
            from r in _dbContext.DeviceSecurityUpdateInstallRecords.AsNoTracking()
            join d in _dbContext.Devices.AsNoTracking() on r.DeviceId equals d.Id
            where d.OrganizationId == organizationId && d.Status == DeviceStatus.Active
                  && (r.Status == SecurityUpdateInstallStatus.PendingReboot
                      || r.Status == SecurityUpdateInstallStatus.ReOffered)
            select new { r.DeviceId, r.UpdateId, r.Status, d.Hostname, d.DisplayName, d.LastSeenAt };

        if (visible is not null)
        {
            pendingQuery = pendingQuery.Where(x => visible.Contains(x.DeviceId));
            recordQuery = recordQuery.Where(x => visible.Contains(x.DeviceId));
        }

        var pendingRows = await pendingQuery.ToListAsync(cancellationToken);
        var recordRows = await recordQuery.ToListAsync(cancellationToken);

        var deviceIds = pendingRows.Select(r => r.DeviceId)
            .Concat(recordRows.Select(r => r.DeviceId))
            .Distinct()
            .ToList();

        var hiddenByDevice = (await _dbContext.DeviceSecurityUpdateSuppressions.AsNoTracking()
                .Where(s => deviceIds.Contains(s.DeviceId))
                .Select(s => new { s.DeviceId, s.UpdateId })
                .ToListAsync(cancellationToken))
            .GroupBy(s => s.DeviceId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.UpdateId).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var rebootById = await _dbContext.DeviceUpdateStatus.AsNoTracking()
            .Where(s => deviceIds.Contains(s.DeviceId))
            .Select(s => new { s.DeviceId, s.RebootRequired })
            .ToDictionaryAsync(s => s.DeviceId, s => s.RebootRequired, cancellationToken);

        var pendingByDevice = pendingRows.ToLookup(r => r.DeviceId);
        var recordsByDevice = recordRows.ToLookup(r => r.DeviceId);
        var now = _timeProvider.GetUtcNow();

        var devices = deviceIds
            .Select(id =>
            {
                var pending = pendingByDevice[id].ToList();
                var records = recordsByDevice[id].ToList();
                var hidden = hiddenByDevice.GetValueOrDefault(id) ?? [];
                var reOffered = records
                    .Where(r => r.Status == SecurityUpdateInstallStatus.ReOffered)
                    .Select(r => r.UpdateId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Actionable = offered and not hidden. A re-offered security patch is
                // still to do (a retry is usually the fix), so it stays in here.
                var actionable = pending.Where(u => !hidden.Contains(u.UpdateId)).ToList();

                // Needs attention = re-offered or hidden, each pending update once.
                // Overlaps with actionable for the re-offered ones, on purpose.
                var cannotComplete = pending.Count(u => hidden.Contains(u.UpdateId) || reOffered.Contains(u.UpdateId));
                var needsRestart = records.Count(r => r.Status == SecurityUpdateInstallStatus.PendingReboot);

                var (hostname, displayName, lastSeenAt) = pending.Count > 0
                    ? (pending[0].Hostname, pending[0].DisplayName, pending[0].LastSeenAt)
                    : (records[0].Hostname, records[0].DisplayName, records[0].LastSeenAt);

                return new SecurityUpdateDeviceSummary(
                    id,
                    hostname,
                    displayName,
                    IsOnline: lastSeenAt is { } seen && now - seen <= _staleAfter,
                    lastSeenAt,
                    // Actionable: everything offered except what an operator hid --
                    // re-offered included. Hidden is counted only in CannotCompleteCount;
                    // re-offered is counted in both.
                    PendingCount: actionable.Count,
                    PreviewCount: actionable.Count(u => u.IsPreview),
                    LastScanAt: pending.Count > 0 ? pending.Max(u => u.CollectedAt) : null,
                    RebootPending: needsRestart > 0 || rebootById.GetValueOrDefault(id),
                    NeedsRestartCount: needsRestart,
                    CannotCompleteCount: cannotComplete);
            })
            // A re-offered record with nothing pending (a list not yet reconciled)
            // has nothing to show; only offers and owed restarts earn a row.
            .Where(d => d.PendingCount + d.CannotCompleteCount + d.NeedsRestartCount > 0)
            .OrderByDescending(d => d.PendingCount - d.PreviewCount)
            .ThenByDescending(d => d.PendingCount)
            .ThenByDescending(d => d.NeedsRestartCount)
            .ThenBy(d => d.Hostname)
            .ToList();

        var summary = new SecurityUpdateSummary(
            DevicesWithPending: devices.Count(d => d.PendingCount > 0),
            TotalPending: devices.Sum(d => d.PendingCount),
            DevicesRebootPending: devices.Count(d => d.RebootPending),
            NeedsRestartCount: devices.Sum(d => d.NeedsRestartCount),
            CannotCompleteCount: devices.Sum(d => d.CannotCompleteCount));

        return new SecurityUpdateOverview(summary, devices);
    }

    /// <summary>
    /// One device's pending security updates, non-preview first then by title, each
    /// with what the platform knows about installing it; plus the install in flight,
    /// the updates an operator hid, and what installed recently. The caller has
    /// already confirmed the device is in scope.
    /// </summary>
    public async Task<DeviceSecurityUpdateList> GetForDeviceAsync(
        Guid deviceId, CancellationToken cancellationToken = default)
    {
        var rows = await _dbContext.DeviceSecurityUpdates.AsNoTracking()
            .Where(u => u.DeviceId == deviceId)
            .OrderBy(u => u.IsPreview)
            .ThenBy(u => u.Title)
            .ToListAsync(cancellationToken);

        var records = (await _dbContext.DeviceSecurityUpdateInstallRecords.AsNoTracking()
                .Where(r => r.DeviceId == deviceId)
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.UpdateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var suppressions = await _dbContext.DeviceSecurityUpdateSuppressions.AsNoTracking()
            .Where(s => s.DeviceId == deviceId)
            .OrderBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);

        // Installs still waiting to be claimed or running, newest first. This is
        // what makes "installing" survive a page reload: it is read from the task
        // table, not remembered by whichever browser queued it.
        var inFlight = await _dbContext.DeviceTasks.AsNoTracking()
            .Where(t => t.DeviceId == deviceId
                        && t.Type == DeviceTaskType.InstallSecurityUpdate
                        && (t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Delivered))
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new { t.Id, t.Status, t.PayloadJson, t.CreatedAt })
            .ToListAsync(cancellationToken);

        var inventoryReboot = await _dbContext.DeviceUpdateStatus.AsNoTracking()
            .Where(s => s.DeviceId == deviceId)
            .Select(s => (bool?)s.RebootRequired)
            .SingleOrDefaultAsync(cancellationToken) ?? false;

        var installing = inFlight
            .SelectMany(t => SecurityUpdateInstallTasks.UpdateIdsOf(t.PayloadJson))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var activeInstall = inFlight.FirstOrDefault() is { } newest
            ? new SecurityUpdateActiveInstall(
                newest.Id, newest.Status.ToString(), SecurityUpdateInstallTasks.UpdateIdsOf(newest.PayloadJson), newest.CreatedAt)
            : null;

        var hiddenIds = suppressions.Select(s => s.UpdateId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var updates = rows
            .Where(u => !hiddenIds.Contains(u.UpdateId))
            .Select(u =>
            {
                // A record speaks for this row only at the revision it attempted (or
                // lower); a higher revision is a new offer with no history of its own.
                var record = records.GetValueOrDefault(u.UpdateId) is { } r
                             && (r.Revision is null || u.Revision <= r.Revision)
                    ? r
                    : null;

                return new SecurityUpdateRow(
                    u.UpdateId,
                    u.Revision,
                    u.Title,
                    u.KbIds,
                    u.Severity,
                    u.IsPreview,
                    u.SizeBytes,
                    InstallStatus: installing.Contains(u.UpdateId)
                        ? SecurityUpdateItemStatus.Installing
                        : SecurityUpdateItemStatus.Of(record?.Status),
                    Attempts: record?.Attempts ?? 0,
                    LastAttemptAt: record?.LastAttemptAt,
                    LastHresult: record?.LastHresult);
            })
            .ToList();

        var hidden = suppressions
            .Select(s => new SecurityUpdateHiddenItem(s.UpdateId, s.Title, s.Reason, s.CreatedByDisplay, s.CreatedAt))
            .ToList();

        var since = _timeProvider.GetUtcNow() - RecentInstallWindow;
        var recentInstalls = records.Values
            .Where(r => r.Status == SecurityUpdateInstallStatus.Installed && r.ConfirmedAt is { } at && at >= since)
            .OrderByDescending(r => r.ConfirmedAt)
            .Select(r => new SecurityUpdateRecentInstall(r.UpdateId, r.Title, r.ConfirmedAt!.Value))
            .ToList();

        // The rows all carry the same scan time; null when nothing is pending, which
        // the console reads as "no pending updates" rather than a scan age.
        var lastScanAt = rows.Count > 0 ? rows.Max(u => u.CollectedAt) : (DateTimeOffset?)null;

        var rebootPending = records.Values.Any(r => r.Status == SecurityUpdateInstallStatus.PendingReboot)
            || inventoryReboot;

        return new DeviceSecurityUpdateList(lastScanAt, rebootPending, updates, activeInstall, hidden, recentInstalls);
    }
}

/// <summary>
/// The install state of one pending update as the console shows it -- the wire
/// strings of <see cref="SecurityUpdateRow.InstallStatus"/>, the same strings the
/// driver list uses.
/// </summary>
public static class SecurityUpdateItemStatus
{
    /// <summary>Never attempted, or nothing to say about the offer currently pending.</summary>
    public const string None = DriverUpdateItemStatus.None;

    /// <summary>Named by an install task still queued or running.</summary>
    public const string Installing = DriverUpdateItemStatus.Installing;

    /// <summary>Windows reported success; the scan that checks whether it stuck has not answered yet.</summary>
    public const string Verifying = DriverUpdateItemStatus.Verifying;

    /// <summary>Windows reported success and wants a restart to finish it.</summary>
    public const string PendingReboot = DriverUpdateItemStatus.PendingReboot;

    /// <summary>Windows reported success and offered it again: this channel cannot complete it.</summary>
    public const string ReOffered = DriverUpdateItemStatus.ReOffered;

    /// <summary>The last attempt failed; the HRESULT says why.</summary>
    public const string Failed = DriverUpdateItemStatus.Failed;

    /// <summary>
    /// The console state for a remembered install. A record that says Installed
    /// while the update is pending again at a higher revision has nothing to add
    /// about the new offer, so it reads as none.
    /// </summary>
    public static string Of(SecurityUpdateInstallStatus? status) => status switch
    {
        SecurityUpdateInstallStatus.InstalledUnverified => Verifying,
        SecurityUpdateInstallStatus.PendingReboot => PendingReboot,
        SecurityUpdateInstallStatus.ReOffered => ReOffered,
        SecurityUpdateInstallStatus.Failed => Failed,
        _ => None,
    };
}

/// <summary>One device in the security-update overview, with its counts.</summary>
/// <param name="PendingCount">
/// Actionable pending updates: offered and not hidden. Re-offered updates ARE
/// counted -- for a security patch a retry is usually the fix (unlike drivers).
/// </param>
/// <param name="PreviewCount">Of the actionable ones, how many are preview updates.</param>
/// <param name="RebootPending">
/// Any security install waiting for a restart, or else the general Windows
/// reboot-required flag from the last inventory.
/// </param>
/// <param name="NeedsRestartCount">Security installs waiting for a restart to finish.</param>
/// <param name="CannotCompleteCount">
/// "Needs attention": pending updates that are hidden or that Windows re-offered
/// after an install -- each counted once even when both. Overlaps with
/// <paramref name="PendingCount"/> for the re-offered ones, on purpose.
/// </param>
public sealed record SecurityUpdateDeviceSummary(
    Guid DeviceId,
    string Hostname,
    string? DisplayName,
    bool IsOnline,
    DateTimeOffset? LastSeenAt,
    int PendingCount,
    int PreviewCount,
    DateTimeOffset? LastScanAt,
    bool RebootPending,
    int NeedsRestartCount,
    int CannotCompleteCount);

/// <summary>Fleet totals across the overview's devices.</summary>
/// <param name="DevicesWithPending">Devices with at least one actionable pending update.</param>
/// <param name="TotalPending">Actionable (not hidden, re-offered included) pending updates across the fleet.</param>
/// <param name="DevicesRebootPending">Devices whose overview row says a restart is pending.</param>
/// <param name="NeedsRestartCount">Security installs waiting for a restart, fleet-wide.</param>
/// <param name="CannotCompleteCount">
/// Hidden or re-offered pending updates, fleet-wide -- overlapping with
/// <paramref name="TotalPending"/> for the re-offered ones.
/// </param>
public sealed record SecurityUpdateSummary(
    int DevicesWithPending,
    int TotalPending,
    int DevicesRebootPending,
    int NeedsRestartCount,
    int CannotCompleteCount);

public sealed record SecurityUpdateOverview(
    SecurityUpdateSummary Summary, IReadOnlyList<SecurityUpdateDeviceSummary> Devices);

/// <summary>One device's pending security updates and the state the console shows around them.</summary>
/// <param name="RebootPending">
/// Any security install waiting for a restart, or else the inventory's reboot-required flag.
/// </param>
/// <param name="Updates">Pending updates the operator can act on -- hidden ones are in <paramref name="Hidden"/>.</param>
/// <param name="ActiveInstall">The newest install task still queued or running, or null.</param>
/// <param name="Hidden">Updates an operator hid on this device.</param>
/// <param name="RecentInstalls">Installs a scan confirmed in the last seven days.</param>
public sealed record DeviceSecurityUpdateList(
    DateTimeOffset? LastScanAt,
    bool RebootPending,
    IReadOnlyList<SecurityUpdateRow> Updates,
    SecurityUpdateActiveInstall? ActiveInstall,
    IReadOnlyList<SecurityUpdateHiddenItem> Hidden,
    IReadOnlyList<SecurityUpdateRecentInstall> RecentInstalls);

/// <summary>One pending security update as the per-device list shows it.</summary>
/// <param name="InstallStatus">One of the <see cref="SecurityUpdateItemStatus"/> strings.</param>
/// <param name="Attempts">How many install results have named this update on this device.</param>
/// <param name="LastAttemptAt">When the last attempt ran, or null if never attempted.</param>
/// <param name="LastHresult">The HRESULT Windows reported on the last attempt, when it reported one.</param>
public sealed record SecurityUpdateRow(
    string UpdateId,
    int Revision,
    string Title,
    string? KbIds,
    string? Severity,
    bool IsPreview,
    long? SizeBytes,
    string InstallStatus,
    int Attempts,
    DateTimeOffset? LastAttemptAt,
    int? LastHresult);

/// <summary>The install task in flight on a device: what it names and since when.</summary>
/// <param name="Status">"Queued" (waiting for the device) or "Delivered" (running on it).</param>
/// <param name="QueuedAt">When the install was queued.</param>
public sealed record SecurityUpdateActiveInstall(
    Guid TaskId, string Status, IReadOnlyList<string> UpdateIds, DateTimeOffset QueuedAt);

/// <summary>An update an operator hid on a device, with the reason they had to give.</summary>
/// <param name="HiddenBy">Who hid it, as they were recorded at the time.</param>
public sealed record SecurityUpdateHiddenItem(
    string UpdateId, string Title, string Reason, string HiddenBy, DateTimeOffset HiddenAt);

/// <summary>An install a scan confirmed: Windows Update no longer offers it.</summary>
public sealed record SecurityUpdateRecentInstall(string UpdateId, string Title, DateTimeOffset ConfirmedAt);
