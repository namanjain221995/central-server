using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Chat;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Live IT-support chat: list the devices with an open chat, start one against a
/// device, read and send messages, and end the session.
/// </summary>
/// <remarks>
/// Every session-keyed route resolves the session against the caller's organization
/// and device scope server-side, answering 404 for anything that is not theirs to
/// see -- so a session id, like a device id, cannot be used to probe the estate.
/// Starting a chat is scope-checked on the named device the same way.
/// </remarks>
public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/chat/devices", ListActiveDevicesAsync)
            .WithName("ListChatDevices")
            .RequirePermission(Permissions.Chat.View);

        endpoints.MapPost("/admin/v1/devices/{deviceId:guid}/chat", StartAsync)
            .WithName("StartChat")
            .RequirePermission(Permissions.Chat.Start);

        var session = endpoints.MapGroup("/admin/v1/chat/{sessionId:guid}");

        session.MapGet("/messages", GetMessagesAsync)
            .WithName("GetChatMessages")
            .RequirePermission(Permissions.Chat.View);

        session.MapPost("/messages", SendMessageAsync)
            .WithName("SendChatMessage")
            .RequirePermission(Permissions.Chat.Start);

        session.MapPost("/end", EndAsync)
            .WithName("EndChat")
            .RequirePermission(Permissions.Chat.Start);

        return endpoints;
    }

    private static async Task<IResult> ListActiveDevicesAsync(
        ChatService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var rows = await service.ListActiveDevicesAsync(actor.OrganizationId, scopedDeviceIds, cancellationToken);

        return Results.Ok(rows.Select(r => new
        {
            r.DeviceId,
            r.Hostname,
            r.DisplayName,
            r.IsOnline,
            r.LastSeenAt,
            r.LoggedOnUser,
            r.ActiveSessionId,
        }));
    }

    private static async Task<IResult> StartAsync(
        Guid deviceId,
        ChatService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await service.StartAsync(
            actor.OrganizationId, deviceId, actor.UserId, actor.Email, cancellationToken);

        return result.Outcome switch
        {
            ChatStartOutcome.Started => Results.Ok(new { sessionId = result.SessionId, status = result.Status }),

            ChatStartOutcome.Duplicate => Results.Problem(
                "This device already has an active chat session.", statusCode: StatusCodes.Status409Conflict),

            // Well-formed request, but the device cannot carry the task: a precondition
            // on the device, so 409 rather than 400.
            ChatStartOutcome.AgentTooOld => Results.Problem(
                "This device's agent is too old for live chat. Update the agent to 1.22.0 or later.",
                statusCode: StatusCodes.Status409Conflict),

            // DeviceNotFound and anything else: one 404 for every shade of "not yours".
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> GetMessagesAsync(
        Guid sessionId,
        [FromQuery] int? after,
        ChatService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await service.GetMessagesAsync(
            actor.OrganizationId, sessionId, after ?? 0, scopedDeviceIds, cancellationToken);

        if (result is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(new
        {
            sessionId,
            result.Status,
            result.Ended,
            result.Messages,
        });
    }

    public sealed record SendMessageRequest(string? Body);

    private static async Task<IResult> SendMessageAsync(
        Guid sessionId,
        SendMessageRequest request,
        ChatService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await service.SendAdminMessageAsync(
            actor.OrganizationId, sessionId, scopedDeviceIds, actor.Email, request.Body ?? "", cancellationToken);

        return result.Outcome switch
        {
            ChatSendOutcome.Sent => Results.Ok(new { seq = result.Seq, sentAt = result.SentAt }),

            ChatSendOutcome.InvalidBody => Results.Problem(
                "A chat message must be non-empty plain text of at most 2000 characters.",
                statusCode: StatusCodes.Status400BadRequest),

            ChatSendOutcome.SessionEnded => Results.Problem(
                "This chat session has ended.", statusCode: StatusCodes.Status409Conflict),

            // SessionNotFound and anything else.
            _ => Results.NotFound(),
        };
    }

    private static async Task<IResult> EndAsync(
        Guid sessionId,
        ChatService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await service.EndAsync(
            actor.OrganizationId, sessionId, scopedDeviceIds, actor.UserId, actor.Email, cancellationToken);

        return result.Outcome == ChatEndOutcome.Ended
            ? Results.Ok(new { status = result.Status })
            : Results.NotFound();
    }
}
