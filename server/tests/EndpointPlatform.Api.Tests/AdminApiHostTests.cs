using System.Net;
using System.Net.Http.Json;
using EndpointPlatform.Contracts.Common;
using EndpointPlatform.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.Api.Tests;

[Collection(AdminApiCollection.Name)]
public sealed class AdminApiHostTests(AdminApiFactory factory)
{
    private readonly AdminApiFactory _factory = factory;

    [Fact]
    public async Task Liveness_reports_healthy_without_touching_any_dependency()
    {
        // Liveness must not depend on PostgreSQL or Redis. This factory points both
        // at unreachable addresses, so a green result here proves the separation.
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("Healthy");
        body.ShouldContain("self");
    }

    [Fact]
    public async Task Readiness_reports_unhealthy_when_postgres_is_unreachable()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// The health endpoints are anonymous so an orchestrator can probe them, which
    /// makes any detail they leak publicly readable. The default health response
    /// writer includes exception text, and a failing Npgsql check puts the
    /// connection string in that text.
    /// </summary>
    [Fact]
    public async Task Health_responses_never_leak_connection_details_or_exception_text()
    {
        using var client = _factory.CreateClient();

        var body = await (await client.GetAsync(new Uri("/health/ready", UriKind.Relative)))
            .Content.ReadAsStringAsync();

        body.ShouldNotContain("Password", Case.Insensitive);
        body.ShouldNotContain("Username", Case.Insensitive);
        body.ShouldNotContain("unreachable_by_design");
        body.ShouldNotContain("Host=");
        body.ShouldNotContain("Exception", Case.Insensitive);
        body.ShouldNotContain("StackTrace", Case.Insensitive);
        body.ShouldNotContain("Npgsql", Case.Insensitive);
    }

    [Fact]
    public async Task Root_returns_non_sensitive_service_information()
    {
        using var client = _factory.CreateClient();

        var info = await client.GetFromJsonAsync<ServiceInfoResponse>(new Uri("/", UriKind.Relative));

        info.ShouldNotBeNull();
        info.Service.ShouldBe("admin-api");
        info.Environment.ShouldBe("Development");
    }

    [Theory]
    [InlineData("X-Content-Type-Options", "nosniff")]
    [InlineData("X-Frame-Options", "DENY")]
    [InlineData("Referrer-Policy", "no-referrer")]
    public async Task Security_headers_are_applied_to_every_response(string header, string expected)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.Headers.TryGetValues(header, out var values).ShouldBeTrue($"{header} must be present");
        values!.ShouldContain(expected);
    }

    [Fact]
    public async Task Content_security_policy_forbids_framing_and_script()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.Headers.TryGetValues("Content-Security-Policy", out var values).ShouldBeTrue();
        var csp = string.Join(' ', values!);
        csp.ShouldContain("default-src 'none'");
        csp.ShouldContain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task A_correlation_id_is_returned_on_every_response()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.Headers.TryGetValues(CorrelationId.HeaderName, out var values).ShouldBeTrue();
        values!.Single().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_well_formed_client_correlation_id_is_echoed_back()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.Add(CorrelationId.HeaderName, "trace-from-dashboard-1");

        var response = await client.SendAsync(request);

        response.Headers.GetValues(CorrelationId.HeaderName).Single().ShouldBe("trace-from-dashboard-1");
    }

    [Fact]
    public async Task An_over_long_client_correlation_id_is_replaced_rather_than_echoed()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        var oversized = new string('a', CorrelationId.MaxLength + 1);
        request.Headers.Add(CorrelationId.HeaderName, oversized);

        var response = await client.SendAsync(request);

        response.Headers.GetValues(CorrelationId.HeaderName).Single().ShouldNotBe(oversized);
    }

    [Fact]
    public async Task The_admin_api_does_not_serve_agent_protocol_routes()
    {
        // The two APIs are separate trust boundaries. An agent endpoint appearing on
        // the administrator host would let a stolen device credential reach
        // administrative surface, so this asserts the boundary rather than trusting it.
        //
        // Asserted on the route table, not on a 404: the deny-by-default fallback
        // policy answers an anonymous request for an unmapped path with 401, so the
        // status code can no longer say whether a route exists. The table can.
        using var client = _factory.CreateClient();

        var routes = _factory.Services
            .GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>()
            .Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .ToList();

        routes.ShouldNotBeEmpty();
        routes.ShouldNotContain(
            r => r.TrimStart('/').StartsWith("agent", StringComparison.OrdinalIgnoreCase),
            "the Admin API must not map any agent-protocol route");

        foreach (var path in new[] { "/agent/v1/enroll", "/agent/v1/heartbeat", "/agent/v1/inventory" })
        {
            var response = await client.GetAsync(new Uri(path, UriKind.Relative));

            // Never anything a caller could use: no route answers, and the fallback
            // policy refuses the anonymous request before routing could matter.
            response.StatusCode.ShouldBe(
                HttpStatusCode.Unauthorized,
                $"the Admin API must not expose the agent route {path}");
        }
    }
}
