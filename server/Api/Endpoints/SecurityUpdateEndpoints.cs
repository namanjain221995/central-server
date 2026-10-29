using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Windows Update security updates: see which machines have security updates
/// pending, list one machine's, and scan or install them -- on one device or the
/// fleet.
/// </summary>
/// <remarks>
/// <para>
/// Reading and scanning ride on <c>update.view</c>; installing rides on
/// <c>update.manage</c> and is high-risk, because it rewrites the operating system
/// itself and usually ends in a reboot. A scan changes nothing on the endpoint, so
/// it is a view-level action; an install patches the machine, so it is a
/// manage-level one, the same split the driver updates draw. Hiding an update
/// (suppress / unsuppress) also rides on <c>update.manage</c>: it changes nothing on
/// the machine, but it decides what install-all will put there -- and, unlike a
/// hidden driver, a hidden security patch must carry a reason.
/// </para>
/// <para>
/// Nothing here installs anything directly. Every device action queues a typed task
/// the endpoint pulls on its next poll and verifies against Windows Update for
/// itself; the server's request is a necessary condition, never a sufficient one. A
/// device the caller may not see is reported absent, not forbidden, exactly as the
/// other device routes do.
/// </para>
/// </remarks>
public static class SecurityUpdateEndpoints
{
    /// <summary>Refuses an implausible selection before any work is queued.</summary>
    private const int MaxUpdateIds = 500;

    public static IEndpointRouteBuilder MapSecurityUpdateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/security-updates/overview", OverviewAsync)
            .WithName("GetSecurityUpdateOverview")
            .RequirePermission(Permissions.Update.View);

        endpoints.MapPost("/admin/v1/security-updates/scan-all", ScanAllAsync)
            .WithName("ScanAllSecurityUpdates")
            .RequirePermission(Permissions.Update.View);

        endpoints.MapPost("/admin/v1/security-updates/install-all", InstallAllAsync)
            .WithName("InstallAllSecurityUpdates")
            .RequirePermission(Permissions.Update.Manage);

        var device = endpoints.MapGroup("/admin/v1/devices/{deviceId:guid}/security-updates");

        device.MapGet("/", ListAsync)
            .WithName("ListDeviceSecurityUpdates")
            .RequirePermission(Permissions.Update.View);

        device.MapPost("/scan", ScanAsync)
            .WithName("ScanDeviceSecurityUpdates")
            .RequirePermission(Permissions.Update.View);

        device.MapPost("/install", InstallAsync)
            .WithName("InstallDeviceSecurityUpdates")
            .RequirePermission(Permissions.Update.Manage);

        // Hiding decides what install-all will queue on the machine, so it carries
        // the install permission, not the read one.
        device.MapPost("/suppress", SuppressAsync)
            .WithName("SuppressDeviceSecurityUpdate")
            .RequirePermission(Permissions.Update.Manage);

        device.MapPost("/unsuppress", UnsuppressAsync)
            .WithName("UnsuppressDeviceSecurityUpdate")
            .RequirePermission(Permissions.Update.Manage);

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
    /// <remarks>
    /// The reason is REQUIRED (1-200 characters after trimming): leaving a machine
    /// unpatched on purpose must be justified, and the justification is audited.
    /// This is the one deliberate difference from the driver route, whose reason is
    /// optional. Validated before the scope check, like the update id: a malformed
    /// request is refused the same way whoever sends it.
    /// </remarks>
    private static async Task<IResult> SuppressAsync(
        Guid deviceId,
        SuppressRequest request,
        SecurityUpdateService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (UpdateIdProblem(request.UpdateId) is { } problem)
        {
            return problem;
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return Results.Problem(
                "Say why this security update is being hidden.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (reason.Length > Domain.Devices.DeviceSecurityUpdateSuppression.MaxReasonLength)
        {
            return Results.Problem(
                $"The reason may be at most {Domain.Devices.DeviceSecurityUpdateSuppression.MaxReasonLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await service.SuppressAsync(
            actor.OrganizationId, deviceId, request.UpdateId!, reason, actor.UserId, actor.Email, cancellationToken);

        return result == SecurityUpdateSuppressionResult.DeviceNotFound ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>
    /// Un-hides one update on one device. Idempotent -- un-hiding a visible update is
    /// still 204 and writes nothing.
    /// </summary>
    private static async Task<IResult> UnsuppressAsync(
        Guid deviceId,
        UnsuppressRequest request,
        SecurityUpdateService service,
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

        return result == SecurityUpdateSuppressionResult.DeviceNotFound ? Results.NotFound() : Results.NoContent();
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
        SecurityUpdateReadService readService,
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
        SecurityUpdateReadService readService,
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
        SecurityUpdateService service,
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
        SecurityUpdateService service,
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
        SecurityUpdateService service,
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
        SecurityUpdateService service,
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
    private static IResult QueueResult(SecurityUpdateQueueResult result) => result.Status switch
    {
        SecurityUpdateQueueStatus.Queued => Results.Ok(new
        {
            taskId = result.Task!.Id,
            status = result.Task.Status.ToString(),
        }),

        // Missing or retired device: reported absent, like every other device route.
        SecurityUpdateQueueStatus.DeviceNotFound => Results.NotFound(),

        // Real, actionable states rather than a not-found: the device is right there,
        // but it either runs too old an agent or has nothing left to install.
        SecurityUpdateQueueStatus.AgentTooOld or SecurityUpdateQueueStatus.NothingToInstall =>
            Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),

        _ => Results.Problem("Unhandled security-update outcome.", statusCode: StatusCodes.Status500InternalServerError),
    };
}
