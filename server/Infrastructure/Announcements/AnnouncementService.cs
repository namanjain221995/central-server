using System.Text.Json;
using EndpointPlatform.Domain.Announcements;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Announcements;

/// <summary>
/// Turns "say this on these screens" into one announcement record and exactly
/// the <c>ShowAnnouncement</c> tasks the targeted fleet can actually display.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivery is the existing task channel, nothing new.</b> Each target gets an
/// ordinary queued task with a day of lifetime, so an offline machine shows the
/// notice when it next checks in and a machine that never does lets the task
/// expire. The agent never learns that a broadcast exists; six screens are six
/// tasks.
/// </para>
/// <para>
/// <b>Every target is authorized server-side.</b> Device and group ids arrive
/// from an untrusted client, so both are re-resolved against the caller's
/// organization and device scope, exactly as
/// <see cref="Software.SoftwareDeploymentService"/> does. A device the caller
/// may not see is dropped during resolution and never appears in the
/// announcement -- not reported as refused either, because saying "you may not
/// target this device" would confirm that it exists.
/// </para>
/// </remarks>
public sealed class AnnouncementService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<AgentServerOptions> agentServerOptions,
    ILogger<AnnouncementService> logger)
{
    /// <summary>The page the console shows; history older than this is in the audit log.</summary>
    public const int MaxListLimit = 50;

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>The console's one definition of offline: no heartbeat for this long.</summary>
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);

    private readonly ILogger<AnnouncementService> _logger = logger;

    /// <summary>
    /// Sends the announcement to a group's active members or to an explicit
    /// device selection. Null when nothing resolves -- an unknown or foreign
    /// group, or device ids that all fall outside the organization or the
    /// caller's scope -- which the endpoint reports as 404.
    /// </summary>
    /// <remarks>
    /// One transaction around the resolution, the tasks, the announcement, its
    /// device rows and the audit entry. The task service commits each task as it
    /// queues it, so without the transaction a failure mid-fleet would leave
    /// tasks that display on screens with no announcement, no per-device record
    /// and no audit entry behind them. Run under the execution strategy because
    /// the production connection retries transient failures, and a strategy that
    /// retries refuses a transaction it does not own.
    /// </remarks>
    public Task<AnnouncementSendResult?> SendAsync(
        Guid organizationId,
        string title,
        string message,
        Guid? groupId,
        IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        // The endpoint validates the shape, but a service is callable from
        // anywhere, so the rule is enforced again here: exactly one way of
        // choosing the audience.
        var hasGroup = groupId is { } group && group != Guid.Empty;
        var hasDevices = deviceIds is { Count: > 0 };
        if (hasGroup == hasDevices)
        {
            throw new ArgumentException("An announcement targets exactly one of a group or a device selection.");
        }

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() => SendInTransactionAsync(
            organizationId, title, message, hasGroup ? groupId : null, deviceIds, scopedDeviceIds,
            actorId, actorDisplay, cancellationToken));
    }

    private async Task<AnnouncementSendResult?> SendInTransactionAsync(
        Guid organizationId,
        string title,
        string message,
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

        // Constructing the announcement first re-validates the text (trimmed,
        // bounded, plain, never empty) before any target is resolved or queued.
        var announcement = groupId is { } targetGroup
            ? Announcement.ToGroup(organizationId, title, message, targetGroup, actorId, actorDisplay)
            : Announcement.ToDevices(organizationId, title, message, actorId, actorDisplay);

        var targets = groupId is { } resolveGroup
            ? await ResolveGroupMembersAsync(organizationId, resolveGroup, scopedDeviceIds, cancellationToken)
            : await ResolveDevicesAsync(organizationId, deviceIds!, scopedDeviceIds, cancellationToken);

        if (targets.Count == 0)
        {
            // Unknown, foreign, out of scope or empty -- all the same answer, so
            // a caller cannot tell a group they may not see from one that does
            // not exist. Nothing was written, so there is nothing to commit.
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var payload = new TaskPayloads.ShowAnnouncement(announcement.Id, announcement.Title, announcement.Message);
        var rows = new List<AnnouncementDevice>(targets.Count);
        var offline = 0;

        foreach (var target in targets)
        {
            if (target.Status == DeviceStatus.Retired)
            {
                // Recorded, not dropped: "this device was targeted and excluded
                // because it is retired" is a useful answer, and silently
                // omitting it would look like the device was never targeted.
                rows.Add(new AnnouncementDevice(
                    announcement.Id, target.Id, target.Hostname, taskId: null, AnnouncementDevice.SkipReasonRetired));
                continue;
            }

            // Still queued through the task service, never by writing a task row
            // here: it is what enforces the retired-device rule, the minimum
            // agent version and the catalogue definition. It saves each task as
            // it queues it; the surrounding transaction makes those saves and
            // the records below one commit.
            var task = await _taskService.QueueAsync(
                organizationId, target.Id, DeviceTaskType.ShowAnnouncement, payload,
                actorId, actorDisplay, cancellationToken);

            if (task is null)
            {
                // The device exists in the organization and is active, so a
                // refusal can only be the catalogue's agent-version gate.
                rows.Add(new AnnouncementDevice(
                    announcement.Id, target.Id, target.Hostname, taskId: null, AnnouncementDevice.SkipReasonAgentTooOld));
                continue;
            }

            rows.Add(new AnnouncementDevice(announcement.Id, target.Id, target.Hostname, task.Id, skipReason: null));

            // Queued but not reachable right now -- the same staleness rule as
            // the console's Offline badge -- so the banner can say honestly how
            // many screens will only see this on reconnect.
            if (target.LastSeenAt is null || now - target.LastSeenAt > _staleAfter)
            {
                offline++;
            }
        }

        var queued = rows.Count(r => r.TaskId is not null);
        var skipped = rows.Count - queued;

        _dbContext.Announcements.Add(announcement);
        _dbContext.AnnouncementDevices.AddRange(rows);

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "announcement.send", AuditResult.Success,
            a => a.OnTarget("announcement", announcement.Id.ToString(), announcement.Title)
                  .Requiring(Domain.Authorization.Permissions.Announcement.Send)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      title = announcement.Title,
                      targeted = rows.Count,
                      queued,
                      skipped,
                  })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Announcement {AnnouncementId} by {Actor}: {Targeted} device(s) targeted, {Queued} queued ({Offline} offline), {Skipped} skipped.",
            announcement.Id, actorDisplay, rows.Count, queued, offline, skipped);

        return new AnnouncementSendResult(announcement.Id, rows.Count, queued, offline, skipped);
    }

    /// <summary>
    /// The organization's announcements, newest first, with where each one's
    /// deliveries stand now.
    /// </summary>
    /// <remarks>
    /// Scoped administrators see the announcements that touch at least one device
    /// they are scoped to, with the counts computed over those devices only. One
    /// rule for visibility and numbers, derived from the same filtered rows, so
    /// the page can never list a broadcast whose every count reads zero -- the
    /// simpler of the two consistent choices, and the one that discloses nothing
    /// about machines outside the caller's scope.
    /// </remarks>
    public async Task<IReadOnlyList<AnnouncementListRow>> ListAsync(
        Guid organizationId,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        int limit,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, MaxListLimit);
        var visible = scopedDeviceIds?.ToList();

        var query = _dbContext.Announcements.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId);
        if (visible is not null)
        {
            query = query.Where(a => _dbContext.AnnouncementDevices
                .Any(r => r.AnnouncementId == a.Id && visible.Contains(r.DeviceId)));
        }

        var announcements = await query
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        if (announcements.Count == 0)
        {
            return [];
        }

        var ids = announcements.Select(a => a.Id).ToList();

        var rowsQuery = _dbContext.AnnouncementDevices.AsNoTracking()
            .Where(r => ids.Contains(r.AnnouncementId));
        if (visible is not null)
        {
            rowsQuery = rowsQuery.Where(r => visible.Contains(r.DeviceId));
        }

        // The live word on each delivery is the task the row points at; a left
        // join, because this history outlives the tasks.
        var rows = await (
            from row in rowsQuery
            join task in _dbContext.DeviceTasks.AsNoTracking() on row.TaskId equals task.Id into taskJoin
            from task in taskJoin.DefaultIfEmpty()
            select new
            {
                row.AnnouncementId,
                row.TaskId,
                TaskStatus = (DeviceTaskStatus?)task.Status,
            })
            .ToListAsync(cancellationToken);
        var byAnnouncement = rows.ToLookup(r => r.AnnouncementId);

        var groupIds = announcements.Where(a => a.DeviceGroupId is not null)
            .Select(a => a.DeviceGroupId!.Value)
            .Distinct()
            .ToList();
        var groupNames = await _dbContext.DeviceGroups.AsNoTracking()
            .Where(g => groupIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken);

        return announcements.Select(a =>
        {
            var deliveries = byAnnouncement[a.Id].ToList();
            return new AnnouncementListRow(
                a.Id, a.Title, a.Message, a.TargetType.ToString(),
                a.DeviceGroupId is { } gid ? groupNames.GetValueOrDefault(gid) : null,
                a.CreatedByDisplay, a.CreatedAt,
                Targeted: deliveries.Count,
                Queued: deliveries.Count(d => d.TaskId is not null),
                Succeeded: deliveries.Count(d => d.TaskStatus == DeviceTaskStatus.Succeeded),
                Pending: deliveries.Count(d =>
                    d.TaskStatus is DeviceTaskStatus.Queued or DeviceTaskStatus.Delivered),
                // Failed, expired and cancelled all come to the same thing for a
                // broadcast: the notice was never shown on that screen.
                Failed: deliveries.Count(d =>
                    d.TaskStatus is DeviceTaskStatus.Failed or DeviceTaskStatus.Expired or DeviceTaskStatus.Cancelled),
                Skipped: deliveries.Count(d => d.TaskId is null));
        }).ToList();
    }

    /// <summary>
    /// The group's active members, the group re-resolved against the caller's
    /// organization so a foreign group answers empty rather than revealing its
    /// members. Retired members are not "active members" and are simply not
    /// targeted -- unlike an explicitly named retired device, which is recorded.
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
}

/// <summary>
/// What a send came to: one announcement, and how its audience split.
/// </summary>
/// <param name="Offline">
/// Of <paramref name="Queued"/>, the devices that were offline at send time --
/// no heartbeat within the console's staleness threshold -- whose task waits
/// for them to reconnect.
/// </param>
public sealed record AnnouncementSendResult(Guid AnnouncementId, int Targeted, int Queued, int Offline, int Skipped);

/// <summary>One announcement as the history page shows it, with live delivery counts.</summary>
public sealed record AnnouncementListRow(
    Guid Id,
    string Title,
    string Message,
    string TargetType,
    string? GroupName,
    string CreatedByDisplay,
    DateTimeOffset CreatedAt,
    int Targeted,
    int Queued,
    int Succeeded,
    int Pending,
    int Failed,
    int Skipped);
