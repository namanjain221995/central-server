using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Software;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Software package registration and deployment (Phase 11). Registering or
/// deploying a package is high-risk (<c>software.deploy</c>); listing is
/// <c>software.view</c>. The privileged install runs on the agent, which
/// re-verifies the content hash and Authenticode signer before touching the
/// Windows Installer - the platform never trusts these endpoints to have
/// delivered the right bytes.
/// </summary>
public static class PackageEndpoints
{
    // 2 GiB ceiling: large enough for real MSIs, small enough to bound a hostile upload.
    private const long MaxPackageBytes = 2L * 1024 * 1024 * 1024;

    public static IEndpointRouteBuilder MapPackageEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/packages");

        group.MapGet("/", ListAsync)
            .WithName("ListPackages")
            .RequirePermission(Permissions.Software.View);

        group.MapPost("/", CreateAsync)
            .WithName("CreatePackage")
            .RequirePermission(Permissions.Software.Deploy)
            .DisableAntiforgery(); // multipart upload; CSRF is covered by the X-Requested-With gate.

        group.MapPost("/{packageId:guid}/withdraw", WithdrawAsync)
            .WithName("WithdrawPackage")
            .RequirePermission(Permissions.Software.Deploy);

        // The inverse. Same permission as withdrawing: both change whether a
        // package may be deployed, and neither touches an installed one.
        group.MapPost("/{packageId:guid}/restore", RestoreAsync)
            .WithName("RestoreSoftwarePackage")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapPost("/{packageId:guid}/deploy", DeployAsync)
            .WithName("DeployPackage")
            .RequirePermission(Permissions.Software.Deploy);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        SoftwarePackageService packageService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        var packages = await packageService.ListAsync(organizationId, cancellationToken);
        return Results.Ok(packages.Select(p => new
        {
            p.Id,
            p.Name,
            p.Version,
            p.Publisher,
            type = p.Type.ToString(),
            p.Sha256,
            p.FileName,
            p.SizeBytes,
            p.MsiProductCode,
            p.RequiredSignerSubject,
            p.IsWithdrawn,
            p.CreatedByDisplay,
            p.CreatedAt,
        }));
    }

    private static async Task<IResult> CreateAsync(
        SoftwarePackageService packageService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        // Same 413 trap as the agent-release upload: the 2 GB ceiling below is
        // unreachable unless Kestrel's per-request cap is lifted first.
        RequestBodyLimits.AllowUploadOf(httpContext, MaxPackageBytes);

        if (!httpContext.Request.HasFormContentType)
        {
            return Results.Problem("Expected a multipart/form-data upload.", statusCode: StatusCodes.Status400BadRequest);
        }

        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"];
        if (file is null || file.Length == 0)
        {
            return Results.Problem("A non-empty 'file' part is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > MaxPackageBytes)
        {
            return Results.Problem("Package exceeds the maximum allowed size.", statusCode: StatusCodes.Status400BadRequest);
        }

        string? Field(string key) => form.TryGetValue(key, out var v) ? v.ToString() : null;

        var name = Field("name");
        var version = Field("version");
        var declaredSha256 = Field("sha256");
        var productCode = Field("msiProductCode");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version)
            || string.IsNullOrWhiteSpace(declaredSha256) || string.IsNullOrWhiteSpace(productCode))
        {
            return Results.Problem(
                "name, version, sha256 and msiProductCode are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        await using var content = file.OpenReadStream();

        var result = await packageService.CreateAsync(
            actor.OrganizationId, name!, version!, Field("publisher"), declaredSha256!,
            file.FileName, productCode!, Field("requiredSignerSubject"), content,
            actor.UserId, actor.Email, cancellationToken);

        return result.Status switch
        {
            PackageCreateStatus.Created => Results.Created(
                $"/admin/v1/packages/{result.Package!.Id}", new { result.Package.Id }),
            PackageCreateStatus.Duplicate => Results.Problem(
                "A package with this content already exists.", statusCode: StatusCodes.Status409Conflict),
            PackageCreateStatus.HashMismatch => Results.Problem(
                "Uploaded content does not match the declared SHA-256.", statusCode: StatusCodes.Status400BadRequest),
            _ => Results.Problem(result.Error ?? "Invalid package.", statusCode: StatusCodes.Status400BadRequest),
        };
    }

    /// <summary>Returns a withdrawn package to the catalogue.</summary>
    private static async Task<IResult> RestoreAsync(
        Guid packageId, SoftwarePackageService packageService, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var ok = await packageService.RestoreAsync(
            actor.OrganizationId, packageId, actor.UserId, actor.Email, cancellationToken);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> WithdrawAsync(
        Guid packageId, SoftwarePackageService packageService, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var ok = await packageService.WithdrawAsync(
            actor.OrganizationId, packageId, actor.UserId, actor.Email, cancellationToken);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> DeployAsync(
        Guid packageId, DeployPackageRequest request, SoftwarePackageService packageService,
        Infrastructure.Security.DeviceScopeAuthorizer scope, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if ((request.DeviceId is null) == (request.GroupId is null))
        {
            return Results.Problem(
                "Provide exactly one of deviceId or groupId.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);

        // Installing software runs as SYSTEM on the target, so the target is scoped
        // like every other device action -- the device the caller may act on, or a
        // group they are scoped to (which means every member). The target comes from
        // the body, not the route, and is checked all the same. Out of scope is the
        // same 404 as a target that does not exist.
        var inScope = request.DeviceId is { } targetDevice
            ? await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, targetDevice, cancellationToken)
            : await scope.CanActOnGroupAsync(actor.UserId, actor.OrganizationId, request.GroupId!.Value, cancellationToken);
        if (!inScope)
        {
            return Results.NotFound();
        }

        if (request.DeviceId is { } deviceId)
        {
            var task = await packageService.DeployToDeviceAsync(
                actor.OrganizationId, packageId, deviceId, actor.UserId, actor.Email, cancellationToken);
            return task is null
                ? Results.NotFound()
                : Results.Accepted($"/admin/v1/devices/{deviceId}/tasks", new { taskId = task.Id });
        }

        var result = await packageService.DeployToGroupAsync(
            actor.OrganizationId, packageId, request.GroupId!.Value, actor.UserId, actor.Email, cancellationToken);
        return result is null
            ? Results.NotFound()
            : Results.Accepted($"/admin/v1/groups/{request.GroupId}", new { result.MemberCount, result.QueuedCount });
    }
}

public sealed record DeployPackageRequest(Guid? DeviceId, Guid? GroupId);
