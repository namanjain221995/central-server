using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Infrastructure.Auditing;
using Microsoft.AspNetCore.Http;

namespace EndpointPlatform.Api.Security;

/// <summary>
/// Stops an administrator who is still holding a server-generated password from
/// doing anything except replacing it.
/// </summary>
/// <remarks>
/// <para>
/// Enforced on the SERVER, not in the console. The temporary password is a fully
/// valid credential: signing in with it returns a session token in the response
/// body, and <see cref="AdminAuthenticationHandler"/> accepts a bearer token in
/// preference to the cookie. So a dashboard-only gate would be skipped by one
/// <c>curl</c>, with the full authority of the assigned role behind it — every
/// permission in the catalogue, for a Super Administrator.
/// </para>
/// <para>
/// Middleware rather than an authorization requirement, because a requirement only
/// runs on endpoints that HAVE a policy. Three routes here use a bare
/// <c>RequireAuthorization()</c> with no permission, and any route added in future
/// might too; those would pass straight through. With an explicit allowlist the
/// default for anything new is denied.
/// </para>
/// <para>
/// Answers <b>403</b>, never 401. The console treats any 401 as an expired session
/// and returns the user to sign-in — which would make a flagged account sign in,
/// get bounced, sign in again, and loop with no way to reach the change-password
/// screen. The session here is perfectly valid; the caller simply may not do
/// anything else yet, which is what 403 means.
/// </para>
/// </remarks>
public sealed class PasswordChangeRequiredMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <summary>
    /// The only paths a flagged administrator may reach.
    /// </summary>
    /// <remarks>
    /// <c>/auth/me</c> is not optional: the console probes it on first load to learn
    /// who is signed in, and blocking it would leave the app rendering the sign-in
    /// page forever, unable to ever reach the change-password screen. <c>/logout</c>
    /// is here so somebody who cannot proceed can still leave cleanly.
    /// </remarks>
    private static readonly string[] Allowed =
    [
        "/admin/v1/auth/me",
        "/admin/v1/auth/change-password",
        "/admin/v1/auth/logout",
    ];

    public async Task InvokeAsync(HttpContext context, AuditWriter auditWriter)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!RequiresPasswordChange(context) || IsAllowed(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var actor = AdminActor.Required(context.User);

        // Audited so a refusal is visible, but deliberately not on every blocked
        // request from a looping client: the console gates itself once it has read
        // the flag from /auth/me, so in practice these rows come from direct API
        // callers, which is exactly the case worth recording.
        await auditWriter.WriteImmediatelyAsync(
            actor.OrganizationId,
            AuditActorType.PlatformUser,
            actor.UserId,
            actor.Email,
            "authz.password_change_required",
            AuditResult.Denied,
            audit => audit.OnTarget(
                "platform_user", actor.UserId.ToString(), $"{context.Request.Method} {context.Request.Path}"),
            context.RequestAborted);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsJsonAsync(
            new
            {
                type = "https://datatracker.ietf.org/doc/html/rfc9110#section-15.5.4",
                title = "You must change your password before continuing.",
                status = StatusCodes.Status403Forbidden,
                // A machine-readable marker, so a client can route to its own
                // change-password screen instead of pattern-matching the title.
                passwordChangeRequired = true,
            },
            context.RequestAborted);
    }

    /// <summary>
    /// Whether the authenticated caller carries the must-change claim.
    /// </summary>
    /// <remarks>
    /// Read from the claim minted at authentication rather than re-queried here, so
    /// this costs nothing per request. The claim is refreshed on every request by
    /// the handler, which resolves the user afresh.
    /// </remarks>
    private static bool RequiresPasswordChange(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
        && context.User.HasClaim(
            AdminAuthenticationHandler.PasswordChangeRequiredClaimType,
            bool.TrueString);

    private static bool IsAllowed(PathString path) =>
        Allowed.Any(allowed => path.Equals(allowed, StringComparison.OrdinalIgnoreCase));
}
