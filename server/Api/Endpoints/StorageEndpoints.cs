using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Fleet-wide storage overview: how full each managed device's fixed volumes are.
/// </summary>
/// <remarks>
/// Read-only over hardware inventory the agent already uploads, grouped across the
/// devices the caller may already see one by one. It therefore rides
/// <see cref="Permissions.Device.View"/> — the same permission that shows a single
/// device's disks — because it exposes no fact a per-device view does not; it only
/// gathers them so a low-disk machine stands out without opening each device in turn.
/// </remarks>
public static class StorageEndpoints
{
    public static IEndpointRouteBuilder MapStorageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/storage/fleet-summary", FleetSummaryAsync)
            .WithName("GetStorageFleetSummary")
            .RequirePermission(Permissions.Device.View);

        return endpoints;
    }

    private static async Task<IResult> FleetSummaryAsync(
        FleetStorageReadService readService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scoped = await scope.ScopedDeviceIdsOrNullAsync(actor.UserId, actor.OrganizationId, cancellationToken);
        return Results.Ok(await readService.GetSummaryAsync(actor.OrganizationId, scoped, cancellationToken));
    }
}
