using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace EndpointPlatform.Infrastructure.Tests.Persistence;

/// <summary>
/// Spins up a throwaway PostgreSQL container and applies the real migrations to it.
/// </summary>
/// <remarks>
/// <para>
/// A real server, not an in-memory or SQLite substitute. The behaviour under test
/// here - triggers, <c>jsonb</c>, <c>inet</c>, partial indexes, role privileges -
/// does not exist in a fake provider, so testing against one would prove nothing
/// about what actually runs.
/// </para>
/// <para>
/// The image is pinned to the same tag as infra/docker-compose.yml, so tests
/// exercise the same server version development and deployment use.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string PostgresImage = "postgres:17.6-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(PostgresImage)
        .WithDatabase("endpoint_platform_test")
        .WithUsername("test_owner")
        .WithPassword("test_owner_password_not_a_real_secret")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public EndpointPlatformDbContext CreateDbContext(TimeProvider? timeProvider = null)
    {
        var options = new DbContextOptionsBuilder<EndpointPlatformDbContext>()
            .UseNpgsql(ConnectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(EndpointPlatformDbContext.MigrationsAssemblyName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EndpointPlatformDbContext.Schema);
            })
            .AddInterceptors(
                new DeviceGroupAssignmentInterceptor(),
                new AuditableEntityInterceptor(timeProvider ?? TimeProvider.System),
                new AuditImmutabilityInterceptor())
            .Options;

        return new EndpointPlatformDbContext(options);
    }

    /// <summary>
    /// A context WITHOUT the audit-immutability interceptor, used to prove that the
    /// database rejects audit mutation on its own rather than relying on the
    /// application-side guard.
    /// </summary>
    public EndpointPlatformDbContext CreateDbContextWithoutAuditGuard()
    {
        var options = new DbContextOptionsBuilder<EndpointPlatformDbContext>()
            .UseNpgsql(ConnectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(EndpointPlatformDbContext.MigrationsAssemblyName);
                npgsql.MigrationsHistoryTable("__ef_migrations_history", EndpointPlatformDbContext.Schema);
            })
            .AddInterceptors(new DeviceGroupAssignmentInterceptor(), new AuditableEntityInterceptor(TimeProvider.System))
            .Options;

        return new EndpointPlatformDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
