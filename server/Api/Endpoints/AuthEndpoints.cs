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

        // --- multi-factor ----------------------------------------------------
        //
        // Anonymous, and rate limited like the login route. The caller holds a
        // challenge ticket rather than a session, so there is no principal to
        // authorise; the ticket is the authority, it expires in five minutes and
        // it burns after five wrong codes.
        group.MapPost("/mfa/verify", VerifyMfaAsync)
            .WithName("AdminVerifyMfa")
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimitPolicy);

        // Authenticated but behind no permission, for the same reason as
        // change-password: enrolling your own second factor is not an
        // administrative act over anybody else. MfaEnrolmentRequiredMiddleware
        // allowlists these two so a not-yet-enrolled account can reach them.
        group.MapPost("/mfa/enroll", BeginMfaEnrolmentAsync)
            .WithName("AdminBeginMfaEnrolment")
            .RequireAuthorization();

        group.MapPost("/mfa/confirm", ConfirmMfaEnrolmentAsync)
            .WithName("AdminConfirmMfaEnrolment")
            .RequireAuthorization();

        // Regenerating recovery codes requires a fully enrolled session, so it is
        // NOT in the enrolment allowlist.
        group.MapPost("/mfa/recovery-codes", RegenerateRecoveryCodesAsync)
            .WithName("AdminRegenerateRecoveryCodes")
            .RequireAuthorization();

        return endpoints;
    }

    public sealed record LoginRequest(string Email, string Password);

    /// <param name="MfaRequired">
    /// Always true. Present so the dashboard can branch on one field rather than
    /// inferring the case from which other fields happen to be absent.
    /// </param>
    /// <param name="ChallengeToken">
    /// The ticket to present at <c>/mfa/verify</c>. It is not a session token: it
    /// authenticates nothing, carries no permissions, and is accepted at exactly
    /// one route.
    /// </param>
    public sealed record MfaChallengeResponse(
        bool MfaRequired, string ChallengeToken, DateTimeOffset ChallengeExpiresAt);

    public sealed record VerifyMfaRequest(string ChallengeToken, string Code);

    public sealed record BeginMfaEnrolmentResponse(string Secret, string OtpAuthUri, string QrCodeSvg);

    public sealed record ConfirmMfaRequest(string Code);

    /// <param name="RecoveryCodes">Shown exactly once. The server keeps only hashes.</param>
    public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

    /// <summary>
    /// Completes a sign-in that was waiting on a second factor.
    /// </summary>
    /// <remarks>
    /// Every failure is the same 401 with the same title, whatever went wrong -
    /// an unknown ticket, an expired one, a wrong code, a replayed code or an
    /// exhausted attempt count. Distinguishing them would tell an attacker
    /// whether the ticket was real and whether their guessing was making
    /// progress. The audit trail records which it was.
    /// </remarks>
    private static async Task<IResult> VerifyMfaAsync(
        [FromBody] VerifyMfaRequest request,
        AdminAuthService authService,
        MfaService mfaService,
        HttpContext httpContext,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ChallengeToken) || request.ChallengeToken.Length > 128
            || string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 64)
        {
            return Results.Problem(title: "Sign-in failed.", statusCode: StatusCodes.Status401Unauthorized);
        }

        var outcome = await authService.CompleteMfaSignInAsync(
            request.ChallengeToken,
            request.Code,
            httpContext.Connection.RemoteIpAddress?.ToString(),
            httpContext.Request.Headers.UserAgent.ToString(),
            mfaService,
            cancellationToken);

        if (!outcome.Success)
        {
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

    /// <summary>
    /// Issues a TOTP secret for the signed-in administrator and renders its QR code.
    /// </summary>
    /// <remarks>
    /// Refused once enrolment is confirmed. Replacing a working second factor from
    /// a live session would let anyone who borrowed an unlocked browser swap the
    /// authenticator for their own; that path is a reset by another administrator,
    /// which is audited and rotates the security stamp.
    /// </remarks>
    private static async Task<IResult> BeginMfaEnrolmentAsync(
        MfaService mfaService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.FromClaims(httpContext.User);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        var start = await mfaService.BeginEnrolmentAsync(actor.UserId, cancellationToken);

        if (start is null)
        {
            return Results.Problem(
                title: "Multi-factor authentication is already set up for this account.",
                detail: "Ask another administrator to reset it if you have lost your authenticator.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // The secret is in this body, so it must not be cached anywhere.
        httpContext.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

        return Results.Ok(new BeginMfaEnrolmentResponse(
            start.Secret, start.Uri, TotpQrCode.ToSvg(start.Uri)));
    }

    /// <summary>Confirms enrolment with a code and returns the recovery codes once.</summary>
    private static async Task<IResult> ConfirmMfaEnrolmentAsync(
        [FromBody] ConfirmMfaRequest request,
        MfaService mfaService,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.FromClaims(httpContext.User);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        var result = await mfaService.ConfirmEnrolmentAsync(actor.UserId, request.Code, cancellationToken);

        if (!result.Success)
        {
            return result.Error switch
            {
                MfaError.NoEnrolmentInProgress => Results.Problem(
                    title: "There is no enrolment in progress.",
                    detail: "Start again from the beginning.",
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.Problem(
                    title: "That code was not correct.",
                    detail: "Check your authenticator app and try the current code.",
                    statusCode: StatusCodes.Status400BadRequest),
            };
        }

        httpContext.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

        return Results.Ok(new RecoveryCodesResponse(result.RecoveryCodes));
    }

    /// <summary>Replaces the recovery codes and returns the new set once.</summary>
    /// <remarks>
    /// Requires a confirmed second factor, so a half-enrolled account cannot mint
    /// bypass codes for itself.
    /// </remarks>
    private static async Task<IResult> RegenerateRecoveryCodesAsync(
        MfaService mfaService,
        EndpointPlatform.Infrastructure.Persistence.EndpointPlatformDbContext dbContext,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.FromClaims(httpContext.User);
        if (actor is null)
        {
            return Results.Unauthorized();
        }

        var user = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .SingleOrDefaultAsync(dbContext.PlatformUsers, u => u.Id == actor.UserId, cancellationToken);

        if (user is null || !user.HasConfirmedMfa)
        {
            return Results.Problem(
                title: "Multi-factor authentication is not set up for this account.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var codes = await mfaService.ReplaceRecoveryCodesAsync(actor.UserId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        httpContext.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

        return Results.Ok(new RecoveryCodesResponse(codes));
    }

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

        // The password was right and a second factor is owed. NOT a success: no
        // cookie is set, no permissions are returned, and the ticket authenticates
        // nothing except an attempt at /mfa/verify. 200 rather than 401 because
        // the caller has something to do next, and the dashboard needs to tell
        // "wrong password" apart from "now show the code screen".
        if (outcome.MfaRequired)
        {
            return Results.Ok(new MfaChallengeResponse(true, outcome.MfaChallengeToken!, outcome.ExpiresAt));
        }

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
            // Same contract as MustChangePassword: the console renders the
            // enrolment interstitial from this, and
            // MfaEnrolmentRequiredMiddleware enforces it independently for callers
            // that never load the console.
            MfaEnrolmentRequired = httpContext.User.HasClaim(
                AdminAuthenticationHandler.MfaEnrolmentRequiredClaimType, bool.TrueString),
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
