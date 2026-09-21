using EndpointPlatform.Api.Security;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Sign-in, sign-out and current-principal endpoints for the Admin API.
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Rate-limit policy name applied to the sign-in endpoint.</summary>
    public const string LoginRateLimitPolicy = "auth-login";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/admin/v1/auth");

        group.MapPost("/login", LoginAsync)
            .WithName("AdminLogin")
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimitPolicy);

        group.MapPost("/logout", LogoutAsync)
            .WithName("AdminLogout")
            // Deliberately anonymous: signing out with an expired/invalid session
            // must still clear the cookie rather than bounce with a 401.
            .AllowAnonymous();

        group.MapGet("/me", Me)
            .WithName("AdminMe")
            .RequireAuthorization();

        // Authenticated, but behind no permission: changing your own password is
        // not an administrative act over someone else, and gating it would mean a
        // user could be locked out of securing their own account by a role change.
        // The current password is re-verified in the service regardless of session.
        group.MapPost("/change-password", ChangePasswordAsync)
            .WithName("AdminChangePassword")
            .RequireAuthorization();

        return endpoints;
    }

    public sealed record LoginRequest(string Email, string Password);

    /// <param name="SessionToken">
    /// The same opaque token the HttpOnly cookie carries, for non-browser clients
    /// that authenticate with <c>Authorization: Bearer</c> (CLIs, tests). The
    /// dashboard ignores this field and rides the cookie; an XSS attacker gains
    /// nothing from the field's existence that the cookie session does not already
    /// give them in-page.
    /// </param>
    public sealed record LoginResponse(
        Guid UserId,
        string Email,
        string DisplayName,
        DateTimeOffset SessionExpiresAt,
        IReadOnlyList<string> Permissions,
        string SessionToken);

    /// <summary>
    /// Changes the signed-in administrator's own password.
    /// </summary>
    /// <remarks>
    /// Every failure returns the same shape and reveals nothing about the
    /// account: a wrong current password and a locked account are indistinguishable
    /// to the caller, matching the sign-in path.
    /// </remarks>
    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext httpContext,
        AdminAuthService authService,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var actor = AdminActor.Required(httpContext.User);

        // Checked before anything reaches the service: a mistyped confirmation is
        // a form error, not an authentication event, and must not count towards
        // the lockout that guards the current-password check.
        if (!string.Equals(request.NewPassword, request.ConfirmPassword, StringComparison.Ordinal))
        {
            return Results.Problem(
                "The new password and its confirmation do not match.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var outcome = await authService.ChangePasswordAsync(
            actor.UserId, request.CurrentPassword ?? string.Empty, request.NewPassword ?? string.Empty,
            cancellationToken);

        if (outcome.Success)
        {
            // The caller's own session is now dead. Clear the cookie so the
            // browser is not left presenting a token that will only ever 401.
            DeleteSessionCookie(httpContext, environment);

            return Results.Ok(new
            {
                changed = true,
                sessionsRevoked = outcome.SessionsRevoked,
                message = "Password changed. All sessions have been signed out, including this one.",
            });
        }

        return outcome.Error switch
        {
            ChangePasswordError.WeakPassword => Results.Problem(
                outcome.Message ?? "The new password does not meet the password policy.",
                statusCode: StatusCodes.Status400BadRequest),

            ChangePasswordError.SameAsCurrent => Results.Problem(
                "The new password must be different from the current one.",
                statusCode: StatusCodes.Status400BadRequest),

            // Deliberately identical for a wrong password and a locked account.
            ChangePasswordError.CurrentPasswordIncorrect => Results.Problem(
                "The current password is incorrect.",
                statusCode: StatusCodes.Status400BadRequest),

            _ => Results.Problem(
                "This account's password cannot be changed here.",
                statusCode: StatusCodes.Status403Forbidden),
        };
    }

    private static async Task<IResult> LoginAsync(
        [FromBody] LoginRequest request,
        AdminAuthService authService,
        HttpContext httpContext,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254
            || string.IsNullOrEmpty(request.Password) || request.Password.Length > 512)
        {
            return Results.Problem(title: "Sign-in failed.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var outcome = await authService.SignInAsync(
            request.Email,
            request.Password,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            cancellationToken);

        if (!outcome.Success)
        {
            // Uniform response for unknown account / wrong password / disabled /
            // locked. The audit trail carries the distinction.
            return Results.Problem(title: "Sign-in failed.", statusCode: StatusCodes.Status401Unauthorized);
        }

        AppendSessionCookie(httpContext, outcome.Token!, outcome.ExpiresAt, environment);

        var user = outcome.User!;

        return Results.Ok(new LoginResponse(
            user.Id,
            user.Email,
            user.DisplayName,
            outcome.ExpiresAt,
            outcome.Permissions,
            outcome.Token!));
    }

    private static async Task<IResult> LogoutAsync(
        AdminAuthService authService,
        HttpContext httpContext,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var token = ReadToken(httpContext);

        if (token is not null)
        {
            await authService.SignOutAsync(token, cancellationToken);
        }

        DeleteSessionCookie(httpContext, environment);
        return Results.NoContent();
    }

    private static IResult Me(HttpContext httpContext)
    {
        var actor = AdminActor.Required(httpContext.User);

        var permissions = httpContext.User
            .FindAll(AdminAuthenticationHandler.PermissionClaimType)
            .Select(c => c.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return Results.Ok(new
        {
            actor.UserId,
            actor.Email,
            DisplayName = httpContext.User.Identity?.Name ?? actor.Email,
            Permissions = permissions,
            // The console gates itself on this. The server gates independently in
            // PasswordChangeRequiredMiddleware, because a bearer token never loads
            // the console at all; this field is for the experience, not the security.
            MustChangePassword = httpContext.User.HasClaim(
                AdminAuthenticationHandler.PasswordChangeRequiredClaimType, bool.TrueString),
        });
    }

    private static string? ReadToken(HttpContext httpContext)
    {
        var authorization = httpContext.Request.Headers.Authorization.ToString();

        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        return httpContext.Request.Cookies.TryGetValue(
            AdminAuthenticationHandler.SessionCookieName, out var cookie)
            ? cookie
            : null;
    }

    private static void AppendSessionCookie(
        HttpContext httpContext,
        string token,
        DateTimeOffset expiresAt,
        IHostEnvironment environment)
    {
        // __Host- prefix requires Secure + Path=/ + no Domain, which pins the
        // cookie to exactly this host. In Development (plain HTTP on localhost)
        // browsers accept Secure cookies from localhost, so the same settings work.
        httpContext.Response.Cookies.Append(
            AdminAuthenticationHandler.SessionCookieName,
            token,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = expiresAt,
                IsEssential = true,
            });
    }

    private static void DeleteSessionCookie(HttpContext httpContext, IHostEnvironment environment)
    {
        httpContext.Response.Cookies.Delete(
            AdminAuthenticationHandler.SessionCookieName,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
            });
    }
}

/// <param name="CurrentPassword">
/// Re-verified server-side. A live session proves who signed in; it does not
/// prove who is at the keyboard now.
/// </param>
/// <param name="ConfirmPassword">
/// Compared in the endpoint. Sent rather than checked only in the browser,
/// because the front end is never the boundary -- a mistyped password that
/// reached the hasher would lock the account's owner out of their own account.
/// </param>
public sealed record ChangePasswordRequest(
    string? CurrentPassword,
    string? NewPassword,
    string? ConfirmPassword);
