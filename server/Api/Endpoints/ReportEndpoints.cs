using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Reporting;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>Consolidated fleet report (device.view).</summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/reports/summary", SummaryAsync)
            .WithName("GetFleetReport")
            .RequirePermission(Permissions.Device.View);
        return endpoints;
    }

    private static async Task<IResult> SummaryAsync(
        ReportReadService reportReadService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        return Results.Ok(await reportReadService.GetSummaryAsync(
            actor.OrganizationId, scopedDeviceIds, cancellationToken));
    }
}
