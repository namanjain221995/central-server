using System.ComponentModel.DataAnnotations;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Identity;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Temporary local administrator rights for an account on one endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation passes the same chain as the rest of the platform: permission,
/// then device scope, then domain validation. The dashboard hides controls a user
/// cannot use as a courtesy; it is never the boundary.
/// </para>
/// <para>
/// Reading is separated from mutating. <c>user.view</c> shows what elevations
/// exist and what state they are in -- an Auditor must be able to see who was
/// given administrator rights -- while granting or ending one needs
/// <c>localuser.elevate</c>, which only IT Admin holds.
/// </para>
/// </remarks>
public static class LocalAdminElevationEndpoints
{
    public static IEndpointRouteBuilder MapLocalAdminElevationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var device = endpoints.MapGroup("/admin/v1/devices/{deviceId:guid}");

        device.MapGet("/elevations", ListAsync)
            .WithName("ListDeviceElevations")
            .RequirePermission(Permissions.LocalUser.View);

        device.MapPost("/elevations", RequestAsync)
            .WithName("RequestDeviceElevation")
            .RequirePermission(Permissions.LocalUser.Elevate);

        var elevation = endpoints.MapGroup("/admin/v1/elevations");

        elevation.MapPost("/{elevationId:guid}/approve", ApproveAsync)
            .WithName("ApproveDeviceElevation")
            .RequirePermission(Permissions.LocalUser.Elevate);

        elevation.MapPost("/{elevationId:guid}/revoke", RevokeAsync)
            .WithName("RevokeDeviceElevation")
            .RequirePermission(Permissions.LocalUser.Elevate);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid deviceId,
        EndpointPlatformDbContext dbContext,
        DeviceScopeAuthorizer scope,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var now = timeProvider.GetUtcNow();

        var rows = await dbContext.LocalAdminElevations
            .AsNoTracking()
            .Where(e => e.DeviceId == deviceId && e.OrganizationId == actor.OrganizationId)
            .OrderByDescending(e => e.RequestedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        return Results.Ok(rows.Select(e => new
        {
            id = e.Id,
            deviceId = e.DeviceId,
            targetSid = e.TargetSid,
            targetUsername = e.TargetUsername,
            state = e.State.ToString(),

            // Computed from the clock rather than read from the state, so a row
            // the sweeper has not reached yet is still reported as conferring
            // nothing. The console must never show a lapsed elevation as live.
            isLive = e.IsLive(now),

            justification = e.Justification,
            requestedAt = e.RequestedAt,
            requestedBy = e.RequestedByDisplay,
            approvedAt = e.ApprovedAt,
            approvedBy = e.ApprovedByDisplay,
            activatedAt = e.ActivatedAt,
            expiresAt = e.ExpiresAt,
            revokedAt = e.RevokedAt,
            decisionNote = e.DecisionNote,
            failureReason = e.FailureReason,
        }));
    }

    private static async Task<IResult> RequestAsync(
        Guid deviceId,
        RequestElevationRequest request,
        HttpContext httpContext,
        LocalAdminElevationService service,
        DeviceScopeAuthorizer scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        // A duration means the requester is also approving. Absent, the request
        // is left pending. The states stay distinct either way, so a genuine
        // second-person approval can be introduced later without reshaping this.
        var duration = request.DurationMinutes is { } minutes
            ? TimeSpan.FromMinutes(minutes)
            : (TimeSpan?)null;

        var (outcome, elevation) = await service.RequestAsync(
            actor.OrganizationId, deviceId, request.TargetSid!, request.Justification!,
            duration, actor.UserId, actor.Email, cancellationToken);

        // Approved on the spot: tell the endpoint now. A pending request
        // authorizes nothing yet, so it has nothing to push.
        if (outcome == ElevationOutcome.Success && elevation!.State == LocalAdminElevationState.Approved)
        {
            await service.QueueElevationPushAsync(
                actor.OrganizationId, deviceId, actor.UserId, actor.Email, cancellationToken);
        }

        return outcome switch
        {
            ElevationOutcome.Success => Results.Ok(new
            {
                id = elevation!.Id,
                state = elevation.State.ToString(),
                expiresAt = elevation.ExpiresAt,
                targetUsername = elevation.TargetUsername,
            }),

            ElevationOutcome.DeviceNotFound => Results.NotFound(),

            ElevationOutcome.AccountNotFound => Results.Problem(
                "This endpoint has not reported an account with that SID. An elevation can only "
                + "target an account the machine has actually reported.",
                statusCode: StatusCodes.Status404NotFound),

            ElevationOutcome.ProtectedAccount => Results.Problem(
                "The built-in Administrator account cannot be elevated. It already holds "
                + "administrator rights and is protected from modification by this platform.",
                statusCode: StatusCodes.Status409Conflict),

            ElevationOutcome.AlreadyElevated => Results.Problem(
                "This account already has a pending or live elevation. Revoke it before issuing "
                + "another, so that the expiry time stays unambiguous.",
                statusCode: StatusCodes.Status409Conflict),

            ElevationOutcome.InvalidDuration => InvalidDuration(),

            _ => Results.Problem("Unhandled elevation outcome.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> ApproveAsync(
        Guid elevationId,
        ApproveElevationRequest request,
        HttpContext httpContext,
        LocalAdminElevationService service,
        DeviceScopeAuthorizer scope,
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = AdminActor.Required(httpContext.User);

        // Scope is checked against the elevation's own device, not against
        // anything the caller supplied: the route names an elevation, and an
        // administrator scoped to one group must not be able to act on another
        // group's device by quoting its elevation id.
        var deviceId = await dbContext.LocalAdminElevations
            .AsNoTracking()
            .Where(e => e.Id == elevationId && e.OrganizationId == actor.OrganizationId)
            .Select(e => (Guid?)e.DeviceId)
            .SingleOrDefaultAsync(cancellationToken);

        if (deviceId is null)
        {
            return Results.NotFound();
        }

        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId.Value, cancellationToken))
        {
            return OutOfScope();
        }

        var (outcome, elevation) = await service.ApproveAsync(
            actor.OrganizationId, elevationId, TimeSpan.FromMinutes(request.DurationMinutes),
            actor.UserId, actor.Email, cancellationToken);

        if (outcome == ElevationOutcome.Success)
        {
            await service.QueueElevationPushAsync(
                actor.OrganizationId, deviceId.Value, actor.UserId, actor.Email, cancellationToken);
        }

        return outcome switch
        {
            ElevationOutcome.Success => Results.Ok(new
            {
                id = elevation!.Id,
                state = elevation.State.ToString(),
                expiresAt = elevation.ExpiresAt,
            }),

            ElevationOutcome.NotFound => Results.NotFound(),

            // Covers an already-approved, active or terminal elevation. Approval
            // is not a way to extend a live window: a longer one means revoking
            // and requesting again, which leaves two audit records rather than
            // one that quietly changed meaning.
            ElevationOutcome.InvalidState => Results.Problem(
                "Only a pending request can be approved. An elevation that is already approved, "
                + "active or finished cannot be approved again, and an existing elevation is "
                + "never extended.",
                statusCode: StatusCodes.Status409Conflict),

            ElevationOutcome.InvalidDuration => InvalidDuration(),

            _ => Results.Problem("Unhandled elevation outcome.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static async Task<IResult> RevokeAsync(
        Guid elevationId,
        RevokeElevationRequest? request,
        HttpContext httpContext,
        LocalAdminElevationService service,
        DeviceScopeAuthorizer scope,
        EndpointPlatformDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var deviceId = await dbContext.LocalAdminElevations
            .AsNoTracking()
            .Where(e => e.Id == elevationId && e.OrganizationId == actor.OrganizationId)
            .Select(e => (Guid?)e.DeviceId)
            .SingleOrDefaultAsync(cancellationToken);

        if (deviceId is null)
        {
            return Results.NotFound();
        }

        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId.Value, cancellationToken))
        {
            return OutOfScope();
        }

        var outcome = await service.RevokeAsync(
            actor.OrganizationId, elevationId, request?.Note, actor.UserId, actor.Email, cancellationToken);

        // The pushed set no longer contains this elevation, so the endpoint removes
        // the rights now instead of waiting for the deadline.
        if (outcome == ElevationOutcome.Success)
        {
            await service.QueueElevationPushAsync(
                actor.OrganizationId, deviceId.Value, actor.UserId, actor.Email, cancellationToken);
        }

        return outcome switch
        {
            ElevationOutcome.Success => Results.NoContent(),
            ElevationOutcome.NotFound => Results.NotFound(),

            ElevationOutcome.InvalidState => Results.Problem(
                "There is nothing to revoke: this elevation has already expired, been revoked, "
                + "been rejected or failed.",
                statusCode: StatusCodes.Status409Conflict),

            _ => Results.Problem("Unhandled elevation outcome.", statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    private static IResult? Validate<T>(T request) where T : notnull
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            return null;
        }

        return Results.ValidationProblem(results.ToDictionary(
            v => v.MemberNames.FirstOrDefault() ?? "request",
            v => new[] { v.ErrorMessage ?? "Invalid." }));
    }

    private static IResult InvalidDuration() => Results.Problem(
        $"An elevation must last between {LocalAdminElevation.MinimumDuration.TotalMinutes:0} minutes "
        + $"and {LocalAdminElevation.MaximumDuration.TotalHours:0} hours.",
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// Deliberately 404, matching the rest of the platform: an administrator who
    /// cannot reach a device should not learn that it exists.
    /// </summary>
    private static IResult OutOfScope() => Results.NotFound();
}

/// <param name="TargetSid">
/// The account's Windows SID. The identity a decision is recorded against, because
/// a username can be renamed and a rename must not retarget a live elevation.
/// </param>
/// <param name="DurationMinutes">
/// Omit to leave the request pending. Supplied when the requesting administrator
/// is also approving it, which is the current arrangement.
/// </param>
public sealed record RequestElevationRequest(
    [property: Required, StringLength(184, MinimumLength = 3)] string? TargetSid,
    [property: Required, StringLength(1000, MinimumLength = 3)] string? Justification,
    [property: Range(15, 480)] int? DurationMinutes = null);

public sealed record ApproveElevationRequest([property: Range(15, 480)] int DurationMinutes);

public sealed record RevokeElevationRequest([property: StringLength(1000)] string? Note);
