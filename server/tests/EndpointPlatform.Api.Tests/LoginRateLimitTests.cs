using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Proves the sign-in endpoint is brute-force rate limited. Uses its own factory
/// with a tiny limit; the database is unreachable on purpose, because rate
/// limiting must reject BEFORE any credential processing happens.
/// </summary>
/// <remarks>
/// Joined to the shared collection ONLY for serialisation: two factories
/// resolving the same entry point in parallel race inside HostFactoryResolver's
/// process-wide listener (see AdminApiCollection remarks).
/// </remarks>
[Collection(AdminApiPostgresCollection.Name)]
public sealed class LoginRateLimitTests
{
    private sealed class TinyLimitFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("Database:ConnectionString",
                "Host=127.0.0.1;Port=1;Database=unreachable_by_design;Username=none;Password=none");
            builder.UseSetting("Redis:ConnectionString", "127.0.0.1:1,abortConnect=false,connectTimeout=100");
            builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            builder.UseSetting("AdminAuth:LoginAttemptsPerMinutePerAddress", "3");

            // Required since recovery-key escrow shipped: the Admin API validates
            // this on start and refuses to build without it, so a factory that
            // omits it fails host construction and every test using it dies with
            // ObjectDisposedException rather than anything informative. A
            // throwaway 32-byte key; it seals nothing real.
            builder.UseSetting("RecoveryEscrow:Key", "dGVzdC1lc2Nyb3cta2V5LTMyLWJ5dGVzLWxvbmchISE=");

            // Same again for multi-factor: Mfa options are validated on start.
            builder.UseSetting("Mfa:TotpKey", "dGVzdC1tZmEta2V5LTMyLWJ5dGVzLWxvbmchISEhISE=");
        }
    }

    [Fact]
    public async Task Sign_in_attempts_beyond_the_limit_receive_429()
    {
        using var factory = new TinyLimitFactory();
        using var client = factory.CreateClient();

        // Guard: if the tiny limit did not bind, the assertions below would test
        // nothing. Fail loudly on the configuration instead.
        var boundOptions = factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<
                Infrastructure.Security.AdminAuthOptions>>().Value;
        boundOptions.LoginAttemptsPerMinutePerAddress.ShouldBe(3);

        // Concurrent, not sequential: each processed attempt spends ~30s failing
        // against the unreachable database (EF retry policy), and sequential
        // attempts would each land in a fresh one-minute window. Firing all five
        // together guarantees they hit the same window - and shows the limiter
        // rejects at the front door, before any slow credential handling.
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
            client.PostAsJsonAsync(
                new Uri("/admin/v1/auth/login", UriKind.Relative),
                new { email = "attacker@test.local", password = "guess" })));

        var statuses = responses.Select(r => r.StatusCode).ToArray();

        statuses.Count(s => s == HttpStatusCode.TooManyRequests).ShouldBe(
            2, "permit limit is 3, so exactly 2 of 5 concurrent attempts must be cut off with 429");
        statuses.Count(s => s != HttpStatusCode.TooManyRequests).ShouldBe(
            3, "the first 3 attempts must reach credential processing");
    }
}
