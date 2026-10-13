using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Software;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Software;
using Microsoft.AspNetCore.Http.Features;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// The Company Portal catalog: which approved packages end users may install
/// themselves, and the record of what they asked for.
/// </summary>
/// <remarks>
/// Publishing an entry pre-approves an install on every device that can see it,
/// so changing the catalog needs <c>software.deploy</c>, the same authority as
/// deploying. Reading it, and the request history, needs <c>software.view</c>.
/// </remarks>
public static class CatalogEndpoints
{
    private const int MaxPageSize = 200;

    // The icon plus the multipart framing around it; anything larger is not an icon.
    private const long IconUploadCeiling = CatalogApp.MaxIconBytes + 16 * 1024;

    private static readonly string IconTooLarge = $"Icon must be at most {CatalogApp.MaxIconBytes / 1024} KB.";

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/catalog");

        group.MapGet("/", ListAsync)
            .WithName("ListCatalogApps")
            .RequirePermission(Permissions.Software.View);

        group.MapGet("/installs", ListInstallsAsync)
            .WithName("ListCatalogInstalls")
            .RequirePermission(Permissions.Software.View);

        group.MapPost("/", CreateAsync)
            .WithName("CreateCatalogApp")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapPut("/{appId:guid}", UpdateAsync)
            .WithName("UpdateCatalogApp")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapDelete("/{appId:guid}", DeleteAsync)
            .WithName("DeleteCatalogApp")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapPost("/{appId:guid}/publish", PublishAsync)
            .WithName("PublishCatalogApp")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapPost("/{appId:guid}/unpublish", UnpublishAsync)
            .WithName("UnpublishCatalogApp")
            .RequirePermission(Permissions.Software.Deploy);

        group.MapGet("/{appId:guid}/icon", GetIconAsync)
            .WithName("GetCatalogAppIcon")
            .RequirePermission(Permissions.Software.View);

        group.MapPut("/{appId:guid}/icon", SetIconAsync)
            .WithName("SetCatalogAppIcon")
            .RequirePermission(Permissions.Software.Deploy)
            .DisableAntiforgery(); // multipart upload; CSRF is covered by the X-Requested-With gate.

        group.MapDelete("/{appId:guid}/icon", ClearIconAsync)
            .WithName("ClearCatalogAppIcon")
            .RequirePermission(Permissions.Software.Deploy);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        CatalogAppService catalogService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        var apps = await catalogService.ListAsync(organizationId, cancellationToken);
        return Results.Ok(apps.Select(ToResponse));
    }

    private static async Task<IResult> ListInstallsAsync(
        int? page, int? pageSize, CatalogAppService catalogService, DeviceScopeAuthorizer scope,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await catalogService.ListInstallRequestsAsync(
            actor.OrganizationId, scopedDeviceIds,
            Math.Max(page ?? 1, 1), Math.Clamp(pageSize ?? 50, 1, MaxPageSize), cancellationToken);

        return Results.Ok(new
        {
            items = result.Items.Select(r => new
            {
                r.Id,
                r.DeviceId,
                r.Hostname,
                r.CatalogAppId,
                r.AppName,
                r.RequestedBy,
                outcome = r.Outcome.ToString(),
                r.TaskId,
                taskStatus = r.TaskStatus?.ToString(),
                r.RequestedAt,
            }),
            result.Total,
            result.Page,
            result.PageSize,
        });
    }

    private static async Task<IResult> CreateAsync(
        CatalogAppRequest request, CatalogAppService catalogService, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseInput(request, out var input, out var problem))
        {
            return problem;
        }

        var actor = AdminActor.Required(httpContext.User);
        var result = await catalogService.CreateAsync(
            actor.OrganizationId, input, request.Publish ?? false, actor.UserId, actor.Email, cancellationToken);

        return result.Status == CatalogAppStatus.Success
            ? Results.Created($"/admin/v1/catalog/{result.App!.Id}", new { result.App.Id })
            : ToProblem(result);
    }

    private static async Task<IResult> UpdateAsync(
        Guid appId, CatalogAppRequest request, CatalogAppService catalogService, HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!TryParseInput(request, out var input, out var problem))
        {
            return problem;
        }

        var actor = AdminActor.Required(httpContext.User);
        var result = await catalogService.UpdateAsync(
            actor.OrganizationId, appId, input, actor.UserId, actor.Email, cancellationToken);

        return result.Status == CatalogAppStatus.Success ? Results.NoContent() : ToProblem(result);
    }

    private static async Task<IResult> DeleteAsync(
        Guid appId, CatalogAppService catalogService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var ok = await catalogService.DeleteAsync(
            actor.OrganizationId, appId, actor.UserId, actor.Email, cancellationToken);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> PublishAsync(
        Guid appId, CatalogAppService catalogService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await catalogService.SetPublishedAsync(
            actor.OrganizationId, appId, true, actor.UserId, actor.Email, cancellationToken);
        return result.Status == CatalogAppStatus.Success ? Results.NoContent() : ToProblem(result);
    }

    private static async Task<IResult> UnpublishAsync(
        Guid appId, CatalogAppService catalogService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await catalogService.SetPublishedAsync(
            actor.OrganizationId, appId, false, actor.UserId, actor.Email, cancellationToken);
        return result.Status == CatalogAppStatus.Success ? Results.NoContent() : ToProblem(result);
    }

    private static async Task<IResult> GetIconAsync(
        Guid appId, CatalogAppService catalogService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var organizationId = AdminActor.Required(httpContext.User).OrganizationId;
        var png = await catalogService.GetIconAsync(organizationId, appId, cancellationToken);
        return png is null ? Results.NotFound() : Results.Bytes(png, "image/png");
    }

    private static async Task<IResult> SetIconAsync(
        Guid appId, CatalogAppService catalogService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (!httpContext.Request.HasFormContentType)
        {
            return Results.Problem("Expected a multipart/form-data upload.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Bounded before a byte is read. ReadFormAsync spools the whole body
        // under the host's multipart ceiling, which is sized for 2 GB packages;
        // an "icon" must never be allowed to be that.
        if (httpContext.Request.ContentLength is > IconUploadCeiling)
        {
            return Results.Problem(IconTooLarge, statusCode: StatusCodes.Status400BadRequest);
        }

        var bodySize = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
        {
            bodySize.MaxRequestBodySize = IconUploadCeiling;
        }

        var form = await httpContext.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"];
        if (file is null || file.Length == 0)
        {
            return Results.Problem("A non-empty 'file' part is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (file.Length > CatalogApp.MaxIconBytes)
        {
            return Results.Problem(IconTooLarge, statusCode: StatusCodes.Status400BadRequest);
        }

        byte[] png;
        await using (var stream = file.OpenReadStream())
        {
            using var buffer = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(buffer, cancellationToken);
            png = buffer.ToArray();
        }

        var actor = AdminActor.Required(httpContext.User);
        var result = await catalogService.SetIconAsync(
            actor.OrganizationId, appId, png, actor.UserId, actor.Email, cancellationToken);

        return result.Status == CatalogAppStatus.Success ? Results.NoContent() : ToProblem(result);
    }

    private static async Task<IResult> ClearIconAsync(
        Guid appId, CatalogAppService catalogService, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var result = await catalogService.SetIconAsync(
            actor.OrganizationId, appId, null, actor.UserId, actor.Email, cancellationToken);

        return result.Status == CatalogAppStatus.Success ? Results.NoContent() : ToProblem(result);
    }

    private static bool TryParseInput(CatalogAppRequest request, out CatalogAppInput input, out IResult problem)
    {
        input = null!;
        problem = null!;

        if (request.PackageId is null || request.PackageId == Guid.Empty || string.IsNullOrWhiteSpace(request.DisplayName))
        {
            problem = Results.Problem("packageId and displayName are required.", statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        // Strings on the wire, parsed by name: an unknown value is a 400, never
        // whichever member happens to sit at that ordinal.
        if (!Enum.TryParse<CatalogVisibility>(request.Visibility, ignoreCase: true, out var visibility)
            || !Enum.IsDefined(visibility))
        {
            problem = Results.Problem("visibility must be AllDevices or SelectedGroups.", statusCode: StatusCodes.Status400BadRequest);
            return false;
        }

        input = new CatalogAppInput(
            request.PackageId.Value, request.DisplayName!, request.Description, request.Category,
            visibility, request.GroupIds);
        return true;
    }

    private static IResult ToProblem(CatalogAppResult result) => result.Status switch
    {
        CatalogAppStatus.NotFound => Results.NotFound(),
        CatalogAppStatus.PackageNotFound => Results.Problem(result.Error, statusCode: StatusCodes.Status404NotFound),
        CatalogAppStatus.DuplicatePackage => Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),
        CatalogAppStatus.PackageWithdrawn => Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),
        _ => Results.Problem(result.Error ?? "Invalid catalog app.", statusCode: StatusCodes.Status400BadRequest),
    };

    private static object ToResponse(CatalogAppSummary a) => new
    {
        a.Id,
        a.PackageId,
        a.PackageName,
        a.PackageVersion,
        a.PackageWithdrawn,
        a.DisplayName,
        a.Description,
        a.Category,
        a.HasIcon,
        visibility = a.Visibility.ToString(),
        a.GroupIds,
        a.IsPublished,
        a.CreatedByDisplay,
        a.PublishedByDisplay,
        a.CreatedAt,
        a.UpdatedAt,
    };
}

/// <summary>Create and update share one body; <see cref="Publish"/> is honoured on create only.</summary>
public sealed record CatalogAppRequest(
    Guid? PackageId,
    string? DisplayName,
    string? Description,
    string? Category,
    string? Visibility,
    IReadOnlyCollection<Guid>? GroupIds,
    bool? Publish);
