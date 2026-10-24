using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EndpointPlatform.Infrastructure.Enrollment;

/// <summary>
/// Processes agent enrollment requests. This is the gate through which an unknown
/// machine becomes a trusted device, so every refusal path is explicit and audited.
/// </summary>
public sealed class AgentEnrollmentService(
    EndpointPlatformDbContext dbContext,
    AuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<AgentEnrollmentService> logger,
    Security.IEscrowSealingKeyProvider sealingKey,
    PendingEnrollmentStore pendingStore)
{
    private readonly EndpointPlatformDbContext _dbContext = dbContext
        ?? throw new ArgumentNullException(nameof(dbContext));

    private readonly AuditWriter _auditWriter = auditWriter
        ?? throw new ArgumentNullException(nameof(auditWriter));

    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new ArgumentNullException(nameof(timeProvider));

    private readonly ILogger<AgentEnrollmentService> _logger = logger
        ?? throw new ArgumentNullException(nameof(logger));

    private readonly Security.IEscrowSealingKeyProvider _sealingKey = sealingKey
        ?? throw new ArgumentNullException(nameof(sealingKey));

    private readonly PendingEnrollmentStore _pendingStore = pendingStore
        ?? throw new ArgumentNullException(nameof(pendingStore));

    /// <param name="alreadyApproved">
    /// True only when an administrator has ALREADY approved this specific machine in
    /// this request's own flow — today that is exactly the claim path, which runs
    /// after an approval minted the single-use token being presented. It bypasses
    /// the re-enrollment approval gate, and nothing else: refusing a claim at the
    /// gate would lose the approval (the claim's Redis record is already redeemed by
    /// then) and send the machine back to square one. Deliberately an explicit
    /// parameter rather than something inferred from the token (for example its
    /// name): a convention would make "administrator approved this" forgeable by
    /// whoever can influence a token's name.
    /// </param>
    public async Task<EnrollmentOutcome> EnrollAsync(
        string presentedToken,
        string hostname,
        string machineIdentifier,
        string agentVersion,
        string? operatingSystem,
        bool alreadyApproved = false,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();

        // Look up by hash. A wrong token produces no row - there is nothing to
        // compare against, and the response must not reveal whether the token
        // ever existed.
        var presentedHash = SecretGenerator.HashSecret(presentedToken);

        var token = await _dbContext.EnrollmentTokens
            .SingleOrDefaultAsync(t => t.SecretHash == presentedHash, cancellationToken);

        if (token is null)
        {
            await AuditRefusalAsync(
                organizationId: null, hostname, "Unknown enrollment token.", cancellationToken);
            return EnrollmentOutcome.Refused();
        }

        // Validate WITHOUT consuming. Whether this attempt may spend a use is not
        // known yet: a re-enrollment of an active device may be parked behind the
        // approval gate below, and a parked agent retries the same token every few
        // minutes — each retry consuming a use would exhaust a limited-use token
        // before anyone could approve. The use is taken further down, only on the
        // attempt that actually enrolls.
        var validation = token.Validate(now);

        if (validation != EnrollmentTokenConsumeResult.Consumed)
        {
            await AuditRefusalAsync(
                token.OrganizationId,
                hostname,
                $"Enrollment token refused: {validation}.",
                cancellationToken,
                token);

            return EnrollmentOutcome.Refused();
        }

        // Deliberately scoped to Active. A retired device is a closed record, not a
        // slot to be reoccupied: re-running the installer on a machine that was
        // retired must produce a NEW device with its own id and its own history,
        // leaving the retired row exactly as the administrator left it.
        //
        // Matching retired rows here would mean one of two bad outcomes -- either
        // the retirement is silently undone by whoever next runs the installer, or
        // (as this code did until now) the machine is refused enrolment outright and
        // can never come back without an administrator reactivating it by hand.
        // Neither is what retiring a device is supposed to mean.
        //
        // Safe because the uniqueness constraint is scoped the same way: the partial
        // unique index on (organization_id, machine_identifier) covers Active rows
        // only, so a retired row and a new active one can share a machine identifier
        // while two active ones still cannot.
        var existingDevice = await _dbContext.Devices
            .SingleOrDefaultAsync(
                d => d.OrganizationId == token.OrganizationId
                     && d.MachineIdentifier == machineIdentifier
                     && d.Status == DeviceStatus.Active,
                cancellationToken);

        // THE RE-ENROLLMENT APPROVAL GATE. A valid token plus a machine identifier
        // — which is not a secret — must not be enough to take over an ACTIVE
        // device: completing this request revokes the real device's credential and
        // hands its queued tasks and device-bound secrets to the caller. So every
        // token-path re-enrollment waits for an administrator, uniformly: a wiped
        // machine's honest reinstall waits exactly like a hostile request would,
        // because the server cannot tell them apart and must not guess. The claim
        // path arrives with alreadyApproved and passes through — its approval
        // already happened, on this same machine identifier, moments ago.
        PendingEnrollment? claimedApproval = null;
        string? reEnrollRequestId = null;

        if (existingDevice is not null && !alreadyApproved)
        {
            reEnrollRequestId = PendingEnrollmentStore.DeriveReEnrollRequestId(
                token.OrganizationId, machineIdentifier);

            claimedApproval = await EvaluateReEnrollGateAsync(
                reEnrollRequestId, token, existingDevice,
                hostname, machineIdentifier, agentVersion, operatingSystem, now, cancellationToken);

            if (claimedApproval is null)
            {
                // Not (yet) approved. The pending record and any audit were written
                // inside the gate; the wire answer is the same uniform refusal as a
                // bad token, so a prober cannot distinguish "needs approval" from
                // "wrong token" — the released agent parks and retries either way.
                return EnrollmentOutcome.Refused();
            }
        }

        // From here the token use is committed atomically with the device and
        // credential rows. The token row carries xmin optimistic concurrency, so
        // two agents racing for the last use cannot both commit.
        var consumeResult = token.TryConsume(now);

        if (consumeResult != EnrollmentTokenConsumeResult.Consumed)
        {
            // Validate() passed above, so only a state change within this request
            // could land here; handled for completeness, exactly as before.
            if (claimedApproval is not null)
            {
                await _pendingStore.RestoreReEnrollApprovalAsync(
                    reEnrollRequestId!, claimedApproval, cancellationToken);
            }

            await AuditRefusalAsync(
                token.OrganizationId,
                hostname,
                $"Enrollment token refused: {consumeResult}.",
                cancellationToken,
                token);

            return EnrollmentOutcome.Refused();
        }

        Device device;
        bool reEnrolled;

        if (existingDevice is null)
        {
            device = Device.Enroll(
                token.OrganizationId, hostname, machineIdentifier, agentVersion, operatingSystem,
                token.Id, now);
            _dbContext.Devices.Add(device);
            reEnrolled = false;
        }
        else
        {
            // Only an Active device reaches here, by construction of the query above.
            // The retired-device refusal that used to live at this point is gone: a
            // retired machine no longer matches at all, so it takes the branch above
            // and enrols as a new device rather than being turned away.
            existingDevice.ReEnroll(hostname, agentVersion, operatingSystem, token.Id, now);
            device = existingDevice;
            reEnrolled = true;

            // The machine is starting a new life; its old credential must die with
            // the old one. Revoke every active credential before issuing.
            var activeCredentials = await _dbContext.AgentCredentials
                .Where(c => c.DeviceId == device.Id && c.RevokedAt == null)
                .ToListAsync(cancellationToken);

            foreach (var stale in activeCredentials)
            {
                stale.Revoke(now);
            }
        }

        var secret = SecretGenerator.GenerateSecret();
        var credential = new AgentCredential(
            device.Id,
            SecretGenerator.GenerateKeyId(),
            SecretGenerator.HashSecret(secret),
            now);

        // Pin the sealing key to this credential, at the one moment the device's
        // identity is being established anyway. Doing it here rather than on first
        // use is what makes trust-on-first-use unnecessary: the fingerprint is
        // bound to the same authenticated exchange that issued the credential.
        //
        // With no sealing key configured the credential is simply left unpinned,
        // and the device stays ineligible for automatic escrow. Enrollment itself
        // is never failed over it -- a machine must be able to enroll and report
        // inventory whether or not escrow is available.
        if (_sealingKey.IsConfigured)
        {
            credential.PinSealingKey(_sealingKey.Fingerprint!);
        }

        _dbContext.AgentCredentials.Add(credential);

        // When the gate admitted this attempt, the approver's name goes into the
        // enrollment audit itself: "who allowed this identity handover" must be
        // answerable from the device.re_enroll entry alone.
        var newState = System.Text.Json.JsonSerializer.Serialize(
            claimedApproval?.ApprovedBy is { } approver
                ? new { agentVersion, credentialKeyId = credential.KeyId, approvedBy = approver }
                : (object)new { agentVersion, credentialKeyId = credential.KeyId });

        _auditWriter.Stage(
            token.OrganizationId,
            AuditActorType.Agent,
            device.Id,
            hostname,
            action: reEnrolled ? "device.re_enroll" : "device.enroll",
            AuditResult.Success,
            audit => audit
                .OnDevice(device.Id, hostname)
                .OnTarget("enrollment_token", token.Id.ToString(), token.Name)
                .WithStateChange(null, newState));

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Lost the race for the token's remaining uses. The other enrollment
            // won; this one is refused rather than retried, because a retry would
            // re-read the token and find it exhausted anyway.
            _logger.LogWarning(
                "Enrollment for {Hostname} lost an optimistic-concurrency race on token {TokenId}; refused.",
                hostname,
                token.Id);

            _dbContext.ChangeTracker.Clear();

            // Nothing committed, so the approval this attempt claimed was not
            // used; put it back for the agent's next retry rather than making the
            // administrator decide the same machine twice over a database race.
            if (claimedApproval is not null)
            {
                await _pendingStore.RestoreReEnrollApprovalAsync(
                    reEnrollRequestId!, claimedApproval, cancellationToken);
            }

            await AuditRefusalAsync(
                token.OrganizationId, hostname,
                "Enrollment token exhausted (lost concurrency race).",
                cancellationToken, token);

            return EnrollmentOutcome.Refused();
        }

        _logger.LogInformation(
            "Device {DeviceId} ({Hostname}) {Action} using token {TokenId}. Credential key id {KeyId}.",
            device.Id,
            hostname,
            reEnrolled ? "re-enrolled" : "enrolled",
            token.Id,
            credential.KeyId);

        return EnrollmentOutcome.Enrolled(
            device.Id, credential.KeyId, secret, reEnrolled,
            _sealingKey.PublicKeySpki, _sealingKey.Fingerprint);
    }

    /// <summary>
    /// Decides whether a token-path re-enrollment of an active device may proceed
    /// right now. Returns the claimed, single-use approval when it may; null means
    /// this attempt is refused, with the pending-record and audit bookkeeping
    /// already done.
    /// </summary>
    /// <remarks>
    /// Refusals out of this gate are indistinguishable on the wire from token
    /// refusals, by way of the caller returning the same
    /// <see cref="EnrollmentOutcome.Refused"/>. The released agents (1.16–1.24)
    /// treat any non-2xx as "park five minutes, retry the same token", which is
    /// exactly the loop this gate rides: the approval completes on the agent's own
    /// next retry, with no agent change. A 2xx "pending" body would instead be
    /// deserialized as a credential and wreck the agent's stored one.
    /// </remarks>
    private async Task<PendingEnrollment?> EvaluateReEnrollGateAsync(
        string requestId,
        EnrollmentToken token,
        Device existingDevice,
        string hostname,
        string machineIdentifier,
        string agentVersion,
        string? operatingSystem,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var candidate = new PendingEnrollment(
            machineIdentifier,
            hostname,
            operatingSystem,
            agentVersion,
            now,
            PendingEnrollmentStatus.Pending,
            // Known NOW, from the validated token — unlike a plain enrollment,
            // where the organization is the approver's to choose.
            OrganizationId: token.OrganizationId,
            Kind: PendingEnrollmentKind.ReEnrollment,
            ExistingDeviceId: existingDevice.Id,
            ExistingDeviceHostname: existingDevice.Hostname,
            TokenId: token.Id);

        var attempt = await _pendingStore.UpsertReEnrollRequestAsync(requestId, candidate, cancellationToken);

        switch (attempt.State)
        {
            case ReEnrollAttemptState.Approved:
                var approval = attempt.Record!;

                // The approval must be FOR this device in this organization. A
                // mismatch means the active row changed between approval and retry
                // (retired and freshly re-enrolled, say — the key derivation would
                // produce the same id); what the administrator approved handing
                // over no longer exists, so nothing transfers.
                if (approval.OrganizationId != token.OrganizationId
                    || approval.ExistingDeviceId != existingDevice.Id)
                {
                    _logger.LogWarning(
                        "Re-enrollment approval for {Hostname} no longer matches the active device {DeviceId}; refused.",
                        hostname,
                        existingDevice.Id);
                    return null;
                }

                // Single use: claimed with GETDEL, so one approval completes at
                // most one re-enrollment. Losing the claim race refuses this
                // attempt and lets the winner finish.
                var claimed = await _pendingStore.ClaimReEnrollApprovalAsync(requestId, cancellationToken);
                if (claimed is null)
                {
                    _logger.LogWarning(
                        "Re-enrollment approval for {Hostname} was claimed concurrently or expired; refused.",
                        hostname);
                }

                return claimed;

            case ReEnrollAttemptState.PendingCreated:
                // The one Denied entry for this whole wait. Later retries refresh
                // the record silently, so the audit trail records the security
                // event — someone with a token is asking for this device — once,
                // not once per five-minute knock.
                await AuditRefusalAsync(
                    token.OrganizationId,
                    hostname,
                    $"Re-enrollment matched active device '{existingDevice.Hostname}'; "
                    + "administrator approval is now pending.",
                    cancellationToken,
                    token,
                    existingDevice,
                    action: "device.re_enroll");

                return null;

            case ReEnrollAttemptState.PendingRefreshed:
                _logger.LogDebug(
                    "Re-enrollment of device {DeviceId} still awaiting approval; retry from {Hostname} refused.",
                    existingDevice.Id,
                    hostname);
                return null;

            case ReEnrollAttemptState.RejectedCoolingOff:
                _logger.LogInformation(
                    "Re-enrollment of device {DeviceId} was rejected by {DecidedBy}; retry from {Hostname} refused.",
                    existingDevice.Id,
                    attempt.Record?.ApprovedBy ?? "an administrator",
                    hostname);
                return null;

            default:
                // Unavailable. Fail closed: "could not check for approval" must
                // never behave as "approved", whatever it costs a legitimate
                // reinstall while Redis is down.
                _logger.LogError(
                    "Re-enrollment of device {DeviceId} refused: the pending-enrollment store is unavailable.",
                    existingDevice.Id);
                return null;
        }
    }

    private async Task AuditRefusalAsync(
        Guid? organizationId,
        string hostname,
        string reason,
        CancellationToken cancellationToken,
        EnrollmentToken? token = null,
        Device? device = null,
        string action = "device.enroll")
    {
        // Refusals for unknown tokens have no organization; they are recorded
        // against the default organization so they remain queryable. If none
        // exists yet the platform is not seeded, and we log rather than throw -
        // refusing to enroll must never depend on audit routing succeeding.
        var targetOrganizationId = organizationId
            ?? await _dbContext.Organizations
                .OrderBy(o => o.CreatedAt)
                .Select(o => (Guid?)o.Id)
                .FirstOrDefaultAsync(cancellationToken);

        if (targetOrganizationId is null)
        {
            _logger.LogWarning(
                "Enrollment refused for {Hostname} ({Reason}), and no organization exists to audit against.",
                hostname,
                reason);
            return;
        }

        await _auditWriter.WriteImmediatelyAsync(
            targetOrganizationId.Value,
            AuditActorType.Anonymous,
            actorId: null,
            actorDisplay: hostname,
            action,
            AuditResult.Denied,
            audit =>
            {
                audit.WithFailureReason(reason);
                if (token is not null)
                {
                    audit.OnTarget("enrollment_token", token.Id.ToString(), token.Name);
                }

                if (device is not null)
                {
                    audit.OnDevice(device.Id, device.Hostname);
                }
            },
            cancellationToken);

        _logger.LogWarning("Enrollment refused for {Hostname}: {Reason}", hostname, reason);
    }
}

/// <summary>
/// Result of an enrollment attempt. Refusals are deliberately reason-free: the
/// reason is audited server-side, and telling an unauthenticated caller *why*
/// (unknown vs expired vs exhausted) would let them probe the token space.
/// </summary>
public sealed record EnrollmentOutcome(
    bool Success,
    Guid DeviceId,
    string? CredentialKeyId,
    string? CredentialSecret,
    bool ReEnrolled,
    string? SealingPublicKey = null,
    string? SealingKeyFingerprint = null)
{
    public static EnrollmentOutcome Enrolled(
        Guid deviceId, string keyId, string secret, bool reEnrolled,
        string? sealingPublicKey = null, string? sealingKeyFingerprint = null) =>
        new(true, deviceId, keyId, secret, reEnrolled, sealingPublicKey, sealingKeyFingerprint);

    public static EnrollmentOutcome Refused() => new(false, Guid.Empty, null, null, false);
}
