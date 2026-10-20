using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.RemoteSupport;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// IT remote support (RustDesk): list devices with their remote-support state, enrol
/// a device for remote support, and reveal its unattended password to start a
/// session.
/// </summary>
/// <remarks>
/// Every target is authorized server-side against the caller's device scope; an
/// out-of-scope or unknown device answers 404, so an admin cannot map the estate by
/// probing. Revealing the password is a POST that carries step-up credentials in the
/// body and returns with no-store headers -- never a GET, never in a URL -- exactly
/// as the BitLocker recovery-key reveal is.
/// </remarks>
public static class RemoteSupportEndpoints
{
    public static IEndpointRouteBuilder MapRemoteSupportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/remote-support/devices", ListDevicesAsync)
            .WithName("ListRemoteSupportDevices")
            .RequirePermission(Permissions.RemoteSupport.View);

        var device = endpoints.MapGroup("/admin/v1/devices/{deviceId:guid}/remote-support");

        device.MapPost("/configure", ConfigureAsync)
            .WithName("ConfigureRemoteSupport")
            .RequirePermission(Permissions.RemoteSupport.Configure);

        device.MapPost("/connect", ConnectAsync)
            .WithName("StartRemoteSupportSession")
            .RequirePermission(Permissions.RemoteSupport.Connect);

        return endpoints;
    }

    private static async Task<IResult> ListDevicesAsync(
        RemoteSupportService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var rows = await service.ListDevicesAsync(actor.OrganizationId, scopedDeviceIds, cancellationToken);

        return Results.Ok(rows.Select(r => new
        {
            r.DeviceId,
            r.Hostname,
            r.DisplayName,
            r.IsOnline,
            r.LastSeenAt,
            r.RustDeskId,
            r.State,
        }));
    }

    private static async Task<IResult> ConfigureAsync(
        Guid deviceId,
        RemoteSupportService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await service.ConfigureAsync(
            actor.OrganizationId, deviceId, actor.UserId, actor.Email, cancellationToken);

        return result.Outcome switch
        {
            RemoteSupportOutcome.Success => Results.Accepted(
                $"/admin/v1/devices/{deviceId}/tasks", new { status = "Queued", taskId = result.TaskId }),

            // The agent is too old; a precondition on the device, not a bad request.
            RemoteSupportOutcome.NotEligible => Results.Problem(
                result.Error, statusCode: StatusCodes.Status409Conflict),

            RemoteSupportOutcome.SecretUnavailable => Results.Problem(
                result.Error, statusCode: StatusCodes.Status503ServiceUnavailable),

            // DeviceNotFound and anything else: one 404 for every shade of "not yours".
            _ => Results.NotFound(),
        };
    }

    public sealed record ConnectRequest(string? CurrentPassword, string? Justification);

    private static async Task<IResult> ConnectAsync(
        Guid deviceId,
        ConnectRequest request,
        RemoteSupportService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.Justification))
        {
            return Results.Problem(
                "currentPassword and justification are required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await service.RevealAsync(
            actor.OrganizationId, deviceId, actor.UserId, actor.Email,
            request.CurrentPassword, request.Justification.Trim(), cancellationToken);

        switch (result.Outcome)
        {
            case RemoteSupportOutcome.Success:
                httpContext.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                httpContext.Response.Headers.Pragma = "no-cache";
                return Results.Ok(new { deviceId, rustDeskId = result.RustDeskId, password = result.Password });

            case RemoteSupportOutcome.RateLimited:
                httpContext.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString();
                return Results.Problem(result.Error, statusCode: StatusCodes.Status429TooManyRequests);

            case RemoteSupportOutcome.StepUpFailed:
                return Results.Problem(result.Error, statusCode: StatusCodes.Status403Forbidden);

            case RemoteSupportOutcome.NotReady:
                return Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict);

            case RemoteSupportOutcome.UnsealFailed:
                return Results.Problem(result.Error, statusCode: StatusCodes.Status500InternalServerError);

            // NotEnrolled and anything else.
            default:
                return Results.NotFound();
        }
    }
}
