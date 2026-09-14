using System.Net.Http.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Agents;
using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Persistence.Interceptors;
using EndpointPlatform.Infrastructure.Persistence.Seeding;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.Tests.Agents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// The full Admin API stack on real PostgreSQL, seeded with one account per
/// built-in role. This is the substrate for the RBAC enforcement matrix.
/// </summary>
public sealed class AdminApiPostgresFixture : IAsyncLifetime
{
    public const string Password = "correct horse battery staple 9!";

    /// <summary>A throwaway 32-byte key for the test host. Seals nothing real.</summary>
    private const string TestEscrowKey = "dGVzdC1lc2Nyb3cta2V5LTMyLWJ5dGVzLWxvbmchISE=";

    public const string SuperAdminEmail = "superadmin@test.local";
    public const string ItAdminEmail = "itadmin@test.local";
    public const string HelpdeskEmail = "helpdesk@test.local";
    public const string AuditorEmail = "auditor@test.local";
    public const string DisabledEmail = "disabled@test.local";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17.6-alpine")
        .WithDatabase("endpoint_platform_adminapi_test")
        .WithUsername("test_owner")
        .WithPassword("test_owner_password_not_a_real_secret")
        .Build();

    // A real Redis so the ephemeral-secret path (create user / reset password) is
    // exercised end to end rather than short-circuited by an unreachable cache.
    private readonly RedisContainer _redis = new RedisBuilder().Build();

    private WebApplicationFactory<Program>? _factory;
    private WebApplicationFactory<Program>? _kestrelFactory;
    private readonly SemaphoreSlim _kestrelGate = new(1, 1);

    public WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture not initialised.");

    /// <summary>
    /// The same application on a REAL Kestrel socket instead of the in-process
    /// test server. Exists for exactly one class of test: limits Kestrel itself
    /// enforces — request-body size above all — which the in-process server
    /// skips entirely. That skip is how a 29 MB upload could pass every API test
    /// and still be refused with 413 in production.
    /// </summary>
    public async Task<WebApplicationFactory<Program>> GetKestrelFactoryAsync()
    {
        if (_kestrelFactory is not null)
        {
            return _kestrelFactory;
        }

        await _kestrelGate.WaitAsync();
        try
        {
            if (_kestrelFactory is null)
            {
                var factory = new AdminApiTestFactory(
                    _container.GetConnectionString(), _redis.GetConnectionString());
                factory.UseKestrel();
                // The server binds on first client creation.
                _ = factory.CreateClient();
                _kestrelFactory = factory;
            }

            return _kestrelFactory;
        }
        finally
        {
            _kestrelGate.Release();
        }
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await _redis.StartAsync();

        await using (var dbContext = CreateDbContext())
        {
            await dbContext.Database.MigrateAsync();

            var seeder = new ReferenceDataSeeder(
                dbContext, Options.Create(new SeedOptions()), NullLogger<ReferenceDataSeeder>.Instance);
            await seeder.SeedAsync();

            var organization = await dbContext.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var roles = await dbContext.Roles.Where(r => r.IsBuiltIn).ToDictionaryAsync(r => r.Key);

            AddUser(dbContext, organization.Id, SuperAdminEmail, roles[SystemRoles.SuperAdministrator].Id);
            AddUser(dbContext, organization.Id, ItAdminEmail, roles[SystemRoles.ItAdministrator].Id);
            AddUser(dbContext, organization.Id, HelpdeskEmail, roles[SystemRoles.Helpdesk].Id);
            AddUser(dbContext, organization.Id, AuditorEmail, roles[SystemRoles.Auditor].Id);

            var disabled = AddUser(dbContext, organization.Id, DisabledEmail, roles[SystemRoles.ItAdministrator].Id);
            disabled.Disable();

            await dbContext.SaveChangesAsync();
        }

        var connectionString = _container.GetConnectionString();
        _factory = new AdminApiTestFactory(connectionString, _redis.GetConnectionString());
    }

    private static PlatformUser AddUser(
        EndpointPlatformDbContext dbContext, Guid organizationId, string email, Guid roleId)
    {
        var user = new PlatformUser(organizationId, email, email.Split('@')[0]);
        user.SetPasswordHash(PasswordHasher.Hash(Password), DateTimeOffset.UtcNow);
        user.AssignRole(roleId);

        // These fixture accounts stand in for established operators, which the
        // production migration grants organization-wide scope. Tests that exercise
        // deny-by-default create their own unscoped administrator instead.
        user.GrantAllDeviceScope();
        dbContext.PlatformUsers.Add(user);
        return user;
    }

    public async Task DisposeAsync()
    {
        if (_kestrelFactory is not null)
        {
            await _kestrelFactory.DisposeAsync();
        }

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        await _container.DisposeAsync();
        await _redis.DisposeAsync();
    }

    public EndpointPlatformDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<EndpointPlatformDbContext>()
            .UseNpgsql(_container.GetConnectionString(), npgsql =>
            {
                npgsql.MigrationsAssembly(EndpointPlatformDbContext.MigrationsAssemblyName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EndpointPlatformDbContext.Schema);
            })
            .AddInterceptors(
                new DeviceGroupAssignmentInterceptor(),
                new AuditableEntityInterceptor(TimeProvider.System),
                new AuditImmutabilityInterceptor())
            .Options;

        return new EndpointPlatformDbContext(options);
    }

    /// <summary>Signs the account in through the real endpoint and returns a Bearer token.</summary>
    public async Task<string> SignInAsync(string email, string password = Password)
    {
        using var client = Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/admin/v1/auth/login", UriKind.Relative),
            new { email, password });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("sessionToken").GetString()!;
    }

    public HttpClient CreateClientFor(string token)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    /// <summary>
    /// The subject the fixture authority signs with. Retained for tests that build
    /// signed artifacts; under the Internal trust mode this host runs in, no test
    /// needs a signature to publish and the signer is never read.
    /// </summary>
    public const string ExpectedSignerSubject = "CN=Techsara Test Signing";

    /// <summary>
    /// One throwaway certificate authority for the whole fixture, generated in
    /// memory and discarded at the end. Used only to produce signed artifacts for
    /// tests that want to prove a signature makes no difference under Internal.
    /// The host does not trust it -- it runs the production default, which trusts
    /// nothing and consults no signature.
    /// </summary>
    public static TestArtifacts.Authority SigningAuthority { get; } = TestArtifacts.CreateAuthority();

    private sealed class AdminApiTestFactory(string connectionString, string redisConnectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("Database:ConnectionString", connectionString);
            builder.UseSetting("Redis:ConnectionString", redisConnectionString);
            builder.UseSetting("Redis:InstanceName", "endpointplatform:adminapitest:");
            builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");

            // Deliberately no AgentReleases settings: the host runs the production
            // default, Internal, with no publisher configured and system trust
            // untouched. That is the configuration the gate must publish under.

        // Mandatory for the Admin API since recovery-key escrow shipped: the
        // options are validated on start, so without a key the host refuses to
        // build and every test in the suite fails at construction. A fixed test
        // key is fine here and is not a secret - it seals nothing real.
        builder.UseSetting("RecoveryEscrow:Key", TestEscrowKey);
        builder.UseSetting("RecoveryEscrow:KeyVersion", "1");
            // Every test request arrives from the same loopback address, so the
            // per-address login limiter must be generous here. The limiter itself
            // has a dedicated test that lowers this again.
            builder.UseSetting("AdminAuth:LoginAttemptsPerMinutePerAddress", "1000");
        }
    }
}

[CollectionDefinition(Name)]
public sealed class AdminApiPostgresCollection : ICollectionFixture<AdminApiPostgresFixture>
{
    public const string Name = "admin-api-postgres";
}
