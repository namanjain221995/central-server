using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Windows Update driver updates: see which machines have driver updates pending,
/// list one machine's, and scan or install them -- on one device or the fleet.
/// </summary>
/// <remarks>
/// <para>
/// Reading and scanning ride on <c>driver.view</c>; installing rides on
/// <c>driver.manage</c> and is high-risk, because a driver runs in the kernel. A
/// scan changes nothing on the endpoint, so it is a view-level action; an install
/// puts kernel code on the machine, so it is a manage-level one, the same split the
/// driver-package catalogue draws. Hiding an update (suppress / unsuppress) also
/// rides on <c>driver.manage</c>: it changes nothing on the machine, but it decides
/// what install-all will put there.
/// </para>
/// <para>
/// Nothing here installs anything directly. Every device action queues a typed task
/// the endpoint pulls on its next poll and verifies against Windows Update for
/// itself; the server's request is a necessary condition, never a sufficient one. A
/// device the caller may not see is reported absent, not forbidden, exactly as the
/// other device routes do.
/// </para>
/// </remarks>
public static class DriverUpdateEndpoints
{
    /// <summary>Refuses an implausible selection before any work is queued.</summary>
    private const int MaxUpdateIds = 500;

    public static IEndpointRouteBuilder MapDriverUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/driver-updates/overview", OverviewAsync)
            .WithName("GetDriverUpdateOverview")
            .RequirePermission(Permissions.Driver.View);

        endpoints.MapPost("/admin/v1/driver-updates/scan-all", ScanAllAsync)
            .WithName("ScanAllDriverUpdates")
            .RequirePermission(Permissions.Driver.View);

        endpoints.MapPost("/admin/v1/driver-updates/install-all", InstallAllAsync)
            .WithName("InstallAllDriverUpdates")
            .RequirePermission(Permissions.Driver.Manage);

        var device = endpoints.MapGroup("/admin/v1/devices/{deviceId:guid}/driver-updates");

        device.MapGet("/", ListAsync)
            .WithName("ListDeviceDriverUpdates")
            .RequirePermission(Permissions.Driver.View);

        device.MapPost("/scan", ScanAsync)
            .WithName("ScanDeviceDriverUpdates")
            .RequirePermission(Permissions.Driver.View);

        device.MapPost("/install", InstallAsync)
            .WithName("InstallDeviceDriverUpdates")
            .RequirePermission(Permissions.Driver.Manage);

        // Hiding decides what install-all will queue on the machine, so it carries
        // the install permission, not the read one.
        device.MapPost("/suppress", SuppressAsync)
            .WithName("SuppressDeviceDriverUpdate")
            .RequirePermission(Permissions.Driver.Manage);

        device.MapPost("/unsuppress", UnsuppressAsync)
            .WithName("UnsuppressDeviceDriverUpdate")
            .RequirePermission(Permissions.Driver.Manage);

        return endpoints;
    }

    /// <summary>Longest Windows Update id accepted -- the same bound the pending list stores.</summary>
    private const int MaxUpdateIdLength = 64;

    public sealed record SuppressRequest(string? UpdateId, string? Reason);

    public sealed record UnsuppressRequest(string? UpdateId);

    /// <summary>
    /// Hides one update on one device: out of the actionable list, never queued by
    /// install-all. Idempotent -- hiding an already-hidden update is still 204 and
    /// writes nothing. Platform-side only; nothing is sent to the device.
    /// </summary>
    private static async Task<IResult> SuppressAsync(
        Guid deviceId,
        SuppressRequest request,
        DriverUpdateService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (UpdateIdProblem(request.UpdateId) is { } problem)
        {
            return problem;
        }

        if (request.Reason is { } reason && reason.Trim().Length > Domain.Devices.DeviceDriverUpdateSuppression.MaxReasonLength)
        {
            return Results.Problem(
                $"The reason may be at most {Domain.Devices.DeviceDriverUpdateSuppression.MaxReasonLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await service.SuppressAsync(
            actor.OrganizationId, deviceId, request.UpdateId!, request.Reason, actor.UserId, actor.Email, cancellationToken);

        return result == DriverUpdateSuppressionResult.DeviceNotFound ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>
    /// Un-hides one update on one device. Idempotent -- un-hiding a visible update is
    /// still 204 and writes nothing.
    /// </summary>
    private static async Task<IResult> UnsuppressAsync(
        Guid deviceId,
        UnsuppressRequest request,
        DriverUpdateService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (UpdateIdProblem(request.UpdateId) is { } problem)
        {
            return problem;
        }

        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await service.UnsuppressAsync(
            actor.OrganizationId, deviceId, request.UpdateId!, actor.UserId, actor.Email, cancellationToken);

        return result == DriverUpdateSuppressionResult.DeviceNotFound ? Results.NotFound() : Results.NoContent();
    }

    private static IResult? UpdateIdProblem(string? updateId)
    {
        if (string.IsNullOrWhiteSpace(updateId))
        {
            return Results.Problem("Name the update to hide or un-hide.", statusCode: StatusCodes.Status400BadRequest);
        }

        return updateId.Trim().Length > MaxUpdateIdLength
            ? Results.Problem(
                $"An update id is at most {MaxUpdateIdLength} characters.", statusCode: StatusCodes.Status400BadRequest)
            : null;
    }

    private static async Task<IResult> OverviewAsync(
        DriverUpdateReadService readService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scoped = await scope.ScopedDeviceIdsOrNullAsync(actor.UserId, actor.OrganizationId, cancellationToken);
        return Results.Ok(await readService.GetOverviewAsync(actor.OrganizationId, scoped, cancellationToken));
    }

    private static async Task<IResult> ListAsync(
        Guid deviceId,
        DriverUpdateReadService readService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        return Results.Ok(await readService.GetForDeviceAsync(deviceId, cancellationToken));
    }

    private static async Task<IResult> ScanAsync(
        Guid deviceId,
        DriverUpdateService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await service.QueueScanAsync(
            actor.OrganizationId, deviceId, actor.UserId, actor.Email, cancellationToken);
        return QueueResult(result);
    }

    public sealed record InstallRequest(IReadOnlyList<string>? UpdateIds);

    private static async Task<IResult> InstallAsync(
        Guid deviceId,
        InstallRequest request,
        DriverUpdateService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var ids = request.UpdateIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? [];
        if (ids.Count == 0)
        {
            return Results.Problem(
                "Select at least one update to install.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (ids.Count > MaxUpdateIds)
        {
            return Results.Problem(
                $"At most {MaxUpdateIds} updates may be installed at once.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await service.QueueInstallAsync(
            actor.OrganizationId, deviceId, ids, actor.UserId, actor.Email, cancellationToken);
        return QueueResult(result);
    }

    private static async Task<IResult> ScanAllAsync(
        DriverUpdateService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scoped = await scope.ScopedDeviceIdsOrNullAsync(actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await service.ScanAllAsync(
            actor.OrganizationId, scoped, actor.UserId, actor.Email, cancellationToken);
        return Results.Ok(new { queued = result.DevicesQueued });
    }

    private static async Task<IResult> InstallAllAsync(
        DriverUpdateService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scoped = await scope.ScopedDeviceIdsOrNullAsync(actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await service.InstallAllAsync(
            actor.OrganizationId, scoped, actor.UserId, actor.Email, cancellationToken);
        return Results.Ok(new { queued = result.DevicesQueued });
    }

    /// <summary>Maps a single-device queue outcome to its HTTP result.</summary>
    private static IResult QueueResult(DriverUpdateQueueResult result) => result.Status switch
    {
        DriverUpdateQueueStatus.Queued => Results.Ok(new
        {
            taskId = result.Task!.Id,
            status = result.Task.Status.ToString(),
        }),

        // Missing or retired device: reported absent, like every other device route.
        DriverUpdateQueueStatus.DeviceNotFound => Results.NotFound(),

        // Real, actionable states rather than a not-found: the device is right there,
        // but it either runs too old an agent or has nothing left to install.
        DriverUpdateQueueStatus.AgentTooOld or DriverUpdateQueueStatus.NothingToInstall =>
            Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),

        _ => Results.Problem("Unhandled driver-update outcome.", statusCode: StatusCodes.Status500InternalServerError),
    };
}
