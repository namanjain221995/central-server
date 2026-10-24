using System.Text.Json;
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

namespace EndpointPlatform.Infrastructure.Chrome;

/// <summary>
/// Removes one Google account's Chrome profiles everywhere: on every active device
/// in an organization, a group, or an explicit selection, one
/// <c>RemoveChromeProfile</c> task per matching inventory row -- optionally
/// followed, per device that got at least one removal, by the ordinary restart task.
/// </summary>
/// <remarks>
/// <para>
/// <b>The account is the request; every payload is a stored row.</b> The caller
/// names an e-mail address, and each device's matching <c>chrome_profiles</c> rows
/// -- the signed-in account matched case-insensitively, the way Google treats
/// addresses -- supply the SID, key, path and expected account exactly as the
/// single-profile route derives them, so a fleet-wide removal cannot aim at
/// anything the inventory does not already describe. A device may hold the account
/// in several profiles across several Windows users; each row is its own task,
/// because each is its own directory on the endpoint.
/// </para>
/// <para>
/// <b>The "Default" profile is never removable</b>, here exactly as on the agent
/// and in the single-profile payload contract: matches whose key is Default are
/// counted (<c>skippedDefault</c>) and never queued, so the tally says why a
/// signed-in machine kept the account rather than reading as a miss.
/// </para>
/// <para>
/// <b>One transaction under the execution strategy</b>, for the same reason as
/// <see cref="Devices.BulkLocalAccountService"/>: the task service commits each
/// queued task as it makes it, so a failure mid-fleet must take the whole batch
/// back rather than leave some machines changed and the operation reported as
/// failed. Restarts are pre-checked against the in-flight set in one query, so the
/// one-active-restart-per-device index is not tripped inside the transaction;
/// losing the remaining sliver of race fails the whole request cleanly queued-nothing,
/// which the endpoint reports as a conflict to retry.
/// </para>
/// <para>
/// <b>Every target is authorized server-side.</b> A group or device-id list from
/// the client is re-resolved against the caller's organization and device scope; a
/// device the caller may not see is dropped during resolution and never touched --
/// the same <c>ScopedDeviceIdsOrNullAsync</c> intersection the bulk local-account
/// fan-out uses. Devices whose agent predates the 1.26.0 removal executor are
/// counted (<c>rejectedAgentTooOld</c>), not failed: a fleet-wide action reports
/// the stragglers instead of refusing the fleet.
/// </para>
/// <para>
/// <b>Audited at both levels.</b> One <c>chrome.account.bulk_remove</c> entry
/// records the decision and its tallies, in the shape of the
/// <c>device.local_user.bulk_*</c> entries; every queued task keeps the per-task
/// audit <see cref="DeviceTaskService.QueueAsync"/> already writes.
/// </para>
/// </remarks>
public sealed class BulkChromeProfileRemovalService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<AgentServerOptions> agentServerOptions,
    ILogger<BulkChromeProfileRemovalService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly DeviceTaskService _taskService = taskService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>The console's one definition of offline: no heartbeat for this long.</summary>
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);

    private readonly ILogger<BulkChromeProfileRemovalService> _logger = logger;

    /// <summary>
    /// Queues the account's profile removals (and, when asked, the follow-up
    /// restarts) across every resolved device. Validation that does not depend on a
    /// device -- the e-mail's shape, the target's shape, the restart permission --
    /// is the caller's to do once up front; this fans out and tallies. Returns
    /// <see cref="BulkChromeProfileRemovalOutcome.NotFound"/> when the target
    /// resolves to no devices the caller may act on.
    /// </summary>
    public Task<BulkChromeProfileRemovalResult> RemoveAccountProfilesAsync(
        Guid organizationId, string accountEmail, bool restartDevices,
        bool allDevices, Guid? groupId, IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() => RemoveInTransactionAsync(
            organizationId, accountEmail, restartDevices,
            allDevices, groupId, deviceIds, scopedDeviceIds, actorId, actorDisplay, cancellationToken));
    }

    private async Task<BulkChromeProfileRemovalResult> RemoveInTransactionAsync(
        Guid organizationId, string accountEmail, bool restartDevices,
        bool allDevices, Guid? groupId, IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay,
        CancellationToken cancellationToken)
    {
        // A retried attempt starts clean; nothing from a failed one may be re-saved.
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var targets = await ResolveTargetsAsync(
            organizationId, allDevices, groupId, deviceIds, scopedDeviceIds, cancellationToken);
        if (targets.Count == 0)
        {
            return BulkChromeProfileRemovalResult.NotFound();
        }

        // Google treats the address case-insensitively, and so does this match --
        // the same lowered comparison a bulk account action uses for usernames.
        var targetIds = targets.Select(t => t.Id).ToHashSet();
        var lowered = accountEmail.Trim().ToLowerInvariant();
        var matches = await _dbContext.ChromeProfiles.AsNoTracking()
            .Where(p => targetIds.Contains(p.DeviceId)
                        && p.AccountEmail != null && p.AccountEmail.ToLower() == lowered)
            .Select(p => new { p.DeviceId, p.UserSid, p.ProfileKey, p.AccountEmail, p.ProfilePath })
            .ToListAsync(cancellationToken);
        var matchesByDevice = matches
            .GroupBy(p => p.DeviceId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Restarts already in flight are known before anything is queued, exactly as
        // a group restart knows them -- doubly important here, where a constraint
        // violation inside the transaction would take the whole batch down.
        var activeRestarts = restartDevices
            ? await _dbContext.DeviceTasks.AsNoTracking()
                .Where(t => t.Type == DeviceTaskType.RestartDevice
                            && (t.Status == DeviceTaskStatus.Queued || t.Status == DeviceTaskStatus.Delivered)
                            && targetIds.Contains(t.DeviceId))
                .Select(t => t.DeviceId)
                .ToHashSetAsync(cancellationToken)
            : [];

        var definition = DeviceTaskCatalog.Require(DeviceTaskType.RemoveChromeProfile);
        var now = _timeProvider.GetUtcNow();
        var tally = new Tally();

        foreach (var target in targets)
        {
            if (!matchesByDevice.TryGetValue(target.Id, out var rows))
            {
                // The device never reported the account in any profile; nothing to
                // remove there, and the tally says so rather than queueing a task
                // the agent would refuse as a mismatch.
                tally.SkippedNoProfile++;
                continue;
            }

            // Default is never removable anywhere, so these rows are counted out
            // before the agent gate: no agent version makes them removable.
            var removable = new List<TaskPayloads.RemoveChromeProfile>(rows.Count);
            foreach (var row in rows)
            {
                if (string.Equals(row.ProfileKey, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    tally.SkippedDefault++;
                    continue;
                }

                removable.Add(new TaskPayloads.RemoveChromeProfile(
                    row.UserSid, row.ProfileKey, row.AccountEmail, row.ProfilePath));
            }

            if (removable.Count == 0)
            {
                // Only the Default profile matched; already tallied above.
                continue;
            }

            if (!DeviceTaskCatalog.IsSupportedBy(definition, target.AgentVersion))
            {
                tally.RejectedAgentTooOld++;
                continue;
            }

            var queuedHere = 0;
            foreach (var payload in removable)
            {
                var task = await _taskService.QueueAsync(
                    organizationId, target.Id, DeviceTaskType.RemoveChromeProfile, payload,
                    actorId, actorDisplay, cancellationToken);

                if (task is not null)
                {
                    queuedHere++;
                }
            }

            if (queuedHere == 0)
            {
                // The gates above passed, so this is the losing side of a race with
                // a retirement or an agent downgrade; nothing was queued here.
                continue;
            }

            tally.QueuedRemovals += queuedHere;
            if (target.LastSeenAt is null || now - target.LastSeenAt > _staleAfter)
            {
                tally.Offline++;
            }

            if (restartDevices && !activeRestarts.Contains(target.Id))
            {
                var restart = await _taskService.QueueAsync(
                    organizationId, target.Id, DeviceTaskType.RestartDevice,
                    new TaskPayloads.RestartOrShutdown(
                        RestartGrace.ImmediateSeconds, RestartGrace.MessageFor(RestartGrace.ImmediateSeconds)),
                    actorId, actorDisplay, cancellationToken);

                if (restart is not null)
                {
                    tally.QueuedRestarts++;
                }
            }
        }

        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action: "chrome.account.bulk_remove", AuditResult.Success,
            a => a.OnTarget("chrome_account_bulk", accountEmail, accountEmail)
                  .Requiring(Domain.Authorization.Permissions.Chrome.Manage)
                  .WithStateChange(null, JsonSerializer.Serialize(new
                  {
                      accountEmail,
                      targetedDevices = targets.Count,
                      queuedRemovals = tally.QueuedRemovals,
                      queuedRestarts = tally.QueuedRestarts,
                      skippedNoProfile = tally.SkippedNoProfile,
                      skippedDefault = tally.SkippedDefault,
                      rejectedAgentTooOld = tally.RejectedAgentTooOld,
                      offline = tally.Offline,
                  })));

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Bulk Chrome profile removal for '{Account}' by {Actor}: {Targeted} device(s) targeted, "
            + "{Removals} removal(s) queued ({Offline} offline) and {Restarts} restart(s), "
            + "{NoProfile} without the account, {Default} Default match(es) skipped, {TooOld} agent(s) too old.",
            accountEmail, actorDisplay, targets.Count, tally.QueuedRemovals, tally.Offline,
            tally.QueuedRestarts, tally.SkippedNoProfile, tally.SkippedDefault, tally.RejectedAgentTooOld);

        return BulkChromeProfileRemovalResult.Completed(
            targets.Count, tally.QueuedRemovals, tally.QueuedRestarts,
            tally.SkippedNoProfile, tally.SkippedDefault, tally.RejectedAgentTooOld, tally.Offline);
    }

    /// <summary>
    /// The devices the fan-out acts on: all active devices in the org, a group's
    /// active members, or an explicit selection's active devices -- then every one
    /// intersected with the caller's device scope, exactly as the bulk local-account
    /// fan-out resolves its targets. Active only in every shape: this feature's
    /// tallies name why a live device was skipped, and a retired device is not a
    /// target at all rather than a flavour of skip.
    /// </summary>
    private async Task<List<TargetDevice>> ResolveTargetsAsync(
        Guid organizationId, bool allDevices, Guid? groupId, IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds, CancellationToken cancellationToken)
    {
        List<TargetDevice> devices;
        if (allDevices)
        {
            devices = await _dbContext.Devices.AsNoTracking()
                .Where(d => d.OrganizationId == organizationId && d.Status == DeviceStatus.Active)
                .OrderBy(d => d.Hostname)
                .Select(d => new TargetDevice(d.Id, d.Hostname, d.AgentVersion, d.LastSeenAt))
                .ToListAsync(cancellationToken);
        }
        else if (groupId is { } group)
        {
            devices = await _dbContext.Devices.AsNoTracking()
                .Where(d => d.DeviceGroupId == group
                            && d.OrganizationId == organizationId
                            && d.Status == DeviceStatus.Active)
                .OrderBy(d => d.Hostname)
                .Select(d => new TargetDevice(d.Id, d.Hostname, d.AgentVersion, d.LastSeenAt))
                .ToListAsync(cancellationToken);
        }
        else
        {
            var targeted = (deviceIds ?? []).ToHashSet();
            devices = await _dbContext.Devices.AsNoTracking()
                .Where(d => targeted.Contains(d.Id)
                            && d.OrganizationId == organizationId
                            && d.Status == DeviceStatus.Active)
                .OrderBy(d => d.Hostname)
                .Select(d => new TargetDevice(d.Id, d.Hostname, d.AgentVersion, d.LastSeenAt))
                .ToListAsync(cancellationToken);
        }

        if (scopedDeviceIds is null)
        {
            return devices;
        }

        var visible = scopedDeviceIds.ToHashSet();
        return devices.Where(d => visible.Contains(d.Id)).ToList();
    }

    private sealed record TargetDevice(Guid Id, string Hostname, string? AgentVersion, DateTimeOffset? LastSeenAt);

    /// <summary>Running counts over the fan-out.</summary>
    private sealed class Tally
    {
        public int QueuedRemovals { get; set; }

        public int QueuedRestarts { get; set; }

        public int SkippedNoProfile { get; set; }

        public int SkippedDefault { get; set; }

        public int RejectedAgentTooOld { get; set; }

        public int Offline { get; set; }
    }
}

/// <summary>What a bulk account removal came to.</summary>
public enum BulkChromeProfileRemovalOutcome
{
    /// <summary>The target resolved to devices and the per-profile tasks were fanned out.</summary>
    Completed,

    /// <summary>Nothing resolved: an unknown or foreign group, or a selection all out of org/scope.</summary>
    NotFound,
}

/// <summary>
/// The outcome of a bulk account removal. Counts are meaningful only when
/// <see cref="Outcome"/> is <see cref="BulkChromeProfileRemovalOutcome.Completed"/>.
/// </summary>
/// <param name="TargetedDevices">Active, in-scope devices the target resolved to.</param>
/// <param name="QueuedRemovals">Removal tasks queued -- one per matching, removable profile row.</param>
/// <param name="QueuedRestarts">Restart tasks queued -- at most one per device that got a removal.</param>
/// <param name="SkippedNoProfile">Devices that reported no profile signed in to the account.</param>
/// <param name="SkippedDefault">Matching profile rows that are the never-removable Default profile.</param>
/// <param name="RejectedAgentTooOld">Devices with removable matches whose agent predates the executor.</param>
/// <param name="Offline">Of the devices with queued removals, those offline now -- the tasks wait for reconnect.</param>
public sealed record BulkChromeProfileRemovalResult(
    BulkChromeProfileRemovalOutcome Outcome,
    int TargetedDevices, int QueuedRemovals, int QueuedRestarts,
    int SkippedNoProfile, int SkippedDefault, int RejectedAgentTooOld, int Offline)
{
    public static BulkChromeProfileRemovalResult Completed(
        int targetedDevices, int queuedRemovals, int queuedRestarts,
        int skippedNoProfile, int skippedDefault, int rejectedAgentTooOld, int offline) =>
        new(BulkChromeProfileRemovalOutcome.Completed,
            targetedDevices, queuedRemovals, queuedRestarts,
            skippedNoProfile, skippedDefault, rejectedAgentTooOld, offline);

    public static BulkChromeProfileRemovalResult NotFound() =>
        new(BulkChromeProfileRemovalOutcome.NotFound, 0, 0, 0, 0, 0, 0, 0);
}
