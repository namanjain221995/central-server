using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Fleet-wide local Windows account management: create one account, reset an
/// account's password, or disable, enable or delete an account, across all devices,
/// a group (department), or an explicit selection.
/// </summary>
/// <remarks>
/// <para>
/// This layer only resolves the target set — server-side, within the caller's
/// organization and device scope — and calls the existing per-device engine once per
/// device (<see cref="BulkLocalAccountService"/>). Every per-device validation, the
/// ephemeral-secret handover, the agent-version gate and the per-task audit are reused
/// unchanged, so a fleet-wide create cannot do anything a single-device create cannot.
/// </para>
/// <para>
/// Create is <see cref="Permissions.LocalUser.Create"/>; creating an Administrator
/// additionally requires <see cref="Permissions.LocalUser.ChangeType"/>, checked in
/// the handler exactly as the per-device route does — so there is no route around the
/// change-type gate. Reset is its own lower authority
/// (<see cref="Permissions.LocalUser.ResetPassword"/>), which Helpdesk holds.
/// </para>
/// <para>
/// Disable and enable share <see cref="Permissions.LocalUser.Disable"/> and delete is
/// <see cref="Permissions.LocalUser.Delete"/> — the same permissions the per-device
/// routes and the task catalogue use, so the bulk routes grant nothing new. A
/// username that resolves to the built-in Administrator anywhere in the fleet is
/// refused outright (400) before any device is touched; the per-device
/// last-administrator guard still applies machine by machine and surfaces as the
/// rejected count.
/// </para>
/// </remarks>
public static class BulkLocalAccountEndpoints
{
    /// <summary>Refuses an implausible explicit selection before any query runs.</summary>
    private const int MaxTargetIds = 2000;

    public static IEndpointRouteBuilder MapBulkLocalAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Read-only: which account exists on which devices, fleet-wide. It rides
        // user.view — the same permission that lists one device's local users —
        // because it is the same inventory rows, merely grouped across devices the
        // caller may already see one by one; a separate permission over identical
        // data would fragment an existing grant without narrowing anything.
        endpoints.MapGet("/admin/v1/local-users/fleet-summary", FleetSummaryAsync)
            .WithName("GetLocalUserFleetSummary")
            .RequirePermission(Permissions.LocalUser.View);

        var group = endpoints.MapGroup("/admin/v1/local-users/bulk");

        group.MapPost("/", CreateAsync)
            .WithName("BulkCreateLocalUser")
            .RequirePermission(Permissions.LocalUser.Create);

        group.MapPost("/reset-password", ResetPasswordAsync)
            .WithName("BulkResetLocalUserPassword")
            .RequirePermission(Permissions.LocalUser.ResetPassword);

        // Enable shares the disable permission deliberately, exactly as the
        // per-device routes and the task catalogue pair them: they are two
        // directions of one authority over whether an account may sign in.
        group.MapPost("/disable", DisableAsync)
            .WithName("BulkDisableLocalUser")
            .RequirePermission(Permissions.LocalUser.Disable);

        group.MapPost("/enable", EnableAsync)
            .WithName("BulkEnableLocalUser")
            .RequirePermission(Permissions.LocalUser.Disable);

        group.MapPost("/delete", DeleteAsync)
            .WithName("BulkDeleteLocalUser")
            .RequirePermission(Permissions.LocalUser.Delete);

        return endpoints;
    }

    // ------------------------------------------------------------------- reads

    /// <summary>
    /// The fleet-wide account summary: every local account reported by an active,
    /// in-scope device, with the devices it exists on. The scope filter is the same
    /// one the bulk mutations resolve targets with, so the operator sees exactly the
    /// devices a subsequent bulk action could touch.
    /// </summary>
    private static async Task<IResult> FleetSummaryAsync(
        FleetAccountReadService readService,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var scoped = await scope.ScopedDeviceIdsOrNullAsync(actor.UserId, actor.OrganizationId, cancellationToken);
        return Results.Ok(await readService.GetSummaryAsync(actor.OrganizationId, scoped, cancellationToken));
    }

    public sealed record BulkCreateLocalUserRequest(
        string? Username,
        string? FullName,
        string? Description,
        string? Password,
        bool Enabled = true,
        bool MustChangePasswordAtNextLogon = false,
        string? AccountType = null,
        bool AllDevices = false,
        Guid? GroupId = null,
        IReadOnlyList<Guid>? DeviceIds = null);

    private static async Task<IResult> CreateAsync(
        BulkCreateLocalUserRequest request,
        BulkLocalAccountService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.Problem("username and password are required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Password.Length < 8)
        {
            return Results.Problem("Password must be at least 8 characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.AccountType is not (null or "StandardUser" or "Administrator"))
        {
            return Results.Problem(
                "accountType must be 'StandardUser' or 'Administrator'.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Validate the name once up front, so a bad name is a single 400 rather than a
        // rejection fanned out to every device. The per-device create re-checks it.
        if (LocalAccountManagementService.ValidateUsername(request.Username) is { } nameProblem)
        {
            return Results.Problem(nameProblem, statusCode: StatusCodes.Status400BadRequest);
        }

        if (ValidateTarget(request.AllDevices, request.GroupId, request.DeviceIds) is { } targetProblem)
        {
            return targetProblem;
        }

        // Default to Standard User. A missing or unrecognised value must never
        // silently produce an administrator.
        var administrator = request.AccountType == "Administrator";

        // Creating an administrator is the same grant as promoting one, so it demands
        // user.change_type on top of user.create — exactly the per-device rule.
        if (administrator && !HasPermission(httpContext, Permissions.LocalUser.ChangeType))
        {
            return Results.Problem(
                title: "Creating an administrator account additionally requires the permission to change account type.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await service.CreateUsersAsync(
            actor.OrganizationId, request.Username.Trim(), Blank(request.FullName), Blank(request.Description),
            request.Password, request.Enabled, request.MustChangePasswordAtNextLogon, administrator,
            request.AllDevices, request.GroupId, request.DeviceIds, scopedDeviceIds,
            actor.UserId, actor.Email, cancellationToken);

        return ToResult(result);
    }

    public sealed record BulkResetPasswordRequest(
        string? Username,
        string? Password,
        bool AllDevices = false,
        Guid? GroupId = null,
        IReadOnlyList<Guid>? DeviceIds = null);

    private static async Task<IResult> ResetPasswordAsync(
        BulkResetPasswordRequest request,
        BulkLocalAccountService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return Results.Problem("username is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        {
            return Results.Problem("Password must be at least 8 characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (ValidateTarget(request.AllDevices, request.GroupId, request.DeviceIds) is { } targetProblem)
        {
            return targetProblem;
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await service.ResetPasswordsAsync(
            actor.OrganizationId, request.Username.Trim(), request.Password,
            request.AllDevices, request.GroupId, request.DeviceIds, scopedDeviceIds,
            actor.UserId, actor.Email, cancellationToken);

        return ToResult(result);
    }

    // ----------------------------------------------- disable / enable / delete

    public sealed record BulkAccountLifecycleRequest(
        string? Username,
        bool AllDevices = false,
        Guid? GroupId = null,
        IReadOnlyList<Guid>? DeviceIds = null);

    private static Task<IResult> DisableAsync(
        BulkAccountLifecycleRequest request,
        BulkLocalAccountService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        LifecycleAsync(request, service.DisableAccountAsync, scope, httpContext, cancellationToken);

    private static Task<IResult> EnableAsync(
        BulkAccountLifecycleRequest request,
        BulkLocalAccountService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        LifecycleAsync(request, service.EnableAccountAsync, scope, httpContext, cancellationToken);

    private static Task<IResult> DeleteAsync(
        BulkAccountLifecycleRequest request,
        BulkLocalAccountService service,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
        LifecycleAsync(request, service.DeleteAccountAsync, scope, httpContext, cancellationToken);

    /// <summary>The one signature all three lifecycle operations share on the service.</summary>
    private delegate Task<BulkLocalAccountLifecycleResult> LifecycleCall(
        Guid organizationId, string username,
        bool allDevices, Guid? groupId, IReadOnlyCollection<Guid>? deviceIds,
        IReadOnlyCollection<Guid>? scopedDeviceIds,
        Guid actorId, string actorDisplay,
        CancellationToken cancellationToken);

    /// <summary>
    /// The shared lifecycle handler: validate the username and the target shape once
    /// up front, resolve the caller's device scope, and fan out. Which task each
    /// device gets — and the per-device safety rules — belong to the service.
    /// </summary>
    private static async Task<IResult> LifecycleAsync(
        BulkAccountLifecycleRequest request,
        LifecycleCall operation,
        DeviceScopeAuthorizer scope,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return Results.Problem("username is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (ValidateTarget(request.AllDevices, request.GroupId, request.DeviceIds) is { } targetProblem)
        {
            return targetProblem;
        }

        var actor = AdminActor.Required(httpContext.User);
        var scopedDeviceIds = await scope.ScopedDeviceIdsOrNullAsync(
            actor.UserId, actor.OrganizationId, cancellationToken);

        var result = await operation(
            actor.OrganizationId, request.Username.Trim(),
            request.AllDevices, request.GroupId, request.DeviceIds, scopedDeviceIds,
            actor.UserId, actor.Email, cancellationToken);

        return ToResult(result);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Exactly one of allDevices, a group, or a device selection; and a sane id ceiling.</summary>
    private static IResult? ValidateTarget(bool allDevices, Guid? groupId, IReadOnlyList<Guid>? deviceIds)
    {
        var hasAll = allDevices;
        var hasGroup = groupId is { } g && g != Guid.Empty;
        var hasDevices = deviceIds is { Count: > 0 };

        if ((hasAll ? 1 : 0) + (hasGroup ? 1 : 0) + (hasDevices ? 1 : 0) != 1)
        {
            return Results.Problem(
                "Target exactly one of allDevices, groupId or deviceIds.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (deviceIds is { Count: > MaxTargetIds })
        {
            return Results.Problem(
                $"At most {MaxTargetIds} devices may be targeted at once.", statusCode: StatusCodes.Status400BadRequest);
        }

        return null;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Whether the caller holds a permission beyond the one the route policy already enforced.</summary>
    private static bool HasPermission(HttpContext httpContext, string permissionKey) =>
        httpContext.User.HasClaim(AdminAuthenticationHandler.PermissionClaimType, permissionKey);

    private static IResult ToResult(BulkLocalAccountResult result) => result.Outcome switch
    {
        BulkLocalAccountOutcome.Completed => Results.Ok(new
        {
            result.Targeted,
            result.Queued,
            result.Rejected,
            result.Offline,
            result.Skipped,
        }),

        // Unknown or foreign group, out-of-scope, or an empty selection — one 404 for
        // every shade of "resolved to no devices you may act on".
        _ => Results.NotFound(),
    };

    private static IResult ToResult(BulkLocalAccountLifecycleResult result) => result.Outcome switch
    {
        BulkLocalAccountLifecycleOutcome.Completed => Results.Ok(new
        {
            result.Targeted,
            result.Queued,
            result.Rejected,
            result.Offline,
            result.SkippedNoAccount,
            result.SkippedOther,
        }),

        // The username resolved, somewhere in the fleet, to a protected built-in
        // account. The whole request is one clear refusal, never a partial fan-out.
        BulkLocalAccountLifecycleOutcome.ProtectedAccount =>
            Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest),

        // Same rule as create/reset: everything that resolved to no devices the
        // caller may act on is one 404.
        _ => Results.NotFound(),
    };
}
