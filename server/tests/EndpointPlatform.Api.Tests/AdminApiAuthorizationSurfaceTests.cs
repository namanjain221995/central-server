using System.Net;
using EndpointPlatform.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The Admin API's authorization surface, read from the real route table: every
/// endpoint declares who may call it, and the anonymous set is exactly the one
/// written down here.
/// </summary>
/// <remarks>
/// <para>
/// The host runs a deny-by-default fallback policy, so an endpoint that forgets
/// <c>.RequirePermission(...)</c> is refused rather than silently anonymous. These
/// tests are the second half of that: they make the intent visible at review time.
/// A new endpoint without any authorization metadata fails the first test, and a
/// new <c>.AllowAnonymous()</c> fails the second until the allowlist below is
/// edited -- so opening a route to the internet is always a deliberate, reviewed
/// change to this file, never a side effect.
/// </para>
/// <para>
/// No database: the route table and the policy provider are host wiring, and the
/// collection's factory points PostgreSQL and Redis at unreachable addresses on
/// purpose.
/// </para>
/// </remarks>
[Collection(AdminApiCollection.Name)]
public sealed class AdminApiAuthorizationSurfaceTests(AdminApiFactory factory)
{
    private readonly AdminApiFactory _factory = factory;

    /// <summary>
    /// Every route reachable without a session. Edit only with a reason that would
    /// survive review: each entry is reachable by anyone on the internet.
    /// </summary>
    private static readonly string[] ExpectedAnonymousEndpoints =
    [
        // The service banner the dashboard's status card reads.
        "GET /",

        // Signing in, the second factor of signing in, and signing out (which must
        // work with an expired session so a stale client can still leave cleanly).
        "POST /admin/v1/auth/login",
        "POST /admin/v1/auth/logout",
        "POST /admin/v1/auth/mfa/verify",

        // The container healthcheck and the deploy script's health wait call these
        // with no credentials. They return check names and statuses only.
        "* /health/live",
        "* /health/ready",
    ];

    private IReadOnlyList<Endpoint> MappedEndpoints()
    {
        // The route table is complete once the host has started; creating a client
        // is what starts it.
        using var client = _factory.CreateClient();
        return _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
    }

    /// <summary>"METHODS /pattern", with "*" when the route accepts any method.</summary>
    private static string Describe(Endpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        var verb = methods is { Count: > 0 }
            ? string.Join(",", methods.Order(StringComparer.Ordinal))
            : "*";
        var route = endpoint is RouteEndpoint routeEndpoint
            ? routeEndpoint.RoutePattern.RawText ?? endpoint.DisplayName
            : endpoint.DisplayName;
        return $"{verb} {route}";
    }

    [Fact]
    public void Every_mapped_endpoint_declares_authorization_or_explicit_anonymity()
    {
        var endpoints = MappedEndpoints();

        // A sanity floor, so an empty or half-built table cannot pass vacuously.
        endpoints.Count.ShouldBeGreaterThan(100);

        var undeclared = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null
                        && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(Describe)
            .Order(StringComparer.Ordinal)
            .ToList();

        undeclared.ShouldBeEmpty(
            "every Admin API endpoint must carry .RequirePermission(...) / .RequireAuthorization() "
            + "or a deliberate .AllowAnonymous(). Undeclared: " + string.Join("; ", undeclared));
    }

    [Fact]
    public void The_anonymous_surface_is_exactly_the_reviewed_allowlist()
    {
        var anonymous = MappedEndpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(Describe)
            .Order(StringComparer.Ordinal)
            .ToList();

        string.Join(Environment.NewLine, anonymous).ShouldBe(
            string.Join(Environment.NewLine, ExpectedAnonymousEndpoints.Order(StringComparer.Ordinal)),
            "adding or removing an anonymous Admin API route requires updating ExpectedAnonymousEndpoints");
    }

    [Fact]
    public async Task The_fallback_policy_requires_an_authenticated_administrator_session()
    {
        using var client = _factory.CreateClient();
        var provider = _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        var fallback = await provider.GetFallbackPolicyAsync();

        fallback.ShouldNotBeNull("without a fallback policy an unannotated endpoint is anonymous");
        fallback.AuthenticationSchemes.Count.ShouldBe(1);
        fallback.AuthenticationSchemes.ShouldContain(AdminAuthenticationHandler.SchemeName);
        fallback.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().ShouldNotBeEmpty();
    }

    [Fact]
    public async Task An_anonymous_request_for_an_unmapped_path_is_refused_not_routed()
    {
        // The fallback policy applies to requests that reach no endpoint as well, so
        // an unauthenticated caller cannot tell an unmapped path from a protected one.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/admin/v1/no-such-route", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
