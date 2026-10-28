using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Enrollment;

/// <summary>
/// Turns an administrator's decision about a pending machine into an enrollment the
/// existing pipeline can complete.
/// </summary>
/// <remarks>
/// <para>
/// This layer exists so approval does NOT become a second enrollment implementation.
/// Approving mints a single-use enrollment token server-side and hands it to the
/// pending record; claiming feeds that token to the existing
/// <see cref="AgentEnrollmentService"/>. Device creation, re-enrolment de-duplication,
/// credential issuance and enrollment auditing therefore all remain in exactly one
/// place — the place that is already tested.
/// </para>
/// <para>
/// The minted token never reaches the agent. It is sealed at rest in Redis, unsealed
/// only inside the claim, and consumed immediately. The agent's own proof of identity
/// is the request secret it generated and never sent until claim time.
/// </para>
/// </remarks>
public sealed class EnrollmentApprovalService(
    EndpointPlatformDbContext dbContext,
    PendingEnrollmentStore pendingStore,
    AgentEnrollmentService enrollmentService,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<EnrollmentApprovalService> logger,
    Security.DeviceScopeAuthorizer deviceScope)
{
    /// <summary>
    /// How long the internally-minted token stays valid. Only ever used within the
    /// claim that follows approval, so this is a safety bound rather than a workflow
    /// window; it matches the pending request's own lifetime.
    /// </summary>
    private static readonly TimeSpan MintedTokenLifetime = PendingEnrollment.Lifetime;

    private readonly EndpointPlatformDbContext _dbContext = dbContext;
    private readonly PendingEnrollmentStore _pendingStore = pendingStore;
    private readonly AgentEnrollmentService _enrollmentService = enrollmentService;
    private readonly AuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<EnrollmentApprovalService> _logger = logger;
    private readonly Security.DeviceScopeAuthorizer _deviceScope = deviceScope;

    /// <summary>
    /// Approves a pending machine into the approving administrator's organization.
    /// </summary>
    /// <remarks>
    /// The organization comes from the authenticated approver, never from the agent:
    /// an unauthenticated caller must not be able to choose which tenant it joins.
    /// </remarks>
    public async Task<ApprovalResult> ApproveAsync(
        string requestId,
        Guid organizationId,
        Guid approverUserId,
        string approverDisplay,
        CancellationToken cancellationToken = default)
    {
        var pending = await _pendingStore.FindAsync(requestId, cancellationToken);
        if (pending is null)
        {
            return ApprovalResult.Gone("The enrollment request has expired or does not exist.");
        }

        if (pending.Status != PendingEnrollmentStatus.Pending)
        {
            return ApprovalResult.Gone($"The enrollment request has already been {pending.Status.ToString().ToLowerInvariant()}.");
        }

        if (pending.Kind == PendingEnrollmentKind.ReEnrollment)
        {
            return await ApproveReEnrollmentAsync(
                requestId, pending, organizationId, approverUserId, approverDisplay, cancellationToken);
        }

        // Single-use, short-lived, and scoped to the approver's organization. It is a
        // server-side implementation detail of this approval, not a credential anyone
        // is meant to hold.
        var secret = SecretGenerator.GenerateSecret();
        var now = _timeProvider.GetUtcNow();

        var token = new EnrollmentToken(
            organizationId,
            $"approved-enrollment-{pending.Hostname}",
            SecretGenerator.HashSecret(secret),
            approverUserId,
            approverDisplay,
            now.Add(MintedTokenLifetime),
            maxUses: 1);

        _dbContext.EnrollmentTokens.Add(token);

        // Record the decision BEFORE committing the token: if the transition loses a
        // race with another administrator, the token must not exist at all.
        var decided = await _pendingStore.DecideAsync(
            requestId, PendingEnrollmentStatus.Approved, organizationId, secret, approverDisplay, cancellationToken);

        if (decided is null)
        {
            _dbContext.EnrollmentTokens.Remove(token);
            return ApprovalResult.Gone("The enrollment request was decided by someone else first.");
        }

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            approverUserId,
            approverDisplay,
            action: "enrollment.approved",
            AuditResult.Success,
            audit => audit
                .OnTarget("enrollment_request", requestId, pending.Hostname)
                // No secret here: the request id is a hash, and neither the request
                // secret nor the minted token is recorded.
                .WithStateChange(null, System.Text.Json.JsonSerializer.Serialize(new
                {
                    hostname = pending.Hostname,
                    machineIdentifier = pending.MachineIdentifier,
                    agentVersion = pending.AgentVersion,
                    operatingSystem = pending.OperatingSystem,
                })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Enrollment request for {Hostname} approved by {Approver} into organization {OrganizationId}.",
            pending.Hostname, approverDisplay, organizationId);

        return ApprovalResult.Ok(pending);
    }

    /// <summary>
    /// Approves handing an ACTIVE device's identity to the machine asking for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No token is minted here, which is the structural difference from a plain
    /// approval: the re-enrolling agent already holds a valid enrollment token and
    /// is retrying it every few minutes. Approval only flips the record; the
    /// agent's own next retry passes the gate, consumes its token and completes
    /// through the one enrollment path.
    /// </para>
    /// <para>
    /// Carries two checks a plain approval does not. The record's organization
    /// must be the approver's — re-enrollment records are born with the token's
    /// organization, unlike plain requests which take the approver's — and the
    /// matched device must be inside the approver's device scope. The
    /// <c>device.enroll</c> permission alone admits unknown machines; it must not
    /// also hand over devices its holder is not scoped to act on, because this
    /// approval revokes a working credential on a specific, existing device.
    /// </para>
    /// </remarks>
    private async Task<ApprovalResult> ApproveReEnrollmentAsync(
        string requestId,
        PendingEnrollment pending,
        Guid organizationId,
        Guid approverUserId,
        string approverDisplay,
        CancellationToken cancellationToken)
    {
        // Another tenant's request reads as nonexistent, exactly as its absence
        // from this administrator's list promises.
        if (pending.OrganizationId != organizationId || pending.ExistingDeviceId is null)
        {
            return ApprovalResult.Gone("The enrollment request has expired or does not exist.");
        }

        // Out of scope reads as not found: the pending list leaves this row out for
        // this administrator, so a "forbidden" would confirm that a device they
        // cannot see is being re-enrolled.
        if (!await _deviceScope.CanActOnDeviceAsync(
                approverUserId, organizationId, pending.ExistingDeviceId.Value, cancellationToken))
        {
            return ApprovalResult.OutOfScope();
        }

        var decided = await _pendingStore.DecideAsync(
            requestId,
            PendingEnrollmentStatus.Approved,
            pending.OrganizationId,
            tokenSecret: null,
            approverDisplay,
            cancellationToken);

        if (decided is null)
        {
            return ApprovalResult.Gone("The enrollment request was decided by someone else first.");
        }

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            approverUserId,
            approverDisplay,
            action: "enrollment.approved",
            AuditResult.Success,
            audit => audit
                .OnTarget("enrollment_request", requestId, pending.Hostname)
                .OnDevice(pending.ExistingDeviceId.Value, pending.ExistingDeviceHostname ?? pending.Hostname)
                .WithStateChange(null, System.Text.Json.JsonSerializer.Serialize(new
                {
                    kind = "re-enrollment",
                    hostname = pending.Hostname,
                    machineIdentifier = pending.MachineIdentifier,
                    agentVersion = pending.AgentVersion,
                    operatingSystem = pending.OperatingSystem,
                    existingDeviceId = pending.ExistingDeviceId,
                    existingDeviceHostname = pending.ExistingDeviceHostname,
                })));

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Re-enrollment of device {DeviceId} ({Hostname}) approved by {Approver}.",
            pending.ExistingDeviceId, pending.ExistingDeviceHostname, approverDisplay);

        return ApprovalResult.Ok(decided);
    }

    /// <summary>Refuses a pending machine. No credential is ever issued for it.</summary>
    public async Task<ApprovalResult> RejectAsync(
        string requestId,
        Guid organizationId,
        Guid approverUserId,
        string approverDisplay,
        CancellationToken cancellationToken = default)
    {
        var pending = await _pendingStore.FindAsync(requestId, cancellationToken);
        if (pending is null)
        {
            return ApprovalResult.Gone("The enrollment request has expired or does not exist.");
        }

        if (pending.Status != PendingEnrollmentStatus.Pending)
        {
            return ApprovalResult.Gone($"The enrollment request has already been {pending.Status.ToString().ToLowerInvariant()}.");
        }

        // Rejecting a re-enrollment is tenant-bound like approving one, and
        // scope-bound too: the row is hidden from an administrator who cannot act
        // on the matched device, and a rejection still decides that device's fate
        // -- the decided record serves its cooling-off, refusing the still-knocking
        // agent without re-pending until the rejection expires.
        if (pending.Kind == PendingEnrollmentKind.ReEnrollment)
        {
            if (pending.OrganizationId != organizationId)
            {
                return ApprovalResult.Gone("The enrollment request has expired or does not exist.");
            }

            if (pending.ExistingDeviceId is { } matched
                && !await _deviceScope.CanActOnDeviceAsync(approverUserId, organizationId, matched, cancellationToken))
            {
                return ApprovalResult.OutOfScope();
            }
        }

        var decided = await _pendingStore.DecideAsync(
            requestId, PendingEnrollmentStatus.Rejected, organizationId, tokenSecret: null, approverDisplay, cancellationToken);

        if (decided is null)
        {
            return ApprovalResult.Gone("The enrollment request was decided by someone else first.");
        }

        _auditWriter.Stage(
            organizationId,
            AuditActorType.PlatformUser,
            approverUserId,
            approverDisplay,
            action: "enrollment.rejected",
            AuditResult.Success,
            audit =>
            {
                audit.OnTarget("enrollment_request", requestId, pending.Hostname);

                if (pending is { Kind: PendingEnrollmentKind.ReEnrollment, ExistingDeviceId: { } deviceId })
                {
                    audit
                        .OnDevice(deviceId, pending.ExistingDeviceHostname ?? pending.Hostname)
                        .WithStateChange(null, System.Text.Json.JsonSerializer.Serialize(new
                        {
                            kind = "re-enrollment",
                            hostname = pending.Hostname,
                            machineIdentifier = pending.MachineIdentifier,
                            existingDeviceId = deviceId,
                            existingDeviceHostname = pending.ExistingDeviceHostname,
                        }));
                }
            });

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Enrollment request for {Hostname} rejected by {Approver}.", pending.Hostname, approverDisplay);

        return ApprovalResult.Ok(decided);
    }
}

/// <summary>Outcome of an approve/reject attempt.</summary>
public sealed record ApprovalResult(
    bool Success, string? FailureReason, PendingEnrollment? Request, bool IsNotFound = false)
{
    public static ApprovalResult Ok(PendingEnrollment request) => new(true, null, request);

    /// <summary>
    /// Expired, already decided, or lost a race. Deliberately one shape: an
    /// administrator does not need these distinguished, and collapsing them keeps
    /// double-approval from ever reading as partial success. Cross-organization
    /// requests collapse into it too, so a request id cannot be probed across
    /// tenants.
    /// </summary>
    public static ApprovalResult Gone(string reason) => new(false, reason, null);

    /// <summary>
    /// A re-enrollment whose matched device is outside the caller's device scope.
    /// The pending list does not show such a row to them, so it answers as not
    /// found -- the platform's answer for anything out of scope -- with no reason
    /// that would confirm the device is being re-enrolled.
    /// </summary>
    public static ApprovalResult OutOfScope() => new(false, null, null, IsNotFound: true);
}
