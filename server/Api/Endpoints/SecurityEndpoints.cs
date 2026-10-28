using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>Fleet security posture overview (device.view).</summary>
public static class SecurityEndpoints
{
    public static IEndpointRouteBuilder MapSecurityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/security/overview", OverviewAsync)
            .WithName("GetSecurityOverview")
            .RequirePermission(Permissions.Device.View);

        return endpoints;
    }

    private static async Task<IResult> OverviewAsync(
        SecurityReadService securityReadService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // Every row names a device, so the overview is narrowed to the caller's
        // scope exactly as the device list is.
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        return Results.Ok(await securityReadService.GetOverviewAsync(
            actor.OrganizationId, scopedDeviceIds, cancellationToken));
    }
}
