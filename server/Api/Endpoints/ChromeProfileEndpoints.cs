using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Chrome;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Chrome Management, profile lifecycle write side: remove one Chrome profile
/// from its device.
/// </summary>
/// <remarks>
/// <para>
/// Addressed by the profile row's id -- the one the device Chrome view hands out
/// on every profile row -- not by device plus key, so the console acts on exactly
/// the row it is showing. There is no request body: everything the agent needs is
/// derived server-side from the stored inventory row, and a body would only be a
/// second, falsifiable description of the same profile.
/// </para>
/// <para>
/// Rides on <c>chrome.manage</c> like the extension rules, and is high-risk in
/// the task catalogue: deleting a profile irreversibly deletes a person's
/// bookmarks, history and sign-in state. 202, not 200: the server has accepted
/// and queued the work, and whether the endpoint actually removes the profile --
/// it refuses a stale target, the Default profile, or a running Chrome -- is the
/// task result's to tell.
/// </para>
/// <para>
/// Anything out of the caller's scope answers 404, matching the other device
/// routes; the scope check happens in the service, which is the first place the
/// profile's device is known. An agent below the executor's floor is a 409
/// naming the version to update to, exactly as the extension-rule routes answer.
/// </para>
/// </remarks>
public static class ChromeProfileEndpoints
{
    public static IEndpointRouteBuilder MapChromeProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/admin/v1/chrome/profiles/{profileId:guid}/actions/remove", RemoveAsync)
            .WithName("RemoveChromeProfile")
            .RequirePermission(Permissions.Chrome.Manage);

        return endpoints;
    }

    private static async Task<IResult> RemoveAsync(
        Guid profileId,
        ChromeProfileRemovalService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await service.QueueRemovalAsync(
            actor.OrganizationId, profileId, actor.UserId, actor.Email, cancellationToken);

        return result.Outcome switch
        {
            ChromeProfileRemovalOutcome.Queued =>
                Results.Accepted(value: new { taskId = result.Task!.Id }),

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
}
