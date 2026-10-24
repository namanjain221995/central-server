using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Chrome;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tasks;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Chrome Management, profile lifecycle write side: remove one Chrome profile
/// from its device, or one account's Chrome profiles from the fleet.
/// </summary>
/// <remarks>
/// <para>
/// The single route is addressed by the profile row's id -- the one the device
/// Chrome view hands out on every profile row -- not by device plus key, so the
/// console acts on exactly the row it is showing. Its body is optional and carries
/// only the one thing the stored row cannot: whether to restart the device
/// afterwards. Everything the agent needs is still derived server-side from the
/// inventory row; a body describing the profile would only be a second,
/// falsifiable description of the same thing. No body, an empty body and
/// <c>{"restartDevice": false}</c> all mean what the route has always meant.
/// </para>
/// <para>
/// The fleet route is addressed by the signed-in account: every matching profile
/// row on every resolved device (all devices, a group, or a selection -- the bulk
/// local-account target shapes) becomes its own removal task, with the tallies
/// saying what was queued, what had no match, what matched only the never-removable
/// Default profile, and which agents are too old.
/// </para>
/// <para>
/// Both ride on <c>chrome.manage</c> like the extension rules, and the removal is
/// high-risk in the task catalogue: deleting a profile irreversibly deletes a
/// person's bookmarks, history and sign-in state. Asking for a restart demands, on
/// top, exactly the permission the restart action itself requires
/// (<c>device.restart</c>) -- refused 403 up front, before anything is queued --
/// and what is then queued per device is exactly the restart route's task, payload
/// and notice, with its own audit entry.
/// </para>
/// <para>
/// The single route answers 202, not 200: the server has accepted and queued the
/// work, and what the endpoint does with it -- removed at once, or scheduled while
/// Chrome is open for the profile's user -- is the task result's to tell. Anything
/// out of the caller's scope answers 404, matching the other device routes; the
/// scope check happens in the service, which is the first place the profile's
/// device is known. An agent below the executor's floor is a 409 naming the
/// version to update to, exactly as the extension-rule routes answer. The fleet
/// route answers 200 with tallies, like the bulk local-account routes, and 404
/// when the target resolves to no device the caller may act on.
/// </para>
/// </remarks>
public static class ChromeProfileEndpoints
{
    /// <summary>Refuses an implausible explicit selection before any query runs.</summary>
    private const int MaxTargetIds = 2000;

    /// <summary>RFC 5321's ceiling on an address; anything longer is not an e-mail.</summary>
    private const int MaxEmailLength = 320;

    public static IEndpointRouteBuilder MapChromeProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/admin/v1/chrome/profiles/{profileId:guid}/actions/remove", RemoveAsync)
            .WithName("RemoveChromeProfile")
            .RequirePermission(Permissions.Chrome.Manage);

        endpoints.MapPost("/admin/v1/chrome/accounts/actions/remove", RemoveAccountAsync)
            .WithName("RemoveChromeAccountProfiles")
            .RequirePermission(Permissions.Chrome.Manage);

        return endpoints;
    }

    // ---------------------------------------------------------- single profile

    /// <summary>
    /// The optional body of a single-profile removal.
    /// </summary>
    /// <param name="RestartDevice">
    /// True also queues the ordinary restart task for the profile's device, after
    /// the removal -- the same task, grace period and on-screen notice the restart
    /// action queues -- so a removal the agent schedules while Chrome is open
    /// completes at the boot the restart causes. Requires <c>device.restart</c>.
    /// </param>
    public sealed record RemoveChromeProfileRequest(bool RestartDevice = false);

    private static async Task<IResult> RemoveAsync(
        Guid profileId,
        // Bound by the framework with EmptyBodyBehavior.Allow, exactly as the
        // restart route binds its optional body: absent binds null, present is
        // parsed whatever the framing, malformed is a 400 before this runs. The
        // dashboard has always posted no body, and that still means "no restart".
        [Microsoft.AspNetCore.Mvc.FromBody(
            EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)]
        RemoveChromeProfileRequest? request,
        ChromeProfileRemovalService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var restartDevice = request?.RestartDevice ?? false;

        // The restart half of the request demands the restart action's own
        // permission, checked before anything is queued: chrome.manage decides
        // what Chrome holds, never whether a machine goes down.
        if (restartDevice && !HasPermission(httpContext, Permissions.Device.Restart))
        {
            return RestartPermissionProblem();
        }

        var actor = AdminActor.Required(httpContext.User);
        var result = await service.QueueRemovalAsync(
            actor.OrganizationId, profileId, actor.UserId, actor.Email, restartDevice, cancellationToken);

        return result.Outcome switch
        {
            ChromeProfileRemovalOutcome.Queued =>
                // restartTaskId is null when no restart was asked for -- and when
                // one was but the device already had a restart queued or
                // delivered, which already does everything a second one would.
                Results.Accepted(value: new
                {
                    taskId = result.Task!.Id,
                    restartTaskId = result.RestartTask?.Id,
                }),

            // Missing profile, retired device, out of scope, other organization:
            // all reported absent, like every other device route.
            ChromeProfileRemovalOutcome.NotFound => Results.NotFound(),

            // A real, actionable state rather than a not-found: the profile is
            // right there, but its device's agent cannot run the removal yet.
            ChromeProfileRemovalOutcome.AgentTooOld =>
                Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),

            _ => Results.Problem(
                "Unhandled chrome-profile-removal outcome.",
                statusCode: StatusCodes.Status500InternalServerError),
        };
    }

    // ------------------------------------------------------------ fleet action

    /// <summary>Which devices a fleet-wide account removal aims at.</summary>
    /// <param name="Kind">"all", "group" or "devices".</param>
    public sealed record BulkRemovalTarget(
        string? Kind,
        Guid? GroupId = null,
        IReadOnlyList<Guid>? DeviceIds = null);

    public sealed record RemoveChromeAccountRequest(
        string? AccountEmail,
        BulkRemovalTarget? Target,
        bool RestartDevices = false);

    private static async Task<IResult> RemoveAccountAsync(
        RemoveChromeAccountRequest request,
        BulkChromeProfileRemovalService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var accountEmail = request.AccountEmail?.Trim();
        if (!LooksLikeEmail(accountEmail))
        {
            return Results.Problem(
                $"accountEmail is required and must be an e-mail address of at most {MaxEmailLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (ValidateTarget(request.Target) is { } targetProblem)
        {
            return targetProblem;
        }

        // Before resolution, like every up-front guard: an operator without the
        // restart authority learns that first, not after a fleet was half-planned.
        if (request.RestartDevices && !HasPermission(httpContext, Permissions.Device.Restart))
        {
            return RestartPermissionProblem();
        }

        var target = request.Target!;
        var allDevices = target.Kind == "all";

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        BulkChromeProfileRemovalResult result;
        try
        {
            result = await service.RemoveAccountProfilesAsync(
                actor.OrganizationId, accountEmail!, request.RestartDevices,
                allDevices, target.GroupId, target.DeviceIds, scopedDeviceIds,
                actor.UserId, actor.Email, cancellationToken);
        }
        catch (DbUpdateException ex) when (DeviceTaskService.IsDuplicateActiveTask(ex))
        {
            // The service pre-checks in-flight restarts, so this is the losing
            // side of a race with a concurrent restart request for one of the
            // targets. The whole batch rolled back -- nothing was queued -- so a
            // retry is clean and the pre-check will then see the winner.
            return Results.Problem(
                "A restart was queued for a targeted device by another request at the same moment; "
                + "nothing was queued here. Retry the action.",
                statusCode: StatusCodes.Status409Conflict);
        }

        return result.Outcome switch
        {
            BulkChromeProfileRemovalOutcome.Completed => Results.Ok(new
            {
                targetedDevices = result.TargetedDevices,
                queuedRemovals = result.QueuedRemovals,
                queuedRestarts = result.QueuedRestarts,
                skippedNoProfile = result.SkippedNoProfile,
                skippedDefault = result.SkippedDefault,
                rejectedAgentTooOld = result.RejectedAgentTooOld,
                offline = result.Offline,
            }),

            // Unknown or foreign group, out-of-scope, or a selection of ghosts --
            // one 404 for every shade of "resolved to no devices you may act on",
            // matching the bulk local-account routes.
            _ => Results.NotFound(),
        };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Exactly one target shape, named by kind -- the bulk local-account rule in a
    /// kinded coat: "all" stands alone, "group" names a group, "devices" lists at
    /// most <see cref="MaxTargetIds"/> ids. A kind contradicted by the other fields
    /// is refused rather than half-read.
    /// </summary>
    private static IResult? ValidateTarget(BulkRemovalTarget? target)
    {
        var hasGroup = target?.GroupId is { } g && g != Guid.Empty;
        var hasDevices = target?.DeviceIds is { Count: > 0 };

        var valid = target?.Kind switch
        {
            "all" => !hasGroup && !hasDevices,
            "group" => hasGroup && !hasDevices,
            "devices" => hasDevices && !hasGroup,
            _ => false,
        };

        if (!valid)
        {
            return Results.Problem(
                "target.kind must be 'all', 'group' (with target.groupId) or 'devices' (with target.deviceIds), "
                + "and nothing beyond what the kind names.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (target!.DeviceIds is { Count: > MaxTargetIds })
        {
            return Results.Problem(
                $"At most {MaxTargetIds} devices may be targeted at once.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }

    /// <summary>
    /// The shape check, not a deliverability check: one '@' with something on both
    /// sides, no whitespace, and within the RFC's 320-character ceiling. The match
    /// against inventory is by equality, so a stricter parse would only refuse
    /// addresses Chrome itself recorded.
    /// </summary>
    private static bool LooksLikeEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxEmailLength)
        {
            return false;
        }

        var at = value.IndexOf('@');
        return at > 0
            && at == value.LastIndexOf('@')
            && at < value.Length - 1
            && !value.Any(char.IsWhiteSpace);
    }

    /// <summary>Whether the caller holds a permission beyond the one the route policy already enforced.</summary>
    private static bool HasPermission(HttpContext httpContext, string permissionKey) =>
        httpContext.User.HasClaim(AdminAuthenticationHandler.PermissionClaimType, permissionKey);

    private static IResult RestartPermissionProblem() =>
        Results.Problem(
            title: "Restarting the device additionally requires the restart permission ('"
                + Permissions.Device.Restart + "') -- the same one the restart action itself requires.",
            statusCode: StatusCodes.Status403Forbidden);
}
