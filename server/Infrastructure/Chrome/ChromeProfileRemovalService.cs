using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Chrome;

public enum ChromeProfileRemovalOutcome
{
    Queued = 0,

    /// <summary>
    /// No such profile in the caller's organization, its device is missing or
    /// retired, or the device is outside the caller's scope -- all answered
    /// alike, so a profile id cannot confirm that an unreachable device exists.
    /// </summary>
    NotFound = 1,

    /// <summary>The device's agent is too old to carry the removal executor.</summary>
    AgentTooOld = 2,
}

/// <summary>The outcome of queuing one profile removal.</summary>
/// <param name="RestartTask">
/// The follow-up restart, when one was asked for AND queued. Null when no restart
/// was requested, and also when one was requested but the device already has a
/// restart queued or delivered -- the machine is coming down anyway, so a second
/// task would only be refused by the one-restart-per-device index.
/// </param>
public sealed record ChromeProfileRemovalResult(
    ChromeProfileRemovalOutcome Outcome, DeviceTask? Task, string? Error, DeviceTask? RestartTask = null);

/// <summary>
/// Turns an administrator's decision to remove one Chrome profile into one
/// <c>RemoveChromeProfile</c> task for the profile's device.
/// </summary>
/// <remarks>
/// <para>
/// <b>The payload is the stored row, never the request.</b> The route carries
/// only the profile row's id; the SID, profile key, path and expected account all
/// come from what the device itself last reported. A client cannot steer the
/// delete at anything the inventory does not already describe, and the agent
/// re-derives and re-checks every one of those facts on the endpoint before it
/// touches a file.
/// </para>
/// <para>
/// <b>Scope is checked on the profile's device, here.</b> The endpoint cannot:
/// the route names a profile, and which device that is -- and so whether the
/// caller may act on it -- is only known once the row is loaded. Out of scope,
/// missing and retired all answer <see cref="ChromeProfileRemovalOutcome.NotFound"/>,
/// matching every other device route.
/// </para>
/// <para>
/// <b>Refusals are told apart.</b> An agent below the executor's floor is
/// <see cref="ChromeProfileRemovalOutcome.AgentTooOld"/> (the endpoint's 409),
/// never a silent queue of work nothing will run and never a 404 the operator
/// can see through. The floor is 1.26.0 -- the agent that completes the task
/// even while Chrome is open, as "scheduled", instead of failing it -- so an
/// operator is never left reading a fail-fast refusal from a 1.25.0 agent as
/// the removal breaking.
/// </para>
/// <para>
/// <b>An optional restart rides along, reusing the restart action whole.</b>
/// When asked, the follow-up is exactly what the single-device restart route
/// queues -- the same <see cref="DeviceTaskType.RestartDevice"/> task, the same
/// <see cref="TaskPayloads.RestartOrShutdown"/> payload with
/// <see cref="RestartGrace.ImmediateSeconds"/> and
/// <see cref="RestartGrace.MessageFor"/>'s notice, through the same
/// <see cref="DeviceTaskService.QueueAsync"/> with its own
/// <c>task.queue.restartdevice</c> audit. The caller's <c>device.restart</c>
/// permission is the endpoint's to check, as it is on the restart route itself.
/// A device that already has a restart queued or delivered keeps it: the reboot
/// the operator wanted is already coming, so none is added and the result's
/// <see cref="ChromeProfileRemovalResult.RestartTask"/> stays null.
/// </para>
/// <para>
/// <b>Audited twice, on purpose.</b> <see cref="DeviceTaskService.QueueAsync"/>
/// records the queued task with its full payload, as it does for every task;
/// this service additionally records the decision itself --
/// <c>chrome.profile.remove</c>, naming the profile key and the account the
/// console believed was signed in -- so the audit trail answers "who removed
/// whose profile" without parsing task payloads.
/// </para>
/// </remarks>
public sealed class ChromeProfileRemovalService(
    EndpointPlatformDbContext dbContext,
    DeviceScopeAuthorizer scope,
    DeviceTaskService taskService,
    AuditWriter auditWriter,
    ILogger<ChromeProfileRemovalService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly DeviceScopeAuthorizer _scope = scope
        ?? throw new ArgumentNullException(nameof(scope));

    private readonly DeviceTaskService _taskService = taskService
        ?? throw new ArgumentNullException(nameof(taskService));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly ILogger<ChromeProfileRemovalService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Queues the removal of one profile, identified by its inventory row, and --
    /// when <paramref name="restartDevice"/> asks for it -- the same restart task
    /// the single-device restart route queues, after it. The endpoint has already
    /// checked the caller holds <c>chrome.manage</c> (and <c>device.restart</c>
    /// when a restart is asked for); device scope is checked here, on the device
    /// the row belongs to.
    /// </summary>
    public async Task<ChromeProfileRemovalResult> QueueRemovalAsync(
        Guid organizationId, Guid profileId, Guid actorId, string actorDisplay,
        bool restartDevice = false,
        CancellationToken cancellationToken = default)
    {
        var row = await (
                from profile in _dbContext.ChromeProfiles.AsNoTracking()
                join device in _dbContext.Devices.AsNoTracking() on profile.DeviceId equals device.Id
                where profile.Id == profileId && device.OrganizationId == organizationId
                select new { Profile = profile, Device = device })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null || row.Device.Status == DeviceStatus.Retired)
        {
            return NotFound();
        }

        if (!await _scope.CanActOnDeviceAsync(actorId, organizationId, row.Device.Id, cancellationToken))
        {
            return NotFound();
        }

        var definition = DeviceTaskCatalog.Require(DeviceTaskType.RemoveChromeProfile);
        if (!DeviceTaskCatalog.IsSupportedBy(definition, row.Device.AgentVersion))
        {
            return new ChromeProfileRemovalResult(
                ChromeProfileRemovalOutcome.AgentTooOld, null,
                $"This device runs agent {row.Device.AgentVersion}; removing a Chrome profile needs "
                + $"{definition.MinimumAgentVersion} or newer. Update the agent first.");
        }

        // Entirely from the stored row. The agent treats every field as a pin to
        // verify, so a removal decided on a stale inventory is refused on the
        // endpoint rather than deleting whatever lives there now.
        var payload = new TaskPayloads.RemoveChromeProfile(
            row.Profile.UserSid,
            row.Profile.ProfileKey,
            row.Profile.AccountEmail,
            row.Profile.ProfilePath);

        var task = await _taskService.QueueAsync(
            organizationId, row.Device.Id, DeviceTaskType.RemoveChromeProfile, payload,
            actorId, actorDisplay, cancellationToken);

        if (task is null)
        {
            // The gates above passed, so this is the losing side of a race with a
            // retirement or an agent downgrade. Absent, like the other routes.
            return NotFound();
        }

        // After the removal, so the device claims and runs them in that order:
        // the removal first (completing immediately -- done, or scheduled while
        // Chrome is open), then the restart that lets a scheduled one finish at
        // the boot it causes.
        var restartTask = restartDevice
            ? await QueueRestartAsync(organizationId, row.Device.Id, actorId, actorDisplay, cancellationToken)
            : null;

        _auditWriter.Stage(
            organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "chrome.profile.remove", AuditResult.Success,
            a => a.OnDevice(row.Device.Id, row.Device.Hostname)
                  .OnTarget("chrome_profile", row.Profile.ProfileKey,
                      row.Profile.AccountEmail ?? row.Profile.ProfileKey)
                  .Requiring(Permissions.Chrome.Manage)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      profileId = row.Profile.Id,
                      userSid = row.Profile.UserSid,
                      profileKey = row.Profile.ProfileKey,
                      expectedEmail = row.Profile.AccountEmail,
                      taskId = task.Id,
                      restartTaskId = restartTask?.Id,
                  })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Chrome profile {ProfileKey} ({Account}) on device {DeviceId} queued for removal "
            + "by {Actor} as task {TaskId}{Restart}.",
            row.Profile.ProfileKey, row.Profile.AccountEmail ?? "no account", row.Device.Id,
            actorDisplay, task.Id,
            restartTask is null ? "" : $" with restart task {restartTask.Id}");

        return new ChromeProfileRemovalResult(ChromeProfileRemovalOutcome.Queued, task, null, restartTask);
    }

    /// <summary>
    /// Queues the follow-up restart exactly as the single-device restart route
    /// does: the same task type, the same immediate grace period and the same
    /// system-defined notice, through the same task service -- which writes the
    /// restart's own <c>task.queue.restartdevice</c> audit entry, as it does for
    /// the route. Null when the device already has a restart queued or delivered:
    /// the reboot is already coming, and the database's
    /// one-active-restart-per-device index would refuse a second anyway.
    /// </summary>
    private async Task<DeviceTask?> QueueRestartAsync(
        Guid organizationId, Guid deviceId, Guid actorId, string actorDisplay,
        CancellationToken cancellationToken)
    {
        var inFlight = await _dbContext.DeviceTasks.AsNoTracking()
            .AnyAsync(t => t.DeviceId == deviceId
                           && t.Type == DeviceTaskType.RestartDevice
                           && (t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Delivered),
                cancellationToken);
        if (inFlight)
        {
            return null;
        }

        try
        {
            return await _taskService.QueueAsync(
                organizationId, deviceId, DeviceTaskType.RestartDevice,
                new TaskPayloads.RestartOrShutdown(
                    RestartGrace.ImmediateSeconds, RestartGrace.MessageFor(RestartGrace.ImmediateSeconds)),
                actorId, actorDisplay, cancellationToken);
        }
        catch (DbUpdateException ex) when (DeviceTaskService.IsDuplicateActiveTask(ex))
        {
            // Lost a race with another restart request between the check above and
            // the insert. The winner's restart is the one that exists, and the
            // removal queued before this is already committed -- so detach the
            // refused rows (the restart task and its staged audit are the only
            // Added entries at this point) and answer as the pre-check would have.
            foreach (var entry in _dbContext.ChangeTracker.Entries()
                         .Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return null;
        }
    }

    private static ChromeProfileRemovalResult NotFound() =>
        new(ChromeProfileRemovalOutcome.NotFound, null, "No such Chrome profile.");
}
