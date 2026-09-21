using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Infrastructure.Auditing;

namespace EndpointPlatform.Api.Security;

/// <summary>
/// Confines an administrator who has not set up a second factor to the enrolment
/// screens.
/// </summary>
/// <remarks>
/// <para>
/// Multi-factor authentication is mandatory, and this is where "mandatory" is
/// enforced. It cannot be enforced at sign-in, because somebody with no
/// enrolment has nothing to be challenged with - the only way in is to let them
/// authenticate with their password and then allow nothing except enrolling.
/// </para>
/// <para>
/// <b>The residual risk, stated plainly.</b> Between the password step and a
/// completed enrolment, a session exists that is protected by the password alone.
/// Anyone holding a leaked password for a not-yet-enrolled account can therefore
/// enrol their OWN authenticator and lock the rightful owner out. This is
/// inherent to bootstrapping a second factor and every implementation has it; it
/// is bounded here by new accounts being created with a server-generated password
/// shown exactly once, and by enrolment being audited. The window closes the
/// moment the real administrator enrols.
/// </para>
/// <para>
/// Deliberately the same shape as
/// <see cref="PasswordChangeRequiredMiddleware"/>: a claim minted at
/// authentication, an exact-match allowlist, and a 403 carrying a
/// machine-readable marker. 403 and never 401 - a 401 would make the console
/// treat the session as invalid and bounce back to the sign-in page, which is an
/// infinite loop rather than a prompt.
/// </para>
/// </remarks>
public sealed class MfaEnrolmentRequiredMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <summary>
    /// The only paths an un-enrolled administrator may reach.
    /// </summary>
    /// <remarks>
    /// <c>/auth/me</c> is how the console learns who is signed in and that
    /// enrolment is owed; blocking it would leave the app unable to render the
    /// enrolment screen at all. <c>/change-password</c> is here because a new
    /// account is usually ALSO flagged for a forced password change, and blocking
    /// it would deadlock the two requirements against each other.
    /// <c>/logout</c> lets somebody who cannot proceed leave cleanly.
    /// </remarks>
    private static readonly string[] Allowed =
    [
        "/admin/v1/auth/me",
        "/admin/v1/auth/logout",
        "/admin/v1/auth/change-password",
        "/admin/v1/auth/mfa/enroll",
        "/admin/v1/auth/mfa/confirm",
    ];

    public async Task InvokeAsync(HttpContext context, AuditWriter auditWriter)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!RequiresEnrolment(context) || IsAllowed(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var actor = AdminActor.Required(context.User);

        await auditWriter.WriteImmediatelyAsync(
            actor.OrganizationId,
            AuditActorType.PlatformUser,
            actor.UserId,
            actor.Email,
            "authz.mfa_enrolment_required",
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
                title = "You must set up two-factor authentication before continuing.",
                status = StatusCodes.Status403Forbidden,
                mfaEnrolmentRequired = true,
            },
            context.RequestAborted);
    }

    private static bool RequiresEnrolment(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
        && context.User.HasClaim(
            AdminAuthenticationHandler.MfaEnrolmentRequiredClaimType,
            bool.TrueString);

    private static bool IsAllowed(PathString path) =>
        Allowed.Any(allowed => path.Equals(allowed, StringComparison.OrdinalIgnoreCase));
}
