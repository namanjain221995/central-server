using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Software;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Groups;

/// <summary>The device actions a group can carry out. Each is an existing single-device action.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<GroupAction>))]
public enum GroupAction
{
    Restart,
    Shutdown,
    Lock,
    SignOut,
}

/// <summary>
/// Carries out a device action on a group: resolves the group's online members
/// on the server, authorizes each one, and queues the ordinary per-device task
/// for each.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no group task.</b> The agent never learns that a group exists.
/// A group restart of four online devices is four <c>RestartDevice</c> tasks,
/// each with the same payload the single-device route would have queued, each
/// claimed, executed, expired and reported on its own. The server owns
/// membership; the endpoint only ever receives a typed instruction about itself.
/// </para>
/// <para>
/// <b>The client names a group, never devices.</b> Membership and online state
/// are read here, at queue time, from the database -- not from whatever the
/// console rendered, which may be minutes stale. A request cannot add a device
/// to the target list, and a device that left the group or went offline since
/// the page loaded is simply not targeted.
/// </para>
/// <para>
/// <b>Per device, not all-or-nothing.</b> Each task and its audit entry are saved
/// together, so a device that cannot take the action -- it already has a restart
/// in flight, its agent is too old -- is reported and the rest still proceed.
/// One device must not cost the whole group its restart.
/// </para>
/// </remarks>
public sealed class DeviceGroupActionService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    DeviceScopeAuthorizer scope,
    ApplicationForceStopService forceStopService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<AgentServerOptions> agentServerOptions)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly DeviceScopeAuthorizer _scope = scope;
    private readonly ApplicationForceStopService _forceStopService = forceStopService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);

    /// <summary>
    /// Queues <paramref name="action"/> for every online device in the group the
    /// caller may act on. The permission for the action is checked by the
    /// endpoint; scope, membership and online state are decided here.
    /// </summary>
    /// <param name="graceSeconds">
    /// For <see cref="GroupAction.Restart"/>, a grace period already validated by
    /// <see cref="RestartGrace.FromDelay"/>. Ignored otherwise.
    /// </param>
    public async Task<GroupActionResult?> RunAsync(
        Guid organizationId, Guid actorId, string actorDisplay,
        Guid groupId, GroupAction action, int graceSeconds,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(organizationId, actorId, groupId, cancellationToken);
        if (resolution is null)
        {
            return null;
        }

        var (groupName, members) = resolution.Value;
        var (type, payload) = TaskFor(action, graceSeconds);

        var results = new List<GroupActionDeviceResult>(members.Count);

        // Restarts already in flight are known before anything is queued, so the
        // common case is a clear answer rather than a caught constraint violation.
        var activeRestarts = type == DeviceTaskType.RestartDevice
            ? await _dbContext.DeviceTasks.AsNoTracking()
                .Where(t => t.Type == DeviceTaskType.RestartDevice
                            && (t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Delivered)
                            && members.Select(m => m.Id).Contains(t.DeviceId))
                .Select(t => t.DeviceId)
                .ToHashSetAsync(cancellationToken)
            : [];

        foreach (var member in members)
        {
            if (!member.IsOnline)
            {
                // Not targeted at all. An offline device neither receives the
                // action nor counts as having carried it out.
                results.Add(new(member.Id, member.Hostname, GroupActionOutcome.Offline, null));
                continue;
            }

            // Re-checked per device at queue time. Resolution already required
            // authority over the group; this catches a device moved out of it, or
            // a scope change, in the moments since.
            if (!await _scope.CanActOnDeviceAsync(actorId, organizationId, member.Id, cancellationToken))
            {
                results.Add(new(member.Id, member.Hostname, GroupActionOutcome.NotAuthorized, null));
                continue;
            }

            if (activeRestarts.Contains(member.Id))
            {
                results.Add(new(member.Id, member.Hostname, GroupActionOutcome.AlreadyInProgress, null));
                continue;
            }

            results.Add(await QueueOneAsync(organizationId, actorId, actorDisplay, member, type, payload, cancellationToken));
        }

        AuditGroupAction(organizationId, actorId, actorDisplay, groupId, groupName, action.ToString(),
            action == GroupAction.Restart ? graceSeconds : null, results);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new GroupActionResult(groupId, groupName, action.ToString(),
            action == GroupAction.Restart ? graceSeconds : null, QueueStatusOf(results), results);
    }

    /// <summary>
    /// Force-stops an application on the group's online devices, through the
    /// existing Force Stop service -- which does its own per-device inventory
    /// matching and reports, per device, whether the application was installed.
    /// </summary>
    public async Task<GroupForceStopResult?> ForceStopAsync(
        Guid organizationId, Guid actorId, string actorDisplay,
        Guid groupId, string applicationName, string? publisher,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(organizationId, actorId, groupId, cancellationToken);
        if (resolution is null)
        {
            return null;
        }

        var (groupName, members) = resolution.Value;
        var online = members.Where(m => m.IsOnline).Select(m => m.Id).ToList();
        var offline = members.Where(m => !m.IsOnline)
            .Select(m => new GroupActionDeviceResult(m.Id, m.Hostname, GroupActionOutcome.Offline, null))
            .ToList();

        var scoped = await _scope.ScopedDeviceIdsOrNullAsync(actorId, organizationId, cancellationToken);

        var stopped = online.Count == 0
            ? new ForceStopResult([], 0)
            : await _forceStopService.StopAsync(
                organizationId, online, applicationName, publisher, scoped, actorId, actorDisplay, cancellationToken);

        return new GroupForceStopResult(groupId, groupName, applicationName, stopped, offline);
    }

    /// <summary>
    /// Cancels every restart in the group that has not yet been delivered.
    /// </summary>
    /// <remarks>
    /// Cancellation exists only while a task is <see cref="DeviceTaskStatus.Queued"/>.
    /// Once delivered the agent may already have handed the countdown to Windows,
    /// and nothing in this platform can take it back, so those are reported as too
    /// late rather than shown as cancelled. See docs/device-groups.md.
    /// </remarks>
    public async Task<GroupActionResult?> CancelPendingRestartsAsync(
        Guid organizationId, Guid actorId, string actorDisplay, Guid groupId,
        CancellationToken cancellationToken = default)
    {
        var resolution = await ResolveAsync(organizationId, actorId, groupId, cancellationToken);
        if (resolution is null)
        {
            return null;
        }

        var (groupName, members) = resolution.Value;
        var memberIds = members.Select(m => m.Id).ToList();
        var hostnames = members.ToDictionary(m => m.Id, m => m.Hostname);

        var restarts = await _dbContext.DeviceTasks.AsNoTracking()
            .Where(t => t.Type == DeviceTaskType.RestartDevice
                        && (t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Delivered)
                        && memberIds.Contains(t.DeviceId))
            .Select(t => new { t.Id, t.DeviceId })
            .ToListAsync(cancellationToken);

        var results = new List<GroupActionDeviceResult>(restarts.Count);
        foreach (var restart in restarts)
        {
            var hostname = hostnames[restart.DeviceId];

            if (!await _scope.CanActOnDeviceAsync(actorId, organizationId, restart.DeviceId, cancellationToken))
            {
                results.Add(new(restart.DeviceId, hostname, GroupActionOutcome.NotAuthorized, restart.Id));
                continue;
            }

            var cancelled = await _taskService.CancelAsync(
                organizationId, restart.DeviceId, restart.Id, actorId, actorDisplay, cancellationToken);

            results.Add(new(restart.DeviceId, hostname,
                cancelled == TaskCancelResult.Success ? GroupActionOutcome.Cancelled : GroupActionOutcome.TooLateToCancel,
                restart.Id));
        }

        AuditGroupAction(organizationId, actorId, actorDisplay, groupId, groupName, "CancelRestart", null, results);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new GroupActionResult(groupId, groupName, "CancelRestart", null,
            results.Count == 0 ? GroupActionQueueStatus.NoEligibleDevices : QueueStatusOf(results), results);
    }

    // --------------------------------------------------------------- internals

    /// <summary>
    /// The group's active devices and whether each is online now, or null when the
    /// group does not exist in the caller's organization or is outside their scope.
    /// </summary>
    private async Task<(string GroupName, List<ResolvedMember> Members)?> ResolveAsync(
        Guid organizationId, Guid actorId, Guid groupId, CancellationToken cancellationToken)
    {
        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, groupId, cancellationToken))
        {
            return null;
        }

        var groupName = await _dbContext.DeviceGroups.AsNoTracking()
            .Where(g => g.Id == groupId)
            .Select(g => g.Name)
            .SingleAsync(cancellationToken);

        // Authoritative online state: the server's clock against the last
        // heartbeat it recorded, the same rule the device list uses.
        var now = _timeProvider.GetUtcNow();
        var rows = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.DeviceGroupId == groupId && d.OrganizationId == organizationId && d.Status == DeviceStatus.Active)
            .OrderBy(d => d.Hostname)
            .Select(d => new { d.Id, d.Hostname, d.LastSeenAt })
            .ToListAsync(cancellationToken);

        var members = rows
            .Select(r => new ResolvedMember(r.Id, r.Hostname, r.LastSeenAt is { } seen && now - seen <= _staleAfter))
            .ToList();

        return (groupName, members);
    }

    private async Task<GroupActionDeviceResult> QueueOneAsync(
        Guid organizationId, Guid actorId, string actorDisplay, ResolvedMember member,
        DeviceTaskType type, object? payload, CancellationToken cancellationToken)
    {
        try
        {
            var task = await _taskService.QueueAsync(
                organizationId, member.Id, type, payload, actorId, actorDisplay, cancellationToken);

            return task is null
                // Retired since resolution, or an agent without the executor.
                ? new(member.Id, member.Hostname, GroupActionOutcome.NotEligible, null)
                : new(member.Id, member.Hostname, GroupActionOutcome.Queued, task.Id);
        }
        catch (DbUpdateException ex) when (DeviceTaskService.IsDuplicateActiveTask(ex))
        {
            // Another request queued this device's restart between the check above
            // and this insert. The database refused the duplicate.
            //
            // The refused task and its audit entry are still tracked. Left there,
            // the next device's save would try to insert them again and fail too,
            // and so would every device after it -- one conflict costing the whole
            // group. Only Added entries are detached: everything saved for earlier
            // devices is already Unchanged, and is left alone.
            foreach (var entry in _dbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return new(member.Id, member.Hostname, GroupActionOutcome.AlreadyInProgress, null);
        }
    }

    private static (DeviceTaskType Type, object? Payload) TaskFor(GroupAction action, int graceSeconds) => action switch
    {
        GroupAction.Restart => (DeviceTaskType.RestartDevice,
            new TaskPayloads.RestartOrShutdown(graceSeconds, RestartGrace.MessageFor(graceSeconds))),
        GroupAction.Shutdown => (DeviceTaskType.ShutdownDevice,
            new TaskPayloads.RestartOrShutdown(RestartGrace.ShutdownGraceSeconds, RestartGrace.ShutdownMessage)),
        GroupAction.Lock => (DeviceTaskType.LockDevice, null),
        GroupAction.SignOut => (DeviceTaskType.SignOutUser, null),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a group action."),
    };

    private static GroupActionQueueStatus QueueStatusOf(IReadOnlyList<GroupActionDeviceResult> results)
    {
        var eligible = results.Where(r => r.Outcome != GroupActionOutcome.Offline).ToList();
        if (eligible.Count == 0)
        {
            return GroupActionQueueStatus.NoEligibleDevices;
        }

        var succeeded = eligible.Count(r => r.Outcome is GroupActionOutcome.Queued or GroupActionOutcome.Cancelled);
        if (succeeded == 0)
        {
            return GroupActionQueueStatus.NothingQueued;
        }

        return succeeded == eligible.Count && eligible.Count == results.Count
            ? GroupActionQueueStatus.AllQueued
            : GroupActionQueueStatus.QueuedWithIssues;
    }

    /// <summary>
    /// One audit entry for the group operation, alongside the per-device
    /// <c>task.queue.*</c> entries the task service writes. Hostnames and outcomes
    /// only: no paths, credentials or payload text.
    /// </summary>
    private void AuditGroupAction(
        Guid organizationId, Guid actorId, string actorDisplay, Guid groupId, string groupName,
        string action, int? graceSeconds, IReadOnlyList<GroupActionDeviceResult> results) =>
        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: $"group.action.{action.ToLowerInvariant()}", AuditResult.Success,
            a => a.OnTarget("device_group", groupId.ToString(), groupName)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      action,
                      graceSeconds,
                      targeted = results.Count(r => r.Outcome != GroupActionOutcome.Offline),
                      offline = results.Count(r => r.Outcome == GroupActionOutcome.Offline),
                      results = results.Select(r => new { hostname = r.Hostname, outcome = r.Outcome.ToString() }),
                  })));

    private readonly record struct ResolvedMember(Guid Id, string Hostname, bool IsOnline);
}

/// <summary>What happened to one device in a group action.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<GroupActionOutcome>))]
public enum GroupActionOutcome
{
    /// <summary>A task was queued; its own result arrives when the device reports.</summary>
    Queued,

    /// <summary>Not online at queue time, so not targeted.</summary>
    Offline,

    /// <summary>The device already has a restart queued or delivered.</summary>
    AlreadyInProgress,

    /// <summary>Retired since resolution, or its agent cannot run this task.</summary>
    NotEligible,

    /// <summary>The caller lost authority over the device between resolution and queueing.</summary>
    NotAuthorized,

    /// <summary>A queued restart was cancelled before delivery.</summary>
    Cancelled,

    /// <summary>The restart was already delivered; it can no longer be cancelled.</summary>
    TooLateToCancel,
}

/// <summary>
/// How queueing went for the group as a whole. This is not how the action went
/// on the devices -- that is each task's own result, which arrives later.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<GroupActionQueueStatus>))]
public enum GroupActionQueueStatus
{
    /// <summary>Every device in the group was online and took the task.</summary>
    AllQueued,

    /// <summary>Some devices took the task; others were offline, busy or ineligible.</summary>
    QueuedWithIssues,

    /// <summary>No device in the group was online.</summary>
    NoEligibleDevices,

    /// <summary>Devices were online, but none of them took the task.</summary>
    NothingQueued,
}

public sealed record GroupActionDeviceResult(Guid DeviceId, string Hostname, GroupActionOutcome Outcome, Guid? TaskId);

public sealed record GroupActionResult(
    Guid GroupId, string GroupName, string Action, int? GraceSeconds,
    GroupActionQueueStatus Status, IReadOnlyList<GroupActionDeviceResult> Devices);

public sealed record GroupForceStopResult(
    Guid GroupId, string GroupName, string ApplicationName,
    ForceStopResult Stop, IReadOnlyList<GroupActionDeviceResult> Offline);
