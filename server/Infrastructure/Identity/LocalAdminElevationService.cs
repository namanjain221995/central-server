using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Tasks;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EndpointPlatform.Infrastructure.Identity;

public enum ElevationOutcome
{
    Success,
    /// <summary>No such device, or not this organization's, or retired.</summary>
    DeviceNotFound,
    /// <summary>The device has never reported an account with that SID.</summary>
    AccountNotFound,
    /// <summary>The built-in Administrator can never be elevated.</summary>
    ProtectedAccount,
    /// <summary>Asked-for duration is outside the permitted window.</summary>
    InvalidDuration,
    /// <summary>An elevation already holds a claim on this account.</summary>
    AlreadyElevated,
    /// <summary>No such elevation.</summary>
    NotFound,
    /// <summary>The elevation is not in a state that allows this transition.</summary>
    InvalidState,
}

/// <summary>
/// Temporary local administrator rights: requesting, approving and revoking.
/// </summary>
/// <remarks>
/// <para>
/// The security model in one paragraph. An account gets administrator rights only
/// through a record that names the account, the reason, the approver and an
/// absolute deadline. The deadline is set once, at approval, and is never
/// extended. Nothing here mutates a Windows account -- that is the agent's job in
/// a later slice -- so every failure in this file leaves the endpoint exactly as
/// it was.
/// </para>
/// <para>
/// <b>Uniqueness is enforced by the database, not by this code.</b> The domain's
/// conflict check runs first so a caller gets a clear refusal instead of a
/// constraint violation, but it reads a snapshot and two concurrent requests can
/// both pass it. The partial unique index is the guarantee, and the insert path
/// below catches its violation and reports the same refusal -- so a race and a
/// sequential duplicate look identical to the caller.
/// </para>
/// </remarks>
public sealed class LocalAdminElevationService(
    EndpointPlatformDbContext dbContext,
    DeviceTaskService taskService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<LocalAdminElevationService> logger)
{
    /// <summary>PostgreSQL's unique-violation SQLSTATE.</summary>
    private const string UniqueViolation = "23505";

    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly DeviceTaskService _taskService = taskService
        ?? throw new ArgumentNullException(nameof(taskService));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<LocalAdminElevationService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Requests an elevation and, when a duration is supplied, approves it in the
    /// same act.
    /// </summary>
    /// <param name="duration">
    /// Null to leave the request awaiting a decision. Supplied when the
    /// administrator raising it is also the one authorized to decide it, which is
    /// the current single-administrator arrangement -- splitting that into two
    /// recorded steps would audit a deliberation that never happened.
    /// </param>
    public async Task<(ElevationOutcome Outcome, LocalAdminElevation? Elevation)> RequestAsync(
        Guid organizationId,
        Guid deviceId,
        string targetSid,
        string justification,
        TimeSpan? duration,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        if (duration is { } d && (d < LocalAdminElevation.MinimumDuration || d > LocalAdminElevation.MaximumDuration))
        {
            return (ElevationOutcome.InvalidDuration, null);
        }

        var device = await _dbContext.Devices.SingleOrDefaultAsync(
            x => x.Id == deviceId && x.OrganizationId == organizationId, cancellationToken);

        if (device is null || device.Status == DeviceStatus.Retired)
        {
            return (ElevationOutcome.DeviceNotFound, null);
        }

        // Refused before the domain sees it, so the caller gets a specific answer
        // rather than a generic malformed-input error. The domain refuses it again
        // at construction -- neither layer trusts the other to have checked.
        if (LocalAccountSafetyRules.IsBuiltInAdministrator(targetSid))
        {
            return (ElevationOutcome.ProtectedAccount, null);
        }

        // The account must be one this endpoint has actually reported. Elevating a
        // SID the machine has never mentioned would create an authorization for
        // something that may not exist, and the agent would have nothing to apply
        // it to.
        var account = await _dbContext.DeviceLocalUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.DeviceId == deviceId && u.Sid == targetSid, cancellationToken);

        if (account is null)
        {
            return (ElevationOutcome.AccountNotFound, null);
        }

        var now = _timeProvider.GetUtcNow();

        // A courtesy check, not the guarantee -- see the class remarks.
        var existing = await _dbContext.LocalAdminElevations
            .Where(e => e.DeviceId == deviceId && e.TargetSid == targetSid)
            .ToListAsync(cancellationToken);

        if (LocalAdminElevation.WouldConflict(existing, deviceId, targetSid, now))
        {
            return (ElevationOutcome.AlreadyElevated, null);
        }

        var elevation = duration is { } approvedFor
            ? LocalAdminElevation.RequestAndApprove(
                organizationId, deviceId, targetSid, account.Name, justification,
                actorId, actorDisplay, approvedFor, now)
            : LocalAdminElevation.Request(
                organizationId, deviceId, targetSid, account.Name, justification,
                actorId, actorDisplay, now);

        _dbContext.LocalAdminElevations.Add(elevation);

        AuditRequested(device, elevation, actorId, actorDisplay);
        if (duration is not null)
        {
            AuditApproved(device, elevation, actorId, actorDisplay);
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsLiveElevationConflict(ex))
        {
            // The partial unique index caught a race the snapshot check could not
            // see. Reported as the same refusal, so a caller cannot tell a race
            // from a sequential duplicate -- and, more importantly, so the second
            // window is never created.
            _logger.LogWarning(
                "A concurrent elevation request for {Sid} on {DeviceId} was refused by the "
                + "uniqueness constraint.", targetSid, deviceId);

            _dbContext.ChangeTracker.Clear();
            return (ElevationOutcome.AlreadyElevated, null);
        }

        _logger.LogInformation(
            "Elevation {ElevationId} {State} for {Username} on {Hostname} by {Actor}.",
            elevation.Id, elevation.State, elevation.TargetUsername, device.Hostname, actorDisplay);

        return (ElevationOutcome.Success, elevation);
    }

    /// <summary>Approves a pending request, which is when the deadline is set.</summary>
    public async Task<(ElevationOutcome Outcome, LocalAdminElevation? Elevation)> ApproveAsync(
        Guid organizationId,
        Guid elevationId,
        TimeSpan duration,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        if (duration < LocalAdminElevation.MinimumDuration || duration > LocalAdminElevation.MaximumDuration)
        {
            return (ElevationOutcome.InvalidDuration, null);
        }

        var elevation = await _dbContext.LocalAdminElevations.SingleOrDefaultAsync(
            e => e.Id == elevationId && e.OrganizationId == organizationId, cancellationToken);

        if (elevation is null)
        {
            return (ElevationOutcome.NotFound, null);
        }

        // Only a pending request can be approved. This is what stops approval
        // being used to extend a live elevation: an already-approved record
        // refuses, so a longer window means revoking and requesting again, which
        // leaves two audit records instead of one that changed meaning.
        if (elevation.State != LocalAdminElevationState.Requested)
        {
            return (ElevationOutcome.InvalidState, null);
        }

        var device = await _dbContext.Devices.SingleOrDefaultAsync(
            d => d.Id == elevation.DeviceId, cancellationToken);

        elevation.Approve(actorId, actorDisplay, duration, _timeProvider.GetUtcNow());

        AuditApproved(device, elevation, actorId, actorDisplay);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Elevation {ElevationId} approved until {ExpiresAt} by {Actor}.",
            elevation.Id, elevation.ExpiresAt, actorDisplay);

        return (ElevationOutcome.Success, elevation);
    }

    /// <summary>Ends an elevation early.</summary>
    public async Task<ElevationOutcome> RevokeAsync(
        Guid organizationId,
        Guid elevationId,
        string? note,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        var elevation = await _dbContext.LocalAdminElevations.SingleOrDefaultAsync(
            e => e.Id == elevationId && e.OrganizationId == organizationId, cancellationToken);

        if (elevation is null)
        {
            return ElevationOutcome.NotFound;
        }

        var now = _timeProvider.GetUtcNow();

        if (!elevation.TryRevoke(actorId, actorDisplay, note, now))
        {
            return ElevationOutcome.InvalidState;
        }

        var device = await _dbContext.Devices.SingleOrDefaultAsync(
            d => d.Id == elevation.DeviceId, cancellationToken);

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: "localuser.elevation.revoked",
            AuditResult.Success,
            audit => audit
                .OnDevice(elevation.DeviceId, device?.Hostname ?? elevation.DeviceId.ToString())
                .OnTarget("local_admin_elevation", elevation.Id.ToString(), elevation.TargetUsername)
                .Requiring(Permissions.LocalUser.Elevate)
                .WithStateChange(
                    StateDocument(elevation, LocalAdminElevationState.Active),
                    StateDocument(elevation, elevation.State)));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Elevation {ElevationId} revoked by {Actor}.", elevation.Id, actorDisplay);

        return ElevationOutcome.Success;
    }

    /// <summary>
    /// The complete set of elevations that authorize administrator rights on one
    /// endpoint right now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single source of what the endpoint is told, so the pushed task and any
    /// future convergence channel cannot disagree.
    /// </para>
    /// <para>
    /// <b>Liveness is computed from the clock, not from the stored state.</b> An
    /// elevation whose deadline has passed drops out of this set the instant it
    /// passes, with no dependence on the sweeper having run. That is what makes
    /// the sweeper bookkeeping rather than the authorization boundary: if it
    /// never ran again, no endpoint would keep rights past its deadline -- only
    /// the console would show a stale label.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<TaskPayloads.LocalAdminElevationGrant>> BuildDesiredElevationsAsync(
        Guid deviceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var live = await _dbContext.LocalAdminElevations
            .AsNoTracking()
            .Where(e => e.DeviceId == deviceId
                && (e.State == LocalAdminElevationState.Approved || e.State == LocalAdminElevationState.Active)
                && e.ExpiresAt != null
                && e.ExpiresAt > now)
            .Select(e => new { e.TargetSid, e.ExpiresAt })
            .ToListAsync(cancellationToken);

        return live
            .Select(e => new TaskPayloads.LocalAdminElevationGrant(e.TargetSid, e.ExpiresAt!.Value))
            .ToList();
    }

    /// <summary>
    /// Queues an <c>ApplyLocalAdminElevation</c> task carrying the endpoint's
    /// complete current set.
    /// </summary>
    /// <remarks>
    /// Whole state, so this is safe to call at any time and any number of times.
    /// A failure to queue is logged rather than thrown: the decision it reflects
    /// is already durable, and the endpoint withdraws rights on its own when the
    /// deadline passes regardless of whether this message ever arrives. Losing
    /// the push delays a change; it never widens access.
    /// <para>
    /// Called by the request (when it approves at once), approve and revoke
    /// endpoints. Until 2026-11 nothing called it, so an approved elevation was
    /// recorded but never reached the endpoint and read "Not applied yet" until
    /// it lapsed.
    /// </para>
    /// </remarks>
    public async Task<DeviceTask?> QueueElevationPushAsync(
        Guid organizationId,
        Guid deviceId,
        Guid actorId,
        string actorDisplay,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var now = _timeProvider.GetUtcNow();
            var desired = await BuildDesiredElevationsAsync(deviceId, now, cancellationToken);

            var payload = new TaskPayloads.ApplyLocalAdminElevation(desired, now);

            var task = await _taskService.QueueAsync(
                organizationId, deviceId, DeviceTaskType.ApplyLocalAdminElevation, payload,
                actorId, actorDisplay, cancellationToken);

            if (task is null)
            {
                // Retired device, or an agent too old to carry the executor; the
                // task service has already logged which.
                _logger.LogWarning(
                    "The elevation set for device {DeviceId} was not pushed; the device cannot take the task.",
                    deviceId);
            }

            return task;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Pushing the elevation set to device {DeviceId} failed.", deviceId);
            return null;
        }
    }

    /// <summary>
    /// Marks lapsed elevations Expired and returns how many changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bookkeeping, not enforcement. Authorization ended at the deadline whatever
    /// this does: <see cref="BuildDesiredElevationsAsync"/> already stops
    /// publishing a lapsed elevation, and the endpoint judges the deadline
    /// against its own clock. This exists so the console reads correctly and so
    /// the expiry is recorded once, at a knowable time.
    /// </para>
    /// <para>
    /// <b>The transition is guarded, not read-then-write.</b> Each row is updated
    /// with its expected state in the WHERE clause, so a second sweeper running
    /// concurrently updates zero rows and writes no audit entry. Without that
    /// guard two instances could both read the same Approved row and both record
    /// an expiry, producing duplicate history for one event.
    /// </para>
    /// </remarks>
    public async Task<int> SweepExpiredAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        var candidates = await _dbContext.LocalAdminElevations
            .AsNoTracking()
            .Where(e => (e.State == LocalAdminElevationState.Approved
                    || e.State == LocalAdminElevationState.Active)
                && e.ExpiresAt != null
                && e.ExpiresAt <= now)
            .OrderBy(e => e.ExpiresAt)
            .Take(batchSize)
            .Select(e => new
            {
                e.Id, e.OrganizationId, e.DeviceId, e.TargetSid, e.TargetUsername, e.State, e.ExpiresAt,
            })
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var expired = 0;

        foreach (var candidate in candidates)
        {
            // The state it was in is part of the WHERE clause. Whoever wins the
            // race updates one row; everyone else updates none and does nothing
            // further -- which is what keeps both the transition and its audit
            // entry exactly-once.
            var rows = await _dbContext.LocalAdminElevations
                .Where(e => e.Id == candidate.Id
                    && (e.State == LocalAdminElevationState.Approved
                        || e.State == LocalAdminElevationState.Active))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(e => e.State, LocalAdminElevationState.Expired)
                        .SetProperty(e => e.UpdatedAt, now),
                    cancellationToken);

            if (rows == 0)
            {
                // Another sweeper, or a revocation, got there first.
                continue;
            }

            expired++;

            _auditWriter.Stage(
                candidate.OrganizationId,
                AuditActorType.System,
                actorId: null,
                actorDisplay: "elevation expiry sweeper",
                action: "localuser.elevation.expired",
                AuditResult.Success,
                audit => audit
                    .OnDevice(candidate.DeviceId, candidate.DeviceId.ToString())
                    .OnTarget("local_admin_elevation", candidate.Id.ToString(), candidate.TargetUsername)
                    .WithStateChange(
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            state = candidate.State.ToString(),
                            targetSid = candidate.TargetSid,
                            expiresAt = candidate.ExpiresAt,
                        }),
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            state = nameof(LocalAdminElevationState.Expired),
                            targetSid = candidate.TargetSid,
                            expiresAt = candidate.ExpiresAt,
                        })));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Deliberately no task is queued here, matching the USB grant sweeper.
        //
        // Two reasons. The endpoint has already withdrawn the rights against its
        // own clock, so a push would tell it something it has acted on -- and an
        // endpoint that never receives it converges anyway, because the next
        // whole-state set it gets omits the lapsed entry.
        //
        // The second reason is structural: a DeviceTask requires a creating user,
        // and there is no person behind an expiry. Inventing a system actor to
        // satisfy that would put a placeholder identity in the task history for
        // every expiry, which is worse than not pushing.

        if (expired > 0)
        {
            _logger.LogInformation("Elevation sweep expired {Count} elevation(s).", expired);
        }

        return expired;
    }

    // ---- audit -------------------------------------------------------------

    private void AuditRequested(Device device, LocalAdminElevation e, Guid actorId, string actorDisplay) =>
        _auditWriter.Stage(
            e.OrganizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: "localuser.elevation.requested",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, device.Hostname)
                .OnTarget("local_admin_elevation", e.Id.ToString(), e.TargetUsername)
                .Requiring(Permissions.LocalUser.Elevate)
                .WithStateChange(null, StateDocument(e, LocalAdminElevationState.Requested)));

    private void AuditApproved(Device? device, LocalAdminElevation e, Guid actorId, string actorDisplay) =>
        _auditWriter.Stage(
            e.OrganizationId,
            AuditActorType.PlatformUser,
            actorId,
            actorDisplay,
            action: "localuser.elevation.approved",
            AuditResult.Success,
            audit => audit
                .OnDevice(e.DeviceId, device?.Hostname ?? e.DeviceId.ToString())
                .OnTarget("local_admin_elevation", e.Id.ToString(), e.TargetUsername)
                .Requiring(Permissions.LocalUser.Elevate)
                .WithStateChange(
                    StateDocument(e, LocalAdminElevationState.Requested),
                    StateDocument(e, LocalAdminElevationState.Approved)));

    /// <summary>
    /// The audit state document.
    /// </summary>
    /// <remarks>
    /// Carries who and what, and nothing secret: an elevation has no credential
    /// material to leak, but the rule is stated here so it stays true if the
    /// entity later gains a field that does. The target SID is included because
    /// it is the identity a decision was made about, and a username alone cannot
    /// survive a rename.
    /// </remarks>
    private static string StateDocument(LocalAdminElevation e, LocalAdminElevationState state) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            state = state.ToString(),
            targetSid = e.TargetSid,
            targetUsername = e.TargetUsername,
            expiresAt = e.ExpiresAt,
            justification = e.Justification,
        });

    /// <summary>True when the failure is our live-elevation uniqueness constraint.</summary>
    /// <remarks>
    /// Matched on the index name as well as the SQLSTATE, so an unrelated unique
    /// violation is not quietly reported to the caller as "already elevated".
    /// </remarks>
    private static bool IsLiveElevationConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: UniqueViolation } pg
        && pg.ConstraintName is { } name
        && name.Contains("ux_local_admin_elevations_live_per_account", StringComparison.Ordinal);
}
