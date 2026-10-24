using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Devices;
using EndpointPlatform.Infrastructure.Devices;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Windows local account and group management for a device.
/// </summary>
/// <remarks>
/// <para>
/// Every mutation here passes three independent gates before a task is queued:
/// the permission policy (<c>RequirePermission</c>), the device scope check
/// (<see cref="DeviceScopeAuthorizer"/>), and the domain safety rules inside
/// <see cref="LocalAccountManagementService"/>. The agent then re-checks the safety
/// rules against live Windows state. The dashboard hides controls a role lacks, but
/// that is a courtesy — removing the check here is what would matter, and it is not
/// removable without deleting one of these lines.
/// </para>
/// <para>
/// Nothing here changes Windows directly. Each endpoint queues a typed task the
/// device pulls on its next check-in, so an offline machine simply applies the
/// change when it returns rather than the operation silently failing.
/// </para>
/// </remarks>
public static class LocalAccountEndpoints
{
    public static IEndpointRouteBuilder MapLocalAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/devices/{deviceId:guid}");

        group.MapGet("/local-users", ListUsersAsync)
            .WithName("ListDeviceLocalUsers")
            .RequirePermission(Permissions.LocalUser.View);

        group.MapGet("/local-users/{sid}", GetUserAsync)
            .WithName("GetDeviceLocalUser")
            .RequirePermission(Permissions.LocalUser.View);

        // Reuses user.view rather than introducing a posture-specific
        // permission. It is the same data, on the same device, behind the same
        // scope check — a second permission over the identical rows would
        // fragment an existing grant without narrowing anything.
        group.MapGet("/local-admin-posture", GetAdminPostureAsync)
            .WithName("GetDeviceLocalAdminPosture")
            .RequirePermission(Permissions.LocalUser.View);

        group.MapGet("/local-user-profiles", ListProfilesAsync)
            .WithName("ListUserConfigurationProfiles")
            .RequirePermission(Permissions.LocalUser.View);

        group.MapGet("/local-groups", ListGroupsAsync)
            .WithName("ListDeviceLocalGroups")
            .RequirePermission(Permissions.Group.View);

        group.MapPost("/local-users", CreateUserAsync)
            .WithName("CreateDeviceLocalUser")
            .RequirePermission(Permissions.LocalUser.Create);

        group.MapPost("/local-users/{sid}/enable", (Guid deviceId, string sid, HttpContext ctx, LocalAccountManagementService svc, DeviceScopeAuthorizer scope, CancellationToken ct)
                => SetEnabledAsync(deviceId, sid, true, ctx, svc, scope, ct))
            .WithName("EnableDeviceLocalUser")
            .RequirePermission(Permissions.LocalUser.Disable);

        group.MapPost("/local-users/{sid}/disable", (Guid deviceId, string sid, HttpContext ctx, LocalAccountManagementService svc, DeviceScopeAuthorizer scope, CancellationToken ct)
                => SetEnabledAsync(deviceId, sid, false, ctx, svc, scope, ct))
            .WithName("DisableDeviceLocalUser")
            .RequirePermission(Permissions.LocalUser.Disable);

        group.MapPost("/local-users/{sid}/change-account-type", ChangeAccountTypeAsync)
            .WithName("ChangeDeviceLocalUserAccountType")
            .RequirePermission(Permissions.LocalUser.ChangeType);

        group.MapPost("/local-users/{sid}/reset-password", ResetPasswordAsync)
            .WithName("ResetDeviceLocalUserPassword")
            .RequirePermission(Permissions.LocalUser.ResetPassword);

        group.MapPost("/local-users/{sid}/force-password-change", ForcePasswordChangeAsync)
            .WithName("ForceDeviceLocalUserPasswordChange")
            .RequirePermission(Permissions.LocalUser.ForcePasswordChange);

        group.MapDelete("/local-users/{sid}", DeleteUserAsync)
            .WithName("DeleteDeviceLocalUser")
            .RequirePermission(Permissions.LocalUser.Delete);

        group.MapPost("/local-groups/{groupSid}/members", AddGroupMemberAsync)
            .WithName("AddDeviceLocalGroupMember")
            .RequirePermission(Permissions.Group.Manage);

        group.MapDelete("/local-groups/{groupSid}/members/{memberSid}", RemoveGroupMemberAsync)
            .WithName("RemoveDeviceLocalGroupMember")
            .RequirePermission(Permissions.Group.Manage);

        return endpoints;
    }

    // ------------------------------------------------------------------ reads

    /// <summary>
    /// Whether this endpoint's interactive accounts are standard users.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Derived on read from the reported accounts rather than stored. The facts
    /// it needs — group membership and enabled state — already live on
    /// <see cref="DeviceLocalUser"/>, so a cached verdict would be a second copy
    /// that can disagree with them. It is computed by
    /// <see cref="LocalAdministratorPosture"/>, the same pure function the policy
    /// engine will call, so the console and a compliance evaluation can never
    /// reach different conclusions from the same rows.
    /// </para>
    /// <para>
    /// Read-only. Milestone 11b evaluates and reports; nothing here changes an
    /// account, and specifically nothing downgrades an existing administrator.
    /// </para>
    /// </remarks>
    private static async Task<IResult> GetAdminPostureAsync(
        Guid deviceId, EndpointPlatformDbContext dbContext, DeviceScopeAuthorizer scope,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var device = await dbContext.Devices
            .AsNoTracking()
            .Where(d => d.Id == deviceId && d.OrganizationId == actor.OrganizationId)
            .Select(d => new { d.Id, d.Hostname, d.DisplayName })
            .SingleOrDefaultAsync(cancellationToken);

        if (device is null)
        {
            return Results.NotFound();
        }

        var accounts = await dbContext.DeviceLocalUsers
            .AsNoTracking()
            .Where(u => u.DeviceId == deviceId)
            .Select(u => new { u.Sid, u.Name, u.Enabled, u.IsLocalAdministrator, u.CollectedAt })
            .ToListAsync(cancellationToken);

        var posture = LocalAdministratorPosture.Evaluate(
            accounts
                .Select(a => new LocalAccountView(a.Sid, a.Name, a.Enabled, a.IsLocalAdministrator))
                .ToList());

        return Results.Ok(new
        {
            deviceId = device.Id,
            hostname = device.Hostname,
            displayName = device.DisplayName,

            compliance = posture.Compliance.ToString(),

            // Null when nothing has been reported, which is exactly the case
            // Unknown exists for. An absent timestamp is the evidence that the
            // verdict is absent too.
            lastReportedAt = accounts.Count == 0
                ? (DateTimeOffset?)null
                : accounts.Max(a => a.CollectedAt),

            // The accounts that make this endpoint non-compliant. Empty when it
            // is compliant, and empty when nothing is known.
            interactiveAdministrators = posture.InteractiveAdministrators
                .Select(f => new { sid = f.Sid, username = f.Username, enabled = f.Enabled })
                .ToList(),

            // Every account considered, including the ones set aside and why.
            // An operator looking at a Compliant machine that visibly has an
            // Administrator account needs to see the reason it was discounted.
            findings = posture.Findings
                .Select(f => new
                {
                    sid = f.Sid,
                    username = f.Username,
                    enabled = f.Enabled,
                    isAdministrator = f.IsAdministrator,
                    excludedReason = f.ExcludedReason,
                    countsAgainstCompliance = f.CountsAgainstCompliance,
                })
                .ToList(),

            // Carried in the payload rather than left to the console to know.
            // A caller acting on this verdict should see its stated scope.
            limitation =
                "Administrator rights held only through a nested group are not detected. "
                + "Membership is read from direct membership of the local Administrators group.",
        });
    }

    private static async Task<IResult> ListUsersAsync(
        Guid deviceId, EndpointPlatformDbContext dbContext, DeviceScopeAuthorizer scope,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var users = await dbContext.DeviceLocalUsers
            .AsNoTracking()
            .Where(u => u.DeviceId == deviceId)
            .OrderBy(u => u.Name)
            .Select(u => new
            {
                u.Sid,
                u.Name,
                u.FullName,
                u.Description,
                u.Enabled,
                u.PasswordRequired,
                u.PasswordExpires,
                u.LastLogon,
                u.IsLocalAdministrator,
                u.CollectedAt,
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(users);
    }

    private static async Task<IResult> GetUserAsync(
        Guid deviceId, string sid, EndpointPlatformDbContext dbContext, DeviceScopeAuthorizer scope,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var user = await dbContext.DeviceLocalUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.DeviceId == deviceId && u.Sid == sid, cancellationToken);

        if (user is null)
        {
            return Results.NotFound();
        }

        // Which local groups currently list this account, from the last inventory.
        var groups = await dbContext.DeviceLocalGroups
            .AsNoTracking()
            .Where(g => g.DeviceId == deviceId && g.MembersJson.Contains(sid))
            .Select(g => new { g.Sid, g.Name })
            .ToListAsync(cancellationToken);

        return Results.Ok(new
        {
            user.Sid,
            user.Name,
            user.FullName,
            user.Description,
            user.Enabled,
            user.PasswordRequired,
            user.PasswordExpires,
            user.LastLogon,
            user.IsLocalAdministrator,
            user.CollectedAt,
            Groups = groups,
        });
    }

    /// <summary>
    /// The baselines an operator can start from, plus which extra groups may be
    /// requested <em>on this device</em>. Served from the server so the UI cannot
    /// invent a profile.
    /// </summary>
    /// <remarks>
    /// The offered groups are the policy allow-list intersected with the groups this
    /// device last reported, because the allow-list is a ceiling and not every Windows
    /// SKU has every group in it — Home editions have no "Remote Desktop Users" or
    /// "Backup Operators". Offering a group the machine lacks invites a request that
    /// can only be half-honoured. The intersection is computed in the domain
    /// (<see cref="UserConfigurationProfiles.PermittedGroupsPresentOn"/>) so the policy
    /// and this filter cannot drift apart.
    /// </remarks>
    private static async Task<IResult> ListProfilesAsync(
        Guid deviceId, EndpointPlatformDbContext dbContext, DeviceScopeAuthorizer scope,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var deviceGroups = await dbContext.DeviceLocalGroups
            .AsNoTracking()
            .Where(g => g.DeviceId == deviceId)
            .Select(g => g.Name)
            .ToListAsync(cancellationToken);

        var offered = UserConfigurationProfiles.PermittedGroupsPresentOn(deviceGroups);
        var offeredSet = offered.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Results.Ok(new
        {
            Profiles = UserConfigurationProfiles.All.Values
                .OrderBy(p => p.GrantsAdministrator)
                .Select(p => new
                {
                    p.Key,
                    p.DisplayName,
                    p.Description,
                    AccountType = p.AccountType.ToString(),
                    p.Enabled,
                    p.MustChangePasswordAtNextLogon,

                    // A baseline group this device does not have is dropped from what
                    // the profile pre-selects, so the form never starts out asking for
                    // something the machine cannot provide.
                    AdditionalGroups = p.AdditionalGroups.Where(offeredSet.Contains).ToList(),
                    p.GrantsAdministrator,
                }),

            // What the operator may pick for THIS device.
            PermittedAdditionalGroups = offered,

            // The unfiltered policy, so the UI can explain why a group is absent
            // rather than silently omitting it.
            PolicyAdditionalGroups =
                UserConfigurationProfiles.PermittedAdditionalGroups.Order(StringComparer.OrdinalIgnoreCase),

            // False when the device has never reported its groups; the full allow-list
            // is offered in that case, and the agent skips whatever is not really there.
            DeviceGroupsKnown = deviceGroups.Count > 0,

            // Tells the UI whether to offer the Administrator option at all. The server
            // re-checks on submit regardless - this only avoids offering something that
            // would be refused.
            CanGrantAdministrator = HasPermission(httpContext, Permissions.LocalUser.ChangeType),
        });
    }

    private static async Task<IResult> ListGroupsAsync(
        Guid deviceId, EndpointPlatformDbContext dbContext, DeviceScopeAuthorizer scope,
        HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        if (!await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken))
        {
            return OutOfScope();
        }

        var groups = await dbContext.DeviceLocalGroups
            .AsNoTracking()
            .Where(g => g.DeviceId == deviceId)
            .OrderBy(g => g.Name)
            .Select(g => new
            {
                g.Sid,
                g.Name,
                g.Description,
                g.MemberCount,
                IsAdministrators = g.IsAdministratorsGroup,
                g.MembersJson,
                g.CollectedAt,
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(groups.Select(g => new
        {
            g.Sid,
            g.Name,
            g.Description,
            g.MemberCount,
            g.IsAdministrators,
            Members = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(g.MembersJson),
            g.CollectedAt,
        }));
    }

    // -------------------------------------------------------------- mutations

    private static async Task<IResult> CreateUserAsync(
        Guid deviceId, CreateLocalUserRequest request, HttpContext httpContext,
        LocalAccountManagementService service, DeviceScopeAuthorizer scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.Problem("Username and password are required.", statusCode: StatusCodes.Status400BadRequest);
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

        // Default to Standard User. A missing or unrecognised value must never
        // silently produce an administrator.
        var administrator = request.AccountType == "Administrator";

        // Creating an administrator grants the same rights as promoting one, so it
        // demands the same permission. Without this, user.create alone would be a
        // route around the change-type gate.
        if (administrator && !HasPermission(httpContext, Permissions.LocalUser.ChangeType))
        {
            return Results.Problem(
                title: "Creating an administrator account additionally requires the permission to change account type.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var context = await ResolveAsync(deviceId, httpContext, scope, cancellationToken);
        if (context is null)
        {
            return OutOfScope();
        }

        var result = await service.CreateUserAsync(
            context, request.Username, request.FullName, request.Description,
            request.Password, request.Enabled, request.MustChangePasswordAtNextLogon,
            administrator, request.AdditionalGroups, request.ProfileKey, cancellationToken);

        return ToResult(result, deviceId);
    }

    private static async Task<IResult> SetEnabledAsync(
        Guid deviceId, string sid, bool enabled, HttpContext httpContext,
        LocalAccountManagementService service, DeviceScopeAuthorizer scope, CancellationToken cancellationToken)
    {
        var context = await ResolveAsync(deviceId, httpContext, scope, cancellationToken);
        if (context is null)
        {
            return OutOfScope();
        }

        return ToResult(await service.SetUserEnabledAsync(context, sid, enabled, cancellationToken), deviceId);
    }

    private static async Task<IResult> ChangeAccountTypeAsync(
        Guid deviceId, string sid, ChangeAccountTypeRequest request, HttpContext httpContext,
        LocalAccountManagementService service, DeviceScopeAuthorizer scope, CancellationToken cancellationToken)
    {
        if (request.AccountType is not ("Administrator" or "StandardUser"))
        {
            return Results.Problem(
                "accountType must be 'Administrator' or 'StandardUser'.", statusCode: StatusCodes.Status400BadRequest);
        }

        var context = await ResolveAsync(deviceId, httpContext, scope, cancellationToken);
        if (context is null)
        {
            return OutOfScope();
        }

        var administrator = request.AccountType == "Administrator";
        return ToResult(await service.ChangeAccountTypeAsync(context, sid, administrator, cancellationToken), deviceId);
    }

    private static async Task<IResult> ResetPasswordAsync(
        Guid deviceId, string sid, ResetPasswordRequest request, HttpContext httpContext,
        LocalAccountManagementService service, DeviceScopeAuthorizer scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
        {
            return Results.Problem("Password must be at least 8 characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        var context = await ResolveAsync(deviceId, httpContext, scope, cancellationToken);
        if (context is null)
        {
            return OutOfScope();
        }

        // Resetting an administrator's password to a known value grants exactly what
        // promoting the caller would, so targeting an administrator demands
        // user.change_type on top of user.reset_password — the same extra-permission
        // mechanism as the create-an-administrator guard above. The service decides
        // (it can see the device's inventory); the endpoint only reports whether this
        // caller holds the permission.
        var callerMayTargetAdministrators = HasPermission(httpContext, Permissions.LocalUser.ChangeType);

        return ToResult(
            await service.ResetPasswordAsync(
                context, sid, request.Password, callerMayTargetAdministrators, cancellationToken),
            deviceId);
    }

    private static async Task<IResult> ForcePasswordChangeAsync(
        Guid deviceId, string sid, HttpContext httpContext,
        LocalAccountManagementService service, DeviceScopeAuthorizer scope, CancellationToken cancellationToken)
    {
        var context = await ResolveAsync(deviceId, httpContext, scope, cancellationToken);
        if (context is null)
        {
            return OutOfScope();
        }

        // Same administrator-targeting rule as reset-password; see that handler.
        var callerMayTargetAdministrators = HasPermission(httpContext, Permissions.LocalUser.ChangeType);

        return ToResult(
            await service.ForcePasswordChangeAsync(context, sid, callerMayTargetAdministrators, cancellationToken),
            deviceId);
    }

    private static async Task<IResult> DeleteUserAsync(
        Guid deviceId, string sid, HttpContext httpContext,
        LocalAccountManagementService service, DeviceScopeAuthorizer scope, CancellationToken cancellationToken)
    {
        var context = await ResolveAsync(deviceId, httpContext, scope, cancellationToken);
        if (context is null)
        {
            return OutOfScope();
        }

        return ToResult(await service.DeleteUserAsync(context, sid, cancellationToken), deviceId);
    }

    private static async Task<IResult> AddGroupMemberAsync(
        Guid deviceId, string groupSid, GroupMemberRequest request, HttpContext httpContext,
        LocalAccountManagementService service, DeviceScopeAuthorizer scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.MemberSid))
        {
            return Results.Problem("memberSid is required.", statusCode: StatusCodes.Status400BadRequest);
        }

        var context = await ResolveAsync(deviceId, httpContext, scope, cancellationToken);
        if (context is null)
        {
            return OutOfScope();
        }

        return ToResult(
            await service.ChangeGroupMembershipAsync(context, groupSid, request.MemberSid, add: true, cancellationToken),
            deviceId);
    }

    private static async Task<IResult> RemoveGroupMemberAsync(
        Guid deviceId, string groupSid, string memberSid, HttpContext httpContext,
        LocalAccountManagementService service, DeviceScopeAuthorizer scope, CancellationToken cancellationToken)
    {
        var context = await ResolveAsync(deviceId, httpContext, scope, cancellationToken);
        if (context is null)
        {
            return OutOfScope();
        }

        return ToResult(
            await service.ChangeGroupMembershipAsync(context, groupSid, memberSid, add: false, cancellationToken),
            deviceId);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Whether the caller holds a permission beyond the one the endpoint policy already
    /// enforced. Used where a single endpoint can perform a more privileged variant.
    /// </summary>
    private static bool HasPermission(HttpContext httpContext, string permissionKey) =>
        httpContext.User.HasClaim(Security.AdminAuthenticationHandler.PermissionClaimType, permissionKey);

    /// <summary>Builds the request context, or null when the device is outside the actor's scope.</summary>
    private static async Task<LocalAccountRequestContext?> ResolveAsync(
        Guid deviceId, HttpContext httpContext, DeviceScopeAuthorizer scope, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        return await scope.CanActOnDeviceAsync(actor.UserId, actor.OrganizationId, deviceId, cancellationToken)
            ? new LocalAccountRequestContext(actor.OrganizationId, deviceId, actor.UserId, actor.Email)
            : null;
    }

    /// <summary>
    /// Out-of-scope and non-existent devices are indistinguishable on the wire, so a
    /// probing operator cannot map the estate by watching which ids 403 vs 404.
    /// </summary>
    private static IResult OutOfScope() =>
        Results.Problem(
            title: "This device is not within your assigned scope.",
            statusCode: StatusCodes.Status403Forbidden);

    private static IResult ToResult(LocalAccountOperationResult result, Guid deviceId) =>
        result.Status switch
        {
            LocalAccountOperationStatus.Queued => Results.Accepted(
                $"/admin/v1/devices/{deviceId}/tasks", new { taskId = result.TaskId, status = "Queued" }),
            LocalAccountOperationStatus.Rejected => Results.Problem(
                title: result.Error, statusCode: StatusCodes.Status409Conflict),
            _ => Results.NotFound(),
        };
}

/// <param name="AccountType">"StandardUser" (default) or "Administrator".</param>
/// <param name="AdditionalGroups">Extra local groups, validated against the allow-list.</param>
/// <param name="ProfileKey">The baseline this came from, recorded for audit.</param>
public sealed record CreateLocalUserRequest(
    string Username,
    string? FullName,
    string? Description,
    string Password,
    bool Enabled = true,
    bool MustChangePasswordAtNextLogon = false,
    string? AccountType = null,
    IReadOnlyList<string>? AdditionalGroups = null,
    string? ProfileKey = null);

public sealed record ChangeAccountTypeRequest(string AccountType);

public sealed record ResetPasswordRequest(string Password);

public sealed record GroupMemberRequest(string MemberSid);
