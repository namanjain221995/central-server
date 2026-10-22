using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Chrome;
using EndpointPlatform.Infrastructure.Chrome;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Chrome Management, write side: the per-device extension rules -- force-install,
/// block, allow or remove an extension -- and the read of what is declared.
/// </summary>
/// <remarks>
/// <para>
/// Reading the rules rides on <c>chrome.view</c> like the rest of the Chrome
/// section; declaring or lifting one rides on <c>chrome.manage</c> and is
/// high-risk, because a rule decides what software runs inside every signed-in
/// browser session on the machine. The same read/write split the driver routes
/// draw between <c>driver.view</c> and <c>driver.manage</c>.
/// </para>
/// <para>
/// Nothing here touches an endpoint directly. Every mutation stores the decision
/// and queues one whole-state <c>ApplyChromeExtensionPolicy</c> task carrying every
/// rule the device has; Chrome's own <c>ExtensionSettings</c> policy does the
/// enforcing. A device whose agent predates the executor is answered 409 with the
/// version to update to, never a silent queue of work that cannot run.
/// </para>
/// <para>
/// Anything out of the caller's scope answers 404, matching the other device
/// routes: an administrator who cannot reach a device must not learn that it
/// exists.
/// </para>
/// </remarks>
public static class ChromeExtensionRuleEndpoints
{
    public static IEndpointRouteBuilder MapChromeExtensionRuleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var rules = endpoints.MapGroup("/admin/v1/devices/{deviceId:guid}/chrome/extension-rules");

        rules.MapGet("/", ListAsync)
            .WithName("ListChromeExtensionRules")
            .RequirePermission(Permissions.Chrome.View);

        rules.MapPut("/{extensionId}", SetAsync)
            .WithName("SetChromeExtensionRule")
            .RequirePermission(Permissions.Chrome.Manage);

        rules.MapDelete("/{extensionId}", RemoveAsync)
            .WithName("RemoveChromeExtensionRule")
            .RequirePermission(Permissions.Chrome.Manage);

        return endpoints;
    }

    /// <param name="Mode">One of <c>ForceInstalled</c>, <c>Blocked</c>, <c>Allowed</c>, <c>Removed</c>.</param>
    /// <param name="DisplayName">
    /// Optional label the console remembers for the rule. Omitting it on an update
    /// keeps the label already stored.
    /// </param>
    public sealed record SetRuleRequest(string? Mode, string? DisplayName);

    private static async Task<IResult> ListAsync(
        Guid deviceId,
        ChromeExtensionRuleService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        return Results.Ok(await service.ListRulesAsync(deviceId, cancellationToken));
    }

    private static async Task<IResult> SetAsync(
        Guid deviceId,
        string extensionId,
        SetRuleRequest request,
        ChromeExtensionRuleService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The caller's own mistakes are answered before scope is consulted: a
        // malformed id or mode is 400 whoever asks about whatever device.
        if (!ChromeExtension.IsValidExtensionId(extensionId))
        {
            return Results.Problem(
                $"A Chrome extension id is exactly {ChromeExtension.ExtensionIdLength} characters, each a-p.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (ParseMode(request.Mode) is not { } mode)
        {
            return Results.Problem(
                "Mode must be one of ForceInstalled, Blocked, Allowed or Removed.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        if (displayName is { Length: > ChromeExtensionRule.MaxDisplayNameLength })
        {
            return Results.Problem(
                $"The display name is at most {ChromeExtensionRule.MaxDisplayNameLength} characters.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await service.SetRuleAsync(
            actor.OrganizationId, deviceId, extensionId, mode, displayName,
            actor.UserId, actor.Email, cancellationToken);
        return QueueResult(result);
    }

    private static async Task<IResult> RemoveAsync(
        Guid deviceId,
        string extensionId,
        ChromeExtensionRuleService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // A malformed id can never name a stored rule, so a delete of one is the
        // delete of a rule that does not exist.
        if (!ChromeExtension.IsValidExtensionId(extensionId))
        {
            return Results.NotFound();
        }

        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return Results.NotFound();
        }

        var result = await service.RemoveRuleAsync(
            actor.OrganizationId, deviceId, extensionId, actor.UserId, actor.Email, cancellationToken);
        return QueueResult(result);
    }

    /// <summary>
    /// The mode names, matched ignoring case so a hand-typed call works, but never
    /// by number: <c>Enum.TryParse</c> accepts "2", and a numeric mode on this route
    /// would defeat the whole point of the names being the contract.
    /// </summary>
    private static ChromeExtensionRuleMode? ParseMode(string? value) => value?.Trim() switch
    {
        { } v when v.Equals(nameof(ChromeExtensionRuleMode.ForceInstalled), StringComparison.OrdinalIgnoreCase)
            => ChromeExtensionRuleMode.ForceInstalled,
        { } v when v.Equals(nameof(ChromeExtensionRuleMode.Blocked), StringComparison.OrdinalIgnoreCase)
            => ChromeExtensionRuleMode.Blocked,
        { } v when v.Equals(nameof(ChromeExtensionRuleMode.Allowed), StringComparison.OrdinalIgnoreCase)
            => ChromeExtensionRuleMode.Allowed,
        { } v when v.Equals(nameof(ChromeExtensionRuleMode.Removed), StringComparison.OrdinalIgnoreCase)
            => ChromeExtensionRuleMode.Removed,
        _ => null,
    };

    /// <summary>Maps a single-device outcome to its HTTP result.</summary>
    private static IResult QueueResult(ChromeExtensionRuleResult result) => result.Outcome switch
    {
        ChromeExtensionRuleOutcome.Queued => Results.Ok(new
        {
            taskId = result.Task!.Id,
            status = result.Task.Status.ToString(),
        }),

        // Missing or retired device, and a rule the device does not have: reported
        // absent, like every other device route.
        ChromeExtensionRuleOutcome.DeviceNotFound or ChromeExtensionRuleOutcome.RuleNotFound =>
            Results.NotFound(),

        // A real, actionable state rather than a not-found: the device is right
        // there, but its agent cannot run the policy yet.
        ChromeExtensionRuleOutcome.AgentTooOld =>
            Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict),

        _ => Results.Problem(
            "Unhandled chrome-extension-rule outcome.", statusCode: StatusCodes.Status500InternalServerError),
    };
}
