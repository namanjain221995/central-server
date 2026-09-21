using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Administering the platform's own administrator accounts.
/// </summary>
/// <remarks>
/// <para>
/// Reading the list requires <see cref="Permissions.Platform.UserView"/>; every
/// mutation requires <see cref="Permissions.Platform.UserManage"/>, which by design
/// only Super Administrator holds. IT Administrator is deliberately excluded from
/// both — managing endpoints and deciding who may manage endpoints are separate
/// duties, and <c>SystemRoleTests</c> asserts that separation.
/// </para>
/// <para>
/// Creating an administrator and resetting a password both return a
/// server-generated plaintext password EXACTLY ONCE, in the response body. It is
/// never stored, logged or audited. The invariants that a permission cannot
/// express — no self-targeting, never removing the last usable Super Administrator,
/// no granting authority the caller lacks — live in
/// <see cref="PlatformUserService"/> so they hold however the service is called.
/// </para>
/// </remarks>
public static class PlatformUserEndpoints
{
    public static IEndpointRouteBuilder MapPlatformUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/platform-users");

        group.MapGet("/", ListAsync)
            .WithName("ListPlatformUsers")
            .RequirePermission(Permissions.Platform.UserView);

        group.MapPost("/", CreateAsync)
            .WithName("CreatePlatformUser")
            .RequirePermission(Permissions.Platform.UserManage);

        group.MapPost("/{userId:guid}/disable", DisableAsync)
            .WithName("DisablePlatformUser")
            .RequirePermission(Permissions.Platform.UserManage);

        group.MapPost("/{userId:guid}/enable", EnableAsync)
            .WithName("EnablePlatformUser")
            .RequirePermission(Permissions.Platform.UserManage);

        group.MapPost("/{userId:guid}/reset-password", ResetPasswordAsync)
            .WithName("ResetPlatformUserPassword")
            .RequirePermission(Permissions.Platform.UserManage);

        return endpoints;
    }

    /// <param name="Email">Must be unique across the whole platform; see the service.</param>
    /// <param name="RoleKey">One of the built-in role keys from <c>/admin/v1/access-levels</c>.</param>
    public sealed record CreatePlatformUserRequest(string? Email, string? DisplayName, string? RoleKey);

    /// <param name="Password">
    /// Shown EXACTLY ONCE. The server keeps only a hash and cannot reproduce this
    /// value; if it is lost, reset the password to issue a new one.
    /// </param>
    /// <param name="Warning">
    /// Carried in the body rather than left to the console to remember, so any
    /// client of this API states the same thing.
    /// </param>
    public sealed record CreatePlatformUserResponse(Guid UserId, string Password, string Warning);

    private const string ShownOnceWarning =
        "This password is shown once and cannot be retrieved again. The administrator "
        + "must change it the first time they sign in.";

    private static async Task<IResult> ListAsync(
        HttpContext httpContext, PlatformUserService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        return Results.Ok(await service.ListAsync(actor.OrganizationId, cancellationToken));
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreatePlatformUserRequest request,
        HttpContext httpContext,
        PlatformUserService service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email)
            || string.IsNullOrWhiteSpace(request.DisplayName)
            || string.IsNullOrWhiteSpace(request.RoleKey))
        {
            return Results.Problem(
                title: "Email, display name and role are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var actor = AdminActor.Required(httpContext.User);

        var result = await service.CreateAsync(
            actor.OrganizationId, actor.UserId, actor.Email,
            request.Email!.Trim(), request.DisplayName!.Trim(), request.RoleKey!, cancellationToken);

        return result.Status == PlatformUserChangeStatus.Success
            ? Results.Created(
                $"/admin/v1/platform-users/{result.UserId}",
                new CreatePlatformUserResponse(result.UserId!.Value, result.GeneratedPassword!, ShownOnceWarning))
            : Failure(result.Status);
    }

    private static async Task<IResult> ResetPasswordAsync(
        Guid userId, HttpContext httpContext, PlatformUserService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        var result = await service.ResetPasswordAsync(
            actor.OrganizationId, actor.UserId, actor.Email, userId, cancellationToken);

        return result.Status == PlatformUserChangeStatus.Success
            ? Results.Ok(new CreatePlatformUserResponse(userId, result.GeneratedPassword!, ShownOnceWarning))
            : Failure(result.Status);
    }

    private static async Task<IResult> DisableAsync(
        Guid userId, HttpContext httpContext, PlatformUserService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        return Result(
            await service.DisableAsync(actor.OrganizationId, actor.UserId, actor.Email, userId, cancellationToken));
    }

    private static async Task<IResult> EnableAsync(
        Guid userId, HttpContext httpContext, PlatformUserService service, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);

        return Result(
            await service.EnableAsync(actor.OrganizationId, actor.UserId, actor.Email, userId, cancellationToken));
    }

    private static IResult Result(PlatformUserChangeStatus status) =>
        status == PlatformUserChangeStatus.Success ? Results.NoContent() : Failure(status);

    /// <summary>
    /// One place deciding what each refusal means on the wire.
    /// </summary>
    /// <remarks>
    /// Every refusal says plainly why, because all of these are reached only by an
    /// already-authorized administrator acting on their own organization. Nothing
    /// here is reachable by an anonymous prober, so there is no information to
    /// withhold — and a vague refusal on "you cannot disable the last Super
    /// Administrator" would read as a malfunction.
    /// </remarks>
    private static IResult Failure(PlatformUserChangeStatus status) => status switch
    {
        PlatformUserChangeStatus.NotFound => Results.NotFound(),

        PlatformUserChangeStatus.EmailInUse => Results.Problem(
            title: "That e-mail address already belongs to an administrator.",
            statusCode: StatusCodes.Status409Conflict),

        PlatformUserChangeStatus.UnknownRole => Results.Problem(
            title: "That is not a known access level.",
            statusCode: StatusCodes.Status400BadRequest),

        PlatformUserChangeStatus.RoleExceedsCallerAuthority => Results.Problem(
            title: "You cannot grant an access level that includes permissions you do not hold yourself.",
            statusCode: StatusCodes.Status403Forbidden),

        PlatformUserChangeStatus.CannotTargetSelf => Results.Problem(
            title: "You cannot perform this action on your own account.",
            statusCode: StatusCodes.Status409Conflict),

        PlatformUserChangeStatus.WouldRemoveLastSuperAdministrator => Results.Problem(
            title: "This is the last Super Administrator that can still sign in. "
                   + "Create or enable another one first, or nobody will be able to administer the platform.",
            statusCode: StatusCodes.Status409Conflict),

        PlatformUserChangeStatus.SystemAccount => Results.Problem(
            title: "A built-in system account cannot be changed here.",
            statusCode: StatusCodes.Status409Conflict),

        _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError),
    };
}
