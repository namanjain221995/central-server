using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Restarts;
using EndpointPlatform.Infrastructure.Restarts;
using Microsoft.AspNetCore.Mvc;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Restart Management: department-wide restarts at a chosen moment.
/// </summary>
/// <remarks>
/// Every route requires <see cref="Permissions.Device.Restart"/>, the permission
/// a restart has always needed: scheduling one for a department is restarting
/// its devices, only later. A group outside the caller's scope is not found,
/// the same answer every group route gives.
/// </remarks>
public static class RestartScheduleEndpoints
{
    private const long MaxSmallBodyBytes = 8 * 1024;

    /// <summary>Room for the device-id list a cancellation may carry: 500 ids is about 20 KB of JSON.</summary>
    private const long MaxCancelBodyBytes = 64 * 1024;

    public const int MaxDevicesPerCancel = 500;

    public static IEndpointRouteBuilder MapRestartScheduleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var schedules = endpoints.MapGroup("/admin/v1/restart-schedules")
            .RequirePermission(Permissions.Device.Restart);

        schedules.MapGet("/groups/{groupId:guid}", OverviewAsync).WithName("GetGroupRestartSchedules");
        schedules.MapGet("/{scheduleId:guid}", GetAsync).WithName("GetRestartSchedule");
        schedules.MapPost("/", CreateAsync).WithName("CreateRestartSchedule")
            .WithMetadata(new RequestSizeLimitAttribute(MaxSmallBodyBytes));
        schedules.MapPost("/{scheduleId:guid}/cancel", CancelAsync).WithName("CancelRestartSchedule")
            .WithMetadata(new RequestSizeLimitAttribute(MaxCancelBodyBytes));

        return endpoints;
    }

    public sealed record CreateRestartScheduleRequest(Guid? GroupId, int? DelaySeconds);

    /// <param name="DeviceIds">Null or absent: the whole department. Otherwise only these devices.</param>
    public sealed record CancelRestartScheduleRequest(IReadOnlyList<Guid>? DeviceIds);

    private static async Task<IResult> OverviewAsync(
        Guid groupId, HttpContext httpContext, RestartScheduleService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var overview = await service.GetOverviewAsync(actor.OrganizationId, actor.UserId, groupId, cancellationToken);
        return overview is null ? Results.NotFound() : Results.Ok(overview);
    }

    private static async Task<IResult> GetAsync(
        Guid scheduleId, HttpContext httpContext, RestartScheduleService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var view = await service.GetAsync(actor.OrganizationId, actor.UserId, scheduleId, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateRestartScheduleRequest? request,
        HttpContext httpContext, RestartScheduleService service, CancellationToken cancellationToken)
    {
        if (request?.GroupId is not { } groupId || groupId == Guid.Empty)
        {
            return Results.Problem("groupId is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.DelaySeconds is not { } delay || !RestartScheduleDelay.IsAccepted(delay))
        {
            return Results.Problem(
                $"delaySeconds must be between {RestartScheduleDelay.MinimumSeconds} and {RestartScheduleDelay.MaximumSeconds} seconds.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var result = await service.CreateAsync(actor.OrganizationId, actor.UserId, actor.Email, groupId, delay, cancellationToken);

        return result.Status switch
        {
            RestartScheduleCreateStatus.Created => Results.Created($"/admin/v1/restart-schedules/{result.Schedule!.Id}", result.Schedule),
            RestartScheduleCreateStatus.GroupNotFound => Results.NotFound(),
            RestartScheduleCreateStatus.AlreadyScheduled => Results.Problem(
                "This department already has a restart scheduled. Cancel it before scheduling another.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["scheduleId"] = result.Schedule?.Id }),
            _ => Results.Problem("The request was not valid.", statusCode: StatusCodes.Status400BadRequest),
        };
    }

    private static async Task<IResult> CancelAsync(
        Guid scheduleId, [FromBody] CancelRestartScheduleRequest? request,
        HttpContext httpContext, RestartScheduleService service, CancellationToken cancellationToken)
    {
        var deviceIds = request?.DeviceIds;
        if (deviceIds is { Count: 0 })
        {
            return Results.Problem("deviceIds names at least one device, or is omitted to cancel for the whole department.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (deviceIds is { Count: > MaxDevicesPerCancel })
        {
            return Results.Problem($"At most {MaxDevicesPerCancel} devices per request.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var result = await service.CancelAsync(
            actor.OrganizationId, actor.UserId, actor.Email, scheduleId, deviceIds, cancellationToken);

        return result.Status switch
        {
            RestartScheduleCancelStatus.Ok => Results.Ok(new { schedule = result.Schedule, devices = result.Devices }),
            RestartScheduleCancelStatus.NotFound => Results.NotFound(),
            RestartScheduleCancelStatus.NotCancellable => Results.Problem(
                $"This schedule is {result.Schedule?.Status.ToLowerInvariant()}; there is nothing left to cancel.",
                statusCode: StatusCodes.Status409Conflict),
            RestartScheduleCancelStatus.Conflict => Results.Problem(
                "The schedule changed while this was in progress. Try again.",
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Problem("The request was not valid.", statusCode: StatusCodes.Status400BadRequest),
        };
    }
}
