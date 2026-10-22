using System.Text.Json;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Infrastructure.Devices;

/// <summary>
/// Applies ONE local-account change across a fleet: create a Windows account, reset
/// an account's password, or disable, enable or delete an account, on every active
/// device in an organization, a group, or an explicit selection.
/// </summary>
/// <remarks>
/// <para>
/// It owns no device logic of its own. It resolves the target set exactly as the
/// Announcements and screen-lock features do, then calls the per-device
/// <see cref="LocalAccountManagementService"/> once per device — so the username
/// validation, the ephemeral-secret handover (the plaintext never persists; only a
/// one-time reference reaches the task), the agent-version gate and the per-task
/// audit entry are identical whether the account is created from a single device's
/// page or from here.
/// </para>
/// <para>
/// <b>One transaction under the execution strategy</b>, for the same reason as
/// <see cref="ScreenLocks.ScreenLockService"/>: the task service commits each queued
/// task as it makes it, so a failure mid-fleet must take the whole batch back rather
/// than leave some machines changed and the operation reported as failed.
/// </para>
/// <para>
/// <b>Every target is authorized server-side.</b> A group or device-id list from the
/// client is re-resolved against the caller's organization and device scope; a device
/// the caller may not see is dropped during resolution and never touched. "All
/// devices" is the organization's active devices intersected with that same scope.
/// </para>
/// </remarks>
public sealed class BulkLocalAccountService(
    EndpointPlatformDbContext dbContext,
    LocalAccountManagementService localAccounts,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    IOptions<AgentServerOptions> agentServerOptions,
    ILogger<BulkLocalAccountService> logger)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly LocalAccountManagementService _localAccounts = localAccounts;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;

    /// <summary>The console's one definition of offline: no heartbeat for this long.</summary>
    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);

    private readonly ILogger<BulkLocalAccountService> _logger = logger;

    // ------------------------------------------------------------------ create

    /// <summary>
    /// Creates the same account on every resolved device. Validation that does not
    /// depend on the device (username, password length, account type) is the caller's
    /// to do once up front; this fans the per-device create out and tallies the
    /// outcome. Returns <see cref="BulkLocalAccountOutcome.NotFound"/> when the target
    /// resolves to no devices the caller may act on.
    /// </summary>
    public Task<BulkLocalAccountResult> CreateUsersAsync(
        Guid organizationId,
        string username, string? fullName, string? description, string password,
        bool enabled, bool mustChangePassword, bool administrator,
        bool allDevices, Guid? groupId, IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() => CreateInTransactionAsync(
            organizationId, username, fullName, description, password,
            enabled, mustChangePassword, administrator,
            allDevices, groupId, deviceIds, scopedDeviceIds, actorId, actorDisplay, cancellationToken));
    }

    private async Task<BulkLocalAccountResult> CreateInTransactionAsync(
        Guid organizationId,
        string username, string? fullName, string? description, string password,
        bool enabled, bool mustChangePassword, bool administrator,
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
            return BulkLocalAccountResult.NotFound();
        }

        var now = _timeProvider.GetUtcNow();
        var tally = new Tally();

        foreach (var target in targets)
        {
            if (target.Status == DeviceStatus.Retired)
            {
                tally.Skipped++;
                continue;
            }

            var context = new LocalAccountRequestContext(organizationId, target.Id, actorId, actorDisplay);
            var result = await _localAccounts.CreateUserAsync(
                context, username, fullName, description, password,
                enabled, mustChangePassword, administrator,
                additionalGroups: null, profileKey: null, cancellationToken);

            tally.Record(result, target, now, _staleAfter);
        }

        StageAudit(organizationId, actorId, actorDisplay,
            action: "device.local_user.bulk_create",
            permission: Domain.Authorization.Permissions.LocalUser.Create,
            targetDisplay: username,
            state: new
            {
                username,
                accountType = administrator ? "Administrator" : "StandardUser",
                targeted = targets.Count,
                tally.Queued,
                tally.Rejected,
                tally.Offline,
                tally.Skipped,
            });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Bulk local-account create '{Username}' ({Type}) by {Actor}: {Targeted} targeted, {Queued} queued ({Offline} offline), {Rejected} rejected, {Skipped} skipped.",
            username, administrator ? "Administrator" : "StandardUser", actorDisplay,
            targets.Count, tally.Queued, tally.Offline, tally.Rejected, tally.Skipped);

        return BulkLocalAccountResult.Completed(targets.Count, tally.Queued, tally.Rejected, tally.Offline, tally.Skipped);
    }

    // ---------------------------------------------------------- reset password

    /// <summary>
    /// Resets the password of an existing account, named by username, on every
    /// resolved device. The same username is a different SID on every machine, so the
    /// SID is resolved per device from the last reported inventory; a device that has
    /// not reported the account is skipped rather than guessed at.
    /// </summary>
    public Task<BulkLocalAccountResult> ResetPasswordsAsync(
        Guid organizationId,
        string username, string password,
        bool allDevices, Guid? groupId, IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() => ResetInTransactionAsync(
            organizationId, username, password,
            allDevices, groupId, deviceIds, scopedDeviceIds, actorId, actorDisplay, cancellationToken));
    }

    private async Task<BulkLocalAccountResult> ResetInTransactionAsync(
        Guid organizationId,
        string username, string password,
        bool allDevices, Guid? groupId, IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay,
        CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var targets = await ResolveTargetsAsync(
            organizationId, allDevices, groupId, deviceIds, scopedDeviceIds, cancellationToken);
        if (targets.Count == 0)
        {
            return BulkLocalAccountResult.NotFound();
        }

        // Windows local usernames are case-insensitive; match the last-reported name
        // that way and map each device to the SID the reset task needs.
        var targetIds = targets.Select(t => t.Id).ToHashSet();
        var lowered = username.Trim().ToLowerInvariant();
        var reported = await _dbContext.DeviceLocalUsers.AsNoTracking()
            .Where(u => targetIds.Contains(u.DeviceId) && u.Name.ToLower() == lowered)
            .Select(u => new { u.DeviceId, u.Sid })
            .ToListAsync(cancellationToken);
        var sidByDevice = reported
            .GroupBy(x => x.DeviceId)
            .ToDictionary(g => g.Key, g => g.First().Sid);

        var now = _timeProvider.GetUtcNow();
        var tally = new Tally();

        foreach (var target in targets)
        {
            if (target.Status == DeviceStatus.Retired || !sidByDevice.TryGetValue(target.Id, out var sid))
            {
                // Retired, or the account has never been reported on this device, so
                // there is no SID to reset. Recorded as skipped, not a failure.
                tally.Skipped++;
                continue;
            }

            var context = new LocalAccountRequestContext(organizationId, target.Id, actorId, actorDisplay);
            var result = await _localAccounts.ResetPasswordAsync(context, sid, password, cancellationToken);
            tally.Record(result, target, now, _staleAfter);
        }

        StageAudit(organizationId, actorId, actorDisplay,
            action: "device.local_user.bulk_reset_password",
            permission: Domain.Authorization.Permissions.LocalUser.ResetPassword,
            targetDisplay: username,
            state: new
            {
                username,
                targeted = targets.Count,
                tally.Queued,
                tally.Rejected,
                tally.Offline,
                tally.Skipped,
            });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Bulk local-account password reset '{Username}' by {Actor}: {Targeted} targeted, {Queued} queued ({Offline} offline), {Rejected} rejected, {Skipped} skipped.",
            username, actorDisplay, targets.Count, tally.Queued, tally.Offline, tally.Rejected, tally.Skipped);

        return BulkLocalAccountResult.Completed(targets.Count, tally.Queued, tally.Rejected, tally.Offline, tally.Skipped);
    }

    // ----------------------------------------------- disable / enable / delete

    /// <summary>
    /// Disables an existing account, named by username, on every resolved device.
    /// See <see cref="DeleteAccountAsync"/> for the shared lifecycle semantics.
    /// </summary>
    public Task<BulkLocalAccountLifecycleResult> DisableAccountAsync(
        Guid organizationId, string username,
        bool allDevices, Guid? groupId, IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default) =>
        LifecycleAsync(LifecycleOperation.Disable, organizationId, username,
            allDevices, groupId, deviceIds, scopedDeviceIds, actorId, actorDisplay, cancellationToken);

    /// <summary>
    /// Re-enables an existing account, named by username, on every resolved device.
    /// See <see cref="DeleteAccountAsync"/> for the shared lifecycle semantics.
    /// </summary>
    public Task<BulkLocalAccountLifecycleResult> EnableAccountAsync(
        Guid organizationId, string username,
        bool allDevices, Guid? groupId, IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default) =>
        LifecycleAsync(LifecycleOperation.Enable, organizationId, username,
            allDevices, groupId, deviceIds, scopedDeviceIds, actorId, actorDisplay, cancellationToken);

    /// <summary>
    /// Deletes an existing account, named by username, on every resolved device.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like a bulk reset, the same username is a different SID on every machine, so
    /// the SID is resolved per device from the last reported inventory
    /// (case-insensitive SAM-name match). A device that has never reported the
    /// account is recorded as skipped-without-the-account, not an error.
    /// </para>
    /// <para>
    /// <b>Protected accounts refuse the whole request.</b> If the username resolves,
    /// on any targeted device, to the built-in Administrator (RID 500) — the account
    /// <see cref="LocalAccountSafetyRules"/> protects from delete and disable — the
    /// request returns <see cref="BulkLocalAccountLifecycleOutcome.ProtectedAccount"/>
    /// before anything is queued anywhere. The per-device engine would refuse it one
    /// machine at a time anyway; refusing up front keeps a protected fleet-wide delete
    /// from half-running against the devices where the name happens to be ordinary.
    /// </para>
    /// <para>
    /// Every queued task goes through the per-device
    /// <see cref="LocalAccountManagementService"/>, so the last-administrator guard
    /// (and the agent's authoritative live re-check) apply per device exactly as they
    /// do from a single device's page; a device the guard refuses is tallied as
    /// rejected while the rest of the fleet proceeds.
    /// </para>
    /// </remarks>
    public Task<BulkLocalAccountLifecycleResult> DeleteAccountAsync(
        Guid organizationId, string username,
        bool allDevices, Guid? groupId, IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay,
        CancellationToken cancellationToken = default) =>
        LifecycleAsync(LifecycleOperation.Delete, organizationId, username,
            allDevices, groupId, deviceIds, scopedDeviceIds, actorId, actorDisplay, cancellationToken);

    private enum LifecycleOperation
    {
        Disable,
        Enable,
        Delete,
    }

    private Task<BulkLocalAccountLifecycleResult> LifecycleAsync(
        LifecycleOperation operation, Guid organizationId, string username,
        bool allDevices, Guid? groupId, IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay,
        CancellationToken cancellationToken)
    {
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() => LifecycleInTransactionAsync(
            operation, organizationId, username,
            allDevices, groupId, deviceIds, scopedDeviceIds, actorId, actorDisplay, cancellationToken));
    }

    private async Task<BulkLocalAccountLifecycleResult> LifecycleInTransactionAsync(
        LifecycleOperation operation, Guid organizationId, string username,
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
            return BulkLocalAccountLifecycleResult.NotFound();
        }

        // Windows local usernames are case-insensitive; match the last-reported name
        // that way and map each device to the SID the task needs — same resolution as
        // a bulk password reset.
        var targetIds = targets.Select(t => t.Id).ToHashSet();
        var lowered = username.Trim().ToLowerInvariant();
        var reported = await _dbContext.DeviceLocalUsers.AsNoTracking()
            .Where(u => targetIds.Contains(u.DeviceId) && u.Name.ToLower() == lowered)
            .Select(u => new { u.DeviceId, u.Sid })
            .ToListAsync(cancellationToken);
        var sidByDevice = reported
            .GroupBy(x => x.DeviceId)
            .ToDictionary(g => g.Key, g => g.First().Sid);

        // The protected-account guard, applied fleet-wide and up front: the built-in
        // Administrator (RID 500) may never be deleted or disabled. One match refuses
        // the WHOLE request so no sibling device gets the task either. Enable is not
        // covered, mirroring LocalAccountSafetyRules exactly: re-enabling the built-in
        // Administrator is a deliberate, permitted administrative act.
        if (operation is not LifecycleOperation.Enable
            && sidByDevice.Values.Any(LocalAccountSafetyRules.IsBuiltInAdministrator))
        {
            return BulkLocalAccountLifecycleResult.Protected(operation == LifecycleOperation.Delete
                ? "The built-in Administrator account is protected and cannot be deleted."
                : "The built-in Administrator account is protected and cannot be disabled.");
        }

        var now = _timeProvider.GetUtcNow();
        var tally = new LifecycleTally();

        foreach (var target in targets)
        {
            if (target.Status == DeviceStatus.Retired)
            {
                tally.SkippedOther++;
                continue;
            }

            if (!sidByDevice.TryGetValue(target.Id, out var sid))
            {
                // The account has never been reported on this device, so there is no
                // SID to act on. Recorded as skipped, not a failure.
                tally.SkippedNoAccount++;
                continue;
            }

            var context = new LocalAccountRequestContext(organizationId, target.Id, actorId, actorDisplay);
            var result = operation switch
            {
                LifecycleOperation.Delete => await _localAccounts.DeleteUserAsync(context, sid, cancellationToken),
                LifecycleOperation.Enable => await _localAccounts.SetUserEnabledAsync(context, sid, enabled: true, cancellationToken),
                _ => await _localAccounts.SetUserEnabledAsync(context, sid, enabled: false, cancellationToken),
            };

            tally.Record(result, target, now, _staleAfter);
        }

        var (action, permission) = operation switch
        {
            LifecycleOperation.Delete =>
                ("device.local_user.bulk_delete", Domain.Authorization.Permissions.LocalUser.Delete),
            LifecycleOperation.Enable =>
                ("device.local_user.bulk_enable", Domain.Authorization.Permissions.LocalUser.Disable),
            _ =>
                ("device.local_user.bulk_disable", Domain.Authorization.Permissions.LocalUser.Disable),
        };

        StageAudit(organizationId, actorId, actorDisplay,
            action, permission,
            targetDisplay: username,
            state: new
            {
                username,
                targeted = targets.Count,
                tally.Queued,
                tally.Rejected,
                tally.Offline,
                tally.SkippedNoAccount,
                tally.SkippedOther,
            });

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "Bulk local-account {Operation} '{Username}' by {Actor}: {Targeted} targeted, {Queued} queued ({Offline} offline), {Rejected} rejected, {SkippedNoAccount} without the account, {SkippedOther} otherwise skipped.",
            operation, username, actorDisplay, targets.Count, tally.Queued, tally.Offline,
            tally.Rejected, tally.SkippedNoAccount, tally.SkippedOther);

        return BulkLocalAccountLifecycleResult.Completed(
            targets.Count, tally.Queued, tally.Rejected, tally.Offline,
            tally.SkippedNoAccount, tally.SkippedOther);
    }

    // ------------------------------------------------------------- resolution

    /// <summary>
    /// The devices a bulk operation acts on: all active devices in the org, a group's
    /// active members, or an explicit selection — then every one intersected with the
    /// caller's device scope. A specific selection keeps retired devices (to be
    /// recorded as skipped); all-devices and group resolve active members only.
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
                .Select(d => new TargetDevice(d.Id, d.Hostname, d.Status, d.LastSeenAt))
                .ToListAsync(cancellationToken);
        }
        else if (groupId is { } group)
        {
            devices = await _dbContext.Devices.AsNoTracking()
                .Where(d => d.DeviceGroupId == group
                            && d.OrganizationId == organizationId
                            && d.Status == DeviceStatus.Active)
                .OrderBy(d => d.Hostname)
                .Select(d => new TargetDevice(d.Id, d.Hostname, d.Status, d.LastSeenAt))
                .ToListAsync(cancellationToken);
        }
        else
        {
            var targeted = (deviceIds ?? []).ToHashSet();
            devices = await _dbContext.Devices.AsNoTracking()
                .Where(d => targeted.Contains(d.Id) && d.OrganizationId == organizationId)
                .OrderBy(d => d.Hostname)
                .Select(d => new TargetDevice(d.Id, d.Hostname, d.Status, d.LastSeenAt))
                .ToListAsync(cancellationToken);
        }

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

    private void StageAudit(
        Guid organizationId, Guid actorId, string actorDisplay,
        string action, string permission, string targetDisplay, object state) =>
        _auditWriter.Stage(organizationId, AuditActorType.PlatformUser, actorId, actorDisplay,
            action, AuditResult.Success,
            a => a.OnTarget("local_user_bulk", targetDisplay, targetDisplay)
                  .Requiring(permission)
                  .WithStateChange(null, JsonSerializer.Serialize(state)));

    private sealed record TargetDevice(Guid Id, string Hostname, DeviceStatus Status, DateTimeOffset? LastSeenAt);

    /// <summary>Running counts over the fan-out, folded from each per-device result.</summary>
    private sealed class Tally
    {
        public int Queued { get; private set; }

        public int Rejected { get; private set; }

        public int Offline { get; private set; }

        public int Skipped { get; set; }

        public void Record(LocalAccountOperationResult result, TargetDevice target, DateTimeOffset now, TimeSpan staleAfter)
        {
            switch (result.Status)
            {
                case LocalAccountOperationStatus.Queued:
                    Queued++;
                    if (target.LastSeenAt is null || now - target.LastSeenAt > staleAfter)
                    {
                        Offline++;
                    }

                    break;

                case LocalAccountOperationStatus.Rejected:
                    Rejected++;
                    break;

                default:
                    Skipped++;
                    break;
            }
        }
    }

    /// <summary>
    /// Running counts over a lifecycle fan-out. Unlike <see cref="Tally"/>, skips are
    /// split: a device that never reported the account is a different answer from a
    /// retired device, and the operator reads them differently.
    /// </summary>
    private sealed class LifecycleTally
    {
        public int Queued { get; private set; }

        public int Rejected { get; private set; }

        public int Offline { get; private set; }

        public int SkippedNoAccount { get; set; }

        public int SkippedOther { get; set; }

        public void Record(LocalAccountOperationResult result, TargetDevice target, DateTimeOffset now, TimeSpan staleAfter)
        {
            switch (result.Status)
            {
                case LocalAccountOperationStatus.Queued:
                    Queued++;
                    if (target.LastSeenAt is null || now - target.LastSeenAt > staleAfter)
                    {
                        Offline++;
                    }

                    break;

                case LocalAccountOperationStatus.Rejected:
                    Rejected++;
                    break;

                default:
                    SkippedOther++;
                    break;
            }
        }
    }
}

/// <summary>What a bulk operation came to.</summary>
public enum BulkLocalAccountOutcome
{
    /// <summary>The target resolved to devices and the per-device tasks were fanned out.</summary>
    Completed,

    /// <summary>Nothing resolved: an unknown or foreign group, or a selection all out of org/scope.</summary>
    NotFound,
}

/// <summary>
/// The outcome of a bulk create or reset. Counts are meaningful only when
/// <see cref="Outcome"/> is <see cref="BulkLocalAccountOutcome.Completed"/>.
/// </summary>
/// <param name="Offline">Of <paramref name="Queued"/>, those offline now — the task waits for reconnect.</param>
/// <param name="Rejected">Devices a per-device validation refused (e.g. the secret store was unavailable).</param>
/// <param name="Skipped">Retired devices, and — for a reset — devices that never reported the account.</param>
public sealed record BulkLocalAccountResult(
    BulkLocalAccountOutcome Outcome, int Targeted, int Queued, int Rejected, int Offline, int Skipped)
{
    public static BulkLocalAccountResult Completed(int targeted, int queued, int rejected, int offline, int skipped) =>
        new(BulkLocalAccountOutcome.Completed, targeted, queued, rejected, offline, skipped);

    public static BulkLocalAccountResult NotFound() =>
        new(BulkLocalAccountOutcome.NotFound, 0, 0, 0, 0, 0);
}

/// <summary>What a bulk lifecycle operation (disable, enable or delete) came to.</summary>
public enum BulkLocalAccountLifecycleOutcome
{
    /// <summary>The target resolved to devices and the per-device tasks were fanned out.</summary>
    Completed,

    /// <summary>Nothing resolved: an unknown or foreign group, or a selection all out of org/scope.</summary>
    NotFound,

    /// <summary>
    /// The username resolved, somewhere in the fleet, to a protected built-in account.
    /// Nothing was queued on any device.
    /// </summary>
    ProtectedAccount,
}

/// <summary>
/// The outcome of a bulk disable, enable or delete. Counts are meaningful only when
/// <see cref="Outcome"/> is <see cref="BulkLocalAccountLifecycleOutcome.Completed"/>;
/// <see cref="Error"/> only when it is
/// <see cref="BulkLocalAccountLifecycleOutcome.ProtectedAccount"/>.
/// </summary>
/// <param name="Offline">Of <paramref name="Queued"/>, those offline now — the task waits for reconnect.</param>
/// <param name="Rejected">Devices a per-device safety rule refused (e.g. the last enabled administrator).</param>
/// <param name="SkippedNoAccount">Devices that never reported an account with that username.</param>
/// <param name="SkippedOther">Retired devices, and devices the per-device engine could not act on.</param>
public sealed record BulkLocalAccountLifecycleResult(
    BulkLocalAccountLifecycleOutcome Outcome,
    int Targeted, int Queued, int Rejected, int Offline, int SkippedNoAccount, int SkippedOther,
    string? Error)
{
    public static BulkLocalAccountLifecycleResult Completed(
        int targeted, int queued, int rejected, int offline, int skippedNoAccount, int skippedOther) =>
        new(BulkLocalAccountLifecycleOutcome.Completed,
            targeted, queued, rejected, offline, skippedNoAccount, skippedOther, null);

    public static BulkLocalAccountLifecycleResult NotFound() =>
        new(BulkLocalAccountLifecycleOutcome.NotFound, 0, 0, 0, 0, 0, 0, null);

    public static BulkLocalAccountLifecycleResult Protected(string error) =>
        new(BulkLocalAccountLifecycleOutcome.ProtectedAccount, 0, 0, 0, 0, 0, 0, error);
}
