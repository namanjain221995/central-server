using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Software;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Software deployments: plan one, create it, and read what happened.
/// </summary>
/// <remarks>
/// Bulk by design. One request carries every device and group, and the server
/// resolves them — the alternative, a request per device, is 350 round trips for
/// one operator action and gives the fleet no single record of what was intended.
/// </remarks>
public static class DeploymentEndpoints
{
    /// <summary>Refuses an implausible target list before any query runs.</summary>
    private const int MaxTargetIds = 2000;

    public static IEndpointRouteBuilder MapDeploymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/deployments");

        group.MapGet("/", ListAsync)
            .WithName("ListDeployments")
            .RequirePermission(Permissions.Software.View);

        group.MapGet("/{deploymentId:guid}", GetAsync)
            .WithName("GetDeployment")
            .RequirePermission(Permissions.Software.View);

        group.MapPost("/preview", PreviewAsync)
            .WithName("PreviewDeployment")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapPost("/", CreateAsync)
            .WithName("CreateDeployment")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapPost("/{deploymentId:guid}/retry", RetryAsync)
            .WithName("RetryDeployment")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapPost("/{deploymentId:guid}/cancel", CancelAsync)
            .WithName("CancelDeployment")
            .RequirePermission(Permissions.Software.Deploy);

        return endpoints;
    }

    /// <summary>
    /// Re-runs the devices that did not succeed, as a new attempt.
    /// </summary>
    /// <remarks>
    /// Requires <c>software.deploy</c> because it queues installs. The service
    /// re-runs authorization, package lifecycle and eligibility rather than
    /// replaying the old decision, so a device that has since become compliant,
    /// been retired, or left the caller's scope is not sent an install.
    /// </remarks>
    private static async Task<IResult> RetryAsync(
        Guid deploymentId,
        SoftwareDeploymentService deploymentService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await deploymentService.RetryAsync(
            actor.OrganizationId, deploymentId, scopedDeviceIds, actor.UserId, actor.Email, cancellationToken);

        // Null covers a missing deployment, another organization's, and a package
        // that is no longer deployable -- all 404, so a caller learns nothing it
        // was not already entitled to know.
        return result is null
            ? Results.NotFound()
            : Results.Accepted(
                $"/admin/v1/deployments/{deploymentId}",
                new { result.DeploymentId, result.Targeted, result.Queued, result.Skipped });
    }

    /// <summary>
    /// Cancels the work in a deployment that has not reached an agent yet.
    /// </summary>
    /// <remarks>
    /// Only Queued tasks are cancellable. A delivered install is running on a
    /// Windows machine and is deliberately left alone rather than reported as
    /// cancelled, which would be a claim the platform cannot support. Only the
    /// caller's in-scope targets are cancelled; a deployment with none is 404.
    /// </remarks>
    private static async Task<IResult> CancelAsync(
        Guid deploymentId,
        SoftwareDeploymentService deploymentService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await deploymentService.CancelPendingAsync(
            actor.OrganizationId, deploymentId, scopedDeviceIds, actor.UserId, actor.Email, cancellationToken);

        return result is null
            ? Results.NotFound()
            : Results.Ok(new { result.DeploymentId, result.Considered, result.Cancelled });
    }

    /// <summary>
    /// What deploying would do, without doing it.
    /// </summary>
    /// <remarks>
    /// Requires <c>software.deploy</c>, not merely view: the resolution discloses
    /// which devices have which software, and it is the preface to an action only
    /// a deployer may take.
    /// </remarks>
    private static async Task<IResult> PreviewAsync(
        DeploymentRequest request,
        SoftwareDeploymentService deploymentService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var plan = await deploymentService.PlanAsync(
            actor.OrganizationId, request.PackageId, request.DeviceIds ?? [], request.GroupIds ?? [],
            scopedDeviceIds, cancellationToken);

        // Missing, another organization's, or withdrawn — all 404, so a caller
        // cannot tell a package they may not use from one that does not exist.
        return plan is null
            ? Results.NotFound()
            : Results.Ok(new
            {
                plan.PackageId,
                plan.PackageName,
                plan.PackageVersion,
                plan.Targeted,
                plan.NeedsInstall,
                plan.AlreadyInstalled,
                plan.NewerInstalled,
                plan.Retired,
                plan.NotComparable,
            });
    }

    private static async Task<IResult> CreateAsync(
        DeploymentRequest request,
        SoftwareDeploymentService deploymentService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (Validate(request) is { } problem)
        {
            return problem;
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await deploymentService.CreateAsync(
            actor.OrganizationId, request.PackageId, request.DeviceIds ?? [], request.GroupIds ?? [],
            scopedDeviceIds, actor.UserId, actor.Email, cancellationToken);

        if (result is null)
        {
            return Results.NotFound();
        }

        // Accepted, not Created: the tasks exist, the installs have not happened.
        // The agent does the work on its own schedule and the request must never
        // wait for it.
        return Results.Accepted(
            $"/admin/v1/deployments/{result.DeploymentId}",
            new { result.DeploymentId, result.Targeted, result.Queued, result.Skipped });
    }

    private static async Task<IResult> ListAsync(
        SoftwareDeploymentReadService readService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 25)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        return Results.Ok(await readService.ListAsync(
            actor.OrganizationId, scopedDeviceIds, page, pageSize, cancellationToken));
    }

    private static async Task<IResult> GetAsync(
        Guid deploymentId,
        SoftwareDeploymentReadService readService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var detail = await readService.GetAsync(
            actor.OrganizationId, deploymentId, scopedDeviceIds, cancellationToken);

        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    /// <summary>Shape checks only; every id is authorized server-side afterwards.</summary>
    private static IResult? Validate(DeploymentRequest request)
    {
        if (request.PackageId == Guid.Empty)
        {
            return Results.Problem("packageId is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var devices = request.DeviceIds?.Count ?? 0;
        var groups = request.GroupIds?.Count ?? 0;

        if (devices == 0 && groups == 0)
        {
            return Results.Problem(
                "Provide at least one deviceId or groupId.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (devices + groups > MaxTargetIds)
        {
            return Results.Problem(
                $"At most {MaxTargetIds} targets may be submitted at once.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }
}

/// <summary>
/// A deployment request. Devices and groups may be combined; the union is
/// de-duplicated so a device in two targeted groups is deployed to once.
/// </summary>
public sealed record DeploymentRequest(
    Guid PackageId, IReadOnlyList<Guid>? DeviceIds, IReadOnlyList<Guid>? GroupIds);
