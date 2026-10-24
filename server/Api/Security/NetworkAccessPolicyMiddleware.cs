using System.Net;
using EndpointPlatform.Domain.Security;
using EndpointPlatform.Infrastructure.Security;

namespace EndpointPlatform.Api.Security;

/// <summary>
/// Enforces the platform-wide network access policy: when enabled, only requests
/// from an allowed range (or loopback) reach the Admin API at all.
/// </summary>
/// <remarks>
/// <para>
/// Runs BEFORE authentication, deliberately: the point of the policy is that a
/// blocked network cannot even see the login form, let alone guess at it. That is
/// also why the policy is platform-wide - there is no identity here yet to scope a
/// per-organization rule by.
/// </para>
/// <para>
/// <b>Loopback always passes.</b> That is the documented break-glass: an operator
/// on the box (typically over an SSH tunnel) can always reach the API to fix a bad
/// policy, and the deploy script's health wait against 127.0.0.1 keeps working.
/// Health endpoints additionally bypass by path, so an orchestrator probing through
/// a non-loopback interface does not read a policy decision as instance death.
/// </para>
/// <para>
/// The client address is <see cref="ConnectionInfo.RemoteIpAddress"/> - the same
/// source the login rate limiter and the audit trail use. The forwarded-headers
/// middleware (enabled process-wide in deployment) has already rewritten it by the
/// time this runs, and an IPv4-mapped IPv6 address is unmapped before matching. A
/// missing address fails CLOSED: behind the reverse proxy it is never missing, so
/// an absent address is not a caller this policy can vouch for.
/// </para>
/// <para>
/// Refusals are logged, not audited: this fires per request for unauthenticated
/// traffic, and letting it write rows would hand anyone on a blocked network a pen
/// that grows the audit table.
/// </para>
/// <para>
/// ADMIN API ONLY. The Agent API must never gate devices by network - the fleet
/// calls in from arbitrary home networks.
/// </para>
/// </remarks>
public sealed class NetworkAccessPolicyMiddleware(
    RequestDelegate next,
    NetworkAccessPolicyProvider provider,
    ILogger<NetworkAccessPolicyMiddleware> logger)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    private readonly NetworkAccessPolicyProvider _provider =
        provider ?? throw new ArgumentNullException(nameof(provider));

    private readonly ILogger<NetworkAccessPolicyMiddleware> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task InvokeAsync(HttpContext context)
    {
        var snapshot = await _provider.GetSnapshotAsync(context.RequestAborted);

        if (!snapshot.Enabled || IsExemptPath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var remote = context.Connection.RemoteIpAddress;

        if (remote is not null)
        {
            var address = NetworkAccessPolicy.NormalizeAddress(remote);

            if (IPAddress.IsLoopback(address) || snapshot.IsAllowed(address))
            {
                await _next(context);
                return;
            }
        }

        // A log line, never an audit row - see the class remarks.
        _logger.LogWarning(
            "Network access policy blocked {ClientIp} from {Path}.",
            remote?.ToString() ?? "(no remote address)",
            context.Request.Path);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(
            new
            {
                title = "Forbidden",
                detail = "This console is restricted to approved networks.",
                status = StatusCodes.Status403Forbidden,
            },
            options: null,
            contentType: "application/problem+json",
            context.RequestAborted);
    }

    /// <summary>
    /// <c>/health/*</c> (and the conventional <c>/healthz</c>) stay reachable from
    /// anywhere: a probe is not a person, and an orchestrator must not interpret a
    /// policy refusal as a dead instance.
    /// </summary>
    private static bool IsExemptPath(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/healthz", StringComparison.OrdinalIgnoreCase);
}
