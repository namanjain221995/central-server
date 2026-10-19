using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.ScreenLocks;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.ScreenLocks;

/// <summary>
/// Turns "lock these machines" into one screen-lock record and exactly the
/// <c>EngageScreenLock</c> tasks the targeted fleet can actually run, and lifts
/// those locks again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivery is the existing task channel, nothing new</b>, exactly as the
/// Announcements feature does it: each target gets an ordinary queued task, with a
/// week of lifetime so an offline machine still comes up locked on reconnect. The
/// lock/unlock pair mirrors ScheduleRestart/CancelRestart -- one task imposes,
/// another lifts.
/// </para>
/// <para>
/// <b>A device is never locked with no way back.</b> Engaging refuses outright
/// unless the organization has an IT unlock code set, because that code is the only
/// thing that can clear the lock on the machine itself if it never reaches the
/// server again. The code's PBKDF2 verifier -- never the code -- rides in every
/// engage payload.
/// </para>
/// <para>
/// <b>Every target is authorized server-side.</b> Device and group ids arrive from
/// an untrusted client, so both are re-resolved against the caller's organization
/// and device scope, exactly as <see cref="Announcements.AnnouncementService"/>
/// does: a device the caller may not see is dropped during resolution and never
/// appears in the lock.
/// </para>
/// </remarks>
public sealed class ScreenLockService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    ScreenUnlockCodeService unlockCodeService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<AgentServerOptions> agentServerOptions,
    ILogger<ScreenLockService> logger)
{
    /// <summary>The page the console shows; history older than this is in the audit log.</summary>
    public const int MaxListLimit = 50;

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly ScreenUnlockCodeService _unlockCodeService = unlockCodeService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>The console's one definition of offline: no heartbeat for this long.</summary>
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);

    private readonly ILogger<ScreenLockService> _logger = logger;

    // ------------------------------------------------------------------ engage

    /// <summary>
    /// Locks a group's active members or an explicit device selection. One
    /// transaction around the tasks, the lock, its device rows and the audit entry,
    /// run under the execution strategy, exactly as AnnouncementService does and for
    /// the same reason: the task service commits each task as it queues it, so a
    /// failure mid-fleet must take the whole thing back or screens would lock with
    /// no record behind them.
    /// </summary>
    public Task<ScreenLockEngageResult> EngageAsync(
        Guid organizationId,
        string message,
        string? contactLine,
        Guid? groupId,
        IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        // Enforced again here, not only at the endpoint: a service is callable from
        // anywhere, and a lock targets exactly one of a group or a device selection.
        var hasGroup = groupId is { } group && group != Guid.Empty;
        var hasDevices = deviceIds is { Count: > 0 };
        if (hasGroup == hasDevices)
        {
            throw new ArgumentException("A screen lock targets exactly one of a group or a device selection.");
        }

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() => EngageInTransactionAsync(
            organizationId, message, contactLine, hasGroup ? groupId : null, deviceIds, scopedDeviceIds,
            actorId, actorDisplay, cancellationToken));
    }

    private async Task<ScreenLockEngageResult> EngageInTransactionAsync(
        Guid organizationId,
        string message,
        string? contactLine,
        Guid? groupId,
        IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken)
    {
        // A retried attempt starts clean; nothing from a failed one may be re-saved.
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Refuse before anything is written if there is no unlock code: locking a
        // machine with no recovery path is the one thing this feature must never do.
        var verifier = await _unlockCodeService.GetVerifierAsync(organizationId, cancellationToken);
        if (verifier is null)
        {
            return ScreenLockEngageResult.NoUnlockCode();
        }

        // Constructing the lock first re-validates the text (trimmed, bounded, plain,
        // message never empty) before any target is resolved or queued.
        var screenLock = groupId is { } targetGroup
            ? ScreenLock.ToGroup(organizationId, message, contactLine, targetGroup, actorId, actorDisplay)
            : ScreenLock.ToDevices(organizationId, message, contactLine, actorId, actorDisplay);

        var targets = groupId is { } resolveGroup
            ? await ResolveGroupMembersAsync(organizationId, resolveGroup, scopedDeviceIds, cancellationToken)
            : await ResolveDevicesAsync(organizationId, deviceIds!, scopedDeviceIds, cancellationToken);

        if (targets.Count == 0)
        {
            // Unknown, foreign, out of scope or empty -- all the same answer, so a
            // caller cannot tell a group they may not see from one that does not
            // exist. Nothing was written, so there is nothing to commit.
            return ScreenLockEngageResult.NotFound();
        }

        var now = _timeProvider.GetUtcNow();
        var payload = new TaskPayloads.EngageScreenLock(
            screenLock.Id, screenLock.Message, screenLock.ContactLine, verifier);
        var ttl = TimeSpan.FromSeconds(DeviceTaskCatalog.Require(DeviceTaskType.EngageScreenLock).DefaultTimeToLiveSeconds);
        var rows = new List<ScreenLockDevice>(targets.Count);
        var offline = 0;

        foreach (var target in targets)
        {
            if (target.Status == DeviceStatus.Retired)
            {
                // Recorded, not dropped: "targeted and excluded because retired" is a
                // useful answer, and silently omitting it would look like the device
                // was never targeted.
                rows.Add(new ScreenLockDevice(
                    screenLock.Id, target.Id, target.Hostname, engageTaskId: null, ScreenLockDevice.SkipReasonRetired));
                continue;
            }

            // Queued through the task service, which enforces the retired-device
            // rule, the minimum agent version and the catalogue definition, and saves
            // each task as it queues it; the surrounding transaction makes those
            // saves and the records below one commit.
            var task = await _taskService.QueueAsync(
                organizationId, target.Id, DeviceTaskType.EngageScreenLock, payload,
                actorId, actorDisplay, cancellationToken, ttl);

            if (task is null)
            {
                // The device is in the organization and active, so a refusal can only
                // be the catalogue's agent-version gate.
                rows.Add(new ScreenLockDevice(
                    screenLock.Id, target.Id, target.Hostname, engageTaskId: null, ScreenLockDevice.SkipReasonAgentTooOld));
                continue;
            }

            rows.Add(new ScreenLockDevice(screenLock.Id, target.Id, target.Hostname, task.Id, skipReason: null));

            // Queued but not reachable right now -- the same staleness rule as the
            // console's Offline badge -- so the response can say honestly how many
            // machines will only lock on reconnect.
            if (target.LastSeenAt is null || now - target.LastSeenAt > _staleAfter)
            {
                offline++;
            }
        }

        var queued = rows.Count(r => r.EngageTaskId is not null);
        var skipped = rows.Count - queued;

        _dbContext.ScreenLocks.Add(screenLock);
        _dbContext.ScreenLockDevices.AddRange(rows);

        // The message can run to 500 characters but the audit target display is
        // bounded at 256; the full text is in the state change below, so a short,
        // safe label is all the display needs.
        var display = screenLock.Message.Length <= 256 ? screenLock.Message : screenLock.Message[..256];
        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "device.screen_lock.engage", AuditResult.Success,
            a => a.OnTarget("screen_lock", screenLock.Id.ToString(), display)
                  .Requiring(Domain.Authorization.Permissions.Device.ScreenLock)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      message = screenLock.Message,
                      targeted = rows.Count,
                      queued,
                      skipped,
                  })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Screen lock {LockId} by {Actor}: {Targeted} device(s) targeted, {Queued} queued ({Offline} offline), {Skipped} skipped.",
            screenLock.Id, actorDisplay, rows.Count, queued, offline, skipped);

        return ScreenLockEngageResult.Success(screenLock.Id, rows.Count, queued, offline, skipped);
    }

    // ----------------------------------------------------------------- release

    /// <summary>
    /// Lifts a lock: a whole prior lock by id, or every live lock on a group's
    /// members or on an explicit device selection. Queues one ReleaseScreenLock per
    /// still-locked device and records which task lifted it, all in one transaction.
    /// Returns how many release tasks were queued.
    /// </summary>
    public Task<int> ReleaseAsync(
        Guid organizationId,
        Guid? lockId,
        Guid? groupId,
        IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        // Exactly one way of choosing what to release, enforced here as the endpoint
        // does: a lock id, a group, or a device selection.
        var targetCount =
            (lockId is { } l && l != Guid.Empty ? 1 : 0)
            + (groupId is { } g && g != Guid.Empty ? 1 : 0)
            + (deviceIds is { Count: > 0 } ? 1 : 0);
        if (targetCount != 1)
        {
            throw new ArgumentException("A release names exactly one of a lock id, a group or a device selection.");
        }

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() => ReleaseInTransactionAsync(
            organizationId, lockId, groupId, deviceIds, scopedDeviceIds, actorId, actorDisplay, cancellationToken));
    }

    private async Task<int> ReleaseInTransactionAsync(
        Guid organizationId,
        Guid? lockId,
        Guid? groupId,
        IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Every still-locked row (engaged, not yet released) in the organization,
        // narrowed to what this release names. Scoped to a single DbSet query with a
        // subquery on the org's locks, so the rows come back tracked and ready to mark.
        var orgLockIds = _dbContext.ScreenLocks
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => x.Id);

        var query = _dbContext.ScreenLockDevices
            .Where(r => orgLockIds.Contains(r.ScreenLockId)
                        && r.EngageTaskId != null
                        && r.ReleaseTaskId == null);

        if (lockId is { } id)
        {
            query = query.Where(r => r.ScreenLockId == id);
        }
        else if (groupId is { } group)
        {
            var members = (await ResolveGroupMembersAsync(organizationId, group, scopedDeviceIds, cancellationToken))
                .Select(t => t.Id).ToHashSet();
            query = query.Where(r => members.Contains(r.DeviceId));
        }
        else
        {
            var devices = (await ResolveDevicesAsync(organizationId, deviceIds!, scopedDeviceIds, cancellationToken))
                .Select(t => t.Id).ToHashSet();
            query = query.Where(r => devices.Contains(r.DeviceId));
        }

        // For release by lock id the scope filter still applies: a scoped admin may
        // lift only the devices they can see, even when naming a lock by id.
        if (scopedDeviceIds is not null)
        {
            var visible = scopedDeviceIds.ToHashSet();
            query = query.Where(r => visible.Contains(r.DeviceId));
        }

        var lockedRows = await query.ToListAsync(cancellationToken);

        var released = 0;
        foreach (var row in lockedRows)
        {
            // Names the device's own lock, so the agent lifts exactly that one.
            var task = await _taskService.QueueAsync(
                organizationId, row.DeviceId, DeviceTaskType.ReleaseScreenLock,
                new TaskPayloads.ReleaseScreenLock(row.ScreenLockId),
                actorId, actorDisplay, cancellationToken);

            // Null is a retired device or an agent without the executor; nothing to
            // release there, so the row stays as it was.
            if (task is not null && row.TryMarkReleased(task.Id))
            {
                released++;
            }
        }

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "device.screen_lock.release", AuditResult.Success,
            a => a.OnTarget("screen_lock", (lockId ?? groupId ?? Guid.Empty).ToString(), "release")
                  .Requiring(Domain.Authorization.Permissions.Device.ScreenLock)
                  .WithStateChange(null, JsonSerializer.Serialize(new { released })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Screen lock release by {Actor}: {Released} device(s) sent an unlock.", actorDisplay, released);

        return released;
    }

    // -------------------------------------------------------------------- list

    /// <summary>
    /// The organization's screen locks, newest first, with where each one stands
    /// now -- derived per device from its engage/release tasks and the agent's
    /// reported lock state, never from a stored flag. Scoped administrators see only
    /// locks that touch a device they are scoped to, with the counts computed over
    /// those devices alone (one rule for visibility and numbers, exactly as
    /// AnnouncementService does).
    /// </summary>
    public async Task<IReadOnlyList<ScreenLockListRow>> ListAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
        var visible = scopedDeviceIds?.ToList();

        var query = _dbContext.ScreenLocks.AsNoTracking()
            .Where(l => l.OrganizationId == organizationId);
        if (visible is not null)
        {
            query = query.Where(l => _dbContext.ScreenLockDevices
                .Any(r => r.ScreenLockId == l.Id && visible.Contains(r.DeviceId)));
        }

        var locks = await query
            .OrderByDescending(l => l.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (locks.Count == 0)
        {
            return [];
        }

        var ids = locks.Select(l => l.Id).ToList();
        var now = _timeProvider.GetUtcNow();
        var onlineThreshold = now - _staleAfter;

        var rowsQuery = _dbContext.ScreenLockDevices.AsNoTracking()
            .Where(r => ids.Contains(r.ScreenLockId));
        if (visible is not null)
        {
            rowsQuery = rowsQuery.Where(r => visible.Contains(r.DeviceId));
        }

        // The live word on each device is its engage task, its release task (if a
        // release was queued for this lock) and what the agent last reported; left
        // joins, because this history outlives the tasks and a device may be gone.
        var rows = await (
            from row in rowsQuery
            join engage in _dbContext.DeviceTasks.AsNoTracking() on row.EngageTaskId equals engage.Id into engageJoin
            from engage in engageJoin.DefaultIfEmpty()
            join release in _dbContext.DeviceTasks.AsNoTracking() on row.ReleaseTaskId equals release.Id into releaseJoin
            from release in releaseJoin.DefaultIfEmpty()
            join device in _dbContext.Devices.AsNoTracking() on row.DeviceId equals device.Id into deviceJoin
            from device in deviceJoin.DefaultIfEmpty()
            select new RowState(
                row.ScreenLockId,
                row.SkipReason,
                (DeviceTaskStatus?)engage.Status,
                (DeviceTaskStatus?)release.Status,
                device.ScreenLockedReported,
                device.LastSeenAt))
            .ToListAsync(cancellationToken);
        var byLock = rows.ToLookup(r => r.ScreenLockId);

        var groupIds = locks.Where(l => l.DeviceGroupId is not null)
            .Select(l => l.DeviceGroupId!.Value)
            .Distinct()
            .ToList();
        var groupNames = await _dbContext.DeviceGroups.AsNoTracking()
            .Where(g => groupIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);

        return locks.Select(l =>
        {
            int locked = 0, releasedCount = 0, pending = 0, failed = 0, skipped = 0, offline = 0;
            var states = byLock[l.Id].ToList();

            foreach (var s in states)
            {
                if (s.SkipReason is not null)
                {
                    skipped++;
                    continue;
                }

                var isOffline = s.LastSeenAt is null || s.LastSeenAt < onlineThreshold;

                switch (s.EngageStatus)
                {
                    case DeviceTaskStatus.Queued:
                        // Still waiting to lock: it has not claimed the task yet.
                        // Offline is counted only here -- the list's equivalent of
                        // the engage-time "offline" tally, a machine that will lock
                        // on reconnect -- and never for a device that has already
                        // engaged, released or failed, so a failed-and-offline device
                        // is not double-counted as offline.
                        pending++;
                        if (isOffline)
                        {
                            offline++;
                        }

                        break;
                    case DeviceTaskStatus.Delivered or DeviceTaskStatus.Succeeded:
                        // Released when the device's own release task succeeded, or
                        // when the agent itself reports it is no longer locked (the
                        // on-device IT unlock code). Otherwise it is holding the lock.
                        if (s.ReleaseStatus == DeviceTaskStatus.Succeeded || s.ScreenLockedReported == false)
                        {
                            releasedCount++;
                        }
                        else
                        {
                            locked++;
                        }

                        break;
                    default:
                        // Failed, expired, cancelled -- or the task row gone: the lock
                        // was never confirmed on that screen.
                        failed++;
                        break;
                }
            }

            return new ScreenLockListRow(
                l.Id, l.Message, l.ContactLine, l.TargetType.ToString(),
                l.DeviceGroupId is { } gid ? groupNames.GetValueOrDefault(gid) : null,
                l.CreatedByDisplay, l.CreatedAt,
                Targeted: states.Count,
                Locked: locked,
                Released: releasedCount,
                Pending: pending,
                Failed: failed,
                Skipped: skipped,
                Offline: offline);
        }).ToList();
    }

    // -------------------------------------------------------------- derivation

    /// <summary>
    /// Whether a device is behind a screen lock right now, derived -- never stored.
    /// </summary>
    /// <remarks>
    /// The rule, combining the task intent with the agent-reported truth: the device
    /// is locked when its latest EngageScreenLock task was Delivered or Succeeded and
    /// no newer ReleaseScreenLock has Succeeded, UNLESS the agent's last heartbeat
    /// said it is not locked (<paramref name="screenLockedReported"/> is false), in
    /// which case it is not -- so a lock cleared on the machine with the IT unlock
    /// code shows as released even though no release task was queued. A null report
    /// (an older agent) defers entirely to the tasks.
    /// </remarks>
    public static bool DeriveDeviceLocked(
        bool? screenLockedReported, IEnumerable<ScreenLockTaskSnapshot> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var relevant = tasks.ToList();

        var latestEngage = relevant
            .Where(t => t.Type == DeviceTaskType.EngageScreenLock
                        && t.Status is DeviceTaskStatus.Delivered or DeviceTaskStatus.Succeeded)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefault();

        if (latestEngage is null)
        {
            return false;
        }

        var releasedSince = relevant.Any(t =>
            t.Type == DeviceTaskType.ReleaseScreenLock
            && t.Status == DeviceTaskStatus.Succeeded
            && t.CreatedAt > latestEngage.CreatedAt);

        if (releasedSince)
        {
            return false;
        }

        // The agent's word wins when it has given one: it reported "not locked".
        return screenLockedReported != false;
    }

    // ------------------------------------------------------------- resolution

    /// <summary>
    /// The group's active members, the group re-resolved against the caller's
    /// organization so a foreign group answers empty rather than revealing its
    /// members. Retired members are not active members and are simply not targeted.
    /// </summary>
    private async Task<List<TargetDevice>> ResolveGroupMembersAsync(
        Guid organizationId, Guid groupId, IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken)
    {
        var members = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.DeviceGroupId == groupId
                        && d.OrganizationId == organizationId
                        && d.Status == DeviceStatus.Active)
            .OrderBy(d => d.Hostname)
            .Select(d => new TargetDevice(d.Id, d.Hostname, d.Status, d.LastSeenAt))
            .ToListAsync(cancellationToken);

        return Scope(members, scopedDeviceIds);
    }

    /// <summary>
    /// The named devices re-resolved against the organization and the caller's
    /// scope; an id that is not theirs to see simply does not come back. Retired
    /// devices DO come back, to be recorded as skipped rather than vanishing.
    /// </summary>
    private async Task<List<TargetDevice>> ResolveDevicesAsync(
        Guid organizationId, IReadOnlyCollection<Guid> deviceIds, IReadOnlyCollection<Guid>? scopedDeviceIds,
        CancellationToken cancellationToken)
    {
        var targeted = deviceIds.ToHashSet();

        var devices = await _dbContext.Devices.AsNoTracking()
            .Where(d => targeted.Contains(d.Id) && d.OrganizationId == organizationId)
            .OrderBy(d => d.Hostname)
            .Select(d => new TargetDevice(d.Id, d.Hostname, d.Status, d.LastSeenAt))
            .ToListAsync(cancellationToken);

        return Scope(devices, scopedDeviceIds);
    }

    private static List<TargetDevice> Scope(List<TargetDevice> devices, IReadOnlyCollection<Guid>? scopedDeviceIds)
    {
        if (scopedDeviceIds is null)
        {
            return devices;
        }

        var visible = scopedDeviceIds.ToHashSet();
        return devices.Where(d => visible.Contains(d.Id)).ToList();
    }

    private sealed record TargetDevice(Guid Id, string Hostname, DeviceStatus Status, DateTimeOffset? LastSeenAt);

    private sealed record RowState(
        Guid ScreenLockId,
        string? SkipReason,
        DeviceTaskStatus? EngageStatus,
        DeviceTaskStatus? ReleaseStatus,
        bool? ScreenLockedReported,
        DateTimeOffset? LastSeenAt);
}

/// <summary>One of a device's screen-lock tasks, for deriving its live lock state.</summary>
public sealed record ScreenLockTaskSnapshot(DeviceTaskType Type, DeviceTaskStatus Status, DateTimeOffset CreatedAt);

/// <summary>What engaging a lock came to, and how it is reported back to the caller.</summary>
public enum ScreenLockEngageOutcome
{
    /// <summary>The lock was recorded and its tasks queued.</summary>
    Engaged,

    /// <summary>Refused: the organization has no IT unlock code, so there would be no recovery path.</summary>
    NoUnlockCode,

    /// <summary>Nothing resolved: an unknown or foreign group, or device ids all out of the org/scope.</summary>
    NotFound,
}

/// <summary>
/// The outcome of an engage. Counts are meaningful only when
/// <see cref="Outcome"/> is <see cref="ScreenLockEngageOutcome.Engaged"/>.
/// </summary>
/// <param name="Offline">
/// Of <paramref name="Queued"/>, the devices offline at engage time -- no heartbeat
/// within the console's staleness threshold -- whose lock waits for them to reconnect.
/// </param>
public sealed record ScreenLockEngageResult(
    ScreenLockEngageOutcome Outcome, Guid LockId, int Targeted, int Queued, int Offline, int Skipped)
{
    public static ScreenLockEngageResult Success(Guid lockId, int targeted, int queued, int offline, int skipped) =>
        new(ScreenLockEngageOutcome.Engaged, lockId, targeted, queued, offline, skipped);

    public static ScreenLockEngageResult NoUnlockCode() =>
        new(ScreenLockEngageOutcome.NoUnlockCode, Guid.Empty, 0, 0, 0, 0);

    public static ScreenLockEngageResult NotFound() =>
        new(ScreenLockEngageOutcome.NotFound, Guid.Empty, 0, 0, 0, 0);
}

/// <summary>One screen lock as the history page shows it, with live per-device counts.</summary>
public sealed record ScreenLockListRow(
    Guid Id,
    string Message,
    string? ContactLine,
    string TargetType,
    string? GroupName,
    string CreatedByDisplay,
    DateTimeOffset CreatedAt,
    int Targeted,
    int Locked,
    int Released,
    int Pending,
    int Failed,
    int Skipped,
    int Offline);
