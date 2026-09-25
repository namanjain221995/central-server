using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Persistence.Interceptors;
using EndpointPlatform.Infrastructure.Persistence.Seeding;
using EndpointPlatform.Infrastructure.Tests;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EndpointPlatform.AgentApi.Tests;

/// <summary>
/// The full Agent API stack against a real PostgreSQL server: enrollment and
/// heartbeat behaviour is only meaningful with real uniqueness constraints,
/// optimistic concurrency (xmin) and triggers underneath it.
/// </summary>
public sealed class AgentApiPostgresFixture : IAsyncLifetime
{
    // A throwaway database on the local PostgreSQL server, dropped on dispose.
    private TestDatabase? _database;

    private string DatabaseConnectionString =>
        _database?.ConnectionString ?? throw new InvalidOperationException("Fixture not initialised.");

    private WebApplicationFactory<Program>? _factory;

    public WebApplicationFactory<Program> Factory =>
        _factory ?? throw new InvalidOperationException("Fixture not initialised.");

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync("agentapi");

        // Migrate + seed before the host starts.
        await using (var dbContext = CreateDbContext())
        {
            await dbContext.Database.MigrateAsync();

            var seeder = new ReferenceDataSeeder(
                dbContext,
                Options.Create(new SeedOptions()),
                NullLogger<ReferenceDataSeeder>.Instance);
            await seeder.SeedAsync();
        }

        _factory = new AgentApiTestFactory(DatabaseConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    public EndpointPlatformDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<EndpointPlatformDbContext>()
            .UseNpgsql(DatabaseConnectionString, npgsql =>
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

    /// <summary>
    /// The escrow sealing keypair for these tests.
    /// </summary>
    /// <remarks>
    /// The test process holds both halves because it plays both roles: the endpoint
    /// that seals, and the operator who later proves the Agent API could not have.
    /// Only the public half is ever configured into the host under test.
    /// </remarks>
    public static readonly RSA SealingKey = RSA.Create(3072);

    public static string SealingPublicKeySpki { get; } =
        Convert.ToBase64String(SealingKey.ExportSubjectPublicKeyInfo());

    public static string SealingFingerprint { get; } =
        Convert.ToHexString(SHA256.HashData(SealingKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

    private sealed class AgentApiTestFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("Database:ConnectionString", connectionString);
            // Redis stays unreachable: nothing in enrollment/heartbeat needs it,
            // and the tests must prove that.
            builder.UseSetting("Redis:ConnectionString", "127.0.0.1:1,abortConnect=false,connectTimeout=100");
            builder.UseSetting("Redis:InstanceName", "endpointplatform:agentapitest:");

            // The enrolment endpoints are rate-limited per source address (120 a
            // minute in production). Every test here enrols its own device from the
            // one loopback address the test server sees, and the whole suite runs
            // in well under a minute, so the production limit throttles the LATER
            // suites of a run with 429s that have nothing to do with what they
            // test. Raised for the test host only; the limiter itself is unchanged.
            builder.UseSetting("AgentServer:EnrollmentRequestsPerMinutePerAddress", "100000");

            // The PUBLIC half only, which is the whole point: the Agent API is
            // given the key endpoints seal to and nothing that could open what
            // they send. AgentApiKeyBoundaryGuard fails this host at startup if a
            // private or master key is ever configured here, so a test that tried
            // to hand it one would not get as far as asserting anything.
            builder.UseSetting("RecoveryEscrow:SealingPublicKey", SealingPublicKeySpki);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class AgentApiPostgresCollection : ICollectionFixture<AgentApiPostgresFixture>
{
    public const string Name = "agent-api-postgres";
}
