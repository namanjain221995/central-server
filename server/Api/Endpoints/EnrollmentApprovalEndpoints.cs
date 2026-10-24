using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Enrollment;
using EndpointPlatform.Infrastructure.Configuration;
using EndpointPlatform.Infrastructure.Enrollment;
using EndpointPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Deciding which machines are allowed to become managed devices.
/// </summary>
/// <remarks>
/// <para>
/// An agent installed from the MSI carries no credential and no token. It asks to be
/// managed through the anonymous agent endpoint, and nothing happens until an
/// administrator approves it here. This is the authorization boundary for the whole
/// enrollment flow: the anonymous side can create a request and poll it, but only an
/// authenticated approval turns one into a device.
/// </para>
/// <para>
/// The same page decides <b>re-enrollments</b>: a caller holding a valid token whose
/// machine identifier matches an ACTIVE device. Those are a different stake — approval
/// revokes the existing device's working credential and hands its identity to the
/// requester — so they carry checks plain requests do not: the row is visible only
/// inside its own organization, and approving requires device scope over the matched
/// device, not just the <c>device.enroll</c> permission.
/// </para>
/// <para>
/// <b>For plain requests the organization comes from the approver, never from the
/// request.</b> A pending request has no organization — an unauthenticated caller must
/// not be able to choose which tenant it joins — so the approving administrator's
/// organization becomes the device's. A re-enrollment's organization is different:
/// it is already proven by the enrollment token the agent presented, so the record is
/// born with it, and approval checks it against the approver's instead of assigning it.
/// </para>
/// <para>
/// These endpoints hold no enrollment logic. Atomicity, double-approval safety and
/// expiry all live in <see cref="EnrollmentApprovalService"/> and
/// <see cref="PendingEnrollmentStore"/>, which the agent-facing side uses too.
/// </para>
/// </remarks>
public static class EnrollmentApprovalEndpoints
{
    public static IEndpointRouteBuilder MapEnrollmentApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/enrollments");

        group.MapGet("/pending", ListPendingAsync)
            .WithName("ListPendingEnrollments")
            .RequirePermission(Permissions.Device.Enroll);

        group.MapPost("/{requestId}/approve", ApproveAsync)
            .WithName("ApproveEnrollment")
            .RequirePermission(Permissions.Device.Enroll);

        group.MapPost("/{requestId}/reject", RejectAsync)
            .WithName("RejectEnrollment")
            .RequirePermission(Permissions.Device.Enroll);

        return endpoints;
    }

    /// <summary>
    /// Machines waiting on a decision.
    /// </summary>
    /// <remarks>
    /// Returns only what an administrator needs to recognise the machine in front of
    /// them. The request id shown here is the SHA-256 the agent published; the secret
    /// behind it never leaves the endpoint, and the sealed enrollment token attached
    /// to an approved request is never projected. Re-enrollment rows additionally say
    /// which device would be handed over and whether that device's own credential is
    /// still in use — the one signal that separates "machine being reinstalled" from
    /// "machine being taken over while it is still running".
    /// </remarks>
    private static async Task<IResult> ListPendingAsync(
        HttpContext httpContext,
        PendingEnrollmentStore pendingStore,
        EndpointPlatformDbContext dbContext,
        IOptions<AgentServerOptions> agentServerOptions,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var all = await pendingStore.ListAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();

        // Re-enrollment rows name a real device, so they are tenant-scoped like
        // everything else that names one. Plain requests carry no organization until
        // approval and remain visible to anyone who may approve, as before.
        var pending = all
            .Where(entry => entry.Request.Kind != PendingEnrollmentKind.ReEnrollment
                            || entry.Request.OrganizationId == actor.OrganizationId)
            .ToList();

        // One query answers "is the matched device's credential still being used"
        // for every re-enrollment row. AgentCredential.LastUsedAt advances on every
        // successful device authentication and — unlike the device's own last-seen,
        // which enrollment itself bumps — is untouched by the request being listed,
        // so the requester cannot make its target look idle.
        var matchedDeviceIds = pending
            .Where(e => e.Request is { Kind: PendingEnrollmentKind.ReEnrollment, ExistingDeviceId: not null })
            .Select(e => e.Request.ExistingDeviceId!.Value)
            .Distinct()
            .ToList();

        var lastUsedByDevice = matchedDeviceIds.Count == 0
            ? new Dictionary<Guid, DateTimeOffset?>()
            : await dbContext.AgentCredentials
                .Where(c => matchedDeviceIds.Contains(c.DeviceId) && c.RevokedAt == null)
                .GroupBy(c => c.DeviceId)
                .Select(g => new { DeviceId = g.Key, LastUsedAt = g.Max(c => c.LastUsedAt) })
                .ToDictionaryAsync(x => x.DeviceId, x => x.LastUsedAt, cancellationToken);

        var offlineAfter = TimeSpan.FromSeconds(agentServerOptions.Value.OfflineAfterSeconds);

        return Results.Ok(pending.Select(entry =>
        {
            var isReEnrollment = entry.Request.Kind == PendingEnrollmentKind.ReEnrollment;

            DateTimeOffset? credentialLastUsedAt = null;
            if (isReEnrollment && entry.Request.ExistingDeviceId is { } deviceId
                && lastUsedByDevice.TryGetValue(deviceId, out var lastUsed))
            {
                credentialLastUsedAt = lastUsed;
            }

            return new
            {
                RequestId = entry.RequestId,
                entry.Request.Hostname,

                // Shown so an administrator can tell a re-enrolling machine from a new
                // one. Not a secret and not an authenticator - the server only uses it to
                // resolve an existing device record instead of creating a duplicate.
                entry.Request.MachineIdentifier,

                entry.Request.OperatingSystem,
                entry.Request.AgentVersion,
                entry.Request.RequestedAt,

                // From the record's actual Redis TTL: lifetimes differ by kind and
                // state and are refreshed by retries, so a reconstruction from
                // RequestedAt would drift. The fallback only covers a race with expiry.
                ExpiresAt = entry.TimeToLive is { } ttl
                    ? now.Add(ttl)
                    : entry.Request.RequestedAt.Add(PendingEnrollment.Lifetime),

                Status = entry.Request.Status.ToString(),

                // Populated only once decided, and only ever a display name.
                entry.Request.ApprovedBy,

                // What is being decided. "re-enrollment" means approving hands the
                // matched device's identity to the requester and revokes its
                // current credential.
                Kind = isReEnrollment ? "re-enrollment" : "enrollment",
                MatchedDeviceId = isReEnrollment ? entry.Request.ExistingDeviceId : null,
                MatchedDeviceHostname = isReEnrollment ? entry.Request.ExistingDeviceHostname : null,
                CredentialLastUsedAt = credentialLastUsedAt,

                // True when the matched device authenticated within the fleet's
                // offline window — a machine that is visibly ALIVE while someone
                // else asks to become it is the suspicious case.
                Online = isReEnrollment
                    ? (bool?)(credentialLastUsedAt is { } used && now - used <= offlineAfter)
                    : null,

                // Deliberately absent: SealedTokenSecret, and anything derived from the
                // agent's proof secret beyond the request id it already published.
            };
        }));
    }

    private static async Task<IResult> ApproveAsync(
        string requestId,
        HttpContext httpContext,
        EnrollmentApprovalService approvalService,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var result = await approvalService.ApproveAsync(
            requestId, actor.OrganizationId, actor.UserId, actor.Email, cancellationToken);

        if (!result.Success)
        {
            // Scope refusals are 403: the administrator can see the row and needs
            // to know the problem is their device scope, not a stale list.
            // Everything else — expired, already decided, lost a race, another
            // tenant's request — is 409 rather than 404: the request may well have
            // existed a moment ago, and the caller's view of it is simply stale.
            return Results.Problem(
                title: result.FailureReason ?? "The enrollment request could not be approved.",
                statusCode: result.IsForbidden
                    ? StatusCodes.Status403Forbidden
                    : StatusCodes.Status409Conflict);
        }

        var isReEnrollment = result.Request!.Kind == PendingEnrollmentKind.ReEnrollment;

        return Results.Ok(new
        {
            RequestId = requestId,
            Status = nameof(PendingEnrollmentStatus.Approved),
            result.Request!.Hostname,
            Kind = isReEnrollment ? "re-enrollment" : "enrollment",
            Message = isReEnrollment
                // The re-enrolling agent retries its token every ~5 minutes; the
                // approval waits for that retry rather than pushing anything.
                ? "The agent completes re-enrollment on its own next retry, within about 5 minutes. "
                  + "The existing device's credential is revoked at that moment."
                : "The agent will collect its credential on its next poll.",
        });
    }

    private static async Task<IResult> RejectAsync(
        string requestId,
        HttpContext httpContext,
        EnrollmentApprovalService approvalService,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var result = await approvalService.RejectAsync(
            requestId, actor.OrganizationId, actor.UserId, actor.Email, cancellationToken);

        if (!result.Success)
        {
            return Results.Problem(
                title: result.FailureReason ?? "The enrollment request could not be rejected.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var isReEnrollment = result.Request!.Kind == PendingEnrollmentKind.ReEnrollment;

        return Results.Ok(new
        {
            RequestId = requestId,
            Status = nameof(PendingEnrollmentStatus.Rejected),
            result.Request!.Hostname,
            Kind = isReEnrollment ? "re-enrollment" : "enrollment",
            Message = isReEnrollment
                ? "The device keeps its current credential. If the machine is still retrying "
                  + "after an hour, the request reappears here."
                : "No credential will be issued for this machine.",
        });
    }
}
