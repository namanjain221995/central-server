using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.ScreenLocks;
using EndpointPlatform.Infrastructure.ScreenLocks;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Screen locks: put chosen devices or a group behind a persistent full-screen
/// lock, lift the lock again, read how past locks stand, and manage the IT unlock
/// code that recovers a locked machine on the device itself.
/// </summary>
/// <remarks>
/// Every route requires <see cref="Permissions.Device.ScreenLock"/> -- a distinct,
/// higher authority than the session lock's <see cref="Permissions.Device.Lock"/>:
/// imposing a persistent full-screen lock, and holding the code that recovers it,
/// belongs to administrators, not first-line support. Target ids are shape-checked
/// here and authorized server-side in the service, exactly as announcements and
/// deployments are.
/// </remarks>
public static class ScreenLockEndpoints
{
    /// <summary>Refuses an implausible selection before any query runs.</summary>
    private const int MaxTargetIds = 2000;

    public static IEndpointRouteBuilder MapScreenLockEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/screen-locks")
            .RequirePermission(Permissions.Device.ScreenLock);

        group.MapPost("/", EngageAsync).WithName("EngageScreenLock");
        group.MapPost("/release", ReleaseAsync).WithName("ReleaseScreenLock");
        group.MapGet("/", ListAsync).WithName("ListScreenLocks");

        // The IT unlock code lives under settings but is the same authority: you may
        // set the code that recovers a lock exactly when you may impose the lock.
        endpoints.MapPut("/admin/v1/settings/screen-unlock-code", SetUnlockCodeAsync)
            .WithName("SetScreenUnlockCode")
            .RequirePermission(Permissions.Device.ScreenLock);
        endpoints.MapGet("/admin/v1/settings/screen-unlock-code", GetUnlockCodeStatusAsync)
            .WithName("GetScreenUnlockCodeStatus")
            .RequirePermission(Permissions.Device.ScreenLock);

        return endpoints;
    }

    public sealed record EngageScreenLockRequest(
        string? Message, string? ContactLine, Guid? GroupId, IReadOnlyList<Guid>? DeviceIds);

    private static async Task<IResult> EngageAsync(
        EngageScreenLockRequest request,
        ScreenLockService screenLockService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (ValidateEngage(request) is { } problem)
        {
            return problem;
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await screenLockService.EngageAsync(
            actor.OrganizationId, request.Message!.Trim(), request.ContactLine?.Trim(),
            request.GroupId, request.DeviceIds, scopedDeviceIds,
            actor.UserId, actor.Email, cancellationToken);

        return result.Outcome switch
        {
            ScreenLockEngageOutcome.Engaged => Results.Ok(new
            {
                result.LockId,
                result.Targeted,
                result.Queued,
                result.Skipped,
                result.Offline,
            }),

            // A precondition on the organization's state, not a bad request shape:
            // there is nothing wrong with what was sent, only with locking before a
            // recovery code exists.
            ScreenLockEngageOutcome.NoUnlockCode => Results.Problem(
                "Set the IT unlock code before locking a device.",
                statusCode: StatusCodes.Status409Conflict),

            // Null covers an unknown group, another organization's, and device ids
            // that all resolve to nothing -- one 404 for every shade of "not yours".
            _ => Results.NotFound(),
        };
    }

    public sealed record ReleaseScreenLockRequest(Guid? LockId, Guid? GroupId, IReadOnlyList<Guid>? DeviceIds);

    private static async Task<IResult> ReleaseAsync(
        ReleaseScreenLockRequest request,
        ScreenLockService screenLockService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var hasLock = request.LockId is { } lockId && lockId != Guid.Empty;
        var hasGroup = request.GroupId is { } groupId && groupId != Guid.Empty;
        var hasDevices = request.DeviceIds is { Count: > 0 };

        if ((hasLock ? 1 : 0) + (hasGroup ? 1 : 0) + (hasDevices ? 1 : 0) != 1)
        {
            return Results.Problem(
                "Release names exactly one of lockId, groupId or deviceIds.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.DeviceIds is { Count: > MaxTargetIds })
        {
            return Results.Problem(
                $"At most {MaxTargetIds} devices may be released at once.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var released = await screenLockService.ReleaseAsync(
            actor.OrganizationId, request.LockId, request.GroupId, request.DeviceIds, scopedDeviceIds,
            actor.UserId, actor.Email, cancellationToken);

        return Results.Ok(new { released });
    }

    private static async Task<IResult> ListAsync(
        ScreenLockService screenLockService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        int limit = ScreenLockService.MaxListLimit)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        return Results.Ok(await screenLockService.ListAsync(
            actor.OrganizationId, scopedDeviceIds, limit, cancellationToken));
    }

    public sealed record SetUnlockCodeRequest(string? Code);

    /// <remarks>
    /// The code is organization-wide: every lock engaged after it changes, in any
    /// group, captures it. Changing it therefore requires all-device scope, refused
    /// with the same 403 a scoped administrator gets for creating a device group --
    /// otherwise an administrator scoped to one group would set the recovery code
    /// for machines they have no authority over. Reading whether one is set stays
    /// open to every <c>device.screenlock</c> holder, since locking needs it.
    /// </remarks>
    private static async Task<IResult> SetUnlockCodeAsync(
        SetUnlockCodeRequest request,
        ScreenUnlockCodeService unlockCodeService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        if (await scope.ScopedGroupIdsOrNullAsync(actor.UserId, actor.OrganizationId, cancellationToken) is not null)
        {
            return Results.Problem(
                "Changing the IT unlock code requires all-device scope.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Shape only; the service re-checks the length and is the one place that
        // ever sees the plaintext, which it immediately turns into a verifier.
        if (string.IsNullOrEmpty(request.Code) || request.Code.Length < ScreenUnlockCodeService.MinCodeLength)
        {
            return Results.Problem(
                $"The unlock code is required and must be at least {ScreenUnlockCodeService.MinCodeLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var set = await unlockCodeService.SetAsync(
            actor.OrganizationId, request.Code, actor.UserId, actor.Email, cancellationToken);

        return set
            ? Results.NoContent()
            : Results.Problem(
                $"The unlock code must be at least {ScreenUnlockCodeService.MinCodeLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> GetUnlockCodeStatusAsync(
        ScreenUnlockCodeService unlockCodeService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var status = await unlockCodeService.GetStatusAsync(actor.OrganizationId, cancellationToken);

        // isSet and when it changed -- never the code.
        return Results.Ok(new { status.IsSet, status.UpdatedAt });
    }

    /// <summary>Shape checks only; every id is authorized server-side afterwards.</summary>
    private static IResult? ValidateEngage(EngageScreenLockRequest request)
    {
        var message = request.Message?.Trim();
        if (string.IsNullOrEmpty(message) || message.Length > ScreenLock.MaxMessageLength)
        {
            return Results.Problem(
                $"message is required and is at most {ScreenLock.MaxMessageLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var contactLine = request.ContactLine?.Trim();
        if (contactLine is { Length: > ScreenLock.MaxContactLineLength })
        {
            return Results.Problem(
                $"contactLine is at most {ScreenLock.MaxContactLineLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The agent's lock executor refuses these outright, so accepting one here
        // would queue a lock fleet-wide that then fails on every device.
        if (!ScreenLock.IsPlainText(message)
            || (!string.IsNullOrEmpty(contactLine) && !ScreenLock.IsPlainText(contactLine)))
        {
            return Results.Problem(
                "message and contactLine are plain text: control characters other than line breaks and tabs are not allowed.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var hasGroup = request.GroupId is { } groupId && groupId != Guid.Empty;
        var hasDevices = request.DeviceIds is { Count: > 0 };

        if (hasGroup == hasDevices)
        {
            return Results.Problem(
                "Target exactly one of groupId or deviceIds.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.DeviceIds is { Count: > MaxTargetIds })
        {
            return Results.Problem(
                $"At most {MaxTargetIds} devices may be targeted at once.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }
}
