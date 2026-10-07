using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Restarts;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EndpointPlatform.Infrastructure.Restarts;

/// <summary>
/// Department-wide restarts at a chosen moment: scheduling them, showing them,
/// cancelling them -- cleanly while nothing has gone out, and through the
/// devices once it has -- and sending them when their moment comes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Held, then sent.</b> A schedule is a row and nothing else until
/// <see cref="RestartSchedule.DispatchAt"/>. Then <see cref="DispatchDueAsync"/>
/// -- called by the sweeper, never by a request -- resolves the group exactly
/// as a group restart would (active members, online now, the scheduler's own
/// scope re-checked per device) and queues the ordinary <c>RestartDevice</c>
/// task with the warning as its grace period. The agent never learns that a
/// schedule exists; a department restart of six devices is six restart tasks.
/// </para>
/// <para>
/// <b>Cancelling is honest about where the restart is.</b> Before dispatch,
/// cancelling deletes a plan. After it, each device is handled by what its
/// task says: a restart still Queued is cancelled where it sits; one the device
/// has accepted gets a <c>CancelRestart</c> task, and the page shows
/// "cancel requested" until the device confirms; one whose moment has passed,
/// or whose agent predates cancellation, is reported as exactly that.
/// </para>
/// <para>
/// <b>Dispatch is one transaction per schedule.</b> Either the schedule becomes
/// Dispatched together with every task it queued, or nothing changes and the
/// next tick tries again. Two sweepers, or a sweeper and a cancel, meeting on
/// the same row are settled by its row version.
/// </para>
/// </remarks>
public sealed class RestartScheduleService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    DeviceScopeAuthorizer scope,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<RestartScheduleOptions> options,
    IOptions<AgentServerOptions> agentServerOptions,
    ILogger<RestartScheduleService> logger)
{
    public const string SystemActorDisplay = "restart schedule";

    /// <summary>How many past schedules the page shows for a department.</summary>
    public const int RecentCount = 10;

    /// <summary>How often a write that lost on the row version is redone before giving up.</summary>
    private const int MaxConcurrencyRetries = 3;

    private static readonly DeviceTaskDefinition CancelDefinition = DeviceTaskCatalog.Require(DeviceTaskType.CancelRestart);

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly DeviceScopeAuthorizer _scope = scope;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly RestartScheduleOptions _options = options.Value;
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);
    private readonly ILogger<RestartScheduleService> _logger = logger;

    // ------------------------------------------------------------------ create

    public async Task<RestartScheduleCreateResult> CreateAsync(
        Guid organizationId, Guid actorId, string actorDisplay, Guid groupId, int delaySeconds,
        CancellationToken cancellationToken = default)
    {
        if (!RestartScheduleDelay.IsAccepted(delaySeconds))
        {
            return new(RestartScheduleCreateStatus.InvalidDelay, null);
        }

        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, groupId, cancellationToken))
        {
            return new(RestartScheduleCreateStatus.GroupNotFound, null);
        }

        var groupName = await GroupNameAsync(groupId, cancellationToken);
        var now = _timeProvider.GetUtcNow();

        var pending = await _dbContext.Set<RestartSchedule>().AsNoTracking()
            .SingleOrDefaultAsync(s => s.DeviceGroupId == groupId && s.Status == RestartScheduleStatus.Pending, cancellationToken);
        if (pending is not null)
        {
            return new(RestartScheduleCreateStatus.AlreadyScheduled, await ViewAsync(pending, groupName, now, cancellationToken));
        }

        var warning = RestartScheduleDelay.WarningFor(delaySeconds, _options.WarningSeconds);
        var schedule = RestartSchedule.Create(organizationId, groupId, delaySeconds, warning, actorId, actorDisplay, now);
        _dbContext.Add(schedule);

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "restart_schedule.create", AuditResult.Success,
            a => a.OnTarget("restart_schedule", schedule.Id.ToString(), groupName)
                  .Requiring(Domain.Authorization.Permissions.Device.Restart)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      groupId,
                      groupName,
                      delaySeconds,
                      warningSeconds = warning,
                      restartAt = schedule.RestartAt,
                      dispatchAt = schedule.DispatchAt,
                  })));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two administrators scheduled the same department at once; the
            // database kept one. Report that one, exactly as the pre-check would.
            _dbContext.ChangeTracker.Clear();
            var winner = await _dbContext.Set<RestartSchedule>().AsNoTracking()
                .SingleOrDefaultAsync(s => s.DeviceGroupId == groupId && s.Status == RestartScheduleStatus.Pending, cancellationToken);
            return new(RestartScheduleCreateStatus.AlreadyScheduled,
                winner is null ? null : await ViewAsync(winner, groupName, now, cancellationToken));
        }

        _logger.LogInformation(
            "Restart schedule {ScheduleId} created for group {GroupId}: restart at {RestartAt:u}, dispatch at {DispatchAt:u}, by {Actor}.",
            schedule.Id, groupId, schedule.RestartAt, schedule.DispatchAt, actorDisplay);

        return new(RestartScheduleCreateStatus.Created, await ViewAsync(schedule, groupName, now, cancellationToken));
    }

    // -------------------------------------------------------------------- read

    /// <summary>
    /// The department's pending schedule, if any, and its recent ones. Null
    /// when the group does not exist in the organization or is outside the
    /// caller's scope -- the same answer every group route gives.
    /// </summary>
    public async Task<RestartScheduleOverview?> GetOverviewAsync(
        Guid organizationId, Guid actorId, Guid groupId, CancellationToken cancellationToken = default)
    {
        if (!await _scope.CanActOnGroupAsync(actorId, organizationId, groupId, cancellationToken))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var group = await _dbContext.DeviceGroups.AsNoTracking()
            .Where(g => g.Id == groupId)
            .Select(g => new { g.Name, g.IsBuiltIn })
            .SingleAsync(cancellationToken);

        var members = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.DeviceGroupId == groupId && d.Status == DeviceStatus.Active)
            .Select(d => new { d.LastSeenAt })
            .ToListAsync(cancellationToken);
        var onlineCount = members.Count(m => IsOnline(m.LastSeenAt, now));

        var schedules = await _dbContext.Set<RestartSchedule>().AsNoTracking()
            .Where(s => s.DeviceGroupId == groupId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(RecentCount + 1)
            .ToListAsync(cancellationToken);

        var active = schedules.FirstOrDefault(s => s.Status == RestartScheduleStatus.Pending);
        var recent = new List<RestartScheduleView>();
        foreach (var schedule in schedules.Where(s => s != active).Take(RecentCount))
        {
            recent.Add(await ViewAsync(schedule, group.Name, now, cancellationToken));
        }

        return new RestartScheduleOverview(
            new RestartScheduleGroupView(groupId, group.Name, group.IsBuiltIn, members.Count, onlineCount, _options.WarningSeconds),
            active is null ? null : await ViewAsync(active, group.Name, now, cancellationToken),
            recent);
    }

    /// <summary>One schedule, or null when it is not the caller's to see.</summary>
    public async Task<RestartScheduleView?> GetAsync(
        Guid organizationId, Guid actorId, Guid scheduleId, CancellationToken cancellationToken = default)
    {
        var schedule = await _dbContext.Set<RestartSchedule>().AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == scheduleId && s.OrganizationId == organizationId, cancellationToken);
        if (schedule is null || !await _scope.CanActOnGroupAsync(actorId, organizationId, schedule.DeviceGroupId, cancellationToken))
        {
            return null;
        }

        return await ViewAsync(schedule, await GroupNameAsync(schedule.DeviceGroupId, cancellationToken),
            _timeProvider.GetUtcNow(), cancellationToken);
    }

    // ------------------------------------------------------------------ cancel

    /// <summary>
    /// Cancels the schedule for the whole department (<paramref name="deviceIds"/>
    /// null) or for the named devices only.
    /// </summary>
    public async Task<RestartScheduleCancelResult> CancelAsync(
        Guid organizationId, Guid actorId, string actorDisplay, Guid scheduleId, IReadOnlyCollection<Guid>? deviceIds,
        CancellationToken cancellationToken = default)
    {
        var schedule = await _dbContext.Set<RestartSchedule>()
            .SingleOrDefaultAsync(s => s.Id == scheduleId && s.OrganizationId == organizationId, cancellationToken);
        if (schedule is null || !await _scope.CanActOnGroupAsync(actorId, organizationId, schedule.DeviceGroupId, cancellationToken))
        {
            return new(RestartScheduleCancelStatus.NotFound, null, []);
        }

        var groupName = await GroupNameAsync(schedule.DeviceGroupId, cancellationToken);

        // While it is pending, the row version settles who wins between this
        // request, another administrator's exclusion and the sweeper. Losing
        // to an exclusion only means reading again and going again; losing to
        // the sweeper means the restart is on its way, so it is cancelled
        // through the devices instead of reporting a race nobody asked about.
        for (var attempt = 0; schedule.Status == RestartScheduleStatus.Pending; attempt++)
        {
            var result = deviceIds is null
                ? await CancelPendingAsync(schedule, groupName, actorId, actorDisplay, cancellationToken)
                : await ExcludeAsync(schedule, groupName, actorId, actorDisplay, deviceIds, cancellationToken);
            if (result is not null)
            {
                return result;
            }

            _dbContext.ChangeTracker.Clear();
            schedule = await _dbContext.Set<RestartSchedule>().SingleAsync(s => s.Id == scheduleId, cancellationToken);

            if (attempt >= MaxConcurrencyRetries && schedule.Status == RestartScheduleStatus.Pending)
            {
                return new(RestartScheduleCancelStatus.Conflict,
                    await ViewAsync(schedule, groupName, _timeProvider.GetUtcNow(), cancellationToken), []);
            }
        }

        if (schedule.Status != RestartScheduleStatus.Dispatched)
        {
            return new(RestartScheduleCancelStatus.NotCancellable,
                await ViewAsync(schedule, groupName, _timeProvider.GetUtcNow(), cancellationToken), []);
        }

        return await CancelDispatchedAsync(schedule, groupName, actorId, actorDisplay, deviceIds, cancellationToken);
    }

    private async Task<RestartScheduleCancelResult?> CancelPendingAsync(
        RestartSchedule schedule, string groupName, Guid actorId, string actorDisplay, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        schedule.TryCancel(now, actorId, actorDisplay);

        _auditWriter.Stage(schedule.OrganizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "restart_schedule.cancel", AuditResult.Success,
            a => a.OnTarget("restart_schedule", schedule.Id.ToString(), groupName)
                  .Requiring(Domain.Authorization.Permissions.Device.Restart)
                  .WithStateChange(JsonSerializer.Serialize(new { status = "Pending", restartAt = schedule.RestartAt }),
                      JsonSerializer.Serialize(new { status = "Cancelled", scope = "department" })));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }

        _logger.LogInformation("Restart schedule {ScheduleId} cancelled before dispatch by {Actor}.", schedule.Id, actorDisplay);
        return new(RestartScheduleCancelStatus.Ok, await ViewAsync(schedule, groupName, now, cancellationToken), []);
    }

    private async Task<RestartScheduleCancelResult?> ExcludeAsync(
        RestartSchedule schedule, string groupName, Guid actorId, string actorDisplay,
        IReadOnlyCollection<Guid> deviceIds, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var members = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.DeviceGroupId == schedule.DeviceGroupId && d.Status == DeviceStatus.Active && deviceIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Hostname })
            .ToDictionaryAsync(d => d.Id, cancellationToken);
        var excluded = await _dbContext.Set<RestartScheduleExclusion>().AsNoTracking()
            .Where(e => e.ScheduleId == schedule.Id)
            .Select(e => e.DeviceId)
            .ToHashSetAsync(cancellationToken);

        var results = new List<RestartScheduleCancelDeviceResult>(deviceIds.Count);
        foreach (var deviceId in deviceIds.Distinct())
        {
            if (!members.TryGetValue(deviceId, out var member))
            {
                results.Add(new(deviceId, null, RestartScheduleCancelDeviceOutcome.NotInSchedule));
                continue;
            }

            if (!await _scope.CanActOnDeviceAsync(actorId, schedule.OrganizationId, deviceId, cancellationToken))
            {
                results.Add(new(deviceId, member.Hostname, RestartScheduleCancelDeviceOutcome.NotAuthorized));
                continue;
            }

            if (excluded.Contains(deviceId))
            {
                results.Add(new(deviceId, member.Hostname, RestartScheduleCancelDeviceOutcome.AlreadyExcluded));
                continue;
            }

            _dbContext.Add(new RestartScheduleExclusion(schedule.Id, deviceId, member.Hostname, actorId, actorDisplay, now));
            results.Add(new(deviceId, member.Hostname, RestartScheduleCancelDeviceOutcome.Excluded));
        }

        // Touching the schedule row makes a concurrent dispatch and this
        // exclusion mutually exclusive: whichever saves second loses on xmin,
        // so an exclusion can never land on a schedule that has already gone out.
        schedule.StampUpdated(now);

        AuditCancel(schedule, groupName, actorId, actorDisplay, "devices", results);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return null;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Excluded by someone else between the read and the write. Once
            // more, now that the rows exist to be seen.
            _dbContext.ChangeTracker.Clear();
            var reloaded = await _dbContext.Set<RestartSchedule>().SingleAsync(s => s.Id == schedule.Id, cancellationToken);
            return reloaded.Status == RestartScheduleStatus.Pending
                ? await ExcludeAsync(reloaded, groupName, actorId, actorDisplay, deviceIds, cancellationToken)
                : null;
        }

        return new(RestartScheduleCancelStatus.Ok, await ViewAsync(schedule, groupName, now, cancellationToken), results);
    }

    /// <summary>
    /// Cancels through the devices. Per device, not all-or-nothing, and each
    /// device's bookkeeping is saved on its own right after the task service
    /// has acted for it: that service saves as it goes and clears the change
    /// tracker when it loses a race, so nothing here may be left tracked across
    /// one of its calls and assumed to still be there afterwards.
    /// </summary>
    private async Task<RestartScheduleCancelResult> CancelDispatchedAsync(
        RestartSchedule schedule, string groupName, Guid actorId, string actorDisplay,
        IReadOnlyCollection<Guid>? deviceIds, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var scheduleId = schedule.Id;
        var organizationId = schedule.OrganizationId;
        var rows = await _dbContext.Set<RestartScheduleDevice>().AsNoTracking()
            .Where(d => d.ScheduleId == scheduleId)
            .ToListAsync(cancellationToken);
        var byDevice = rows.ToDictionary(r => r.DeviceId);

        var targets = deviceIds is null ? rows.Select(r => r.DeviceId).ToList() : deviceIds.Distinct().ToList();
        var agents = await _dbContext.Devices.AsNoTracking()
            .Where(d => targets.Contains(d.Id))
            .Select(d => new { d.Id, d.AgentVersion, d.Status })
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        var results = new List<RestartScheduleCancelDeviceResult>(targets.Count);
        foreach (var deviceId in targets)
        {
            if (!byDevice.TryGetValue(deviceId, out var snapshot))
            {
                results.Add(new(deviceId, null, RestartScheduleCancelDeviceOutcome.NotInSchedule));
                continue;
            }

            if (!await _scope.CanActOnDeviceAsync(actorId, organizationId, deviceId, cancellationToken))
            {
                results.Add(new(deviceId, snapshot.Hostname, RestartScheduleCancelDeviceOutcome.NotAuthorized));
                continue;
            }

            var agent = agents.GetValueOrDefault(deviceId);
            var (outcome, record) = await CancelOneAsync(
                organizationId, snapshot, agent?.AgentVersion, agent?.Status == DeviceStatus.Active,
                actorId, actorDisplay, now, cancellationToken);
            results.Add(new(deviceId, snapshot.Hostname, outcome));

            if (record is { } change)
            {
                // Re-read tracked, after the task service's own saves and clears
                // are behind us, and save this device's bookkeeping on its own.
                var row = await _dbContext.Set<RestartScheduleDevice>().SingleAsync(d => d.Id == snapshot.Id, cancellationToken);
                row.RecordCancel(change.Outcome, change.CancelTaskId, now, actorDisplay);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        // The same for the schedule itself: a fresh tracked copy, then one save
        // with the audit entry for the whole operation. Two cancellations in
        // the same instant meet on the row version; the loser's device work is
        // already saved, so it only redoes this last write on top of the winner's.
        for (var attempt = 0; ; attempt++)
        {
            _dbContext.ChangeTracker.Clear();
            schedule = await _dbContext.Set<RestartSchedule>().SingleAsync(s => s.Id == scheduleId, cancellationToken);
            if (deviceIds is null)
            {
                schedule.RecordCancelRequested(now, actorId, actorDisplay);
            }

            AuditCancel(schedule, groupName, actorId, actorDisplay, deviceIds is null ? "department" : "devices", results);
            try
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxConcurrencyRetries)
            {
                _logger.LogInformation("Restart schedule {ScheduleId} changed while its cancellation was recorded; retrying.", scheduleId);
            }
        }

        _logger.LogInformation(
            "Restart schedule {ScheduleId}: cancellation by {Actor} for {Scope}: {Summary}.",
            schedule.Id, actorDisplay, deviceIds is null ? "the department" : $"{targets.Count} device(s)",
            string.Join(", ", results.GroupBy(r => r.Outcome).Select(g => $"{g.Key} {g.Count()}")));

        return new(RestartScheduleCancelStatus.Ok, await ViewAsync(schedule, groupName, now, cancellationToken), results);
    }

    private readonly record struct CancelRecord(RestartCancelOutcome Outcome, Guid? CancelTaskId);

    /// <summary>
    /// What to do about one dispatched device, judged from where its restart is
    /// right now. Acts through the task service and returns what to record on
    /// the device's row; the caller saves that, so this never leaves a tracked
    /// change behind across a task-service call.
    /// </summary>
    private async Task<(RestartScheduleCancelDeviceOutcome Outcome, CancelRecord? Record)> CancelOneAsync(
        Guid organizationId, RestartScheduleDevice row, string? agentVersion, bool deviceActive,
        Guid actorId, string actorDisplay, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (row.DispatchOutcome != RestartDispatchOutcome.Queued || row.RestartTaskId is null)
        {
            return (RestartScheduleCancelDeviceOutcome.NothingToCancel, null);
        }

        var restart = await _dbContext.DeviceTasks.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == row.RestartTaskId.Value, cancellationToken);
        if (restart is null)
        {
            return (RestartScheduleCancelDeviceOutcome.NothingToCancel, null);
        }

        switch (row.CancelOutcome)
        {
            case RestartCancelOutcome.CancelledBeforeDelivery:
                return (RestartScheduleCancelDeviceOutcome.AlreadyCancelled, null);

            case RestartCancelOutcome.Requested when row.CancelTaskId is { } cancelId:
                var cancel = await _dbContext.DeviceTasks.AsNoTracking()
                    .SingleOrDefaultAsync(t => t.Id == cancelId, cancellationToken);
                if (cancel?.Status is DeviceTaskStatus.Queued or DeviceTaskStatus.Delivered)
                {
                    return (RestartScheduleCancelDeviceOutcome.AlreadyRequested, null);
                }

                if (cancel?.Status == DeviceTaskStatus.Succeeded)
                {
                    return (RestartScheduleCancelDeviceOutcome.AlreadyCancelled, null);
                }

                // Failed, expired or gone: the device did not act on it. Try
                // again below if there is still something to cancel.
                break;

            case RestartCancelOutcome.NotApplicable:
                return (RestartScheduleCancelDeviceOutcome.NothingToCancel, null);

            // Unsupported: the agent may have been updated since; re-check below.
        }

        if (restart.Status == DeviceTaskStatus.Queued)
        {
            var cancelled = await _taskService.CancelAsync(
                organizationId, row.DeviceId, restart.Id, actorId, actorDisplay, cancellationToken);
            if (cancelled == TaskCancelResult.Success)
            {
                return (RestartScheduleCancelDeviceOutcome.CancelledBeforeDelivery,
                    new CancelRecord(RestartCancelOutcome.CancelledBeforeDelivery, null));
            }

            // Claimed in the same instant: the device has it now. Re-read and
            // carry on as for a delivered one.
            restart = await _dbContext.DeviceTasks.AsNoTracking().SingleAsync(t => t.Id == restart.Id, cancellationToken);
        }

        var pendingOnDevice = restart.Status == DeviceTaskStatus.Delivered
            || (restart.Status == DeviceTaskStatus.Succeeded && RestartAtOf(restart) is { } at && at > now);
        if (!pendingOnDevice)
        {
            // Already restarted, failed, expired or cancelled: nothing a device can undo.
            return (RestartScheduleCancelDeviceOutcome.NothingToCancel, new CancelRecord(RestartCancelOutcome.NotApplicable, null));
        }

        if (!deviceActive || !DeviceTaskCatalog.IsSupportedBy(CancelDefinition, agentVersion))
        {
            return (RestartScheduleCancelDeviceOutcome.Unsupported, new CancelRecord(RestartCancelOutcome.Unsupported, null));
        }

        var task = await _taskService.QueueAsync(
            organizationId, row.DeviceId, DeviceTaskType.CancelRestart,
            new TaskPayloads.CancelRestart(restart.Id, actorDisplay), actorId, actorDisplay, cancellationToken);
        if (task is null)
        {
            return (RestartScheduleCancelDeviceOutcome.Unsupported, new CancelRecord(RestartCancelOutcome.Unsupported, null));
        }

        return (RestartScheduleCancelDeviceOutcome.CancelRequested, new CancelRecord(RestartCancelOutcome.Requested, task.Id));
    }

    private void AuditCancel(
        RestartSchedule schedule, string groupName, Guid actorId, string actorDisplay, string scopeLabel,
        IReadOnlyList<RestartScheduleCancelDeviceResult> results) =>
        _auditWriter.Stage(schedule.OrganizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "restart_schedule.cancel", AuditResult.Success,
            a => a.OnTarget("restart_schedule", schedule.Id.ToString(), groupName)
                  .Requiring(Domain.Authorization.Permissions.Device.Restart)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      scope = scopeLabel,
                      status = schedule.Status.ToString(),
                      results = results.Select(r => new { hostname = r.Hostname, outcome = r.Outcome.ToString() }),
                  })));

    // ---------------------------------------------------------------- dispatch

    /// <summary>
    /// Sends every schedule whose moment has come, each in its own transaction.
    /// Returns how many schedules were looked at, so a caller can keep going
    /// while a batch is full.
    /// </summary>
    public async Task<int> DispatchDueAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var due = await _dbContext.Set<RestartSchedule>().AsNoTracking()
            .Where(s => s.Status == RestartScheduleStatus.Pending && s.DispatchAt <= now)
            .OrderBy(s => s.DispatchAt)
            .Take(batchSize)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in due)
        {
            await DispatchOneAsync(id, cancellationToken);
        }

        return due.Count;
    }

    private async Task DispatchOneAsync(Guid scheduleId, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var schedule = await _dbContext.Set<RestartSchedule>()
                .SingleOrDefaultAsync(s => s.Id == scheduleId, cancellationToken);
            if (schedule is null || schedule.Status != RestartScheduleStatus.Pending)
            {
                return; // Cancelled or dispatched since the tick began.
            }

            var now = _timeProvider.GetUtcNow();
            var groupName = await GroupNameAsync(schedule.DeviceGroupId, cancellationToken);

            if (now - schedule.DispatchAt > TimeSpan.FromSeconds(_options.MissedAfterSeconds))
            {
                schedule.MarkMissed(now);
                _auditWriter.Stage(schedule.OrganizationId, AuditActorType.System, actorId: null, SystemActorDisplay,
                    action: "restart_schedule.missed", AuditResult.Failure,
                    a => a.OnTarget("restart_schedule", schedule.Id.ToString(), groupName)
                          .WithFailureReason(
                              $"The dispatch moment {schedule.DispatchAt:u} passed more than {_options.MissedAfterSeconds}s ago; the restart was not sent late."));
                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                _logger.LogWarning(
                    "Restart schedule {ScheduleId} for group {GroupId} was missed: due at {DispatchAt:u}, seen at {Now:u}.",
                    schedule.Id, schedule.DeviceGroupId, schedule.DispatchAt, now);
                return;
            }

            var members = await _dbContext.Devices.AsNoTracking()
                .Where(d => d.DeviceGroupId == schedule.DeviceGroupId
                            && d.OrganizationId == schedule.OrganizationId
                            && d.Status == DeviceStatus.Active)
                .OrderBy(d => d.Hostname)
                .Select(d => new { d.Id, d.Hostname, d.LastSeenAt })
                .ToListAsync(cancellationToken);
            var memberIds = members.Select(m => m.Id).ToList();

            var excluded = await _dbContext.Set<RestartScheduleExclusion>().AsNoTracking()
                .Where(e => e.ScheduleId == schedule.Id)
                .Select(e => e.DeviceId)
                .ToHashSetAsync(cancellationToken);

            // Known before anything is queued, so the common case is a clear
            // answer rather than a caught constraint violation.
            var activeRestarts = await _dbContext.DeviceTasks.AsNoTracking()
                .Where(t => t.Type == DeviceTaskType.RestartDevice
                            && (t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Delivered)
                            && memberIds.Contains(t.DeviceId))
                .Select(t => t.DeviceId)
                .ToHashSetAsync(cancellationToken);

            // The scheduler's account as it is now. A disabled, invited-only or
            // deleted administrator's plan is not carried out on their behalf;
            // the scope check per device below covers a narrower loss of
            // authority. Both fail closed.
            var schedulerActive = await _dbContext.PlatformUsers.AsNoTracking()
                .Where(u => u.Id == schedule.CreatedByUserId && u.OrganizationId == schedule.OrganizationId)
                .Select(u => u.Status == PlatformUserStatus.Active)
                .SingleOrDefaultAsync(cancellationToken);
            if (!schedulerActive)
            {
                _logger.LogWarning(
                    "Restart schedule {ScheduleId}: its scheduler {UserId} is no longer an active administrator; no device is restarted on their behalf.",
                    schedule.Id, schedule.CreatedByUserId);
            }

            var grace = schedule.WarningSeconds;
            var payload = new TaskPayloads.RestartOrShutdown(grace, RestartGrace.MessageFor(grace));
            var rows = new List<RestartScheduleDevice>(members.Count);

            foreach (var member in members)
            {
                RestartDispatchOutcome outcome;
                Guid? taskId = null;

                if (excluded.Contains(member.Id))
                {
                    outcome = RestartDispatchOutcome.Excluded;
                }
                else if (!IsOnline(member.LastSeenAt, now))
                {
                    outcome = RestartDispatchOutcome.Offline;
                }
                // The scheduler's authority, re-checked now: an account disabled,
                // or a scope removed, since the schedule was made means the
                // device is not restarted on their behalf.
                else if (!schedulerActive
                         || !await _scope.CanActOnDeviceAsync(schedule.CreatedByUserId, schedule.OrganizationId, member.Id, cancellationToken))
                {
                    outcome = RestartDispatchOutcome.NotAuthorized;
                }
                else if (activeRestarts.Contains(member.Id))
                {
                    outcome = RestartDispatchOutcome.AlreadyInProgress;
                }
                else
                {
                    var task = await _taskService.QueueAsync(
                        schedule.OrganizationId, member.Id, DeviceTaskType.RestartDevice, payload,
                        schedule.CreatedByUserId, schedule.CreatedByDisplay, cancellationToken);
                    outcome = task is null ? RestartDispatchOutcome.NotEligible : RestartDispatchOutcome.Queued;
                    taskId = task?.Id;
                }

                rows.Add(new RestartScheduleDevice(schedule.Id, member.Id, member.Hostname, outcome, taskId));
            }

            schedule.MarkDispatched(now);
            _dbContext.AddRange(rows);

            _auditWriter.Stage(schedule.OrganizationId, AuditActorType.System, actorId: null, SystemActorDisplay,
                action: "restart_schedule.dispatch", AuditResult.Success,
                a => a.OnTarget("restart_schedule", schedule.Id.ToString(), groupName)
                      .WithStateChange(null, JsonSerializer.Serialize(new
                      {
                          scheduledBy = schedule.CreatedByDisplay,
                          restartAt = schedule.RestartAt,
                          graceSeconds = grace,
                          queued = rows.Count(r => r.DispatchOutcome == RestartDispatchOutcome.Queued),
                          results = rows.Select(r => new { hostname = r.Hostname, outcome = r.DispatchOutcome.ToString() }),
                      })));

            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Restart schedule {ScheduleId} dispatched for group {GroupId}: {Queued} queued of {Members} member(s).",
                schedule.Id, schedule.DeviceGroupId, rows.Count(r => r.DispatchOutcome == RestartDispatchOutcome.Queued), rows.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Cancelled, excluded from, or dispatched by someone else while
            // this ran. Their write stands; nothing of this one does.
            await transaction.RollbackAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            _logger.LogInformation("Restart schedule {ScheduleId} changed during dispatch; left as it is.", scheduleId);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // A restart was queued for one of these devices in the same instant
            // from elsewhere. Nothing of this dispatch stands; the next tick
            // sees that restart and reports the device as already in progress.
            await transaction.RollbackAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            _logger.LogWarning("Restart schedule {ScheduleId}: a device gained a restart during dispatch; retrying next tick.", scheduleId);
        }
    }

    // ------------------------------------------------------------------- views

    private async Task<RestartScheduleView> ViewAsync(
        RestartSchedule schedule, string groupName, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var devices = schedule.Status switch
        {
            RestartScheduleStatus.Pending => await PlannedDevicesAsync(schedule, now, cancellationToken),
            RestartScheduleStatus.Dispatched => await DispatchedDevicesAsync(schedule, now, cancellationToken),
            _ => [],
        };

        return new RestartScheduleView(
            schedule.Id, schedule.DeviceGroupId, groupName, schedule.Status.ToString(),
            schedule.RequestedDelaySeconds, schedule.WarningSeconds, schedule.RestartAt, schedule.DispatchAt,
            schedule.CreatedAt, schedule.CreatedByDisplay, schedule.DispatchedAt,
            schedule.CancelledAt, schedule.CancelledByDisplay, schedule.MissedAt,
            CanCancelCleanly: schedule.Status == RestartScheduleStatus.Pending && now < schedule.DispatchAt,
            devices);
    }

    /// <summary>What a pending schedule would do if it went out now: the group's members as they are.</summary>
    private async Task<IReadOnlyList<RestartScheduleDeviceView>> PlannedDevicesAsync(
        RestartSchedule schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var members = await _dbContext.Devices.AsNoTracking()
            .Where(d => d.DeviceGroupId == schedule.DeviceGroupId && d.Status == DeviceStatus.Active)
            .OrderBy(d => d.Hostname)
            .Select(d => new { d.Id, d.Hostname, d.DisplayName, d.AgentVersion, d.LastSeenAt })
            .ToListAsync(cancellationToken);
        var excluded = await _dbContext.Set<RestartScheduleExclusion>().AsNoTracking()
            .Where(e => e.ScheduleId == schedule.Id)
            .ToDictionaryAsync(e => e.DeviceId, cancellationToken);

        return members.Select(m =>
        {
            var online = IsOnline(m.LastSeenAt, now);
            var (state, detail) = excluded.TryGetValue(m.Id, out var exclusion)
                ? (RestartScheduleDeviceState.Excluded, $"Cancelled for this device by {exclusion.ExcludedByDisplay}.")
                : online
                    ? (RestartScheduleDeviceState.WillRestart, null)
                    : (RestartScheduleDeviceState.OfflineNow, "Offline now. It is skipped if it is still offline when the restart goes out.");
            return new RestartScheduleDeviceView(
                m.Id, m.Hostname, m.DisplayName, online, m.AgentVersion,
                DeviceTaskCatalog.IsSupportedBy(CancelDefinition, m.AgentVersion),
                state.ToString(), detail, null, null, null);
        }).ToList();
    }

    /// <summary>What a dispatched schedule did to each device, and where each restart is now.</summary>
    private async Task<IReadOnlyList<RestartScheduleDeviceView>> DispatchedDevicesAsync(
        RestartSchedule schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await _dbContext.Set<RestartScheduleDevice>().AsNoTracking()
            .Where(d => d.ScheduleId == schedule.Id)
            .OrderBy(d => d.Hostname)
            .ToListAsync(cancellationToken);
        var deviceIds = rows.Select(r => r.DeviceId).ToList();
        var live = await _dbContext.Devices.AsNoTracking()
            .Where(d => deviceIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DisplayName, d.AgentVersion, d.LastSeenAt })
            .ToDictionaryAsync(d => d.Id, cancellationToken);
        var taskIds = rows.SelectMany(r => new[] { r.RestartTaskId, r.CancelTaskId }).Where(id => id.HasValue).Select(id => id!.Value).ToList();
        var tasks = await _dbContext.DeviceTasks.AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        return rows.Select(row =>
        {
            var device = live.GetValueOrDefault(row.DeviceId);
            var restart = row.RestartTaskId is { } rid ? tasks.GetValueOrDefault(rid) : null;
            var cancel = row.CancelTaskId is { } cid ? tasks.GetValueOrDefault(cid) : null;
            var supportsCancel = DeviceTaskCatalog.IsSupportedBy(CancelDefinition, device?.AgentVersion);
            var (state, detail) = StateOf(row, restart, cancel, supportsCancel, now);
            return new RestartScheduleDeviceView(
                row.DeviceId, row.Hostname, device?.DisplayName,
                device is not null && IsOnline(device.LastSeenAt, now),
                device?.AgentVersion ?? "",
                supportsCancel,
                state.ToString(), detail, row.RestartTaskId, row.CancelTaskId, restart is null ? null : RestartAtOf(restart));
        }).ToList();
    }

    private static (RestartScheduleDeviceState State, string? Detail) StateOf(
        RestartScheduleDevice row, DeviceTask? restart, DeviceTask? cancel, bool supportsCancel, DateTimeOffset now)
    {
        switch (row.DispatchOutcome)
        {
            case RestartDispatchOutcome.Excluded:
                return (RestartScheduleDeviceState.Excluded, "Cancelled for this device before the restart went out.");
            case RestartDispatchOutcome.Offline:
                return (RestartScheduleDeviceState.SkippedOffline, "Offline when the restart went out, so it was not sent.");
            case RestartDispatchOutcome.AlreadyInProgress:
                return (RestartScheduleDeviceState.SkippedBusy, "Already had a restart queued or in progress from elsewhere.");
            case RestartDispatchOutcome.NotEligible:
                return (RestartScheduleDeviceState.SkippedIneligible, "Retired, or its agent could not take the task.");
            case RestartDispatchOutcome.NotAuthorized:
                return (RestartScheduleDeviceState.SkippedUnauthorized, "The administrator who scheduled it no longer had authority over this device.");
        }

        if (restart is null)
        {
            return (RestartScheduleDeviceState.Failed, "The restart task record is missing.");
        }

        if (restart.Status == DeviceTaskStatus.Cancelled)
        {
            return (RestartScheduleDeviceState.Cancelled, restart.ResultMessage);
        }

        if (cancel is not null)
        {
            switch (cancel.Status)
            {
                case DeviceTaskStatus.Queued:
                case DeviceTaskStatus.Delivered:
                    return (RestartScheduleDeviceState.CancelRequested, "Cancellation sent; waiting for the device to confirm.");
                case DeviceTaskStatus.Succeeded:
                    return (RestartScheduleDeviceState.Cancelled, cancel.ResultMessage);
                case DeviceTaskStatus.Failed:
                    return (RestartScheduleDeviceState.CancelFailed, cancel.ResultMessage);
                case DeviceTaskStatus.Expired:
                    return (RestartScheduleDeviceState.CancelFailed, "The device did not pick up the cancellation in time.");
            }
        }

        var pendingOnDevice = restart.Status is DeviceTaskStatus.Queued or DeviceTaskStatus.Delivered
            || (restart.Status == DeviceTaskStatus.Succeeded && RestartAtOf(restart) is { } at && at > now);
        // Judged from the agent as it is NOW, not as it was when the
        // cancellation was refused: a device updated since can be cancelled
        // from the page again, and the service re-checks it the same way.
        if (row.CancelOutcome == RestartCancelOutcome.Unsupported && pendingOnDevice && !supportsCancel)
        {
            return (RestartScheduleDeviceState.CancelUnsupported,
                $"This device's agent cannot cancel a restart it has accepted; agent {CancelDefinition.MinimumAgentVersion} or later is needed.");
        }

        return restart.Status switch
        {
            DeviceTaskStatus.Queued => (RestartScheduleDeviceState.Queued, "Waiting for the device to check in."),
            DeviceTaskStatus.Delivered => (RestartScheduleDeviceState.Executing, "The device has the task and has not reported yet."),
            DeviceTaskStatus.Succeeded => RestartAtOf(restart) is { } when2 && when2 > now
                ? (RestartScheduleDeviceState.Scheduled, restart.ResultMessage)
                : (RestartScheduleDeviceState.Restarted, restart.ResultMessage),
            DeviceTaskStatus.Failed => (RestartScheduleDeviceState.Failed, restart.ResultMessage),
            // Expired before the device claimed it: nothing restarted. Expired
            // after it was delivered: the device took it and never reported, so
            // it may or may not have restarted; its heartbeat is the only proof.
            DeviceTaskStatus.Expired => restart.DeliveredAt is null
                ? (RestartScheduleDeviceState.Expired, "The device did not pick up the restart in time; nothing restarted.")
                : (RestartScheduleDeviceState.Expired, "The device took the restart but never reported back; check its last heartbeat."),
            _ => (RestartScheduleDeviceState.Failed, restart.ResultMessage),
        };
    }

    // --------------------------------------------------------------- helpers

    private async Task<string> GroupNameAsync(Guid groupId, CancellationToken cancellationToken) =>
        await _dbContext.DeviceGroups.AsNoTracking()
            .Where(g => g.Id == groupId)
            .Select(g => g.Name)
            .SingleAsync(cancellationToken);

    private bool IsOnline(DateTimeOffset? lastSeenAt, DateTimeOffset now) =>
        lastSeenAt is { } seen && now - seen <= _staleAfter;

    /// <summary>When Windows said it would act, from the restart task's result; null until the device reported.</summary>
    internal static DateTimeOffset? RestartAtOf(DeviceTask restart)
    {
        if (restart.ResultJson is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(restart.ResultJson);
            return doc.RootElement.TryGetProperty("restartAt", out var at) && at.ValueKind == JsonValueKind.String
                && at.TryGetDateTimeOffset(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

// ------------------------------------------------------------------ results

public enum RestartScheduleCreateStatus
{
    Created,
    InvalidDelay,
    GroupNotFound,
    /// <summary>The department already has a pending schedule; it is returned.</summary>
    AlreadyScheduled,
}

public sealed record RestartScheduleCreateResult(RestartScheduleCreateStatus Status, RestartScheduleView? Schedule);

public enum RestartScheduleCancelStatus
{
    Ok,
    NotFound,
    /// <summary>Already cancelled or missed; nothing to do.</summary>
    NotCancellable,
    /// <summary>The schedule kept changing under this request; the caller may simply try again.</summary>
    Conflict,
}

/// <summary>What a cancellation did about one device.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RestartScheduleCancelDeviceOutcome>))]
public enum RestartScheduleCancelDeviceOutcome
{
    /// <summary>Taken out of a pending schedule; it will be skipped at dispatch.</summary>
    Excluded,
    AlreadyExcluded,
    /// <summary>Its restart task had not been delivered and was cancelled where it sat.</summary>
    CancelledBeforeDelivery,
    /// <summary>A CancelRestart task was queued; the device confirms on its next check-in.</summary>
    CancelRequested,
    /// <summary>A cancellation is already on its way to the device.</summary>
    AlreadyRequested,
    AlreadyCancelled,
    /// <summary>The device's agent cannot abort an accepted restart.</summary>
    Unsupported,
    /// <summary>Nothing pending for this device: it was never targeted, or its restart already ran, failed or expired.</summary>
    NothingToCancel,
    NotInSchedule,
    NotAuthorized,
}

public sealed record RestartScheduleCancelDeviceResult(Guid DeviceId, string? Hostname, RestartScheduleCancelDeviceOutcome Outcome);

public sealed record RestartScheduleCancelResult(
    RestartScheduleCancelStatus Status, RestartScheduleView? Schedule, IReadOnlyList<RestartScheduleCancelDeviceResult> Devices);

// -------------------------------------------------------------------- views

/// <summary>Where one device stands in a schedule, as the page names it.</summary>
public enum RestartScheduleDeviceState
{
    // Before dispatch
    WillRestart,
    Excluded,
    OfflineNow,

    // After dispatch, from the restart task
    Queued,
    Executing,
    Scheduled,
    Restarted,
    Failed,
    Expired,
    Cancelled,

    // After dispatch, from a cancellation
    CancelRequested,
    CancelFailed,
    CancelUnsupported,

    // Not targeted at dispatch
    SkippedOffline,
    SkippedBusy,
    SkippedIneligible,
    SkippedUnauthorized,
}

public sealed record RestartScheduleDeviceView(
    Guid DeviceId, string Hostname, string? DisplayName, bool IsOnline, string AgentVersion, bool SupportsCancel,
    string State, string? Detail, Guid? RestartTaskId, Guid? CancelTaskId, DateTimeOffset? RestartAt);

public sealed record RestartScheduleView(
    Guid Id, Guid GroupId, string GroupName, string Status,
    int RequestedDelaySeconds, int WarningSeconds, DateTimeOffset RestartAt, DateTimeOffset DispatchAt,
    DateTimeOffset CreatedAt, string CreatedByDisplay, DateTimeOffset? DispatchedAt,
    DateTimeOffset? CancelledAt, string? CancelledByDisplay, DateTimeOffset? MissedAt,
    bool CanCancelCleanly, IReadOnlyList<RestartScheduleDeviceView> Devices);

/// <param name="WarningSeconds">
/// The configured lead time, so the console can say what a new schedule will
/// do before it is confirmed. A schedule's own value is on the schedule.
/// </param>
public sealed record RestartScheduleGroupView(
    Guid Id, string Name, bool IsBuiltIn, int DeviceCount, int OnlineCount, int WarningSeconds);

public sealed record RestartScheduleOverview(
    RestartScheduleGroupView Group, RestartScheduleView? Active, IReadOnlyList<RestartScheduleView> Recent);
