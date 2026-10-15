using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Announcements;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Announcements;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Announcements: broadcast a short plain-text notice to device screens, and
/// read how past broadcasts went.
/// </summary>
/// <remarks>
/// Both routes require <see cref="Permissions.Announcement.Send"/> -- the history
/// shows what was said to the whole estate and by whom, which belongs to the
/// people allowed to say it. Target ids are shape-checked here and authorized
/// server-side in the service, exactly as deployments are.
/// </remarks>
public static class AnnouncementEndpoints
{
    /// <summary>Refuses an implausible selection before any query runs.</summary>
    private const int MaxTargetIds = 2000;

    public static IEndpointRouteBuilder MapAnnouncementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/announcements")
            .RequirePermission(Permissions.Announcement.Send);

        group.MapPost("/", SendAsync).WithName("SendAnnouncement");
        group.MapGet("/", ListAsync).WithName("ListAnnouncements");

        return endpoints;
    }

    public sealed record SendAnnouncementRequest(
        string? Title, string? Message, Guid? GroupId, IReadOnlyList<Guid>? DeviceIds);

    private static async Task<IResult> SendAsync(
        SendAnnouncementRequest request,
        AnnouncementService announcementService,
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

        var result = await announcementService.SendAsync(
            actor.OrganizationId, request.Title!.Trim(), request.Message!.Trim(),
            request.GroupId, request.DeviceIds, scopedDeviceIds,
            actor.UserId, actor.Email, cancellationToken);

        // Null covers an unknown group, another organization's, and device ids
        // that all resolve to nothing -- one 404 for every shade of "not yours",
        // so a caller learns nothing it was not already entitled to know.
        return result is null
            ? Results.NotFound()
            : Results.Ok(new { result.AnnouncementId, result.Targeted, result.Queued, result.Offline, result.Skipped });
    }

    private static async Task<IResult> ListAsync(
        AnnouncementService announcementService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        int limit = AnnouncementService.MaxListLimit)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        return Results.Ok(await announcementService.ListAsync(
            actor.OrganizationId, scopedDeviceIds, limit, cancellationToken));
    }

    /// <summary>Shape checks only; every id is authorized server-side afterwards.</summary>
    private static IResult? Validate(SendAnnouncementRequest request)
    {
        var title = request.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > Announcement.MaxTitleLength)
        {
            return Results.Problem(
                $"title is required and is at most {Announcement.MaxTitleLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var message = request.Message?.Trim();
        if (string.IsNullOrEmpty(message) || message.Length > Announcement.MaxMessageLength)
        {
            return Results.Problem(
                $"message is required and is at most {Announcement.MaxMessageLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // The agent's display executor refuses these outright, so accepting one
        // here would queue a broadcast fleet-wide that then fails on every device.
        if (!Announcement.IsPlainText(title) || !Announcement.IsPlainText(message))
        {
            return Results.Problem(
                "title and message are plain text: control characters other than line breaks and tabs are not allowed.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var hasGroup = request.GroupId is { } groupId && groupId != Guid.Empty;
        var hasDevices = request.DeviceIds is { Count: > 0 };

        if (hasGroup == hasDevices)
        {
            return Results.Problem(
                "Target exactly one of groupId or deviceIds.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.DeviceIds is { Count: > MaxTargetIds })
        {
            return Results.Problem(
                $"At most {MaxTargetIds} devices may be targeted at once.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }
}
