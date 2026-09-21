using EndpointPlatform.Infrastructure.Persistence;
using EndpointPlatform.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Infrastructure.Tests.Persistence;

/// <summary>
/// Creates a throwaway database on the local PostgreSQL server and applies the real
/// migrations to it.
/// </summary>
/// <remarks>
/// <para>
/// A real server, not an in-memory or SQLite substitute. The behaviour under test
/// here - triggers, <c>jsonb</c>, <c>inet</c>, partial indexes, role privileges -
/// does not exist in a fake provider, so testing against one would prove nothing
/// about what actually runs.
/// </para>
/// <para>
/// Which server is decided by <see cref="TestDatabase.PostgresVariable"/>. Run the
/// suite against PostgreSQL 17, the version development and deployment use.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private TestDatabase? _database;

    public string ConnectionString =>
        _database?.ConnectionString ?? throw new InvalidOperationException("Fixture not initialised.");

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync("infra");

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

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
