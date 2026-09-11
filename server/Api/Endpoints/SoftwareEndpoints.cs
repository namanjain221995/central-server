using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Software;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Fleet-wide software inventory views (read-only, software.view), plus the one
/// action that names an application across devices: Force Stop.
/// </summary>
public static class SoftwareEndpoints
{
    /// <summary>Bounds an implausible request before any query runs.</summary>
    private const int MaxTargetedDevices = 500;

    /// <summary>
    /// The lengths inventory itself stores these narrowing values at:
    /// <c>DeviceSoftware</c> bounds a publisher to 256 characters and a version
    /// to 128. A longer string cannot match any row, so accepting one buys the
    /// caller nothing -- but it does reach the query, and on the Force Stop path
    /// it is copied verbatim into the queued task's payload and into the
    /// <c>task.queue</c> audit entry written from it, which is no place for an
    /// unbounded string a browser supplied.
    /// </summary>
    private const int MaxPublisherLength = 256;

    /// <inheritdoc cref="MaxPublisherLength"/>
    private const int MaxVersionLength = 128;

    public static IEndpointRouteBuilder MapSoftwareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/software");

        group.MapGet("/", ListAsync)
            .WithName("ListSoftwareTitles")
            .RequirePermission(Permissions.Software.View);

        group.MapGet("/installations", InstallationsAsync)
            .WithName("ListSoftwareInstallations")
            .RequirePermission(Permissions.Software.View);

        group.MapGet("/publishers", PublishersAsync)
            .WithName("ListSoftwarePublishers")
            .RequirePermission(Permissions.Software.View);

        group.MapPost("/force-stop", ForceStopAsync)
            .WithName("ForceStopApplication")
            .RequirePermission(Permissions.Task.Execute);

        return endpoints;
    }


    /// <summary>
    /// Stops a named installed application on one or more devices.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Gated on <c>task.execute</c>, the same permission as terminating a process
    /// directly, because that is what this ultimately does.
    /// </para>
    /// <para>
    /// The body names an <em>application</em>. It cannot name a process, an image
    /// or a path: the server resolves those from its own inventory, so no request
    /// from a browser can ask the fleet to terminate something arbitrary.
    /// </para>
    /// </remarks>
    private static async Task<IResult> ForceStopAsync(
        ForceStopRequest request,
        ApplicationForceStopService forceStopService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 384)
        {
            return Results.Problem("An application name is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Publisher is { Length: > MaxPublisherLength })
        {
            return Results.Problem(
                $"A publisher may be at most {MaxPublisherLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.DeviceIds is not { Count: > 0 })
        {
            return Results.Problem("At least one deviceId is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.DeviceIds.Count > MaxTargetedDevices)
        {
            return Results.Problem(
                $"At most {MaxTargetedDevices} devices may be targeted at once.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await forceStopService.StopAsync(
            actor.OrganizationId, request.DeviceIds, request.Name.Trim(), request.Publisher,
            scopedDeviceIds, actor.UserId, actor.Email, cancellationToken);

        // Accepted, not Ok: the tasks exist, the processes have not been stopped
        // yet. The agent does that on its next poll.
        return Results.Accepted("/admin/v1/software", new
        {
            result.ProcessesQueued,
            devices = result.Devices.Select(d => new
            {
                d.DeviceId,
                d.Hostname,
                outcome = d.Outcome.ToString(),
                d.ProcessesQueued,
            }),
        });
    }

    /// <summary>
    /// Which devices have one title installed.
    /// </summary>
    /// <remarks>
    /// Device-scoped, because this names machines rather than counting them. An
    /// administrator restricted to a group sees only their devices; the response
    /// is narrowed rather than refused, so scope never reveals that a device it
    /// excludes exists.
    /// </remarks>
    /// <param name="running">
    /// <c>all</c> (the default), <c>running</c> or <c>stopped</c>, judged from
    /// the last inventory's evidence. Rows whose agent reported no evidence have
    /// no state and appear under <c>all</c> only.
    /// </param>
    private static async Task<IResult> InstallationsAsync(
        SoftwareReadService softwareReadService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        string name,
        string? version,
        string? publisher,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 50,
        string? running = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Results.Problem("name is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        SoftwareRunningFilter runningFilter;
        switch (running?.Trim().ToLowerInvariant())
        {
            case null or "" or "all":
                runningFilter = SoftwareRunningFilter.All;
                break;
            case "running":
                runningFilter = SoftwareRunningFilter.Running;
                break;
            case "stopped":
                runningFilter = SoftwareRunningFilter.Stopped;
                break;
            default:
                return Results.Problem("running must be 'all', 'running' or 'stopped'.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await softwareReadService.ListInstallationsAsync(
            actor.OrganizationId, scopedDeviceIds, name, version, publisher, page, pageSize,
            runningFilter, cancellationToken);

        return Results.Ok(result);
    }

    /// <param name="view">
    /// <c>applications</c> (the default) hides frameworks, inbox apps and
    /// components; <c>all</c> shows everything the endpoints reported.
    /// </param>
    private static async Task<IResult> ListAsync(
        SoftwareReadService softwareReadService,
        HttpContext httpContext,
        string? search,
        string? publisher,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 50,
        string? view = null)
    {
        SoftwareView softwareView;
        switch (view?.Trim().ToLowerInvariant())
        {
            case null or "" or "applications":
                softwareView = SoftwareView.Applications;
                break;
            case "all":
                softwareView = SoftwareView.All;
                break;
            default:
                return Results.Problem("view must be 'applications' or 'all'.", statusCode: StatusCodes.Status400BadRequest);
        }

        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        var result = await softwareReadService.ListTitlesAsync(
            organizationId, page, pageSize, search, publisher, softwareView, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> PublishersAsync(
        SoftwareReadService softwareReadService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        return Results.Ok(await softwareReadService.ListPublishersAsync(organizationId, cancellationToken));
    }
}

/// <summary>
/// A Force Stop request: an application, and the devices to stop it on.
/// </summary>
/// <remarks>
/// Deliberately has no field for a process name, image or executable path. The
/// server resolves those from inventory, so the browser cannot ask for an
/// arbitrary process to be terminated.
/// </remarks>
public sealed record ForceStopRequest(
    IReadOnlyList<Guid>? DeviceIds, string? Name, string? Publisher);

