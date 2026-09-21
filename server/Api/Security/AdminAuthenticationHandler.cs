using System.Security.Claims;
using System.Text.Encodings.Web;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.Api.Security;

/// <summary>
/// Authenticates Admin API requests from the opaque session token.
/// </summary>
/// <remarks>
/// <para>
/// The token is read from the HttpOnly session cookie (how the dashboard
/// authenticates — script cannot read it, so XSS cannot exfiltrate it) or from
/// <c>Authorization: Bearer</c> (for tooling and tests). Cookie flow CSRF
/// defences: SameSite=Strict on the cookie, plus mutations requiring the
/// <c>X-Requested-With</c> header, enforced in the endpoint layer.
/// </para>
/// <para>
/// On success the principal carries one claim per resolved permission; the
/// authorization policies check those claims. Permissions are resolved fresh per
/// request, and the session's security-stamp snapshot means any role or
/// credential change invalidates the session outright.
/// </para>
/// </remarks>
public sealed class AdminAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "AdminSession";

    public const string SessionCookieName = "__Host-epadmin";

    public const string PermissionClaimType = "epp:permission";
    public const string UserIdClaimType = "epp:user_id";
    public const string OrganizationClaimType = "epp:organization_id";

    /// <summary>
    /// Present and "True" while the administrator must replace a server-generated
    /// password. Read by <see cref="PasswordChangeRequiredMiddleware"/>.
    /// </summary>
    public const string PasswordChangeRequiredClaimType = "epp:password_change_required";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ExtractToken();

        if (token is null)
        {
            return AuthenticateResult.NoResult();
        }

        // Resolved from RequestServices (not constructor) because the handler is
        // created per request by the authentication middleware but the service is
        // scoped with the DbContext.
        var authService = Context.RequestServices.GetRequiredService<AdminAuthService>();

        var admin = await authService.ValidateSessionAsync(token, Context.RequestAborted);

        if (admin is null)
        {
            return AuthenticateResult.Fail("Session is not valid.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, admin.UserId.ToString()),
            new(ClaimTypes.Name, admin.Email),
            new(UserIdClaimType, admin.UserId.ToString()),
            new(OrganizationClaimType, admin.OrganizationId.ToString()),
        };

        // Added only when true, so the ordinary case carries no extra claim.
        if (admin.MustChangePassword)
        {
            claims.Add(new Claim(PasswordChangeRequiredClaimType, bool.TrueString));
        }

        claims.AddRange(admin.Permissions.Select(p => new Claim(PermissionClaimType, p)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        return AuthenticateResult.Success(ticket);
    }

    private string? ExtractToken()
    {
        // Bearer first: an explicit header wins over ambient cookie state.
        var authorization = Request.Headers.Authorization.ToString();

        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var value = authorization["Bearer ".Length..].Trim();
            return value.Length is > 0 and <= 128 ? value : null;
        }

        if (Request.Cookies.TryGetValue(SessionCookieName, out var cookie)
            && !string.IsNullOrWhiteSpace(cookie)
            && cookie.Length <= 128)
        {
            return cookie;
        }

        return null;
    }
}
