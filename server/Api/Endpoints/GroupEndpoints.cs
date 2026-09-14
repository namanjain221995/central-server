using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Tasks;
using EndpointPlatform.Infrastructure.Groups;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Device groups: the partitions a device lives in, and the actions a group can
/// carry out on its online devices.
/// </summary>
/// <remarks>
/// <para>
/// No new permissions. Reading groups is <c>group.view</c> and changing them is
/// <c>group.manage</c>, as before; a group action takes exactly the permission
/// the same action takes on one device, so a group is never a way round a
/// device permission.
/// </para>
/// <para>
/// Each action has its own route, as the device actions do, so its permission is
/// declared on it rather than decided in code from a route value. The shape is
/// still <c>POST /admin/v1/groups/{groupId}/actions/{action}</c>.
/// </para>
/// <para>
/// Bodies are bound by the framework with an explicit size cap. The device
/// restart route learned that a hand-rolled body reader keyed on Content-Length
/// discards every chunked body unread.
/// </para>
/// </remarks>
public static class GroupEndpoints
{
    /// <summary>
    /// Membership bodies carry at most <see cref="DeviceGroupService.MaxDevicesPerRequest"/>
    /// GUIDs -- about 20 KB of JSON. Anything far beyond that is refused before it is read.
    /// </summary>
    private const long MaxMembershipBodyBytes = 64 * 1024;

    /// <summary>Action and rename bodies are a handful of fields.</summary>
    private const long MaxSmallBodyBytes = 8 * 1024;

    public static IEndpointRouteBuilder MapGroupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/groups");

        group.MapGet("/", ListAsync).WithName("ListGroups").RequirePermission(Permissions.Group.View);
        group.MapGet("/{groupId:guid}", GetAsync).WithName("GetGroup").RequirePermission(Permissions.Group.View);

        group.MapPost("/", CreateAsync).WithName("CreateGroup")
            .RequirePermission(Permissions.Group.Manage)
            .WithMetadata(new RequestSizeLimitAttribute(MaxMembershipBodyBytes));

        group.MapPatch("/{groupId:guid}", RenameAsync).WithName("RenameGroup")
            .RequirePermission(Permissions.Group.Manage)
            .WithMetadata(new RequestSizeLimitAttribute(MaxSmallBodyBytes));

        group.MapDelete("/{groupId:guid}", DeleteAsync).WithName("DeleteGroup")
            .RequirePermission(Permissions.Group.Manage);

        group.MapGet("/{groupId:guid}/candidates", CandidatesAsync).WithName("GroupCandidates")
            .RequirePermission(Permissions.Group.Manage);

        group.MapPost("/{groupId:guid}/devices", AddDevicesAsync).WithName("AddGroupDevices")
            .RequirePermission(Permissions.Group.Manage)
            .WithMetadata(new RequestSizeLimitAttribute(MaxMembershipBodyBytes));

        group.MapPost("/{groupId:guid}/devices/remove", RemoveDevicesAsync).WithName("RemoveGroupDevices")
            .RequirePermission(Permissions.Group.Manage)
            .WithMetadata(new RequestSizeLimitAttribute(MaxMembershipBodyBytes));

        // Actions: each the permission its single-device equivalent requires.
        group.MapPost("/{groupId:guid}/actions/restart", RestartAsync).WithName("RestartGroup")
            .RequirePermission(Permissions.Device.Restart)
            .WithMetadata(new RequestSizeLimitAttribute(MaxSmallBodyBytes));

        group.MapPost("/{groupId:guid}/actions/shutdown",
                (Guid groupId, HttpContext ctx, DeviceGroupActionService svc, CancellationToken ct)
                    => RunAsync(groupId, GroupAction.Shutdown, 0, ctx, svc, ct))
            .WithName("ShutdownGroup").RequirePermission(Permissions.Device.Shutdown);

        group.MapPost("/{groupId:guid}/actions/lock",
                (Guid groupId, HttpContext ctx, DeviceGroupActionService svc, CancellationToken ct)
                    => RunAsync(groupId, GroupAction.Lock, 0, ctx, svc, ct))
            .WithName("LockGroup").RequirePermission(Permissions.Device.Lock);

        group.MapPost("/{groupId:guid}/actions/signout",
                (Guid groupId, HttpContext ctx, DeviceGroupActionService svc, CancellationToken ct)
                    => RunAsync(groupId, GroupAction.SignOut, 0, ctx, svc, ct))
            .WithName("SignOutGroup").RequirePermission(Permissions.Device.SignOutUser);

        // Force Stop terminates processes, so it takes task.execute, as the
        // fleet-wide Force Stop does.
        group.MapPost("/{groupId:guid}/actions/force-stop", ForceStopAsync).WithName("ForceStopGroup")
            .RequirePermission(Permissions.Task.Execute)
            .WithMetadata(new RequestSizeLimitAttribute(MaxSmallBodyBytes));

        // Cancelling a restart is part of restarting.
        group.MapPost("/{groupId:guid}/actions/cancel-restart", CancelRestartAsync).WithName("CancelGroupRestart")
            .RequirePermission(Permissions.Device.Restart);

        return endpoints;
    }

    public sealed record CreateGroupRequest(string? Name, string? Description, IReadOnlyList<Guid>? DeviceIds);

    public sealed record RenameGroupRequest(string? Name, string? Description);

    public sealed record GroupDevicesRequest(IReadOnlyList<Guid>? DeviceIds);

    public sealed record GroupRestartRequest(int DelaySeconds);

    public sealed record GroupForceStopRequest(string? ApplicationName, string? Publisher);

    // ------------------------------------------------------------------ reads

    private static async Task<IResult> ListAsync(
        HttpContext httpContext, DeviceGroupService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        return Results.Ok(await service.ListAsync(actor.OrganizationId, actor.UserId, cancellationToken));
    }

    private static async Task<IResult> GetAsync(
        Guid groupId, HttpContext httpContext, DeviceGroupService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var detail = await service.GetAsync(actor.OrganizationId, actor.UserId, groupId, cancellationToken);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    private static async Task<IResult> CandidatesAsync(
        Guid groupId, HttpContext httpContext, DeviceGroupService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var candidates = await service.CandidatesAsync(actor.OrganizationId, actor.UserId, groupId, cancellationToken);
        return candidates is null ? Results.NotFound() : Results.Ok(candidates);
    }

    // ----------------------------------------------------------------- writes

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateGroupRequest request, HttpContext httpContext, DeviceGroupService service,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await service.CreateAsync(
            actor.OrganizationId, actor.UserId, actor.Email,
            request.Name ?? string.Empty, request.Description, request.DeviceIds ?? [], cancellationToken);

        return result.Status switch
        {
            GroupChangeStatus.Ok => Results.Created($"/admin/v1/groups/{result.GroupId}",
                new { id = result.GroupId, devices = result.Devices }),
            GroupChangeStatus.DuplicateName => Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),
            GroupChangeStatus.Forbidden => Results.Problem(result.Error, statusCode: StatusCodes.Status403Forbidden),
            _ => Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest),
        };
    }

    private static async Task<IResult> RenameAsync(
        Guid groupId, [FromBody] RenameGroupRequest request, HttpContext httpContext, DeviceGroupService service,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var status = await service.RenameAsync(
            actor.OrganizationId, actor.UserId, actor.Email, groupId,
            request.Name ?? string.Empty, request.Description, cancellationToken);

        return ChangeResult(status, Results.NoContent());
    }

    private static async Task<IResult> DeleteAsync(
        Guid groupId, HttpContext httpContext, DeviceGroupService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await service.DeleteAsync(actor.OrganizationId, actor.UserId, actor.Email, groupId, cancellationToken);
        return ChangeResult(result.Status, Results.Ok(new { devicesMoved = result.DevicesMoved }));
    }

    private static async Task<IResult> AddDevicesAsync(
        Guid groupId, [FromBody] GroupDevicesRequest request, HttpContext httpContext, DeviceGroupService service,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await service.AddDevicesAsync(
            actor.OrganizationId, actor.UserId, actor.Email, groupId, request.DeviceIds ?? [], cancellationToken);
        return ChangeResult(result.Status, Results.Ok(new { devices = result.Devices }));
    }

    private static async Task<IResult> RemoveDevicesAsync(
        Guid groupId, [FromBody] GroupDevicesRequest request, HttpContext httpContext, DeviceGroupService service,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await service.RemoveDevicesAsync(
            actor.OrganizationId, actor.UserId, actor.Email, groupId, request.DeviceIds ?? [], cancellationToken);
        return ChangeResult(result.Status, Results.Ok(new { devices = result.Devices }));
    }

    // ---------------------------------------------------------------- actions

    /// <summary>
    /// Restarts the group's online devices with one grace period, under exactly
    /// the single-device contract: 0 is "now" (a 30-second warning), 30-3600 is
    /// that many seconds, anything else is refused and nothing is queued.
    /// </summary>
    private static Task<IResult> RestartAsync(
        Guid groupId,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] GroupRestartRequest? request,
        HttpContext httpContext, DeviceGroupActionService service, CancellationToken cancellationToken)
    {
        var grace = RestartGrace.FromDelay(request?.DelaySeconds ?? 0);
        if (grace is null)
        {
            return Task.FromResult(Results.Problem(
                $"delaySeconds must be 0 (restart now, after a {RestartGrace.ImmediateSeconds}-second warning) " +
                $"or between {RestartGrace.MinimumDelaySeconds} and {RestartGrace.MaximumDelaySeconds} seconds.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        return RunAsync(groupId, GroupAction.Restart, grace.Value, httpContext, service, cancellationToken);
    }

    private static async Task<IResult> RunAsync(
        Guid groupId, GroupAction action, int graceSeconds, HttpContext httpContext,
        DeviceGroupActionService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await service.RunAsync(
            actor.OrganizationId, actor.UserId, actor.Email, groupId, action, graceSeconds, cancellationToken);

        // Accepted, not Ok: tasks were queued, nothing has happened on a device yet.
        return result is null ? Results.NotFound() : Results.Accepted($"/admin/v1/groups/{groupId}", result);
    }

    private static async Task<IResult> ForceStopAsync(
        Guid groupId, [FromBody] GroupForceStopRequest request, HttpContext httpContext,
        DeviceGroupActionService service, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApplicationName) || request.ApplicationName.Length > 384)
        {
            return Results.Problem("An application name is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Publisher is { Length: > 256 })
        {
            return Results.Problem("A publisher may be at most 256 characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var result = await service.ForceStopAsync(
            actor.OrganizationId, actor.UserId, actor.Email, groupId,
            request.ApplicationName.Trim(), request.Publisher, cancellationToken);

        if (result is null)
        {
            return Results.NotFound();
        }

        // Shaped by hand, as the fleet-wide Force Stop endpoint shapes its own:
        // ForceStopOutcome has no string converter, and giving it one would change
        // that endpoint's JSON too. Offline devices are listed alongside, never as
        // having been stopped.
        return Results.Accepted($"/admin/v1/groups/{groupId}", new
        {
            result.GroupId,
            result.GroupName,
            result.ApplicationName,
            processesQueued = result.Stop.ProcessesQueued,
            devices = result.Stop.Devices
                .Select(d => new { d.DeviceId, d.Hostname, outcome = d.Outcome.ToString(), d.ProcessesQueued })
                .Concat(result.Offline.Select(d => new
                {
                    d.DeviceId, d.Hostname, outcome = d.Outcome.ToString(), ProcessesQueued = 0,
                })),
        });
    }

    private static async Task<IResult> CancelRestartAsync(
        Guid groupId, HttpContext httpContext, DeviceGroupActionService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await service.CancelPendingRestartsAsync(
            actor.OrganizationId, actor.UserId, actor.Email, groupId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static IResult ChangeResult(GroupChangeStatus status, IResult ok) => status switch
    {
        GroupChangeStatus.Ok => ok,
        GroupChangeStatus.NotFound => Results.NotFound(),
        GroupChangeStatus.BuiltIn => Results.Problem(
            $"The \"{Domain.Groups.DeviceGroup.AllDevicesName}\" group cannot be renamed, deleted or emptied.",
            statusCode: StatusCodes.Status409Conflict),
        GroupChangeStatus.DuplicateName => Results.Problem(
            "A group with that name already exists.", statusCode: StatusCodes.Status409Conflict),
        GroupChangeStatus.DestinationOutOfScope => Results.Problem(
            $"Devices leaving a group return to \"{Domain.Groups.DeviceGroup.AllDevicesName}\", which is outside your scope.",
            statusCode: StatusCodes.Status403Forbidden),
        GroupChangeStatus.Conflict => Results.Problem(
            "The group changed while this was in progress. Reload and try again.",
            statusCode: StatusCodes.Status409Conflict),
        GroupChangeStatus.Forbidden => Results.Problem(statusCode: StatusCodes.Status403Forbidden),
        _ => Results.Problem("The request was not valid.", statusCode: StatusCodes.Status400BadRequest),
    };
}
