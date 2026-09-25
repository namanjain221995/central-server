using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Chrome;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Chrome Management, read side: what the fleet reported about Chrome
/// installations, profiles and extensions, by group, by device and by profile.
/// </summary>
/// <remarks>
/// <para>
/// Read-only in this phase. Every route is behind <c>chrome.view</c>, and there is
/// deliberately no route here that changes anything on an endpoint;
/// <c>chrome.manage</c> is in the catalogue for the phases that will, and gates
/// nothing yet.
/// </para>
/// <para>
/// The group route lists a group's members by the Groups page's own predicate, so
/// "All Devices" here is the built-in group's devices -- the ones in no custom
/// group -- and never the whole fleet. A device is listed under exactly one group.
/// </para>
/// <para>
/// Anything out of the caller's scope answers 404, matching the device and group
/// endpoints: an administrator who cannot reach a device must not learn that it
/// exists, and a profile id under the wrong device is answered exactly as one
/// that does not exist.
/// </para>
/// </remarks>
public static class ChromeEndpoints
{
    public static IEndpointRouteBuilder MapChromeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var chrome = endpoints.MapGroup("/admin/v1/chrome");

        chrome.MapGet("/overview", OverviewAsync)
            .WithName("GetChromeOverview")
            .RequirePermission(Permissions.Chrome.View);

        chrome.MapGet("/groups/{groupId:guid}/devices", ListGroupDevicesAsync)
            .WithName("ListChromeGroupDevices")
            .RequirePermission(Permissions.Chrome.View);

        // Under the device routes rather than /chrome, as drivers and BitLocker
        // are: the device is what the scope check is about.
        endpoints.MapGet("/admin/v1/devices/{deviceId:guid}/chrome", GetDeviceAsync)
            .WithName("GetDeviceChrome")
            .RequirePermission(Permissions.Chrome.View);

        endpoints.MapGet(
                "/admin/v1/devices/{deviceId:guid}/chrome/profiles/{profileId:guid}/extensions",
                ListProfileExtensionsAsync)
            .WithName("ListChromeProfileExtensions")
            .RequirePermission(Permissions.Chrome.View);

        return endpoints;
    }

    private static async Task<IResult> OverviewAsync(
        ChromeReadService chrome, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        return Results.Ok(await chrome.GetOverviewAsync(actor.OrganizationId, actor.UserId, cancellationToken));
    }

    private static async Task<IResult> ListGroupDevicesAsync(
        Guid groupId,
        ChromeReadService chrome,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        // Matches hostname or display name, case-insensitively, as the device
        // list's search does.
        string? q = null)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await chrome.ListGroupDevicesAsync(actor.OrganizationId, actor.UserId, groupId, q, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GetDeviceAsync(
        Guid deviceId, ChromeReadService chrome, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await chrome.GetDeviceAsync(actor.OrganizationId, actor.UserId, deviceId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ListProfileExtensionsAsync(
        Guid deviceId, Guid profileId, ChromeReadService chrome, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await chrome.ListProfileExtensionsAsync(
            actor.OrganizationId, actor.UserId, deviceId, profileId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
}
