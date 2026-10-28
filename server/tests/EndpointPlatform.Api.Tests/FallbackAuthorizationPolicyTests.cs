using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The deny-by-default fallback policy against the real host on real PostgreSQL
/// and Redis: what it must refuse, and what it must never break.
/// </summary>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class FallbackAuthorizationPolicyTests(AdminApiPostgresFixture fixture)
{
    private const string UnannotatedPath = "/__test/unannotated-route";

    private readonly AdminApiPostgresFixture _fixture = fixture;

    /// <summary>
    /// The container healthcheck (<c>curl -fsS .../health/ready</c>) and the deploy
    /// script's health wait carry no credentials. If the fallback policy ever caught
    /// these, every deploy would fail its health gate -- so they are pinned at 200,
    /// anonymously, against a host whose dependencies are actually up.
    /// </summary>
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_probes_answer_200_without_credentials(string path)
    {
        using var anonymous = _fixture.Factory.CreateClient();

        var response = await anonymous.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{path} must stay anonymous for the orchestrator");
    }

    /// <summary>
    /// An endpoint mapped with no authorization metadata at all -- exactly the
    /// mistake the fallback policy exists for -- is refused to an anonymous caller
    /// and served to a signed-in administrator.
    /// </summary>
    /// <remarks>
    /// The route is added to the REAL route table, behind the real middleware and
    /// authorization, by a startup filter that runs after the production pipeline
    /// has been composed. The authenticated 200 is the control: it proves the route
    /// exists and that the anonymous 401 came from the fallback policy rather than
    /// from routing.
    /// </remarks>
    [Fact]
    public async Task An_endpoint_mapped_without_authorization_metadata_requires_a_session()
    {
        await using var factory = _fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new UnannotatedRouteStartupFilter())));

        using var anonymous = factory.CreateClient();

        // Prove the route really is unannotated before relying on it.
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == UnannotatedPath);
        endpoint.Metadata.GetMetadata<IAuthorizeData>().ShouldBeNull();
        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();

        (await anonymous.GetAsync(new Uri(UnannotatedPath, UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "an unannotated endpoint must not be anonymous");

        var token = await _fixture.SignInAsync(AdminApiPostgresFixture.AuditorEmail);
        using var signedIn = factory.CreateClient();
        signedIn.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var allowed = await signedIn.GetAsync(new Uri(UnannotatedPath, UriKind.Relative));
        allowed.StatusCode.ShouldBe(HttpStatusCode.OK, "the fallback asks for a session, nothing more");
        (await allowed.Content.ReadAsStringAsync()).ShouldBe("reached");
    }

    /// <summary>
    /// Maps one endpoint with no authorization metadata onto the application's own
    /// route table. After <c>next</c> has composed the production pipeline,
    /// <c>UseEndpoints</c> resolves the application's route builder, so the endpoint
    /// sits behind the real authentication, gates and authorization middleware.
    /// </summary>
    private sealed class UnannotatedRouteStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(routes => routes.MapGet(UnannotatedPath, () => "reached"));
        };
    }
}
