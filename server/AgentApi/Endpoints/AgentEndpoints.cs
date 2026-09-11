using EndpointPlatform.Contracts;
using EndpointPlatform.Contracts.Agent;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Enrollment;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.AgentApi.Endpoints;

/// <summary>
/// The agent-facing endpoint surface: enrollment and heartbeat.
/// </summary>
/// <remarks>
/// <para>
/// Every handler here treats its caller as hostile until authenticated. Refusals
/// are uniform 401/403/400 responses with no distinguishing detail — the reasons
/// live in the audit trail and the server log, not on the wire, so a probing
/// caller learns nothing about which part of their guess was wrong.
/// </para>
/// <para>
/// Request size is bounded by the host's default Kestrel limits; field lengths
/// are validated here before anything touches the domain.
/// </para>
/// </remarks>
public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(AgentProtocol.RoutePrefix);

        group.MapPost(AgentProtocol.Routes.Enroll, EnrollAsync)
            .WithName("EnrollAgent")
            .AllowAnonymous() // The enrollment token IS the credential here.
            .RequireRateLimiting(EnrollmentRateLimitPolicy);

        // Approval-gated enrollment. Anonymous because a brand-new machine has no
        // credential yet - but anonymous access stops here: neither endpoint reads or
        // writes anything about a managed device, and neither grants management
        // access. A pending request can only ever become a device when an
        // authenticated administrator approves it.
        group.MapPost(AgentProtocol.Routes.EnrollRequest, RequestEnrollmentAsync)
            .WithName("RequestAgentEnrollment")
            .AllowAnonymous()
            .RequireRateLimiting(EnrollmentRateLimitPolicy);

        group.MapPost(AgentProtocol.Routes.EnrollClaim, ClaimEnrollmentAsync)
            .WithName("ClaimAgentEnrollment")
            .AllowAnonymous()
            .RequireRateLimiting(EnrollmentRateLimitPolicy);

        group.MapPost(AgentProtocol.Routes.Heartbeat, HeartbeatAsync)
            .WithName("AgentHeartbeat");

        group.MapPost(AgentProtocol.Routes.Inventory, InventoryAsync)
            .WithName("AgentInventory");

        group.MapPost(AgentProtocol.Routes.Usb, UsbReportAsync)
            .WithName("AgentUsbReport");

        group.MapGet(AgentProtocol.Routes.Tasks, ClaimTasksAsync)
            .WithName("AgentClaimTasks");

        group.MapPost(AgentProtocol.Routes.Tasks + "/{taskId:guid}" + AgentProtocol.Routes.TaskResultSuffix,
                PostTaskResultAsync)
            .WithName("AgentPostTaskResult");

        group.MapGet(AgentProtocol.Routes.AgentUpdate + "/latest", GetAgentUpdateInfoAsync)
            .WithName("AgentGetUpdateInfo");

        group.MapGet(AgentProtocol.Routes.AgentUpdate + "/{releaseId:guid}/content", GetAgentUpdateContentAsync)
            .WithName("AgentGetUpdateContent");

        group.MapGet(AgentProtocol.Routes.Policies, GetPoliciesAsync)
            .WithName("AgentGetPolicies");

        group.MapPost(AgentProtocol.Routes.Policies + AgentProtocol.Routes.PolicyComplianceSuffix, PostComplianceAsync)
            .WithName("AgentPostCompliance");

        group.MapGet(
                AgentProtocol.Routes.Packages + "/{packageId:guid}" + AgentProtocol.Routes.PackageContentSuffix,
                GetPackageContentAsync)
            .WithName("AgentGetPackageContent");

        group.MapGet(
                AgentProtocol.Routes.DriverPackages + "/{packageId:guid}" + AgentProtocol.Routes.PackageContentSuffix,
                GetDriverPackageContentAsync)
            .WithName("AgentGetDriverPackageContent");

        group.MapPost(AgentProtocol.Routes.SecretRedeem, RedeemSecretAsync)
            .WithName("AgentRedeemSecret");

        group.MapPost(AgentProtocol.Routes.BitLockerEscrow, EscrowRecoveryKeyAsync)
            .WithName("AgentEscrowRecoveryKey");

        group.MapGet(AgentProtocol.Routes.BitLockerEscrowStatus, GetEscrowStatusAsync)
            .WithName("AgentBitLockerEscrowStatus");

        return endpoints;
    }

    /// <summary>
    /// Exchanges a one-time secret reference for its plaintext, exactly once.
    /// </summary>
    /// <remarks>
    /// The reference is bound to the issuing device and deleted atomically on read, so
    /// a replay - or a reference stolen from a persisted task row - yields nothing. The
    /// secret is never logged here, and the failure response is deliberately uniform so
    /// it cannot distinguish "expired" from "someone else's".
    /// </remarks>
    private static async Task<IResult> RedeemSecretAsync(
        [FromBody] RedeemSecretRequest request,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Security.EphemeralSecretStore secretStore,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.", statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        var secret = await secretStore.RedeemAsync(auth.Device!.Id, request.SecretReference, cancellationToken);

        return secret is null
            ? Results.Problem(title: "Secret reference is not redeemable.", statusCode: StatusCodes.Status404NotFound)
            : Results.Ok(new RedeemSecretResponse(secret));
    }

    private static async Task<IResult> GetPackageContentAsync(
        Guid packageId,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Software.SoftwarePackageService packageService,
        Infrastructure.Software.IPackageContentStore contentStore,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.", statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        // The package must belong to the device's organization and not be withdrawn.
        var package = await packageService.GetDeployableAsync(
            auth.Device!.OrganizationId, packageId, cancellationToken);
        if (package is null)
        {
            return Results.NotFound();
        }

        var stream = await contentStore.OpenReadAsync(package.Sha256, cancellationToken);
        if (stream is null)
        {
            return Results.NotFound();
        }

        // The agent re-hashes and re-verifies the signer; these headers are hints, not trust.
        return Results.File(stream, "application/octet-stream", package.FileName, enableRangeProcessing: false);
    }

    /// <summary>
    /// Streams an approved driver package's archive to the requesting device.
    /// </summary>
    /// <remarks>
    /// Deliberately a separate handler from the software-package stream rather than a
    /// shared one parameterised by catalogue. The two artefacts have different
    /// verification gates on the endpoint, and one route serving both would mean an id
    /// resolving to whichever catalogue happened to contain it.
    /// </remarks>
    private static async Task<IResult> GetDriverPackageContentAsync(
        Guid packageId,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Drivers.DriverPackageService driverPackageService,
        Infrastructure.Software.IPackageContentStore contentStore,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.", statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        // Must belong to the device's own organization and not be withdrawn. A
        // withdrawn package stops being downloadable immediately, so a task queued
        // before the withdrawal cannot still fetch it.
        var package = await driverPackageService.GetDeployableAsync(
            auth.Device!.OrganizationId, packageId, cancellationToken);
        if (package is null)
        {
            return Results.NotFound();
        }

        var stream = await contentStore.OpenReadAsync(package.Sha256, cancellationToken);
        if (stream is null)
        {
            return Results.NotFound();
        }

        // The agent re-hashes the archive and verifies the catalogue signature and
        // signer pin itself. This transfer is not a trust boundary.
        return Results.File(stream, "application/octet-stream", package.FileName, enableRangeProcessing: false);
    }

    private static async Task<IResult> GetAgentUpdateInfoAsync(
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Agents.AgentReleaseService releaseService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.", statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        var latest = await releaseService.GetLatestPublishedAsync(cancellationToken);

        // This response is what the agent trusts over any task payload: an
        // UpdateAgent task that names anything else is refused by the agent.
        return latest is null
            ? Results.Ok(new AgentUpdateInfo(false, null, null, null, null, null, null, null))
            : Results.Ok(new AgentUpdateInfo(
                true, latest.Id, latest.Version, latest.Architecture,
                latest.FileName, latest.Sha256, latest.SignerSubject, latest.ContentSizeBytes));
    }

    private static async Task<IResult> GetAgentUpdateContentAsync(
        Guid releaseId,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Agents.AgentReleaseService releaseService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.", statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        // Only a Published release streams; Draft, Revoked and unknown are one
        // indistinguishable 404. The agent re-hashes what it receives anyway.
        var (stream, release) = await releaseService.OpenPublishedContentAsync(releaseId, cancellationToken);
        return stream is null || release is null
            ? Results.NotFound()
            : Results.File(stream, "application/octet-stream", release.FileName, enableRangeProcessing: false);
    }

    private static async Task<IResult> GetPoliciesAsync(
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Policies.PolicyService policyService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.", statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        var effective = await policyService.GetEffectivePoliciesAsync(auth.Device!.Id, cancellationToken);
        var policies = effective.Select(e => new AgentPolicy(
            e.Policy.Id, e.Version.Id, e.Version.VersionNumber, e.Policy.Type.ToString(), e.Version.DesiredStateJson)).ToArray();

        return Results.Ok(new AgentPolicyListResponse(policies));
    }

    private static async Task<IResult> PostComplianceAsync(
        [FromBody] AgentPolicyComplianceReport report,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Policies.PolicyService policyService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.", statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        if (report.Results is { Count: > 1000 })
        {
            return Results.Problem(title: "Too many compliance results.", statusCode: StatusCodes.Status400BadRequest);
        }

        var items = new List<Infrastructure.Policies.ComplianceInput>();
        foreach (var r in report.Results ?? [])
        {
            if (!Enum.TryParse<Domain.Policies.PolicyComplianceState>(r.State, out var state))
            {
                return Results.Problem(title: "Invalid compliance state.", statusCode: StatusCodes.Status400BadRequest);
            }

            var deviations = (r.Deviations ?? []).Where(d => d.Length <= 512).Take(64).ToArray();
            items.Add(new Infrastructure.Policies.ComplianceInput(
                r.PolicyId, r.PolicyVersionId, r.VersionNumber, state, deviations));
        }

        await policyService.RecordComplianceAsync(auth.Device!.OrganizationId, auth.Device.Id, items, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> ClaimTasksAsync(
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Tasks.DeviceTaskService taskService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        var claimed = await taskService.ClaimForDeviceAsync(auth.Device!.Id, cancellationToken);

        var tasks = claimed
            .Select(t => new AgentTask(t.Id, t.Type.ToString(), t.PayloadJson, t.ExpiresAt))
            .ToArray();

        return Results.Ok(new AgentTaskListResponse(tasks));
    }

    private static async Task<IResult> PostTaskResultAsync(
        Guid taskId,
        [FromBody] AgentTaskResult result,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Tasks.DeviceTaskService taskService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var auth = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);
        if (!auth.Success)
        {
            return Results.Unauthorized();
        }

        if (result.Message is { Length: > 1024 })
        {
            return Results.Problem(title: "Result message too long.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // A device may only report results for its OWN tasks: the task id is scoped
        // to the authenticated device, so a stolen credential cannot forge outcomes
        // for another machine.
        var applied = await taskService.CompleteAsync(
            auth.Device!.Id, taskId, result.Succeeded, result.Message, result.ResultJson, cancellationToken);

        return applied ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> EnrollAsync(
        [FromBody] EnrollRequest request,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentEnrollmentService enrollmentService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var validationError = ValidateEnrollRequest(request);
        if (validationError is not null)
        {
            return Results.Problem(title: validationError, statusCode: StatusCodes.Status400BadRequest);
        }

        var outcome = await enrollmentService.EnrollAsync(
            request.EnrollmentToken.Trim(),
            request.Hostname.Trim(),
            request.MachineIdentifier.Trim(),
            request.AgentVersion.Trim(),
            string.IsNullOrWhiteSpace(request.OperatingSystem) ? null : request.OperatingSystem.Trim(),
            cancellationToken);

        if (!outcome.Success)
        {
            // Deliberately indistinguishable for unknown/expired/revoked/exhausted.
            return Results.Problem(
                title: "Enrollment refused.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(new EnrollResponse(
            outcome.DeviceId,
            outcome.CredentialKeyId!,
            outcome.CredentialSecret!,
            outcome.ReEnrolled,
            outcome.SealingPublicKey,
            outcome.SealingKeyFingerprint));
    }

    /// <summary>
    /// Name of the rate-limit policy guarding the anonymous enrollment endpoints.
    /// </summary>
    /// <remarks>
    /// These are the only endpoints reachable without a credential, so they are the
    /// only ones an unauthenticated caller can flood. 10 requests per minute per
    /// The limit itself lives in AgentServerOptions because it must be tuned per
    /// deployment: a site behind NAT presents one address for every machine on it.
    /// </remarks>
    public const string EnrollmentRateLimitPolicy = "agent-enrollment";

    /// <summary>
    /// An unenrolled machine asking to be managed.
    /// </summary>
    /// <remarks>
    /// Accepts no organization and no secret. The organization is decided later by
    /// the administrator who approves; the agent proves possession of its request
    /// secret at claim time rather than presenting a bearer token now.
    /// </remarks>
    private static async Task<IResult> RequestEnrollmentAsync(
        [FromBody] EnrollmentRequestRequest request,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        PendingEnrollmentStore pendingStore,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // A request id is a SHA-256 hex digest. Validating the shape keeps arbitrary
        // client strings out of the Redis key space.
        if (string.IsNullOrWhiteSpace(request.RequestId)
            || request.RequestId.Length != 64
            || !request.RequestId.All(char.IsAsciiHexDigitLower))
        {
            return Results.Problem(
                title: "requestId must be a lowercase SHA-256 hex digest.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.MachineIdentifier) || request.MachineIdentifier.Length > 128
            || string.IsNullOrWhiteSpace(request.Hostname) || request.Hostname.Length > 253
            || string.IsNullOrWhiteSpace(request.AgentVersion) || request.AgentVersion.Length > 64
            || request.OperatingSystem?.Length > 256)
        {
            return Results.Problem(
                title: "Enrollment request details are missing or too long.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var pending = new PendingEnrollment(
            request.MachineIdentifier.Trim(),
            request.Hostname.Trim(),
            string.IsNullOrWhiteSpace(request.OperatingSystem) ? null : request.OperatingSystem.Trim(),
            request.AgentVersion.Trim(),
            timeProvider.GetUtcNow(),
            PendingEnrollmentStatus.Pending);

        var stored = await pendingStore.RequestAsync(request.RequestId, pending, cancellationToken);
        if (!stored)
        {
            // Retryable, not a refusal: the agent should keep trying rather than give up.
            return Results.Problem(
                title: "Enrollment is temporarily unavailable.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new EnrollmentRequestResponse("pending", PollAfterSeconds: 30));
    }

    /// <summary>
    /// The agent proving possession of its request secret, and collecting its
    /// credential once an administrator has approved.
    /// </summary>
    private static async Task<IResult> ClaimEnrollmentAsync(
        [FromBody] EnrollmentClaimRequest request,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        PendingEnrollmentStore pendingStore,
        AgentEnrollmentService enrollmentService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.RequestSecret) || request.RequestSecret.Length > 512)
        {
            return Results.Problem(title: "requestSecret is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Consumes the pending request atomically when approved, so a replayed claim
        // cannot obtain a second credential.
        var outcome = await pendingStore.ClaimAsync(request.RequestSecret, cancellationToken);

        switch (outcome.Status)
        {
            case ClaimStatus.Pending:
                return Results.Ok(new EnrollmentClaimResponse(
                    "pending", null, null, null, false, PollAfterSeconds: 30));

            case ClaimStatus.Rejected:
                return Results.Ok(new EnrollmentClaimResponse(
                    "rejected", null, null, null, false, PollAfterSeconds: 0));

            case ClaimStatus.Unavailable:
                return Results.Problem(
                    title: "Enrollment is temporarily unavailable.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            case ClaimStatus.Approved:
                break;

            default:
                // Unknown, already claimed, or expired — all indistinguishable on
                // purpose, so a caller cannot probe which request ids exist.
                return Results.Problem(title: "Enrollment refused.", statusCode: StatusCodes.Status403Forbidden);
        }

        // Approved: complete through the EXISTING enrollment path, so device creation,
        // re-enrolment de-duplication, credential issuance and enrollment auditing all
        // behave exactly as they do for a token-based enrolment.
        var pending = outcome.Request!;
        var enrolled = await enrollmentService.EnrollAsync(
            outcome.EnrollmentTokenSecret!,
            pending.Hostname,
            pending.MachineIdentifier,
            pending.AgentVersion,
            pending.OperatingSystem,
            cancellationToken);

        if (!enrolled.Success)
        {
            return Results.Problem(title: "Enrollment refused.", statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(new EnrollmentClaimResponse(
            "approved",
            enrolled.DeviceId,
            enrolled.CredentialKeyId!,
            enrolled.CredentialSecret!,
            enrolled.ReEnrolled,
            PollAfterSeconds: 0,
            // The claim path issues a credential exactly as direct enrollment
            // does, so it pins the same sealing key. Omitting it here would leave
            // approved-by-request devices permanently ineligible for automatic
            // escrow, for no reason a reader could find.
            enrolled.SealingPublicKey,
            enrolled.SealingKeyFingerprint));
    }

    private static async Task<IResult> HeartbeatAsync(
        [FromBody] HeartbeatRequest request,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Persistence.EndpointPlatformDbContext dbContext,
        Infrastructure.Policies.PolicyService policyService,
        IOptions<AgentServerOptions> agentServerOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Authenticate before validating the body: an unauthenticated caller gets
        // 401 regardless of what they sent, and learns nothing about our schema.
        var authentication = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);

        if (!authentication.Success)
        {
            return Results.Unauthorized();
        }

        var validationError = ValidateHeartbeatRequest(request);
        if (validationError is not null)
        {
            return Results.Problem(title: validationError, statusCode: StatusCodes.Status400BadRequest);
        }

        var device = authentication.Device!;
        var now = timeProvider.GetUtcNow();

        device.RecordHeartbeat(
            request.Hostname.Trim(),
            request.AgentVersion.Trim(),
            string.IsNullOrWhiteSpace(request.OperatingSystem) ? null : request.OperatingSystem.Trim(),
            now);

        // Heartbeats are routine, high-volume signals - they update last_seen but
        // do not each produce an audit entry, which would bury real events under
        // one row per device per minute. Enrollment, task execution and mutations
        // are the audited operations.
        await dbContext.SaveChangesAsync(cancellationToken);

        var tasksPending = await dbContext.DeviceTasks
            .AnyAsync(t => t.DeviceId == device.Id
                           && t.Status == Domain.Tasks.DeviceTaskStatus.Queued, cancellationToken);

        var policiesPending = await policyService.HasPendingComplianceAsync(device.Id, cancellationToken);

        return Results.Ok(new HeartbeatResponse(
            now,
            agentServerOptions.Value.HeartbeatIntervalSeconds,
            InventoryRequested: device.IsInventoryRefreshPending,
            TasksPending: tasksPending,
            PoliciesPending: policiesPending));
    }

    private static async Task<IResult> InventoryAsync(
        [FromBody] InventoryReport report,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Devices.DeviceInventoryService inventoryService,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var authentication = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);

        if (!authentication.Success)
        {
            return Results.Unauthorized();
        }

        var validationError = ValidateInventoryReport(report);
        if (validationError is not null)
        {
            return Results.Problem(title: validationError, statusCode: StatusCodes.Status400BadRequest);
        }

        await inventoryService.ApplyAsync(authentication.Device!, report, cancellationToken);

        return Results.Ok(new InventoryResponse(timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// Accepts a USB report from an enrolled endpoint and answers with the USB
    /// storage policy that endpoint must enforce.
    /// </summary>
    /// <remarks>
    /// The report is untrusted input and is treated as such: it can describe
    /// hardware and confess what the agent is enforcing, but nothing in it can
    /// create, extend or widen a grant. The response is computed purely from
    /// administrator decisions already recorded in the database, so an agent
    /// that lies about its inventory still receives only the access an
    /// administrator granted for a device instance ID.
    /// </remarks>
    private static async Task<IResult> UsbReportAsync(
        [FromBody] UsbReport report,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.Peripherals.UsbService usbService,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var authentication = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);

        if (!authentication.Success)
        {
            return Results.Unauthorized();
        }

        if (report?.Devices is null)
        {
            return Results.Problem(
                title: "A USB report must carry a device list; send an empty array for 'nothing attached'.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (report.Devices.Count > Infrastructure.Peripherals.UsbService.MaxDevicesPerReport)
        {
            return Results.Problem(
                title: $"A USB report may describe at most "
                    + $"{Infrastructure.Peripherals.UsbService.MaxDevicesPerReport} devices.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var policy = await usbService.IngestReportAsync(authentication.Device!, report, cancellationToken);

        return Results.Ok(policy);
    }

    private static string? ValidateInventoryReport(InventoryReport report)
    {
        if (report.Hardware is null)
        {
            return "Hardware section is required.";
        }

        if (report.NetworkInterfaces is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxNetworkInterfaces })
        {
            return "Too many network interfaces.";
        }

        if (report.Hardware.Disks is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxDisks })
        {
            return "Too many disks.";
        }

        foreach (var nic in report.NetworkInterfaces ?? [])
        {
            if (string.IsNullOrWhiteSpace(nic.Name) || nic.Name.Length > 256)
            {
                return "Every network interface requires a name of at most 256 characters.";
            }

            if (nic.IpAddresses is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxIpAddressesPerInterface })
            {
                return "Too many IP addresses on one interface.";
            }

            // Pre-validate the MAC so a malformed one is a clean 400 here rather
            // than an exception inside the domain's normaliser.
            if (nic.MacAddress is { } mac)
            {
                var hexDigits = mac.Count(Uri.IsHexDigit);
                if (mac.Length > 23 || (hexDigits != 12 && hexDigits != 16))
                {
                    return "A network interface MAC address is malformed.";
                }
            }

            foreach (var ip in nic.IpAddresses ?? [])
            {
                if (string.IsNullOrWhiteSpace(ip) || ip.Length > 64)
                {
                    return "A network interface IP address is malformed.";
                }
            }
        }

        if (report.LoggedOnUser is { Length: > 256 })
        {
            return "Logged-on user must be at most 256 characters.";
        }

        if (report.Software is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxSoftwareEntries })
        {
            return "Too many software entries.";
        }

        foreach (var app in report.Software ?? [])
        {
            if (string.IsNullOrWhiteSpace(app.Name) || app.Name.Length > 384
                || app.Version is { Length: > 128 } || app.Publisher is { Length: > 256 }
                || app.InstallLocation is { Length: > 512 }
                || app.InstallationScope is { Length: > 16 }
                || app.InstalledForUser is { Length: > 256 }
                || app.ProductCode is { Length: > 64 })
            {
                return "A software entry is malformed.";
            }

            // Application discovery (agent 1.9.0). Absent is fine -- an older
            // agent -- but a value must be one the contract names, at its length.
            if (app.IdentityKind is { } kind && !InventorySoftware.IdentityKinds.Contains(kind)
                || app.Confidence is { } confidence && !InventorySoftware.Confidences.Contains(confidence)
                || app.Category is { } category && !InventorySoftware.Categories.Contains(category)
                || app.SignatureStatus is { } status && !InventorySoftware.SignatureStatuses.Contains(status)
                || app.StableKey is { Length: > InventorySoftware.MaxStableKey }
                || app.VersionKey is { Length: > InventorySoftware.MaxVersionKey }
                || app.PackageFamilyName is { Length: > InventorySoftware.MaxPackageName }
                || app.PackageFullName is { Length: > InventorySoftware.MaxPackageName }
                || app.UpgradeCode is { Length: > InventorySoftware.MaxUpgradeCode }
                || app.ExecutablePath is { Length: > InventorySoftware.MaxExecutablePath }
                || app.SignerSubject is { Length: > InventorySoftware.MaxSignerSubject })
            {
                return "A software entry is malformed.";
            }

            if (app.Evidence is { Count: > InventorySoftware.MaxEvidence })
            {
                return "A software entry carries too much evidence.";
            }

            foreach (var witness in app.Evidence ?? [])
            {
                if (witness is null
                    || string.IsNullOrWhiteSpace(witness.Source)
                    || !InventorySoftwareEvidence.Sources.Contains(witness.Source)
                    || witness.Name is { Length: > InventorySoftwareEvidence.MaxName }
                    || witness.Detail is { Length: > InventorySoftwareEvidence.MaxDetail })
                {
                    return "A software evidence entry is malformed.";
                }
            }
        }

        if (report.SecurityPosture is { } posture)
        {
            // Bounds guard against a hostile agent sending absurd values.
            if (posture.DefenderSignatureAgeDays is < 0 or > 3650
                || posture.LocalAdministratorCount is < 0 or > 100000
                || posture.TpmSpecVersion is { Length: > 32 }
                || posture.BitLockerSystemDriveStatus is { Length: > 32 })
            {
                return "Security posture values are out of range.";
            }
        }

        if (report.Services is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxServices })
        {
            return "Too many services.";
        }

        if (report.Processes is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxProcesses })
        {
            return "Too many processes.";
        }

        if (report.WindowsUpdate?.History is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxUpdateHistory })
        {
            return "Too many update history entries.";
        }

        if (report.LocalAccounts is { } accounts)
        {
            if (accounts.Users is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxLocalUsers })
            {
                return "Too many local users.";
            }

            if (accounts.Groups is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxLocalGroups })
            {
                return "Too many local groups.";
            }

            foreach (var user in accounts.Users ?? [])
            {
                if (!IsValidSid(user.Sid) || string.IsNullOrWhiteSpace(user.Name) || user.Name.Length > 256
                    || user.FullName is { Length: > 256 } || user.Description is { Length: > 512 })
                {
                    return "A local user entry is malformed.";
                }
            }

            foreach (var group in accounts.Groups ?? [])
            {
                if (!IsValidSid(group.Sid) || string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 256
                    || group.Description is { Length: > 512 })
                {
                    return "A local group entry is malformed.";
                }

                if (group.Members is { Count: > Infrastructure.Devices.DeviceInventoryService.MaxGroupMembers })
                {
                    return "A local group reports too many members.";
                }

                foreach (var member in group.Members ?? [])
                {
                    if (string.IsNullOrWhiteSpace(member.Name) || member.Name.Length > 256
                        || (member.Sid is { } sid && !IsValidSid(sid)))
                    {
                        return "A group member entry is malformed.";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Structural SID check: "S-1-..." with digits and dashes, bounded length.</summary>
    private static bool IsValidSid(string? sid)
    {
        if (sid is null || sid.Length is < 4 or > 184 || !sid.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in sid.AsSpan(2))
        {
            if (c is not ((>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ValidateEnrollRequest(EnrollRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EnrollmentToken) || request.EnrollmentToken.Length > 128)
        {
            return "Enrollment token is missing or malformed.";
        }

        if (string.IsNullOrWhiteSpace(request.Hostname) || request.Hostname.Length > 253)
        {
            return "Hostname is required and must be at most 253 characters.";
        }

        if (string.IsNullOrWhiteSpace(request.MachineIdentifier) || request.MachineIdentifier.Length > 128)
        {
            return "Machine identifier is required and must be at most 128 characters.";
        }

        if (string.IsNullOrWhiteSpace(request.AgentVersion) || request.AgentVersion.Length > 64)
        {
            return "Agent version is required and must be at most 64 characters.";
        }

        if (request.OperatingSystem is { Length: > 256 })
        {
            return "Operating system must be at most 256 characters.";
        }

        return null;
    }

    private static string? ValidateHeartbeatRequest(HeartbeatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Hostname) || request.Hostname.Length > 253)
        {
            return "Hostname is required and must be at most 253 characters.";
        }

        if (string.IsNullOrWhiteSpace(request.AgentVersion) || request.AgentVersion.Length > 64)
        {
            return "Agent version is required and must be at most 64 characters.";
        }

        if (request.OperatingSystem is { Length: > 256 })
        {
            return "Operating system must be at most 256 characters.";
        }

        return null;
    }
    // ---- automatic BitLocker recovery-password escrow ---------------------

    /// <summary>
    /// Accepts an endpoint-sealed recovery envelope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This endpoint cannot read what it stores.</b> The Agent API holds the
    /// public sealing key and nothing that could unwrap an envelope, so a request
    /// here deposits ciphertext the process is structurally unable to open. That is
    /// the reason automatic escrow was built this way rather than posting the
    /// password over TLS: this process is reachable by every managed endpoint.
    /// </para>
    /// <para>
    /// The device comes from the authenticated credential and never from the body,
    /// so an agent can only file against itself.
    /// </para>
    /// </remarks>
    private static async Task<IResult> EscrowRecoveryKeyAsync(
        [FromBody] EscrowRecoveryKeyRequest request,
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.BitLocker.AutomaticEscrowIngestionService ingestion,
        Infrastructure.Persistence.EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var authentication = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);

        if (!authentication.Success)
        {
            return Results.Unauthorized();
        }

        if (request is null
            || string.IsNullOrWhiteSpace(request.VolumeDeviceIdentifier)
            || string.IsNullOrWhiteSpace(request.KeyProtectorId)
            || string.IsNullOrWhiteSpace(request.SealedEnvelope))
        {
            return Results.Problem(
                title: "A volume, a protector and a sealed envelope are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The credential the caller actually authenticated with, re-read so
        // eligibility is judged on current state rather than on anything the
        // request asserts.
        var credential = await dbContext.AgentCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == authentication.CredentialId, cancellationToken);

        if (credential is null)
        {
            return Results.Unauthorized();
        }

        var result = await ingestion.IngestAsync(
            authentication.Device!,
            credential,
            request.VolumeDeviceIdentifier.Trim(),
            request.KeyProtectorId.Trim(),
            request.SealedEnvelope,
            cancellationToken);

        return result.Outcome switch
        {
            Infrastructure.BitLocker.AutomaticEscrowIngestOutcome.Escrowed =>
                Results.Ok(new EscrowRecoveryKeyResponse("escrowed", result.EscrowId)),

            // Idempotent success: repeated inventory must be free.
            Infrastructure.BitLocker.AutomaticEscrowIngestOutcome.AlreadyEscrowed =>
                Results.Ok(new EscrowRecoveryKeyResponse("already-escrowed", result.EscrowId)),

            Infrastructure.BitLocker.AutomaticEscrowIngestOutcome.NotEligible =>
                Results.Problem(title: result.Error, statusCode: StatusCodes.Status403Forbidden),

            Infrastructure.BitLocker.AutomaticEscrowIngestOutcome.FingerprintMismatch =>
                Results.Problem(title: result.Error, statusCode: StatusCodes.Status403Forbidden),

            _ => Results.Problem(title: result.Error, statusCode: StatusCodes.Status400BadRequest),
        };
    }

    /// <summary>
    /// Reports which of this device's protectors are already escrowed.
    /// </summary>
    /// <remarks>
    /// Metadata only. The agent uses this to avoid retrieving a password it has
    /// already filed, which is both an idempotence measure and a privacy one: a
    /// machine already escrowed never reads its recovery password again.
    /// </remarks>
    private static async Task<IResult> GetEscrowStatusAsync(
        [FromHeader(Name = AgentProtocol.Headers.Credential)] string? credentialHeader,
        [FromHeader(Name = AgentProtocol.Headers.ProtocolVersion)] int? protocolVersion,
        AgentAuthenticationService authenticationService,
        Infrastructure.BitLocker.AutomaticEscrowIngestionService ingestion,
        Infrastructure.Security.IEscrowSealingKeyProvider sealingKey,
        Infrastructure.Persistence.EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        if (protocolVersion != AgentProtocol.Version)
        {
            return Results.Problem(
                title: "Unsupported agent protocol version.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var authentication = await authenticationService.AuthenticateAsync(credentialHeader, cancellationToken);

        if (!authentication.Success)
        {
            return Results.Unauthorized();
        }

        var credential = await dbContext.AgentCredentials
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == authentication.CredentialId, cancellationToken);

        if (credential is null)
        {
            return Results.Unauthorized();
        }

        var protectors = await ingestion.GetStatusAsync(authentication.Device!.Id, cancellationToken);

        return Results.Ok(new BitLockerEscrowStatusResponse(
            credential.IsAutomaticEscrowEligible,
            credential.SealingKeyFingerprint,
            // Offered, not trusted: the agent checks this against the fingerprint
            // it pinned at enrollment before it seals anything to it.
            sealingKey.PublicKeySpki,
            [.. protectors.Select(p => new BitLockerEscrowStatusItem(
                p.Volume, p.Protector, p.Escrowed, p.EscrowedAt, p.State, p.Due, p.NextAttemptAt))]));
    }
}
