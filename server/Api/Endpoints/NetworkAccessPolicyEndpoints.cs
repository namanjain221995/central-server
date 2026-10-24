using System.Net;
using System.Text.Json;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Auditing;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Security;
using EndpointPlatform.Infrastructure.Auditing;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Reads and writes the platform-wide network access policy - the allow-list of
/// networks the ADMIN console may be reached from.
/// </summary>
/// <remarks>
/// <para>
/// One GET and one PUT over the singleton row
/// (<see cref="NetworkAccessPolicy.SingletonId"/>). The PUT carries the whole
/// policy every time; there is no entry-by-entry editing to leave the stored list
/// and the administrator's mental model disagreeing.
/// </para>
/// <para>
/// <b>Self-lockout guard:</b> a PUT that enables the policy while the caller's own
/// current (non-loopback) address is outside every allowed range is refused with a
/// 400 naming that address. Without it, one typo locks the whole console shut until
/// someone reaches loopback on the box. A loopback caller is exempt - that is the
/// operator doing exactly the break-glass this feature documents.
/// </para>
/// <para>
/// Enforcement lives in <see cref="NetworkAccessPolicyMiddleware"/>; these
/// endpoints only edit the row, audit the change and invalidate the provider's
/// cache so a save applies on the very next request.
/// </para>
/// </remarks>
public static class NetworkAccessPolicyEndpoints
{
    public static IEndpointRouteBuilder MapNetworkAccessPolicyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/settings/network", GetAsync)
            .WithName("GetNetworkAccessPolicy")
            .RequirePermission(Permissions.Platform.SettingsManage);

        endpoints.MapPut("/admin/v1/settings/network", PutAsync)
            .WithName("PutNetworkAccessPolicy")
            .RequirePermission(Permissions.Platform.SettingsManage);

        return endpoints;
    }

    /// <param name="CallerIp">
    /// The caller's current normalized address - exactly what the middleware would
    /// compare against the ranges - so the console can preflight "would this list
    /// still include me" and show the operator what to add. <c>unknown</c> when the
    /// connection carries no address, which the middleware fails closed on.
    /// </param>
    public sealed record NetworkAccessPolicyResponse(
        bool Enabled,
        IReadOnlyList<string> AllowedCidrs,
        DateTimeOffset? UpdatedAtUtc,
        string? UpdatedBy,
        string CallerIp);

    public sealed record UpdateNetworkAccessPolicyRequest(bool Enabled, IReadOnlyList<string>? AllowedCidrs);

    private static async Task<IResult> GetAsync(
        EndpointPlatformDbContext dbContext,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var policy = await dbContext.NetworkAccessPolicies
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == NetworkAccessPolicy.SingletonId, cancellationToken);

        return Results.Ok(ToResponse(policy, httpContext));
    }

    private static async Task<IResult> PutAsync(
        UpdateNetworkAccessPolicyRequest request,
        EndpointPlatformDbContext dbContext,
        AuditWriter auditWriter,
        NetworkAccessPolicyProvider provider,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (NetworkAccessPolicy.ValidateAndNormalize(
                request.Enabled, request.AllowedCidrs, out var normalized) is { } problem)
        {
            return Results.Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        // ------------------------------------------------- self-lockout guard
        //
        // Checked against the caller's CURRENT address, normalized exactly as the
        // middleware will normalize it. Loopback is exempt (the operator on the box
        // is the break-glass and can always repair a policy); an address the server
        // cannot determine is refused like the middleware would refuse it, rather
        // than letting a save prove nothing.
        if (request.Enabled)
        {
            var caller = CallerAddress(httpContext);

            if (caller is null)
            {
                return Results.Problem(
                    "Your current address could not be determined, so this request cannot prove the new "
                    + "policy would not lock you out. Save it from the server itself (loopback) instead.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!IPAddress.IsLoopback(caller) && !Covers(normalized, caller))
            {
                return Results.Problem(
                    $"Your current address {caller} is not inside any allowed range; add it (or a range "
                    + "containing it) first — saving this would lock you out.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        var actor = AdminActor.Required(httpContext.User);
        var now = timeProvider.GetUtcNow();

        var strategy = dbContext.Database.CreateExecutionStrategy();
        var saved = await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var policy = await dbContext.NetworkAccessPolicies
                .SingleOrDefaultAsync(p => p.Id == NetworkAccessPolicy.SingletonId, cancellationToken);

            var previousState = JsonSerializer.Serialize(new
            {
                enabled = policy?.Enabled ?? false,
                allowedCidrs = policy?.AllowedCidrs ?? [],
            });

            if (policy is null)
            {
                policy = NetworkAccessPolicy.Create(
                    request.Enabled, normalized, now, actor.UserId, actor.Email);
                dbContext.NetworkAccessPolicies.Add(policy);
            }
            else
            {
                policy.Update(request.Enabled, normalized, now, actor.UserId, actor.Email);
            }

            // The audit is scoped to the actor's organization - the policy itself is
            // platform-wide, but the person who changed it belongs somewhere, and
            // that is where the accountable record should surface.
            auditWriter.Stage(
                actor.OrganizationId, AuditActorType.PlatformUser, actor.UserId, actor.Email,
                action: "platform.network_policy.update", AuditResult.Success,
                audit => audit
                    .OnTarget(
                        "network_access_policy",
                        NetworkAccessPolicy.SingletonId.ToString(),
                        "admin console network access policy")
                    .Requiring(Permissions.Platform.SettingsManage)
                    .WithStateChange(
                        previousState,
                        JsonSerializer.Serialize(new
                        {
                            enabled = policy.Enabled,
                            allowedCidrs = policy.AllowedCidrs,
                        })));

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return policy;
        });

        // After commit, so a rolled-back save never flushes a working cache - and a
        // committed one applies on the very next request instead of on cache expiry.
        provider.Invalidate();

        return Results.Ok(ToResponse(saved, httpContext));
    }

    private static NetworkAccessPolicyResponse ToResponse(NetworkAccessPolicy? policy, HttpContext httpContext) =>
        new(
            Enabled: policy?.Enabled ?? false,
            AllowedCidrs: policy?.AllowedCidrs ?? [],
            UpdatedAtUtc: policy?.UpdatedAtUtc,
            UpdatedBy: policy?.UpdatedByEmail,
            CallerIp: CallerAddress(httpContext)?.ToString() ?? "unknown");

    /// <summary>The address the middleware would compare: RemoteIpAddress, unmapped.</summary>
    private static IPAddress? CallerAddress(HttpContext httpContext)
    {
        var remote = httpContext.Connection.RemoteIpAddress;
        return remote is null ? null : NetworkAccessPolicy.NormalizeAddress(remote);
    }

    private static bool Covers(IReadOnlyList<string> normalizedCidrs, IPAddress address)
    {
        foreach (var cidr in normalizedCidrs)
        {
            if (IPNetwork.TryParse(cidr, out var network) && network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}
